using System;
using System.Diagnostics;
using System.Threading;

namespace DotNetCore.HayateOP;

/// <summary>
/// M16（2.5，breaking）：借出租约上下文。
/// <para>
/// 一次借出对应一个不可变租约上下文：租约 ID（进程级单调递增）+ 借出帧数组 +
/// 借出时刻（Stopwatch ticks）。采集载体为 <see cref="AsyncLocal{T}"/>（<see cref="Current"/>）——
/// 上下文随调用方的异步执行流（ExecutionContext）流动，<b>并发借还各自持有独立副本，
/// 不再相互覆盖</b>（2.4 及之前包装对象上的 string 采集会被同对象复借覆盖，且跨异步
/// 流共享时无法区分归属）。
/// </para>
/// <para>
/// 生命周期：Acquire 取证采集时创建并写入当前异步流 + 包装对象；Release 结束租约时
/// 清空当前异步流；Destroy 时清空包装对象上的引用。取证开关与频率仍由
/// <see cref="HayateLeakTraceCaptureMode"/> 控制（Off 默认不采集，L1 语义不变）。
/// </para>
/// </summary>
public sealed class HayateLeaseContext
{
    // M16：异步流载体。static readonly——每次对 AsyncLocal 写值都是「复制当前执行上下文 +
    // 覆盖本流槽位」，兄弟异步流互不可见，天然并发隔离；上下文实例本身不可变。
    internal static readonly AsyncLocal<HayateLeaseContext> Flow = new();

    private static long _leaseIdCounter;

    /// <summary>进程级唯一的租约 ID（单调递增）。</summary>
    public long LeaseId { get; }

    /// <summary>借出时刻的调用栈帧（fNeedFileInfo:false 低开销采集；可能为空数组）。</summary>
    public StackFrame[] Frames { get; }

    /// <summary>借出时刻（Stopwatch ticks，与池内时长判定同口径）。</summary>
    public long BorrowedAt { get; }

    /// <summary>当前异步流的租约上下文；未在租约期（未采集/已结束）为 null。</summary>
    public static HayateLeaseContext Current => Flow.Value;

    internal HayateLeaseContext(StackFrame[] frames, long borrowedAt)
    {
        LeaseId = Interlocked.Increment(ref _leaseIdCounter);
        Frames = frames ?? Array.Empty<StackFrame>();
        BorrowedAt = borrowedAt;
    }

    /// <summary>把本租约写入当前异步流（借出路径调用）。</summary>
    internal void AttachToFlow() => Flow.Value = this;

    /// <summary>结束当前异步流的租约（Release 路径调用；写值有执行上下文复制成本，由调用方门控）。</summary>
    internal static void DetachFromFlow() => Flow.Value = null;
}
