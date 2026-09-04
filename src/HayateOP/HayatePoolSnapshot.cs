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
    public IReadOnlyList<string> LeakTraces { get; set; } = [];
}