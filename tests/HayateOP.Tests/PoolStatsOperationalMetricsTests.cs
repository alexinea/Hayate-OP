using System;
using System.Threading;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// G-1: the ratio-class operational members of <see cref="HayatePoolStats"/> — reuse efficiency,
/// creation rate, throughput, peak concurrency, uptime and last activity. The file has two halves on
/// purpose. The derivations are exercised directly on the value object, because they are pure
/// functions of the counters and a hand-built stats object pins the arithmetic without a pool in the
/// way. The engine is then held to the wiring the plan requires: the members stay at their
/// "gate closed" readings while <c>EnableMetrics</c> is off, and the peak is a high-water mark that
/// only the two read paths deriving the borrowed count can raise.
/// Acceptance (plan 1.7): the pre-existing members are untouched, the four average-class derivations
/// are untouched, a zero denominator returns 0 rather than NaN or Infinity, and the new tracking
/// costs nothing while metrics are off.
/// </summary>
public class PoolStatsOperationalMetricsTests
{
    private sealed class TestObject { }

    // ── Derivations ────────────────────────────────────────────────────────────

    [Fact(Timeout = 30_000)]
    public void Ratios_ShouldReadZeroWhileTheMetricsGateIsClosed()
    {
        // The counters are present and would divide cleanly, so a non-zero ratio here would mean the
        // gate is decorative. ReuseEfficiency is the discriminating one: (8 - 0) / 8 is a perfect 1.0,
        // which is exactly the lie the flag exists to prevent -- TotalAcquired keeps counting with
        // metrics off while TotalMissed does not, so the two sides of the subtraction disagree.
        var stats = new HayatePoolStats
        {
            MetricsEnabled = false,
            TotalAcquired = 8,
            TotalCreated = 4,
            TotalMissed = 0,
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-10)
        };

        Assert.Equal(0, stats.ReuseEfficiency);
        Assert.Equal(0, stats.CreatesPerAcquire);
        Assert.Equal(0, stats.AcquiresPerSecond);
    }

    [Fact(Timeout = 30_000)]
    public void ReuseEfficiency_ShouldBeTheServedShareOfBorrows()
    {
        Assert.Equal(1.0, new HayatePoolStats { MetricsEnabled = true, TotalAcquired = 8 }.ReuseEfficiency);
        Assert.Equal(0.7, new HayatePoolStats { MetricsEnabled = true, TotalAcquired = 10, TotalMissed = 3 }.ReuseEfficiency);
        Assert.Equal(0.0, new HayatePoolStats { MetricsEnabled = true, TotalAcquired = 4, TotalMissed = 4 }.ReuseEfficiency);
    }

    [Fact(Timeout = 30_000)]
    public void ReuseEfficiency_ShouldClampAtZero_WhenMissesOutnumberBorrows()
    {
        // TotalMissed also counts a borrow the reject policy refused, and a refused borrow never
        // becomes a TotalAcquired, so the difference can go negative on a pool that rejects under load.
        var stats = new HayatePoolStats { MetricsEnabled = true, TotalAcquired = 1, TotalMissed = 5 };

        Assert.Equal(0.0, stats.ReuseEfficiency);
    }

    [Fact(Timeout = 30_000)]
    public void Ratios_ShouldBeZeroAndFinite_WhenTheDenominatorIsZero()
    {
        // Plan acceptance 3: a zero denominator returns 0, never NaN or Infinity. Unguarded, the three
        // expressions here would be 0/0, 7/0 and 0/0.
        var stats = new HayatePoolStats
        {
            MetricsEnabled = true,
            TotalAcquired = 0,
            TotalCreated = 7,
            TotalMissed = 0,
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-30)
        };

        Assert.Equal(0, stats.ReuseEfficiency);
        Assert.Equal(0, stats.CreatesPerAcquire);
        Assert.Equal(0, stats.AcquiresPerSecond);

        Assert.False(double.IsNaN(stats.ReuseEfficiency));
        Assert.False(double.IsNaN(stats.CreatesPerAcquire));
        Assert.False(double.IsInfinity(stats.AcquiresPerSecond));
    }

    [Fact(Timeout = 30_000)]
    public void CreatesPerAcquire_ShouldCountEveryCreation_NotOnlyTheMissedOnes()
    {
        // Pre-warm and auto-scaling create objects no borrow asked for, so the ratio is deliberately
        // TotalCreated / TotalAcquired rather than TotalMissed / TotalAcquired. Here four pre-warmed
        // objects served eight borrows with no miss at all.
        var stats = new HayatePoolStats
        {
            MetricsEnabled = true,
            TotalAcquired = 8,
            TotalCreated = 4,
            TotalMissed = 0
        };

        Assert.Equal(0.5, stats.CreatesPerAcquire);
    }

    [Fact(Timeout = 30_000)]
    public void UptimeSeconds_ShouldBeZeroWithoutAnOrigin_AndClampABackwardsClock()
    {
        Assert.Equal(0, new HayatePoolStats().UptimeSeconds);

        var elapsed = new HayatePoolStats { StartedAt = DateTimeOffset.UtcNow.AddSeconds(-2) }.UptimeSeconds;
        Assert.InRange(elapsed, 1.5, 30.0);

        // A wall-clock adjustment backwards must not turn the age negative.
        Assert.Equal(0, new HayatePoolStats { StartedAt = DateTimeOffset.UtcNow.AddMinutes(5) }.UptimeSeconds);
    }

    [Fact(Timeout = 30_000)]
    public void AcquiresPerSecond_ShouldDivideByUptime()
    {
        var stats = new HayatePoolStats
        {
            MetricsEnabled = true,
            TotalAcquired = 100,
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-2)
        };

        // 100 borrows over ~2 s. The bounds leave room for scheduling jitter, not for a wrong divisor.
        Assert.InRange(stats.AcquiresPerSecond, 20.0, 500.0);
    }

    [Fact(Timeout = 30_000)]
    public void ToString_ShouldRenderTheOperationalSection_WithSentinelsAsDashes()
    {
        var stats = new HayatePoolStats { MetricsEnabled = true, TotalAcquired = 4, PeakActiveObjects = 2 };

        var text = stats.ToString();

        Assert.Contains("PeakActiveObjects: 2", text);
        Assert.Contains("MetricsEnabled: True", text);
        // An unset timestamp prints as "-" rather than as year 1, the same treatment MinWaitTimeMs gets.
        Assert.Contains("StartedAt: -", text);
        Assert.Contains("LastActivityTime: -", text);
    }

    // ── Engine wiring ──────────────────────────────────────────────────────────

    /// <summary>
    /// Plan acceptance 1: the pre-existing fields keep their exact values. This is the golden reading
    /// for a configuration with every source of variation turned off — one shard, fixed sizing, no
    /// auto-scaling, eviction, validation or leak detection — so any drift in the twenty-four settable
    /// fields or the four average-class derivations shows up as a failing number rather than as a
    /// reviewer noticing.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public void ExistingFields_ShouldKeepTheirExactValues()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("g1-golden")
            .WithMinSize(4)
            .WithMaxSize(16)
            .WithShardCount(1)
            .WithEnableAutoScaling(false)
            .WithEnableEviction(false)
            .WithEnableValidation(false)
            .WithEnableLeakDetection(false)
            .WithEnableAllocationTracking(false)
            .WithEnableMetrics(true)
            .Build();

        var item = pool.Acquire();
        pool.Release(item);

        var stats = pool.GetStats();

        // Capacity.
        Assert.Equal(4, stats.MinSize);
        Assert.Equal(4, stats.CurrentSize);
        Assert.Equal(4, stats.PooledCount);
        Assert.Equal(4, stats.AvailableSlots);

        // Lifecycle.
        Assert.Equal(4, stats.TotalCreated);
        Assert.Equal(1, stats.TotalReleased);
        Assert.Equal(0, stats.TotalMissed);
        Assert.Equal(1, stats.TotalAcquired);
        Assert.Equal(0, stats.TotalDestroyed);
        Assert.Equal(0, stats.LeakDetectedCount);
        Assert.Equal(0, stats.LeakSuspectedCount);
        Assert.Equal(0, stats.AbandonedRemovedCount);
        Assert.Equal(0, stats.LifetimeRotatedCount);

        // Allocation tracking: off, so every member reports the documented zero.
        Assert.False(stats.AllocationTrackingEnabled);
        Assert.Equal(0, stats.AcquireAllocatedBytes);
        Assert.Equal(0, stats.ReleaseAllocatedBytes);
        Assert.Equal(0, stats.AcquireAllocationSamples);
        Assert.Equal(0, stats.ReleaseAllocationSamples);
        Assert.Equal(0, stats.AverageAcquireAllocatedBytes);
        Assert.Equal(0, stats.AverageReleaseAllocatedBytes);

        // Timing: the counts are exact, the values are whatever the clock said.
        Assert.Equal(1, stats.WaitTimeCount);
        Assert.Equal(1, stats.LeaseTimeCount);
        Assert.True(stats.MaxWaitTimeMs >= 0);
        Assert.True(stats.MinWaitTimeMs >= 0);
        Assert.True(stats.AverageWaitTimeMs >= 0);
        Assert.True(stats.MaxLeaseTimeMs >= 0);
        Assert.True(stats.MinLeaseTimeMs >= 0);
        Assert.True(stats.AverageLeaseTimeMs >= 0);
    }

    /// <summary>
    /// Plan acceptance 2, the "zero borrows" half: every new member has a determined value before the
    /// pool has been used, and none of them is a NaN or an Infinity produced by an empty denominator.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public void ZeroBorrows_ShouldReportDeterminedZeros()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("g1-zero-borrows")
            .WithMinSize(4)
            .WithMaxSize(16)
            .WithShardCount(1)
            .WithEnableMetrics(true)
            .Build();

        var stats = pool.GetStats();

        Assert.Equal(0, stats.ReuseEfficiency);
        Assert.Equal(0, stats.CreatesPerAcquire);
        Assert.Equal(0, stats.AcquiresPerSecond);
        Assert.Equal(0, stats.PeakActiveObjects);
        Assert.Equal(0, stats.TotalAcquired);
        Assert.Null(stats.LastActivityTime);
        // Pre-warm ran, so the origin exists even though nothing has been borrowed: the pool is
        // measuring, it just has nothing to divide yet.
        Assert.NotEqual(default(DateTimeOffset), stats.StartedAt);
        Assert.True(stats.UptimeSeconds >= 0);

        Assert.False(double.IsNaN(stats.ReuseEfficiency));
        Assert.False(double.IsNaN(stats.CreatesPerAcquire));
        Assert.False(double.IsNaN(stats.AcquiresPerSecond));
    }

    [Fact(Timeout = 30_000)]
    public void MetricsOff_ShouldLeaveEveryOperationalMemberAtItsGateClosedReading()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("g1-off")
            .WithMinSize(4)
            .WithMaxSize(16)
            .WithShardCount(1)
            .WithEnableMetrics(false)
            .Build();

        // Two objects stay out on loan across the read, so an ungated peak would read 2 here.
        var a = pool.Acquire();
        var b = pool.Acquire();

        var stats = pool.GetStats();

        // The pre-existing metrics-independent field is the anchor: the burst really happened.
        Assert.Equal(2, stats.TotalAcquired);

        Assert.False(stats.MetricsEnabled);
        Assert.Equal(0, stats.PeakActiveObjects);
        Assert.Equal(default(DateTimeOffset), stats.StartedAt);
        Assert.Equal(0, stats.UptimeSeconds);
        Assert.Null(stats.LastActivityTime);
        Assert.Equal(0, stats.ReuseEfficiency);
        Assert.Equal(0, stats.CreatesPerAcquire);
        Assert.Equal(0, stats.AcquiresPerSecond);

        pool.Release(a);
        pool.Release(b);

        // Read once more after the returns. The return path is where the engine writes the activity
        // stamp on every branch, so this second read is what holds the stamp's own gate to account --
        // the read above cannot see it, because with metrics off the borrow path never reaches the
        // stamp at all.
        Assert.Null(pool.GetStats().LastActivityTime);
    }

    [Fact(Timeout = 30_000)]
    public void MetricsOn_ShouldRecordTheSameBurst()
    {
        // The control for the test above: the identical burst with the gate open. Without this pair the
        // zeros there could just as well mean the peak is never recorded at all.
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("g1-on")
            .WithMinSize(4)
            .WithMaxSize(16)
            .WithShardCount(1)
            .WithEnableMetrics(true)
            .Build();

        var a = pool.Acquire();
        var b = pool.Acquire();

        var stats = pool.GetStats();

        Assert.True(stats.MetricsEnabled);
        Assert.Equal(2, stats.PeakActiveObjects);
        Assert.NotEqual(default(DateTimeOffset), stats.StartedAt);
        Assert.True(stats.UptimeSeconds >= 0);
        Assert.NotEqual(default(DateTimeOffset), stats.LastActivityTime.GetValueOrDefault());
        // Both borrows were served from the four pre-warmed objects, so nothing was missed.
        Assert.Equal(1.0, stats.ReuseEfficiency);

        pool.Release(a);
        pool.Release(b);
    }

    [Fact(Timeout = 30_000)]
    public void PeakActiveObjects_ShouldBeAHighWaterMark()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("g1-peak")
            .WithMinSize(4)
            .WithMaxSize(16)
            .WithShardCount(1)
            .WithEnableMetrics(true)
            .Build();

        var held = new[] { pool.Acquire(), pool.Acquire(), pool.Acquire() };

        Assert.Equal(3, pool.GetStats().PeakActiveObjects);

        foreach (var item in held) pool.Release(item);

        // A high-water mark: draining the pool does not lower it.
        Assert.Equal(3, pool.GetStats().PeakActiveObjects);
    }

    [Fact(Timeout = 30_000)]
    public void TakeSnapshot_ShouldAlsoRaiseThePeak()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("g1-snapshot-peak")
            .WithMinSize(4)
            .WithMaxSize(16)
            .WithShardCount(1)
            .WithEnableMetrics(true)
            .Build();

        var held = new[] { pool.Acquire(), pool.Acquire(), pool.Acquire() };

        // The only read that happens while the objects are out on loan. The GetStats below runs after
        // they are all back, so a peak of 3 can only have come from here.
        pool.TakeSnapshot();

        foreach (var item in held) pool.Release(item);

        Assert.Equal(3, pool.GetStats().PeakActiveObjects);
    }

    [Fact(Timeout = 30_000)]
    public void LastActivityTime_ShouldTrackBorrowsAndReturns_ButNotPreWarm()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("g1-activity")
            .WithMinSize(1)
            .WithMaxSize(4)
            .WithShardCount(1)
            .WithEnableMetrics(true)
            .Build();

        // Pre-warm created an object, but nothing has been borrowed or returned yet: the stamp is
        // absent rather than set to the construction time.
        var fresh = pool.GetStats();
        Assert.Null(fresh.LastActivityTime);
        Assert.NotEqual(default(DateTimeOffset), fresh.StartedAt);

        var item = pool.Acquire();
        var borrowedAt = pool.GetStats().LastActivityTime.GetValueOrDefault();

        pool.Release(item);
        var returnedAt = pool.GetStats().LastActivityTime.GetValueOrDefault();

        Assert.NotEqual(default(DateTimeOffset), borrowedAt);
        Assert.NotEqual(default(DateTimeOffset), returnedAt);
        Assert.True(returnedAt >= borrowedAt);
        Assert.True(borrowedAt >= fresh.StartedAt);
        Assert.True(returnedAt <= DateTimeOffset.UtcNow.AddSeconds(1));
    }

    [Fact(Timeout = 30_000)]
    public void Ratios_ShouldBeLiveOnAPoolThatOnlyReuses()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("g1-ratios")
            .WithMinSize(4)
            .WithMaxSize(16)
            .WithShardCount(1)
            .WithEnableMetrics(true)
            .Build();

        for (var i = 0; i < 8; i++)
        {
            var item = pool.Acquire();
            pool.Release(item);
        }

        // UptimeSeconds reads 0 when this pool was constructed in the same clock tick as this read, and
        // AcquiresPerSecond is defined as 0 in that case, so the live value only becomes observable once
        // the tick has advanced. DateTime.UtcNow advances on the system timer (~15.6 ms on Windows, which
        // the .NET Framework leg of the matrix runs on) while this test finishes in a few milliseconds,
        // so without the wait the throughput assertion is a coin toss.
        Thread.Sleep(25);
        var stats = pool.GetStats();

        // Four pre-warmed objects served eight borrows and nothing was missed, so every borrow was a
        // reuse and the pool created half an object per borrow.
        Assert.Equal(8, stats.TotalAcquired);
        Assert.Equal(4, stats.TotalCreated);
        Assert.Equal(0, stats.TotalMissed);
        Assert.Equal(1.0, stats.ReuseEfficiency);
        Assert.Equal(0.5, stats.CreatesPerAcquire);
        Assert.True(stats.AcquiresPerSecond > 0,
            $"AcquiresPerSecond={stats.AcquiresPerSecond} UptimeSeconds={stats.UptimeSeconds} StartedAt={stats.StartedAt:O} MetricsEnabled={stats.MetricsEnabled} TotalAcquired={stats.TotalAcquired}");
        Assert.True(stats.UptimeSeconds >= 0,
            $"UptimeSeconds={stats.UptimeSeconds} StartedAt={stats.StartedAt:O} MetricsEnabled={stats.MetricsEnabled}");
    }

    [Fact(Timeout = 30_000)]
    public void ARejectedBorrow_ShouldNotDriveReuseEfficiencyNegative()
    {
        // The engine half of the clamp: an Abort rejection records a miss without an acquire, so the
        // denominator is zero and the difference is negative. Neither may leak into the ratio.
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("g1-abort")
            .WithMinSize(0)
            .WithMaxSize(4)
            .WithRejectPolicy(HayatePoolRejectPolicy.Abort)
            .WithEnableMetrics(true)
            .Build();

        Assert.Throws<InvalidOperationException>(() => { pool.Acquire(); });

        var stats = pool.GetStats();
        Assert.Equal(1, stats.TotalMissed);
        Assert.Equal(0, stats.TotalAcquired);
        Assert.Equal(0, stats.ReuseEfficiency);
        Assert.False(double.IsNaN(stats.ReuseEfficiency));
    }

    [Fact(Timeout = 30_000)]
    public void LeanProfile_ShouldReportTheOperationalMembersAsUnmeasured()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithLean()
            .WithMinSize(4)
            .WithMaxSize(16)
            .Build();

        var item = pool.Acquire();
        pool.Release(item);

        var stats = pool.GetStats();

        // The lean profile closes the master diagnostic switch, which closes metrics with it. Every
        // operational member has to say "not measured" rather than report a zero that reads like one.
        Assert.False(stats.MetricsEnabled);
        Assert.Equal(0, stats.PeakActiveObjects);
        Assert.Equal(default(DateTimeOffset), stats.StartedAt);
        Assert.Equal(0, stats.UptimeSeconds);
        Assert.Null(stats.LastActivityTime);
        Assert.Equal(0, stats.ReuseEfficiency);
        Assert.Equal(0, stats.CreatesPerAcquire);
        Assert.Equal(0, stats.AcquiresPerSecond);
    }

    [Fact(Timeout = 30_000)]
    public void UnboundedPool_ShouldReportItsOwnOperationalReading()
    {
        using var pool = new HayateUnboundedPool<TestObject>(maxIdle: 4);

        var fresh = pool.GetStats();

        // This model has no switches to gate the counters behind, so the ratios are live from the
        // start -- but nothing has been borrowed yet, so they are still 0 and the stamp is absent.
        Assert.True(fresh.MetricsEnabled);
        Assert.NotEqual(default(DateTimeOffset), fresh.StartedAt);
        Assert.Null(fresh.LastActivityTime);
        Assert.Equal(0, fresh.ReuseEfficiency);

        var first = pool.Acquire();

        // The ownership model tracks no borrowed object, so it does not fabricate a peak it cannot know.
        Assert.Equal(0, pool.GetStats().PeakActiveObjects);

        pool.Release(first);

        var second = pool.Acquire();
        pool.Release(second);

        var stats = pool.GetStats();
        // The first borrow created (and missed); the second reused the returned object.
        Assert.Equal(2, stats.TotalAcquired);
        Assert.Equal(1, stats.TotalCreated);
        Assert.Equal(1, stats.TotalMissed);
        Assert.Equal(0.5, stats.ReuseEfficiency);
        Assert.Equal(0.5, stats.CreatesPerAcquire);
        Assert.NotEqual(default(DateTimeOffset), stats.LastActivityTime.GetValueOrDefault());
    }
}
