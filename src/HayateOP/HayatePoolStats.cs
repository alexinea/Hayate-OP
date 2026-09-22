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
    /// Cumulative number of borrowed objects reclaimed as abandoned (K2): objects borrowed past
    /// <see cref="HayatePoolOptions.RemoveAbandonedTimeout"/> that the opt-in recovery
    /// (<see cref="HayatePoolOptions.RemoveAbandonedOnBorrow"/> /
    /// <see cref="HayatePoolOptions.RemoveAbandonedOnMaintenance"/>) destroyed. Always 0 when recovery
    /// is off — the default remains forensics-only. The CHOPIN <c>DestroyedByAbandonedCount</c> analog.
    /// </summary>
    public long AbandonedRemovedCount { get; set; }

    /// <summary>
    /// Cumulative number of objects destroyed on the borrow path for having outlived
    /// <see cref="HayatePoolOptions.MaxLifeTime"/> (A3a). Always 0 unless
    /// <see cref="HayatePoolOptions.EnableLifetimeRotationOnBorrow"/> is on — the default keeps
    /// <see cref="HayatePoolOptions.MaxLifeTime"/> a limit on idle objects only.
    /// </summary>
    public long LifetimeRotatedCount { get; set; }

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

    /// <summary>
    /// Whether the operational members below are being collected. It mirrors
    /// <see cref="AllocationTrackingEnabled"/>: the engine writes <see cref="PeakActiveObjects"/>,
    /// <see cref="StartedAt"/> and <see cref="LastActivityTime"/> only while
    /// <see cref="HayatePoolOptions.EnableMetrics"/> is on, and the three ratios are derived from
    /// counters that the same switch gates. When it is <c>false</c> every member below reads 0,
    /// <c>null</c> or <c>default</c> — the same "the gate is closed" reading the cumulative counters
    /// already give. The flag exists so that reading cannot be mistaken for a measurement: without it
    /// <see cref="ReuseEfficiency"/> would report a perfect 1.0 on the default configuration, where
    /// <see cref="TotalAcquired"/> keeps counting but <see cref="TotalMissed"/> does not.
    /// </summary>
    public bool MetricsEnabled { get; set; }

    /// <summary>
    /// The highest number of objects that were out on loan at the same time, as observed when the
    /// statistics were read. <see cref="IHayateObjectPool.GetStats"/> and <c>TakeSnapshot</c> are the
    /// only places the engine derives the borrowed count: the shard deliberately keeps no paired borrow
    /// counter (see <c>HayateObjectPool.Shard.cs</c> — a rejected return destroys the object without going
    /// through the shard's add path, so an increment and a decrement cannot be paired one to one), and
    /// walking the shards on the borrow path would put a per-shard lock and a registry probe on the path
    /// <see cref="HayatePoolOptions.EnableMetrics"/> exists to keep cheap. The value is therefore sampled
    /// at read time: it never decreases, but a burst that starts and ends between two reads is only seen
    /// if it is still in flight when one of them runs. 0 unless <see cref="MetricsEnabled"/>.
    /// </summary>
    public int PeakActiveObjects { get; set; }

    /// <summary>
    /// When the pool was constructed, in UTC. <see cref="DateTimeOffset.MinValue"/> unless
    /// <see cref="MetricsEnabled"/> — capturing it is what makes <see cref="UptimeSeconds"/> and
    /// <see cref="AcquiresPerSecond"/> meaningful.
    /// </summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>
    /// Seconds elapsed since <see cref="StartedAt"/>, or 0 when the pool is not tracking. Computed on
    /// read rather than stored, so a caller holding the object sees the pool's real age; the subtraction
    /// is clamped at 0 so a wall-clock adjustment backwards cannot report a negative age.
    /// </summary>
    public double UptimeSeconds
        => StartedAt == default ? 0 : Math.Max(0, (DateTimeOffset.UtcNow - StartedAt).TotalSeconds);

    /// <summary>
    /// When a borrow or a return last completed on this pool, in UTC, or <c>null</c> when nothing has
    /// happened yet or the pool is not tracking. Null rather than a sentinel timestamp, so "never active"
    /// cannot be read as "active in year 1".
    /// </summary>
    public DateTimeOffset? LastActivityTime { get; set; }

    /// <summary>
    /// The share of borrows that were served from the pool instead of having to create an object:
    /// <c>(TotalAcquired - TotalMissed) / TotalAcquired</c>. Clamped into [0, 1] because
    /// <see cref="TotalMissed"/> also counts a borrow the reject policy refused, and such a borrow never
    /// becomes a <see cref="TotalAcquired"/> — the difference can therefore go negative on a pool that
    /// rejects under load. 0 when metrics are off or nothing has been borrowed.
    /// </summary>
    public double ReuseEfficiency
        => MetricsEnabled && TotalAcquired > 0
            ? Clamp01((double)(TotalAcquired - TotalMissed) / TotalAcquired)
            : 0;

    /// <summary>
    /// Objects created per borrow: <c>TotalCreated / TotalAcquired</c>, i.e. how much the pool is still
    /// growing. Counts every creation, including pre-warm and auto-scaling, not only the ones a borrow
    /// asked for (those are <see cref="TotalMissed"/>). 0 when metrics are off or nothing has been
    /// borrowed.
    /// </summary>
    public double CreatesPerAcquire
        => MetricsEnabled && TotalAcquired > 0 ? (double)TotalCreated / TotalAcquired : 0;

    /// <summary>
    /// Borrows per second over <see cref="UptimeSeconds"/>. 0 when metrics are off, nothing has been
    /// borrowed, or the pool is younger than the wall clock's resolution.
    /// </summary>
    public double AcquiresPerSecond
        => MetricsEnabled && UptimeSeconds > 0 ? TotalAcquired / UptimeSeconds : 0;

    /// <summary>Clamps a ratio into [0, 1]; the counters it is derived from are cumulative and can disagree.</summary>
    private static double Clamp01(double value) => value < 0 ? 0 : value > 1 ? 1 : value;

    public override string ToString()
    {
        // Handle the sentinel value of double.MaxValue used for "minimum" before any sample.
        var actualMinWaitTime = MinWaitTimeMs == double.MaxValue ? 0 : MinWaitTimeMs;
        var actualMinLeaseTime = MinLeaseTimeMs == double.MaxValue ? 0 : MinLeaseTimeMs;

        // Same idea for the operational members: an unset timestamp prints as "-" rather than as year 1.
        var actualStartedAt = StartedAt == default ? "-" : StartedAt.ToString("O");
        var actualLastActivity = LastActivityTime?.ToString("O") ?? "-";

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
  AbandonedRemovedCount: {AbandonedRemovedCount}
  LifetimeRotatedCount: {LifetimeRotatedCount}
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
[Operational (gated by EnableMetrics)]
  MetricsEnabled: {MetricsEnabled}
  PeakActiveObjects: {PeakActiveObjects}
  StartedAt: {actualStartedAt}
  UptimeSeconds: {UptimeSeconds:F1}
  LastActivityTime: {actualLastActivity}
  ReuseEfficiency: {ReuseEfficiency:P1}
  CreatesPerAcquire: {CreatesPerAcquire:F4}
  AcquiresPerSecond: {AcquiresPerSecond:F2}
======================================";

        // Normalize newlines to the platform-native sequence (works on both Windows and Linux).
        return statsString.Replace("\n", Environment.NewLine);
    }
}