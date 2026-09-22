using System;
using System.Collections.Generic;
using System.Linq;
using DotNetCore.HayateOP;

namespace DotNetCore.HayateOP.Specialized;

/// <summary>
/// Aggregates the diagnostics of a specialized pool's base engine and its lazily created capacity
/// tiers into one view, so <see cref="IHayateObjectPool.GetStats"/> and
/// <see cref="IHayateObjectPool.TakeSnapshot"/> keep reporting the whole pool after tiers appear.
/// </summary>
internal static class SpecializedPoolAggregation
{
    /// <summary>
    /// Sums the lifecycle and capacity counters of the base pool and every tier. Wait and lease timing
    /// fields are reported from the base pool only: tier pools run create-on-demand and never block, so
    /// they contribute no meaningful timing, and the engine's timing sums are internal to the core
    /// assembly and cannot be merged from here.
    /// </summary>
    public static HayatePoolStats AggregateStats(IHayateObjectPool basePool, IEnumerable<IHayateObjectPool> tiers)
    {
        var stats = basePool.GetStats();
        foreach (var tier in tiers)
        {
            var t = tier.GetStats();
            stats.PooledCount += t.PooledCount;
            stats.CurrentSize += t.CurrentSize;
            stats.AvailableSlots += t.AvailableSlots;
            stats.TotalCreated += t.TotalCreated;
            stats.TotalReleased += t.TotalReleased;
            stats.TotalMissed += t.TotalMissed;
            stats.TotalAcquired += t.TotalAcquired;
            stats.LeakDetectedCount += t.LeakDetectedCount;
            stats.LeakSuspectedCount += t.LeakSuspectedCount;
            stats.AcquireAllocatedBytes += t.AcquireAllocatedBytes;
            stats.ReleaseAllocatedBytes += t.ReleaseAllocatedBytes;
            stats.AcquireAllocationSamples += t.AcquireAllocationSamples;
            stats.ReleaseAllocationSamples += t.ReleaseAllocationSamples;
            stats.AllocationTrackingEnabled |= t.AllocationTrackingEnabled;
            stats.MaxWaitTimeMs = Math.Max(stats.MaxWaitTimeMs, t.MaxWaitTimeMs);
            stats.MaxLeaseTimeMs = Math.Max(stats.MaxLeaseTimeMs, t.MaxLeaseTimeMs);
            stats.MinWaitTimeMs = Math.Min(stats.MinWaitTimeMs, t.MinWaitTimeMs);
            stats.MinLeaseTimeMs = Math.Min(stats.MinLeaseTimeMs, t.MinLeaseTimeMs);

            // G-1 operational members. The three ratios need no merge — they are computed from the
            // counters above, which are already summed. The rest are merged on the reading that keeps the
            // aggregate honest: the pool's age is its earliest origin, its last activity is the latest of
            // any tier's, and its peak is the highest any tier reached (a tier's peak is never higher than
            // the whole pool's, but the base pool alone can be lower than a tier's if the base was idle).
            stats.MetricsEnabled |= t.MetricsEnabled;
            stats.PeakActiveObjects = Math.Max(stats.PeakActiveObjects, t.PeakActiveObjects);
            if (t.StartedAt != default && (stats.StartedAt == default || t.StartedAt < stats.StartedAt))
            {
                stats.StartedAt = t.StartedAt;
            }

            if (t.LastActivityTime.HasValue &&
                (!stats.LastActivityTime.HasValue || t.LastActivityTime.Value > stats.LastActivityTime.Value))
            {
                stats.LastActivityTime = t.LastActivityTime;
            }
        }

        return stats;
    }

    /// <summary>Combines the base pool's snapshot with every tier's, concatenating the per-object rows.</summary>
    public static HayatePoolSnapshot AggregateSnapshot(IHayateObjectPool basePool, IEnumerable<IHayateObjectPool> tiers)
    {
        var snapshot = basePool.TakeSnapshot();
        var details = new List<HayatePoolObjectDetail>(snapshot.ObjectDetails);
        var timestamp = snapshot.Timestamp;

        foreach (var tier in tiers)
        {
            var t = tier.TakeSnapshot();
            details.AddRange(t.ObjectDetails);
            snapshot.PooledCount += t.PooledCount;
            snapshot.BorrowedCount += t.BorrowedCount;
            snapshot.TotalCreated += t.TotalCreated;
            snapshot.TotalMissed += t.TotalMissed;
            snapshot.TotalAcquired += t.TotalAcquired;
            snapshot.LeakCount += t.LeakCount;
            snapshot.LeakSuspectedCount += t.LeakSuspectedCount;
            snapshot.AcquireAllocatedBytes += t.AcquireAllocatedBytes;
            snapshot.ReleaseAllocatedBytes += t.ReleaseAllocatedBytes;
            snapshot.AllocationTrackingEnabled |= t.AllocationTrackingEnabled;
            if (t.Timestamp > timestamp)
            {
                timestamp = t.Timestamp;
            }

            if (t.LeakTraces.Count > 0)
            {
                snapshot.LeakTraces = snapshot.LeakTraces.Concat(t.LeakTraces).ToArray();
            }
        }

        snapshot.Timestamp = timestamp;
        snapshot.ObjectDetails = details;
        return snapshot;
    }
}
