using System;
using System.Collections.Generic;

namespace DotNetCore.HayateOP;

public class HayatePoolSnapshot
{
    public DateTimeOffset Timestamp { get; set; }
    public int PooledCount { get; set; }
    public int BorrowedCount { get; set; }
    public long TotalCreated { get; set; }
    public long TotalMissed { get; set; }
    public long TotalAcquired { get; set; }
    public long LeakCount { get; set; }

    /// <summary>
    /// 疑似泄漏次数（M4，泄漏检测关闭时的回查告警计数）。
    /// </summary>
    public long LeakSuspectedCount { get; set; }

    public IReadOnlyList<string> LeakTraces { get; set; } = [];

    /// <summary>
    /// 是否启用分配追踪（M3）。
    /// </summary>
    public bool AllocationTrackingEnabled { get; set; }

    /// <summary>
    /// 借出路径累计分配字节数（M3，仅同步 <c>Acquire</c>）。
    /// </summary>
    public long AcquireAllocatedBytes { get; set; }

    /// <summary>
    /// 归还路径累计分配字节数（M3，仅同步 <c>Release</c>）。
    /// </summary>
    public long ReleaseAllocatedBytes { get; set; }

    /// <summary>
    /// M20（2.5）：逐对象生命周期明细（覆盖全部存活包装对象：空闲 + 借出）。
    /// </summary>
    public IReadOnlyList<HayatePoolObjectDetail> ObjectDetails { get; set; } = [];
}