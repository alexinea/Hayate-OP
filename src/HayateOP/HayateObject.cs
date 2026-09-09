using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace DotNetCore.HayateOP;

/// <summary>
/// Describes where a pooled wrapper currently lives.
/// It drives the atomic claim protocol of <see cref="HayatePoolBasic{T}.Shard"/>:
/// only the caller that wins the <c>InPool -> Removing</c> transition is allowed
/// to destroy the object, which eliminates the "destroy while borrowed" race.
/// </summary>
internal enum HayateObjectLocation
{
    /// <summary>Just created, not owned by any shard yet.</summary>
    None = 0,

    /// <summary>Sitting idle inside a shard free list.</summary>
    InPool = 1,

    /// <summary>Handed out to a caller.</summary>
    Borrowed = 2,

    /// <summary>Claimed by eviction / idle validation, awaiting destruction.</summary>
    Removing = 3,

    /// <summary>Already destroyed; must never be handed out again.</summary>
    Destroyed = 4
}

public class HayateObject<T> where T : class
{
    public T Value { get; set; }

    /// <summary>
    /// PR-D A1（2.3 行为变更）：时间戳由 <c>DateTime</c> 改为 <see cref="Stopwatch.GetTimestamp"/> 值，
    /// 消除热路径（创建/借出/归还/驱逐判定）上 <see cref="DateTime.UtcNow"/> 的 5–10× 系统调用开销。
    /// 时长换算：<c>(end - start) / Stopwatch.Frequency</c>（秒）或 <c>(end - start) * 1000.0 / Stopwatch.Frequency</c>（毫秒）。
    /// </summary>
    public long CreatedAt { get; set; }

    /// <inheritdoc cref="CreatedAt"/>
    public long LastBorrowedAt { get; set; }

    /// <inheritdoc cref="CreatedAt"/>
    public long LastReleasedAt { get; set; }

    /// <summary>
    /// M20（2.5）：创建时刻的挂钟时间戳（<see cref="DateTimeOffset.UtcNow.Ticks"/>）。
    /// 与 <see cref="CreatedAt"/>（Stopwatch ticks，用于时长测量）互补——
    /// 本字段面向快照/日志输出的人类可读口径，不参与任何时长判定。
    /// </summary>
    public long CreatedAtTick { get; set; }

    /// <summary>
    /// M20（2.5）：累计借出次数。借出路径单调递增（借出瞬间包装对象由本线程独占，
    /// 驱逐/校验无法认领借出中对象，故普通自增即可，无需 Interlocked）。
    /// </summary>
    public int LeaseCount { get; internal set; }

    /// <summary>
    /// M20（2.5）：所属池的逻辑名（创建时由池写入，生命周期内不变）。
    /// 供跨池诊断 / 快照输出定位对象归属。
    /// </summary>
    public string OwnerPoolName { get; set; }

    /// <summary>
    /// M16（2.5，breaking）：借出租约上下文（租约 ID + 借出帧数组 + 借出时刻）。
    /// 采集载体为 <see cref="HayateLeaseContext"/> 的 AsyncLocal 异步流 + 本属性（包装侧快照引用）；
    /// 并发借还各自持有独立上下文实例，不再相互覆盖。取证模式与采集频率仍由
    /// <see cref="HayateLeakTraceCaptureMode"/> 控制（L1 三模式语义不变）。
    /// 2.5 批次二 M10 曾短暂引入 <c>AcquireStackFrames: StackFrame[]</c>，本属性为其最终形态；
    /// 2.4 及之前的 <c>AcquireTrace: string</c> 已移除，文本形态经 <c>TakeSnapshot().LeakTraces</c> 获取。
    /// </summary>
    public HayateLeaseContext LeaseContext { get; internal set; }

    /// <summary>
    /// P2-新-1：借出状态改为 <see cref="Location"/> 的计算属性，消除双源不一致窗口。
    /// 原独立 bool 字段与 Location（Borrowed 状态）由两条路径分别维护，
    /// 弱内存模型下存在 stale 读风险；Location 为 volatile 且全部迁移在 Shard 自旋锁内完成，
    /// 以它为唯一事实源。Removing（驱逐认领）/Destroyed 均不属于 Borrowed，语义与原字段一致。
    /// </summary>
    public bool IsBorrowed => Location == HayateObjectLocation.Borrowed;
    public int Generation { get; set; } // 0 年轻代 1 老年代

    public int ValidationSkipCount { get; set; } // 老年代跳过验证计数

    public long LeaseTimeMs { get; set; }

    /// <summary>
    /// 对象被借出时所属的分片索引。
    /// 由 <see cref="HayatePoolBasic{T}"/> 在 Acquire 命中时记录，Release 时按此 round-trip，
    /// 避免使用 <c>Thread.GetCurrentProcessorId() % ShardCount</c> 落到 max=0 的分片而静默 dispose 对象。
    /// 默认值 0 是 PreWarm 单对象的合法归宿。
    /// </summary>
    public int ShardIndex { get; set; }

    /// <summary>
    /// 对象当前所处位置，驱动分片端的原子认领协议。
    /// 所有状态迁移都在 Shard 的自旋锁内完成，因此这里只需要 volatile 保证跨锁可见性。
    /// </summary>
    internal volatile HayateObjectLocation Location;

    /// <summary>
    /// 对象在所属分片空闲链表中的节点引用；为 null 表示不在任何分片链表内。
    /// 仅允许在 Shard 自旋锁内读写，用于把 Remove 从 O(n) 重建队列降级为 O(1) 摘除。
    /// </summary>
    internal LinkedListNode<HayateObject<T>> Node;

    /// <summary>
    /// 销毁幂等标记（0 = 未销毁，1 = 已销毁）。
    /// 驱逐、空闲校验、归还拒绝三条路径可能并发命中同一个对象，用 CAS 保证只销毁一次。
    /// </summary>
    internal int Destroyed;

    public HayateObject(T value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
        CreatedAt = Stopwatch.GetTimestamp();
        LastReleasedAt = CreatedAt;
        // M20：挂钟创建时间（快照输出口径，不参与时长判定）
        CreatedAtTick = DateTimeOffset.UtcNow.Ticks;
    }
}
