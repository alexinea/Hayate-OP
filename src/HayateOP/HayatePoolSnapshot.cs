using System;
using System.Collections.Generic;
namespace DotNetCore.HayateOP;
public class HayatePoolSnapshot
{
    /// <summary>The UTC timestamp at which this snapshot was taken.</summary>
    public DateTimeOffset Timestamp { get; set; }
    /// <summary>The total number of objects currently held by the pool (idle plus borrowed).</summary>
    public int PooledCount { get; set; }
    /// <summary>The number of objects currently borrowed out.</summary>
    public int BorrowedCount { get; set; }
    /// <summary>The cumulative number of objects the pool has ever created.</summary>
    public long TotalCreated { get; set; }
    /// <summary>The cumulative number of acquires that could not be satisfied.</summary>
    public long TotalMissed { get; set; }
    /// <summary>The cumulative number of successful acquires.</summary>
    public long TotalAcquired { get; set; }
    /// <summary>The cumulative leak count reported by leak detection.</summary>
    public long LeakCount { get; set; }
    /// <summary>
    /// The suspected-leak count (a retrospective alert counter used when leak detection is disabled).
    /// </summary>
    public long LeakSuspectedCount { get; set; }
    /// <summary>The captured leak traces (stack frames or placeholders), if leak tracing is enabled.</summary>
    public IReadOnlyList<string> LeakTraces { get; set; } = [];
    /// <summary>
    /// Whether allocation tracking is enabled.
    /// </summary>
    public bool AllocationTrackingEnabled { get; set; }
    /// <summary>
    /// Cumulative bytes allocated on the borrow path (synchronous <c>Acquire</c> only).
    /// </summary>
    public long AcquireAllocatedBytes { get; set; }
    /// <summary>
    /// Cumulative bytes allocated on the return path (synchronous <c>Release</c> only).
    /// </summary>
    public long ReleaseAllocatedBytes { get; set; }
    /// <summary>
    /// Per-object lifecycle details (covering every live wrapper object: idle plus borrowed).
    /// </summary>
    public IReadOnlyList<HayatePoolObjectDetail> ObjectDetails { get; set; } = [];
}