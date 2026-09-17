using System.Collections.Concurrent;
using DotNetCore.HayateOP.Policies;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// K2 → M4+ — abandoned-object recovery. The M4 leak surface is forensics-only (LeakDetectedCount /
/// LeakSuspectedCount, no reclamation); K2 adds an <em>opt-in</em> reclamation: borrowed objects past
/// <c>RemoveAbandonedTimeout</c> are destroyed on the borrow path (<c>RemoveAbandonedOnBorrow</c>) and/or
/// the background maintenance pass (<c>RemoveAbandonedOnMaintenance</c>), CHOPIN's AbandonedConfig.
/// The default configuration must behave exactly like M4 — count, never reclaim.
/// </summary>
public class AbandonedRecoveryTests
{
    private sealed class TestObject : IDisposable
    {
        public int DisposeCount;

        public void Dispose() => Interlocked.Increment(ref DisposeCount);
    }

    /// <summary>A policy that records every destroyed value, so tests can observe reclamation.</summary>
    private sealed class TrackingPolicy : IHayateObjectPolicy<TestObject>
    {
        private readonly ConcurrentBag<TestObject> _destroyed = new();

        public IReadOnlyCollection<TestObject> Destroyed => _destroyed;

        public TestObject Create() => new();

        public bool OnRelease(TestObject item) => true;

        public bool Validate(TestObject item) => true;

        public void OnAcquire(TestObject item) { }

        public void OnPassivate(TestObject item) { }

        public void OnDestroy(TestObject item) => _destroyed.Add(item);
    }

    [Fact(Timeout = 30_000)]
    public void Default_NoOptIn_BorrowedPastTimeout_IsNotReclaimed_AndStillCountsSuspected()
    {
        // The default forensics-only configuration (both toggles off) must be exactly M4:
        // the borrowed object past the threshold is counted in LeakSuspectedCount but never reclaimed.
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(1)
            .WithMaxSize(2)
            .WithEnableLeakDetection(false)
            .WithEnableEviction(false)
            .WithEnableAutoScaling(false)
            .WithLeakDetectionThreshold(TimeSpan.FromMilliseconds(150))
            .WithRemoveAbandonedTimeout(TimeSpan.FromMilliseconds(100))
            .Build();

        var abandoned = pool.Acquire();
        Thread.Sleep(300);

        var snapshot = pool.TakeSnapshot();
        Assert.True(snapshot.LeakSuspectedCount >= 1,
            "Default forensics-only config must keep counting suspected leaks (M4 behavior), actual " + snapshot.LeakSuspectedCount);
        Assert.Equal(0, snapshot.AbandonedRemovedCount);
        Assert.Equal(0, pool.GetStats().AbandonedRemovedCount);

        // The object is still alive in the pool — nothing was reclaimed.
        Assert.Equal(1, snapshot.BorrowedCount);

        pool.Release(abandoned);
    }

    [Fact(Timeout = 30_000)]
    public void RemoveAbandonedOnBorrow_AbandonedObject_IsReclaimed_NotCountedAsLeak()
    {
        var policy = new TrackingPolicy();
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(1)
            .WithMaxSize(2)
            .WithPolicy(policy)
            .WithEnableLeakDetection(false)
            .WithEnableEviction(false)
            .WithEnableAutoScaling(false)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand)
            .WithLeakDetectionThreshold(TimeSpan.FromSeconds(30))
            .WithRemoveAbandonedOnBorrow()
            .WithRemoveAbandonedTimeout(TimeSpan.FromMilliseconds(100))
            .Build();

        // Borrow one object and keep it past the abandoned timeout.
        var abandoned = pool.Acquire();
        Thread.Sleep(250);

        // The next borrow runs the abandoned scan on the borrow path and reclaims the held object.
        var second = pool.Acquire();

        var snapshot = pool.TakeSnapshot();
        Assert.True(snapshot.AbandonedRemovedCount >= 1,
            "Opt-in borrow-path recovery must reclaim the abandoned object, actual " + snapshot.AbandonedRemovedCount);
        Assert.Equal(snapshot.AbandonedRemovedCount, pool.GetStats().AbandonedRemovedCount);
        Assert.Contains(abandoned, policy.Destroyed);
        Assert.Equal(0, snapshot.LeakSuspectedCount);
        Assert.Equal(0, snapshot.LeakCount);

        pool.Release(second);
    }

    [Fact(Timeout = 30_000)]
    public void RemoveAbandonedOnBorrow_ObjectNotPastTimeout_IsNotReclaimed()
    {
        var policy = new TrackingPolicy();
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(1)
            .WithMaxSize(2)
            .WithPolicy(policy)
            .WithEnableLeakDetection(false)
            .WithEnableEviction(false)
            .WithEnableAutoScaling(false)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand)
            .WithRemoveAbandonedOnBorrow()
            .WithRemoveAbandonedTimeout(TimeSpan.FromSeconds(30))
            .Build();

        var held = pool.Acquire();
        var second = pool.Acquire(); // runs the scan; the held object is far from the timeout

        Assert.Equal(0, pool.GetStats().AbandonedRemovedCount);
        Assert.DoesNotContain(held, policy.Destroyed);
        Assert.Equal(2, pool.TakeSnapshot().BorrowedCount);

        pool.Release(held);
        pool.Release(second);
    }

    [Fact(Timeout = 30_000)]
    public void RemoveAbandonedOnMaintenance_AbandonedObject_IsReclaimed()
    {
        var policy = new TrackingPolicy();
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(1)
            .WithMaxSize(2)
            .WithPolicy(policy)
            .WithEnableLeakDetection(false)
            .WithEnableEviction(false)
            .WithEnableAutoScaling(false)
            .WithRemoveAbandonedOnMaintenance()
            .WithRemoveAbandonedInterval(50)
            .WithRemoveAbandonedTimeout(TimeSpan.FromMilliseconds(100))
            .Build();

        var abandoned = pool.Acquire();

        // The first maintenance pass fires one interval after construction; wait past the timeout and
        // several passes so the reclaim is deterministic.
        Thread.Sleep(700);

        var snapshot = pool.TakeSnapshot();
        Assert.True(snapshot.AbandonedRemovedCount >= 1,
            "Opt-in maintenance recovery must reclaim the abandoned object, actual " + snapshot.AbandonedRemovedCount);
        Assert.Contains(abandoned, policy.Destroyed);

        // The reclaimed object no longer counts as a leak false positive.
        Assert.Equal(0, snapshot.LeakSuspectedCount);
    }

    [Fact(Timeout = 30_000)]
    public void RemoveAbandonedOnBorrow_ReleasedObject_SurvivesReclamationScan()
    {
        // A normally returned object must never be reclaimed: after Release it leaves the borrowed list.
        var policy = new TrackingPolicy();
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(1)
            .WithMaxSize(8)
            .WithPolicy(policy)
            .WithEnableLeakDetection(false)
            .WithEnableEviction(false)
            .WithEnableAutoScaling(false)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand)
            .WithRemoveAbandonedOnBorrow()
            .WithRemoveAbandonedTimeout(TimeSpan.FromMilliseconds(100))
            .Build();

        var a = pool.Acquire();
        var b = pool.Acquire();
        // Both are returned well within the abandoned timeout, so after the sleep nothing borrowed
        // crosses the threshold — the borrow-path scan must find an empty borrowed list.
        pool.Release(a);
        pool.Release(b);

        Thread.Sleep(250);

        var c = pool.Acquire(); // triggers the scan; a and b are idle, not borrowed → must not be reclaimed

        Assert.Equal(0, pool.GetStats().AbandonedRemovedCount);
        Assert.DoesNotContain(a, policy.Destroyed);
        Assert.DoesNotContain(b, policy.Destroyed);

        pool.Release(c);
    }

    [Fact(Timeout = 30_000)]
    public void ReleaseAfterReclaim_DoesNotThrow_AndPoolRemainsUsable()
    {
        // Reclamation is a documented opt-in: the caller may still hold the reference and eventually
        // release it. The pool must stay consistent — the later release is treated as a foreign object
        // (disposed, existing foreign-return semantics) and the pool keeps working.
        var policy = new TrackingPolicy();
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(1)
            .WithMaxSize(4)
            .WithPolicy(policy)
            .WithEnableLeakDetection(false)
            .WithEnableEviction(false)
            .WithEnableAutoScaling(false)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand)
            .WithRemoveAbandonedOnBorrow()
            .WithRemoveAbandonedTimeout(TimeSpan.FromMilliseconds(100))
            .Build();

        var abandoned = pool.Acquire();
        Thread.Sleep(250);

        var second = pool.Acquire(); // reclaims `abandoned`
        Assert.True(pool.GetStats().AbandonedRemovedCount >= 1);

        // The stale caller releases the already-reclaimed object: no throw, pool keeps functioning.
        pool.Release(abandoned);

        var third = pool.Acquire();
        pool.Release(third);
        pool.Release(second);

        Assert.True(pool.GetStats().TotalAcquired >= 3);
    }

    [Fact(Timeout = 30_000)]
    public void LogAbandoned_Enabled_ReclamationDoesNotThrow()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(1)
            .WithMaxSize(2)
            .WithEnableLeakDetection(false)
            .WithEnableEviction(false)
            .WithEnableAutoScaling(false)
            .WithRemoveAbandonedOnBorrow()
            .WithRemoveAbandonedTimeout(TimeSpan.FromMilliseconds(100))
            .WithLogAbandoned()
            .WithLeakTraceCapture(HayateLeakTraceCaptureMode.EveryAcquire)
            .Build();

        var abandoned = pool.Acquire();
        Thread.Sleep(250);

        var second = pool.Acquire(); // reclaims with logging + a captured lease trace

        Assert.True(pool.GetStats().AbandonedRemovedCount >= 1);
        pool.Release(second);
    }

    [Fact(Timeout = 30_000)]
    public void Builder_RemoveAbandonedTimeout_Zero_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HayatePoolBuilder<TestObject>().WithRemoveAbandonedTimeout(TimeSpan.Zero));
    }

    [Fact(Timeout = 30_000)]
    public void Options_RecoveryEnabled_WithZeroTimeout_IsInvalid()
    {
        var options = new HayatePoolOptions
        {
            RemoveAbandonedOnBorrow = true,
            RemoveAbandonedTimeout = TimeSpan.Zero,
        };
        Assert.False(options.IsValid());

        options.RemoveAbandonedTimeout = TimeSpan.FromSeconds(300);
        Assert.True(options.IsValid());
    }

    [Fact(Timeout = 30_000)]
    public void CopyTo_PreservesAbandonedRecoverySettings()
    {
        var source = new HayatePoolOptions
        {
            RemoveAbandonedOnBorrow = true,
            RemoveAbandonedOnMaintenance = true,
            RemoveAbandonedTimeout = TimeSpan.FromMinutes(2),
            LogAbandoned = true,
            RemoveAbandonedIntervalMs = 1234,
        };

        var copy = source.CopyTo();

        Assert.True(copy.RemoveAbandonedOnBorrow);
        Assert.True(copy.RemoveAbandonedOnMaintenance);
        Assert.Equal(TimeSpan.FromMinutes(2), copy.RemoveAbandonedTimeout);
        Assert.True(copy.LogAbandoned);
        Assert.Equal(1234, copy.RemoveAbandonedIntervalMs);
    }

    [Fact(Timeout = 30_000)]
    public void ConcurrentReclaim_DoesNotCorruptPool()
    {
        var policy = new TrackingPolicy();
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(4)
            .WithMaxSize(16)
            .WithPolicy(policy)
            .WithEnableLeakDetection(false)
            .WithEnableEviction(false)
            .WithEnableAutoScaling(false)
            .WithRemoveAbandonedOnBorrow()
            .WithRemoveAbandonedOnMaintenance()
            .WithRemoveAbandonedInterval(50)
            .WithRemoveAbandonedTimeout(TimeSpan.FromMilliseconds(100))
            .Build();

        // Hold 8 objects past the timeout while other threads keep borrowing/releasing: the reclaim
        // paths (borrow + maintenance) race on the borrowed list and must never corrupt it.
        var held = new List<TestObject>();
        for (var i = 0; i < 8; i++) held.Add(pool.Acquire());
        Thread.Sleep(200);

        var stop = 0;
        var workers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (Volatile.Read(ref stop) == 0)
            {
                var obj = pool.Acquire();
                pool.Release(obj);
            }
        })).ToArray();

        Thread.Sleep(500);
        Volatile.Write(ref stop, 1);
        Task.WaitAll(workers);

        Assert.True(pool.GetStats().AbandonedRemovedCount >= 1,
            "Concurrent reclaim should have reclaimed at least the held abandoned objects, actual " + pool.GetStats().AbandonedRemovedCount);
        Assert.True(policy.Destroyed.Count >= 1);

        // The pool is still fully usable.
        var final = pool.Acquire();
        pool.Release(final);
    }
}
