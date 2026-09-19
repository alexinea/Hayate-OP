using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// Acceptance for the pluggable eviction rule (O7, the counterpart of CHOPIN's
/// <c>EvictionPolicyClassName</c>): the default policy reproduces exactly the rule the background run
/// applied before the extension point existed, a custom policy replaces that rule for the run, and the
/// candidate a policy receives carries the measurements and thresholds it decides on.
/// </summary>
public class EvictionPolicyTests
{
    private class TestObject { }

    /// <summary>Keeps everything: proves a custom rule replaces the built-in one rather than extending it.</summary>
    private sealed class NeverEvictPolicy : IHayateEvictionPolicy<TestObject>
    {
        public bool ShouldEvict(in HayateEvictionCandidate<TestObject> candidate) => false;
    }

    /// <summary>Evicts everything: proves the policy is consulted even when no time-based rule fires.</summary>
    private sealed class AlwaysEvictPolicy : IHayateEvictionPolicy<TestObject>
    {
        public bool ShouldEvict(in HayateEvictionCandidate<TestObject> candidate) => true;
    }

    /// <summary>Records what the run offers, so the candidate contents can be asserted.</summary>
    private sealed class CapturingPolicy : IHayateEvictionPolicy<TestObject>
    {
        private readonly List<HayateEvictionCandidate<TestObject>> _candidates = new();
        private readonly object _gate = new();

        public HayateEvictionCandidate<TestObject>[] Candidates
        {
            get { lock (_gate) return _candidates.ToArray(); }
        }

        public bool ShouldEvict(in HayateEvictionCandidate<TestObject> candidate)
        {
            lock (_gate) _candidates.Add(candidate);
            return false;
        }
    }

    private static readonly TimeSpan MaxLife = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MaxIdle = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SoftIdle = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long a test waits for an effect of the background eviction run before calling it absent.
    /// </summary>
    private static readonly TimeSpan ScanDeadline = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Waits until <paramref name="condition"/> holds, up to <paramref name="timeout"/>.
    /// </summary>
    /// <remarks>
    /// The eviction run fires on its own schedule, so a test that observes one of its effects has to
    /// wait for the effect, not for a fixed duration: a sleep long enough when the class runs alone can
    /// elapse before the first tick when the whole suite is competing for the CPU. Polling keeps the
    /// case fast in the common one and reliable under load, and the deadline turns a genuine
    /// regression into a failure instead of into a hang.
    /// </remarks>
    private static bool WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (condition()) return true;
            Thread.Sleep(25);
        }

        return condition();
    }

    /// <summary>Builds a candidate by hand, the way the run would measure it.</summary>
    private static HayateEvictionCandidate<TestObject> Candidate(
        TimeSpan age, TimeSpan idleTime, int idleCount, int minIdleCount,
        TimeSpan? maxLifeTime = null, TimeSpan? maxIdleTime = null, TimeSpan? softIdleTime = null,
        int leaseCount = 0)
        => new HayateEvictionCandidate<TestObject>(
            new TestObject(), age, idleTime, leaseCount, idleCount, minIdleCount,
            maxLifeTime ?? MaxLife, maxIdleTime ?? MaxIdle, softIdleTime ?? SoftIdle, 0);

    [Fact]
    public void DefaultPolicy_ShouldReproduceTheBuiltInRule()
    {
        var policy = HayateDefaultEvictionPolicy<TestObject>.Instance;

        // Expired: past MaxLifeTime, whatever the idle time and the pool's occupancy are.
        Assert.True(policy.ShouldEvict(Candidate(MaxLife + TimeSpan.FromSeconds(1), TimeSpan.Zero, 16, 0)));
        // Exactly at the lifetime boundary the object is still kept (the rule is strictly ">").
        Assert.False(policy.ShouldEvict(Candidate(MaxLife, TimeSpan.Zero, 16, 0)));

        // Idle too long: past MaxIdleTime even though the lifetime is fine.
        Assert.True(policy.ShouldEvict(Candidate(TimeSpan.FromMinutes(1), MaxIdle + TimeSpan.FromSeconds(1), 16, 0)));

        // Soft idleness with idle objects above the shard's share of MinPoolSize.
        Assert.True(policy.ShouldEvict(Candidate(TimeSpan.FromMinutes(1), SoftIdle + TimeSpan.FromSeconds(1), 4, 2)));

        // Soft idleness at the floor: the pool must not shrink below MinPoolSize.
        Assert.False(policy.ShouldEvict(Candidate(TimeSpan.FromMinutes(1), SoftIdle + TimeSpan.FromSeconds(1), 2, 2)));

        // Fresh: kept.
        Assert.False(policy.ShouldEvict(Candidate(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10), 8, 2)));
    }

    [Fact]
    public void DefaultPolicy_Instance_ShouldBeShared()
    {
        Assert.Same(HayateDefaultEvictionPolicy<TestObject>.Instance,
            HayateDefaultEvictionPolicy<TestObject>.Instance);
    }

    [Fact(Timeout = 60_000)]
    public void CustomPolicy_ShouldKeepObjectsTheBuiltInRuleWouldEvict()
    {
        // Every object here outlives the 100 ms lifetime, so the built-in rule would empty the pool
        // within one eviction cycle. The policy says "keep", and the policy decides.
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithEnableEviction(true)
            .WithEvictionInterval(1000)
            .WithMaxLifeTime(TimeSpan.FromMilliseconds(100))
            .WithMinSize(5)
            .WithEvictionPolicy(new NeverEvictPolicy())
            .Build();

        Assert.Equal(5, pool.GetStats().PooledCount);

        // Two eviction cycles at the builder's minimum interval. This is an observation window rather
        // than a deadline — the assertion is that nothing was evicted, so a late tick cannot fail it.
        Thread.Sleep(2500);

        Assert.Equal(5, pool.GetStats().PooledCount);

        // The pool still hands objects out and takes them back.
        var item = pool.Acquire();
        pool.Release(item);
        Assert.NotNull(item);
    }

    [Fact(Timeout = 60_000)]
    public void CustomPolicy_ShouldEvictObjectsNoTimeRuleWouldEvict()
    {
        // No time-based criterion fires here — lifetime, idle time and soft-idle time are all an hour —
        // so anything that leaves this pool left it because the policy said so.
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithEnableEviction(true)
            .WithEvictionInterval(1000)
            .WithMaxLifeTime(TimeSpan.FromHours(1))
            .WithMaxIdleTime(TimeSpan.FromHours(1))
            .WithSoftMinEvictableIdleTime(TimeSpan.FromHours(1))
            .WithMinSize(0)
            .WithMaxSize(8)
            .WithShardCount(2)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand)
            .WithEvictionPolicy(new AlwaysEvictPolicy())
            .Build();

        // MinSize 0: nothing is pre-created, so the three objects below are the pool's whole content.
        var first = pool.Acquire();
        var second = pool.Acquire();
        var third = pool.Acquire();
        pool.Release(first);
        pool.Release(second);
        pool.Release(third);

        Assert.Equal(3, pool.GetStats().PooledCount);

        // The policy says "evict everything", so the pool must empty once the run has had a pass —
        // whichever pass of however many it needs, inside the deadline.
        Assert.True(WaitFor(() => pool.GetStats().PooledCount == 0, ScanDeadline),
            "the eviction run should have emptied the pool before the deadline");

        // An emptied pool still serves the next borrow.
        var replacement = pool.Acquire();
        Assert.NotNull(replacement);
        pool.Release(replacement);
    }

    [Fact(Timeout = 60_000)]
    public void Policy_ShouldReceiveTheMeasuredCandidate()
    {
        var policy = new CapturingPolicy();

        using var pool = new HayatePoolBuilder<TestObject>()
            .WithEnableEviction(true)
            .WithEvictionInterval(1000)
            .WithMaxLifeTime(TimeSpan.FromHours(1))
            .WithMaxIdleTime(TimeSpan.FromHours(1))
            .WithSoftMinEvictableIdleTime(TimeSpan.FromHours(1))
            .WithMinSize(2)
            .WithMaxSize(8)
            .WithShardCount(2)
            .WithEvictionPolicy(policy)
            .Build();

        var item = pool.Acquire();
        pool.Release(item);

        // Wait for the run to actually offer a candidate instead of sleeping for a fixed period.
        Assert.True(WaitFor(() => policy.Candidates.Length > 0, ScanDeadline),
            "the background eviction run should have offered a candidate before the deadline");

        var candidates = policy.Candidates;
        Assert.NotEmpty(candidates);

        foreach (var candidate in candidates)
        {
            Assert.NotNull(candidate.Item);
            Assert.InRange(candidate.ShardIndex, 0, 1);
            // Two shards over MinPoolSize 2: the soft-idle floor is one idle object per shard.
            Assert.Equal(1, candidate.MinIdleCount);
            Assert.True(candidate.IdleCount >= 1, "a candidate is an idle object, so its shard holds at least one");
            Assert.True(candidate.LeaseCount >= 0);
            // Age is measured from creation, idle time from the last return, so age can never be shorter.
            Assert.True(candidate.Age >= candidate.IdleTime,
                $"age {candidate.Age} must not be shorter than idle time {candidate.IdleTime}");
            // The thresholds travel with the candidate, so a policy needs no access to the options.
            Assert.Equal(TimeSpan.FromHours(1), candidate.MaxLifeTime);
            Assert.Equal(TimeSpan.FromHours(1), candidate.MaxIdleTime);
            Assert.Equal(TimeSpan.FromHours(1), candidate.SoftMinEvictableIdleTime);
        }

        // The capturing policy never evicts, so both pre-warmed objects are still there.
        Assert.Equal(2, pool.GetStats().PooledCount);
    }

    [Fact]
    public void WithEvictionPolicy_ShouldRejectNull()
    {
        var builder = new HayatePoolBuilder<TestObject>();
        Assert.Throws<ArgumentNullException>(() => builder.WithEvictionPolicy(null!));
    }

    [Fact]
    public void WithEvictionPolicy_ShouldBeRejectedWhenEvictionIsDisabled()
    {
        // A custom rule that could never run is a configuration mistake, so the build fails fast
        // instead of silently ignoring it.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new HayatePoolBuilder<TestObject>()
                .WithEnableEviction(false)
                .WithEvictionPolicy(new NeverEvictPolicy())
                .Build());

        Assert.Contains("EnableEviction", ex.Message);
    }

    [Fact]
    public void WithEvictionPolicy_ShouldBeRejectedOnTheLeanProfile()
    {
        // The lean fast path runs without idle eviction by construction.
        Assert.Throws<InvalidOperationException>(() =>
            new HayatePoolBuilder<TestObject>()
                .WithLean()
                .WithEvictionPolicy(new NeverEvictPolicy())
                .Build());
    }

    [Fact]
    public void WithEvictionPolicy_DefaultInstance_ShouldBeAcceptedEvenWhenEvictionIsDisabled()
    {
        // Installing the default policy is the same as installing none, so the fail-fast guard above
        // must not fire for it.
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithEnableEviction(false)
            .WithEvictionPolicy(HayateDefaultEvictionPolicy<TestObject>.Instance)
            .Build();

        var item = pool.Acquire();
        pool.Release(item);
        Assert.NotNull(item);
    }
}
