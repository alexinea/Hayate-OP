using System;

namespace DotNetCore.HayateOP;

/// <summary>
/// Statistics information for object pool
/// </summary>
public class HayatePoolStats
{
    /// <summary>
        /// Current number of available objects in the pool
    /// </summary>
    public int PooledCount { get; set; }

    /// <summary>
        /// Total number of objects created since pool initialization
    /// </summary>
    public long TotalCreated { get; set; }

    /// <summary>
        /// Total number of objects returned to the pool
    /// </summary>
    public long TotalReleased { get; set; }

    /// <summary>
        /// Total number of cache misses (times when new objects had to be created)
    /// </summary>
    public long TotalMissed { get; set; }
    
    /// <summary>
    /// Total number of objects leased (borrowed) since pool initialization
    /// </summary>
    public long TotalAcquired { get; set; }

    /// <summary>
    /// Total number of objects destroyed instead of being parked (returns rejected past
    /// <c>MaxIdle</c>, failed validation, <c>Clear</c>/<c>Evict</c>/<c>Dispose</c> sweeps);
    /// reported by the unbounded pool (N1), 0 on the bounded engine.
    /// </summary>
    public long TotalDestroyed { get; set; }

    /// <summary>
        /// Number of available slots in the pool
    /// </summary>
    public int AvailableSlots { get; set; }

    /// <summary>
        /// Minimum capacity of the object pool
    /// </summary>
    public int MinSize { get; set; }

    /// <summary>
        /// Current capacity of the object pool
    /// </summary>
    public int CurrentSize { get; set; }

    /// <summary>
        /// Number of detected object leaks
    /// </summary>
    public long LeakDetectedCount { get; set; }

    /// <summary>
        /// When EnableLeakDetection=false, TakeSnapshot counts objects by the same LeakDetectionThreshold
    /// (borrowed past the threshold without being returned); it only counts, does not capture evidence or reclaim.
    /// </summary>
    public long LeakSuspectedCount { get; set; }

    /// <summary>
    /// Whether allocation tracking is enabled.
    /// </summary>
    public bool AllocationTrackingEnabled { get; set; }

    /// <summary>
    /// Cumulative bytes allocated on the acquire path (synchronized only from <c>Acquire</c>; 0 when allocation tracking is disabled).
    /// </summary>
    public long AcquireAllocatedBytes { get; set; }

    /// <summary>
    /// Cumulative bytes allocated on the release path (synchronized only from <c>Release</c>;
    /// 0 when allocation tracking is disabled).
    /// </summary>
    public long ReleaseAllocatedBytes { get; set; }

    /// <summary>
    /// Number of allocation samples taken on the acquire path.
    /// </summary>
    public long AcquireAllocationSamples { get; set; }

    /// <summary>
    /// Number of allocation samples taken on the release path.
    /// </summary>
    public long ReleaseAllocationSamples { get; set; }

    /// <summary>
    /// Average bytes allocated per acquire (acquire path).
    /// </summary>
    public double AverageAcquireAllocatedBytes
        => AcquireAllocationSamples > 0 ? (double)AcquireAllocatedBytes / AcquireAllocationSamples : 0;

    /// <summary>
    /// Average bytes allocated per release (release path).
    /// </summary>
    public double AverageReleaseAllocatedBytes
        => ReleaseAllocationSamples > 0 ? (double)ReleaseAllocatedBytes / ReleaseAllocationSamples : 0;

    /// <summary>
    /// The average wait time in milliseconds.
    /// </summary>
    public double AverageWaitTimeMs => WaitTimeCount > 0 ? (double)WaitTimeSum / WaitTimeCount : 0;

    /// <summary>
        /// Average lease time in milliseconds
    /// </summary>
    public double AverageLeaseTimeMs => LeaseTimeCount > 0 ? (double)LeaseTimeSum / LeaseTimeCount : 0;

    /// <summary>
        /// Maximum wait time in milliseconds
    /// </summary>
    public double MaxWaitTimeMs { get; set; }

    /// <summary>
        /// Maximum lease time in milliseconds
    /// </summary>
    public double MaxLeaseTimeMs { get; set; }

    /// <summary>
        /// Minimum wait time in milliseconds
    /// </summary>
    public double MinWaitTimeMs { get; set; } = double.MaxValue;

    /// <summary>
        /// Minimum lease time in milliseconds
    /// </summary>
    public double MinLeaseTimeMs { get; set; } = double.MaxValue;

    /// <summary>
        /// Number of wait time records
    /// </summary>
    public long WaitTimeCount { get; set; }

    /// <summary>
    /// The number of lease-time records.
    /// </summary>
    public long LeaseTimeCount { get; set; }

    internal long WaitTimeSum { get; set; }
    internal long LeaseTimeSum { get; set; }

    public override string ToString()
    {
        // Handle the sentinel value of double.MaxValue used for "minimum" before any sample.
        var actualMinWaitTime = MinWaitTimeMs == double.MaxValue ? 0 : MinWaitTimeMs;
        var actualMinLeaseTime = MinLeaseTimeMs == double.MaxValue ? 0 : MinLeaseTimeMs;

        // Build a structured, category-grouped string for readability.
        var statsString = $@"
=== Hayate Object Pool Statistics ===
[Basic Capacity]
  MinSize: {MinSize}
  CurrentSize: {CurrentSize}
  PooledCount: {PooledCount}
  AvailableSlots: {AvailableSlots}
[Object Lifecycle]
  TotalCreated: {TotalCreated}
  TotalReleased: {TotalReleased}
  TotalMissed: {TotalMissed}
  LeakDetectedCount: {LeakDetectedCount}
  LeakSuspectedCount: {LeakSuspectedCount}
  AllocationTrackingEnabled: {AllocationTrackingEnabled}
  AverageAcquireAllocatedBytes: {AverageAcquireAllocatedBytes:F1}
  AverageReleaseAllocatedBytes: {AverageReleaseAllocatedBytes:F1}
  AcquireAllocationSamples: {AcquireAllocationSamples}
  ReleaseAllocationSamples: {ReleaseAllocationSamples}
[Wait Time (ms)]
  AverageWaitTime: {AverageWaitTimeMs:F2}
  MaxWaitTime: {MaxWaitTimeMs:F2}
  MinWaitTime: {actualMinWaitTime:F2}
  WaitTimeCount: {WaitTimeCount}
[Lease Time (ms)]
  AverageLeaseTime: {AverageLeaseTimeMs:F2}
  MaxLeaseTime: {MaxLeaseTimeMs:F2}
  MinLeaseTime: {actualMinLeaseTime:F2}
  LeaseTimeCount: {LeaseTimeCount}
======================================";

        // Normalize newlines to the platform-native sequence (works on both Windows and Linux).
        return statsString.Replace("\n", Environment.NewLine);
    }
}