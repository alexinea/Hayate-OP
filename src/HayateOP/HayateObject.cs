using System;
using System.Collections.Generic;
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
    public DateTime CreatedAt { get; set; }
    public DateTime LastBorrowedAt { get; set; }
    public DateTime LastReleasedAt { get; set; }
    public string AcquireTrace { get; set; }

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
        CreatedAt = DateTime.UtcNow;
        LastReleasedAt = DateTime.UtcNow;
    }
}
