using System;
using System.Threading;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// Counter gating contract. `EnableMetrics` gates three of the four cumulative traffic counters and the
/// timing statistics, but <c>TotalAcquired</c> is deliberately written unconditionally because it is the
/// borrow-count contract that callers and tests verify with metrics off. The leak-detection and
/// allocation-tracking counters follow their own switches rather than the metrics switch.
/// Acceptance: this split is asserted rather than described, so it cannot drift silently.
/// </summary>
public class MetricsGatingTests
{
    private sealed class TestObject { }

    [Fact(Timeout = 30_000)]
    public void MetricsOff_ShouldStillCountBorrowsButNotTheGatedCounters()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("gating-off")
            .WithMinSize(4)
            .WithMaxSize(16)
            .WithEnableMetrics(false)
            .Build();

        var a = pool.Acquire();
        var b = pool.Acquire();
        var c = pool.Acquire();
        pool.Release(a);
        pool.Release(b);
        pool.Release(c);

        var stats = pool.GetStats();

        // The borrow-count contract is metrics-independent.
        Assert.Equal(3, stats.TotalAcquired);

        // The other three cumulative counters are gated by metrics, so they never move.
        Assert.Equal(0, stats.TotalCreated);
        Assert.Equal(0, stats.TotalReleased);
        Assert.Equal(0, stats.TotalMissed);
        Assert.Equal(0, stats.WaitTimeCount);
        Assert.Equal(0, stats.LeaseTimeCount);
    }

    [Fact(Timeout = 30_000)]
    public void MetricsOn_ShouldAdvanceTheGatedCounters()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("gating-on")
            .WithMinSize(4)
            .WithMaxSize(16)
            .WithEnableMetrics(true)
            .Build();

        var a = pool.Acquire();
        var b = pool.Acquire();
        var c = pool.Acquire();
        pool.Release(a);
        pool.Release(b);
        pool.Release(c);

        var stats = pool.GetStats();

        Assert.Equal(3, stats.TotalAcquired);
        Assert.Equal(4, stats.TotalCreated);   // the four pre-warmed objects
        Assert.Equal(3, stats.TotalReleased);
        Assert.Equal(0, stats.TotalMissed);
        Assert.Equal(3, stats.WaitTimeCount);
        Assert.Equal(3, stats.LeaseTimeCount);
    }

    [Fact(Timeout = 30_000)]
    public void TotalMissed_ShouldFollowTheMetricsGate()
    {
        // An empty pool under the Abort policy rejects immediately, which is the cheapest deterministic
        // way to produce a miss. Cold boot does not run on this branch, so nothing is ever created.
        using var metricsOff = new HayatePoolBuilder<TestObject>()
            .WithPoolName("gating-miss-off")
            .WithMinSize(0)
            .WithMaxSize(4)
            .WithRejectPolicy(HayatePoolRejectPolicy.Abort)
            .WithEnableMetrics(false)
            .Build();

        Assert.Throws<InvalidOperationException>(() => { metricsOff.Acquire(); });
        Assert.Equal(0, metricsOff.GetStats().TotalMissed);

        using var metricsOn = new HayatePoolBuilder<TestObject>()
            .WithPoolName("gating-miss-on")
            .WithMinSize(0)
            .WithMaxSize(4)
            .WithRejectPolicy(HayatePoolRejectPolicy.Abort)
            .WithEnableMetrics(true)
            .Build();

        Assert.Throws<InvalidOperationException>(() => { metricsOn.Acquire(); });
        Assert.Equal(1, metricsOn.GetStats().TotalMissed);
    }

    [Fact(Timeout = 30_000)]
    public void LeakCounters_ShouldFollowTheLeakDetectionGate_NotMetrics()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("gating-leak")
            .WithMinSize(1)
            .WithMaxSize(4)
            // Leak detection off, so the snapshot reports a *suspected* leak instead of a detected one --
            // and metrics off, which must not suppress either counter.
            .WithEnableLeakDetection(false)
            .WithEnableMetrics(false)
            .WithEnableEviction(false)
            .WithEnableValidation(false)
            .WithLeakDetectionThreshold(TimeSpan.FromMilliseconds(50))
            .Build();

        var held = pool.Acquire();
        Assert.NotNull(held);
        // Deliberately not released: the object stays borrowed past the threshold. Comfortably above the
        // 50 ms threshold so machine load cannot make the comparison marginal.
        Thread.Sleep(250);

        var snapshot = pool.TakeSnapshot();

        Assert.True(snapshot.LeakSuspectedCount >= 1);
        Assert.Equal(0, snapshot.LeakCount);           // leak detection is off, so nothing is "detected"
        Assert.Equal(0, pool.GetStats().TotalReleased); // metrics are off, so the return counter never moved
    }

    [Fact(Timeout = 30_000)]
    public void AllocationCounters_ShouldFollowTheAllocationTrackingGate_NotMetrics()
    {
        using var trackingOff = new HayatePoolBuilder<TestObject>()
            .WithPoolName("gating-alloc-off")
            .WithMaxSize(8)
            .WithEnableMetrics(false)
            .WithEnableAllocationTracking(false)
            .Build();

        var a = trackingOff.Acquire();
        trackingOff.Release(a);

        var offStats = trackingOff.GetStats();
        Assert.False(offStats.AllocationTrackingEnabled);
        Assert.Equal(0, offStats.AcquireAllocationSamples);
        Assert.Equal(0, offStats.ReleaseAllocationSamples);
        Assert.Equal(0, offStats.AcquireAllocatedBytes);
        Assert.Equal(0, offStats.ReleaseAllocatedBytes);

        using var trackingOn = new HayatePoolBuilder<TestObject>()
            .WithPoolName("gating-alloc-on")
            .WithMaxSize(8)
            .WithEnableMetrics(false)
            .WithEnableAllocationTracking(true)
            .Build();

        var b = trackingOn.Acquire();
        trackingOn.Release(b);

        var onStats = trackingOn.GetStats();
        // Samples are counted on every target framework; the byte delta itself is only available on
        // net6.0+ (net48 and netstandard2.0 report 0), so only the sample counts are asserted.
        Assert.True(onStats.AllocationTrackingEnabled);
        Assert.True(onStats.AcquireAllocationSamples >= 1);
        Assert.True(onStats.ReleaseAllocationSamples >= 1);
    }
}
