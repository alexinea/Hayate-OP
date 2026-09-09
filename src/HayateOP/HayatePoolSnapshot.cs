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
}