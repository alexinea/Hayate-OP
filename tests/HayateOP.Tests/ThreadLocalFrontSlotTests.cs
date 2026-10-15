using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using DotNetCore.HayateOP.Policies;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// Acceptance for the per-pool thread-local front slot (G-10). With the switch off — the default — the
/// borrow and return paths must be exactly what they were, which is why one case here is a plain cycle
/// with no switch set. With it on, the contract is the one <c>docs/storage-backends.md</c> states: a
/// thread that has borrowed gets its own object back without touching a shard, the Rents rule keeps a
/// thread that never borrowed from parking an object nobody can reach, a parked object still counts as
/// idle and is still reached by Clear and by eviction, and the tier works with the lock-free store on
/// or off — the two switches are independent by design.
/// </summary>
public class ThreadLocalFrontSlotTests
{
    /// <summary>A pooled object distinguishable by identity, so a test can tell a reuse from a fresh instance.</summary>
    private sealed class TestObject
    {
        public int Id { get; set; }
    }

    /// <summary>Hands out numbered instances, records how many it created and what was destroyed.</summary>
    private sealed class CountingPolicy : IHayateObjectPolicy<TestObject>
    {
        private readonly ConcurrentBag<TestObject> _destroyed = new();
        private int _next;

        public int Created { get; private set; }

        /// <summary>Every value the pool tore down, in no particular order.</summary>
        public IReadOnlyCollection<TestObject> Destroyed => _destroyed;

        public TestObject Create()
        {
            Created++;
            return new() { Id = ++_next };
        }

        public bool OnRelease(TestObject item) => true;
        public bool Validate(TestObject item) => true;
        public void OnAcquire(TestObject item) { }
        public void OnPassivate(TestObject item) { }
        public void OnDestroy(TestObject item) => _destroyed.Add(item);
    }

    /// <summary>
    /// An engine pool with one shard, no auto-scaling and no background eviction. The front tier is
    /// per pool instance, so a helper that always builds a fresh pool is what keeps one case's parked
    /// object out of the next case's slot.
    /// </summary>
    private static HayatePoolBuilder<TestObject> EngineBuilder(
        CountingPolicy policy,
        bool frontSlot,
        int minSize = 4,
        int maxSize = 4)
    {
        var builder = new HayatePoolBuilder<TestObject>()
            .WithPolicy(policy)
            .WithShardCount(1)
            .WithEnableAutoScaling(false)
            .WithEnableEviction(false)
            .WithMinSize(minSize)
            .WithMaxSize(maxSize)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand);

        if (frontSlot) builder = builder.WithThreadLocalFrontSlot();

        return builder;
    }

    [Fact(Timeout = 30_000)]
    public void OffByDefault_TheCycleIsWhatItAlwaysWas()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var policy = new CountingPolicy();
        using var pool = EngineBuilder(policy, frontSlot: false, minSize: 2, maxSize: 2).Build();

        var a = pool.Acquire();
        var b = pool.Acquire();
        Assert.Equal(2, policy.Created);

        pool.Release(a);
        pool.Release(b);
        Assert.Equal(2, pool.GetStats().PooledCount);

        var c = pool.Acquire();
        var d = pool.Acquire();
        Assert.Equal(2, policy.Created);
        pool.Release(c);
        pool.Release(d);
    }

    [Fact(Timeout = 30_000)]
    public void Enabled_TheSameThreadGetsItsOwnObjectBack()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        // Three idle objects, two borrowed: one stays in the shard throughout. So a borrow that finds
        // the parked object can only have come from the front slot — the shard would have handed out the
        // one it held, which is a different instance.
        var policy = new CountingPolicy();
        using var pool = EngineBuilder(policy, frontSlot: true, minSize: 3, maxSize: 4).Build();

        var a = pool.Acquire();
        var b = pool.Acquire();
        pool.Release(a);

        var c = pool.Acquire();

        Assert.Same(a, c);
        Assert.Equal(3, policy.Created);

        pool.Release(b);
        pool.Release(c);
    }

    [Fact(Timeout = 30_000)]
    public void Enabled_AnAwaitedBorrowReadsTheSameSlot()
    {
        // The release half of the tier is the shared CompleteReturn, so a pool that only ever borrows
        // asynchronously still parks. The asynchronous borrow therefore has to read the slot back: if it
        // did not, every object such a pool parked would be stranded in a slot nothing reads.
        //
        // The borrows are driven through the awaitable API but complete synchronously — an idle object is
        // always available, and the only await ahead of the scan is on an already-completed warmup task —
        // so the thread that parks is the thread that reads, which is what makes the identity assertion
        // deterministic rather than a race with thread-pool work stealing.
        var policy = new CountingPolicy();
        using var pool = EngineBuilder(policy, frontSlot: true, minSize: 2, maxSize: 4).Build();

        var first = pool.AcquireAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        pool.Release(first);

        var second = pool.AcquireAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult();

        Assert.Same(first, second);
        Assert.Equal(2, policy.Created);

        pool.Release(second);
    }

    [Fact(Timeout = 30_000)]
    public void AThreadThatNeverBorrowed_DoesNotParkTheReturn()
    {
        // The Rents rule. The return runs on a thread that has never borrowed from this pool, so it must
        // take the shared path: if it parked the object in that thread's slot instead, the object would
        // be unreachable to every other borrower while still counting as live, and this thread could not
        // get it back without creating a new one.
        var policy = new CountingPolicy();
        using var pool = EngineBuilder(policy, frontSlot: true, minSize: 1, maxSize: 1).Build();

        var item = pool.Acquire();

        // Both calls take the token: xUnit1051 reports each blocking wait that could be cancelled, and
        // the outer Task.Run is one too. Deliberately still a blocking wait rather than `await` — the
        // assertion below has to run on this thread, which has never borrowed from the pool.
        Task.Run(() => pool.Release(item), TestContext.Current.CancellationToken)
            .Wait(TestContext.Current.CancellationToken);

        var again = pool.Acquire();

        Assert.Same(item, again);
        Assert.Equal(1, policy.Created);

        pool.Release(again);
    }

    [Fact(Timeout = 30_000)]
    public void Enabled_AParkedObjectStillCountsAsIdle()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        // A parked object lives in no shard, so the shard-only sums would report it as borrowed: the pool
        // would say it holds nothing while holding an object. Both the statistics and the snapshot have
        // to include it.
        var policy = new CountingPolicy();
        using var pool = EngineBuilder(policy, frontSlot: true, minSize: 1, maxSize: 4).Build();

        var item = pool.Acquire();
        Assert.Equal(0, pool.GetStats().PooledCount);

        pool.Release(item);

        Assert.Equal(1, pool.GetStats().PooledCount);
        Assert.Equal(1, pool.TakeSnapshot().PooledCount);
    }

    [Fact(Timeout = 30_000)]
    public void Enabled_ClearLeavesNothingParked()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var policy = new CountingPolicy();
        using var pool = EngineBuilder(policy, frontSlot: true, minSize: 2, maxSize: 4).Build();

        var a = pool.Acquire();
        var b = pool.Acquire();
        pool.Release(a);
        pool.Release(b);
        Assert.Equal(2, pool.GetStats().PooledCount);

        pool.Clear();

        Assert.Equal(0, pool.GetStats().PooledCount);

        // The tier's slots are empty and its objects were torn down through the shard drain, so the pool
        // is usable again — served by a fresh object, not by a survivor of the clear.
        var after = pool.Acquire();
        Assert.Equal(3, policy.Created);

        pool.Release(after);
    }

    [Fact(Timeout = 30_000)]
    public void Enabled_ProactiveEvictReachesAParkedObject()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        // A parked object is marked Borrowed (that is what keeps a shard from accepting the wrapper
        // twice), so it cannot come out of a shard snapshot; the eviction paths have to look at the
        // tier's own slots.
        var policy = new CountingPolicy();
        using var pool = EngineBuilder(policy, frontSlot: true, minSize: 1, maxSize: 4).Build();

        var item = pool.Acquire();
        pool.Release(item);

        var evicted = pool.Evict(HayateEvictReason.Touched);

        Assert.Equal(1, evicted);
        Assert.Equal(0, pool.GetStats().PooledCount);
    }

    [Fact(Timeout = 30_000)]
    public void Enabled_AnObjectTakenBackFromTheSlotIsStillTrackedAsBorrowed()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        // A park unlinks the wrapper from the borrowed list — it is idle in the slot, and an abandoned
        // scan must not treat an idle object as a loan — so the slot hit has to re-link it. Without that,
        // an object borrowed from the slot and then abandoned is invisible to the scan and is never
        // reclaimed: the loan silently falls outside K2, which is exactly what this case measures.
        var policy = new CountingPolicy();
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPolicy(policy)
            .WithShardCount(1)
            .WithEnableAutoScaling(false)
            .WithEnableEviction(false)
            .WithMinSize(1)
            .WithMaxSize(2)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand)
            .WithEnableLeakDetection(false)
            .WithLeakDetectionThreshold(TimeSpan.FromSeconds(30))
            .WithRemoveAbandonedOnBorrow()
            .WithRemoveAbandonedTimeout(TimeSpan.FromMilliseconds(100))
            .WithThreadLocalFrontSlot()
            .Build();

        // One cycle puts the object in this thread's slot; the second borrow takes it back out of the
        // slot rather than out of the shard.
        var first = pool.Acquire();
        pool.Release(first);
        var abandoned = pool.Acquire();
        Assert.Same(first, abandoned);

        Thread.Sleep(250);

        // The next borrow runs the abandoned scan on the borrow path; the loan made out of the slot has
        // to be visible to it.
        var next = pool.Acquire();

        Assert.True(pool.GetStats().AbandonedRemovedCount >= 1,
            "A loan taken out of the front slot must still be reclaimable as abandoned, actual " + pool.GetStats().AbandonedRemovedCount);
        Assert.Contains(abandoned, policy.Destroyed);

        pool.Release(next);
    }

    [Fact(Timeout = 30_000)]
    public void TheTwoStorageSwitches_AreIndependent()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        // Evaluated as a pair, switched independently: every combination has to lend, count and reuse the
        // same objects, or one of the two would be reporting a pool the other cannot serve.
        foreach (var lockFree in new[] { false, true })
        {
            foreach (var frontSlot in new[] { false, true })
            {
                var policy = new CountingPolicy();
                var builder = new HayatePoolBuilder<TestObject>()
                    .WithPolicy(policy)
                    .WithShardCount(1)
                    .WithEnableAutoScaling(false)
                    .WithEnableEviction(false)
                    .WithMinSize(2)
                    .WithMaxSize(4)
                    .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand);

                if (lockFree) builder = builder.WithLockFreeStorage();
                if (frontSlot) builder = builder.WithThreadLocalFrontSlot();

                using var pool = builder.Build();

                Assert.Equal(lockFree, pool.GetOptions().EnableLockFreeStorage);
                Assert.Equal(frontSlot, pool.GetOptions().EnableThreadLocalFrontSlot);

                var a = pool.Acquire();
                var b = pool.Acquire();
                pool.Release(a);
                pool.Release(b);
                Assert.Equal(2, pool.GetStats().PooledCount);

                var c = pool.Acquire();
                var d = pool.Acquire();
                Assert.Equal(2, policy.Created);

                pool.Release(c);
                pool.Release(d);
                Assert.Equal(2, pool.GetStats().PooledCount);
            }
        }
    }

    [Fact(Timeout = 30_000)]
    public void LeanMode_TurnsTheSlotOff()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        // Lean has its own fast lane ahead of its own storage, so the general-purpose tier cannot apply;
        // it is normalized off rather than accepted and ignored.
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithLean()
            .WithThreadLocalFrontSlot()
            .WithMinSize(1)
            .WithMaxSize(4)
            .Build();

        Assert.False(pool.GetOptions().EnableThreadLocalFrontSlot);
    }
}
