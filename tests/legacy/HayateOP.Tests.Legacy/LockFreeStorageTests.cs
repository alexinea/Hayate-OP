using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DotNetCore.HayateOP.Policies;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// Acceptance for the lock-free shard store (G-9). With the switch off — the default — the borrow and
/// return paths must be exactly what they were, which is why one case here is a plain borrow/return
/// cycle with no switch set. With it on, the observable contract is what the design page
/// (<c>docs/storage-backends.md</c>) states: takes stop entering the shard lock, the cold paths keep
/// their meaning, the idle set stays counted and sampled, and the two things the backend gives up —
/// retrieval order and a resizable ceiling — are refused rather than quietly ignored.
/// </summary>
public class LockFreeStorageTests
{
    /// <summary>A pooled object distinguishable by identity, so a test can tell a reuse from a fresh instance.</summary>
    private sealed class TestObject
    {
        public int Id { get; set; }
    }

    /// <summary>Hands out numbered instances and records how many it created.</summary>
    private sealed class CountingPolicy : IHayateObjectPolicy<TestObject>
    {
        private int _next;

        public int Created { get; private set; }

        public TestObject Create()
        {
            Created++;
            return new() { Id = ++_next };
        }

        public bool OnRelease(TestObject item) => true;
        public bool Validate(TestObject item) => true;
        public void OnAcquire(TestObject item) { }
        public void OnPassivate(TestObject item) { }
        public void OnDestroy(TestObject item) { }
    }

    /// <summary>
    /// An engine pool with one shard — so the per-shard capacity is the whole ceiling and every borrow
    /// lands on the same store — no auto-scaling and no background eviction, which keeps every case
    /// deterministic except the one that deliberately runs threads.
    /// </summary>
    private static HayatePoolBuilder<TestObject> EngineBuilder(
        CountingPolicy policy,
        bool lockFree,
        int minSize = 4,
        int maxSize = 4,
        int shardCount = 1)
    {
        var builder = new HayatePoolBuilder<TestObject>()
            .WithPolicy(policy)
            .WithShardCount(shardCount)
            .WithEnableAutoScaling(false)
            .WithEnableEviction(false)
            .WithMinSize(minSize)
            .WithMaxSize(maxSize)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand);

        if (lockFree) builder = builder.WithLockFreeStorage();

        return builder;
    }

    /// <summary>A pooled object plus the policy that counts its creations, ready to build from.</summary>
    private static (CountingPolicy Policy, HayatePoolBuilder<TestObject> Builder) NewPool(bool lockFree, int minSize = 4, int maxSize = 4)
    {
        var policy = new CountingPolicy();
        return (policy, EngineBuilder(policy, lockFree, minSize, maxSize));
    }

    /// <summary>
    /// Reads the store's own take counter out of the pool's shards. Both the field and the property are
    /// internal, and the point of the counter is to make "the lock is gone" an assertion about a measured
    /// number rather than about a wall-clock reading — so it is read through reflection rather than
    /// published as API the feature does not otherwise need.
    /// </summary>
    private static int LockFreeTakes(object pool)
    {
        var shardsField = pool.GetType().GetField("_shards", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(shardsField);
        var shards = (Array)shardsField!.GetValue(pool)!;

        var total = 0;
        foreach (var shard in shards)
        {
            var property = shard!.GetType().GetProperty("LockFreeTakes", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(property);
            total += (int)property!.GetValue(shard)!;
        }

        return total;
    }

    [Fact]
    public void OffByDefault_TheCycleIsWhatItAlwaysWas()
    {
        var (policy, builder) = NewPool(lockFree: false);
        using var pool = builder.Build();

        var borrowed = new TestObject[4];
        for (var i = 0; i < borrowed.Length; i++) borrowed[i] = pool.Acquire();
        Assert.Equal(4, policy.Created);
        Assert.Equal(0, pool.GetStats().PooledCount);

        foreach (var item in borrowed) pool.Release(item);

        Assert.Equal(4, pool.GetStats().PooledCount);
        Assert.Equal(0, LockFreeTakes(pool));
    }

    [Fact]
    public void Enabled_LendsEveryObjectAndReusesIt()
    {
        var (policy, builder) = NewPool(lockFree: true);
        using var pool = builder.Build();

        var first = new TestObject[4];
        for (var i = 0; i < first.Length; i++) first[i] = pool.Acquire();
        Assert.Equal(4, policy.Created);

        foreach (var held in first) pool.Release(held);
        Assert.Equal(4, pool.GetStats().PooledCount);

        // The second round must be served entirely from the store: same four instances, nothing created.
        var seen = new System.Collections.Generic.HashSet<int>();
        for (var i = 0; i < first.Length; i++)
        {
            var again = pool.Acquire();
            Assert.Contains(again, first);
            Assert.True(seen.Add(again.Id));
            pool.Release(again);
        }

        Assert.Equal(4, policy.Created);
        Assert.Equal(4, pool.GetStats().PooledCount);
    }

    [Fact]
    public void Enabled_ServesEveryTakeFromTheStoreAndNotTheLock()
    {
        var (_, builder) = NewPool(lockFree: true, minSize: 2, maxSize: 2);
        using var pool = builder.Build();

        var item = pool.Acquire();
        Assert.True(LockFreeTakes(pool) > 0, "a take on the lock-free store must not have gone through the shard lock");
        pool.Release(item);

        var before = LockFreeTakes(pool);
        for (var i = 0; i < 10; i++)
        {
            var again = pool.Acquire();
            pool.Release(again);
        }

        Assert.Equal(before + 10, LockFreeTakes(pool));
    }

    [Fact]
    public void Enabled_RefusesLifoAtBuildTime()
    {
        var policy = new CountingPolicy();

        // The store keeps an explicit order it cannot honour, so the build is refused instead of the
        // order being quietly dropped. Fifo (what a pool that never asked for anything holds) builds.
        var thrown = Assert.Throws<InvalidOperationException>(() =>
            EngineBuilder(policy, lockFree: true)
                .WithBorrowStrategy(HayateBorrowStrategy.Lifo)
                .Build());

        Assert.Contains("lock-free shard store", thrown.Message, StringComparison.Ordinal);

        using var accepted = EngineBuilder(new CountingPolicy(), lockFree: true)
            .WithBorrowStrategy(HayateBorrowStrategy.Fifo)
            .Build();
        Assert.True(accepted.GetOptions().EnableLockFreeStorage);
    }

    [Fact]
    public void LeanMode_TurnsTheStoreOff()
    {
        // Lean has its own storage, so the general-purpose store cannot apply. It is normalized off —
        // accepted and ignored is the failure mode the option set exists to prevent.
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithLean()
            .WithLockFreeStorage()
            .WithMinSize(1)
            .WithMaxSize(4)
            .Build();

        Assert.False(pool.GetOptions().EnableLockFreeStorage);
    }

    [Fact]
    public void Enabled_TheIdleSetIsStillCountedAndSampled()
    {
        var (_, builder) = NewPool(lockFree: true);
        using var pool = builder.Build();

        var borrowed = new TestObject[4];
        for (var i = 0; i < borrowed.Length; i++) borrowed[i] = pool.Acquire();
        Assert.Equal(0, pool.GetStats().PooledCount);

        for (var i = 0; i < borrowed.Length; i++)
        {
            pool.Release(borrowed[i]);
            // The store's count is a counter rather than a list walk, so it has to be right after every
            // single return, not only once the round is over.
            Assert.Equal(i + 1, pool.GetStats().PooledCount);
        }

        // GetAll / SnapshotHead / DrainForClear all go through the same store; the snapshot below is the
        // observable one.
        var snapshot = pool.TakeSnapshot();
        Assert.Equal(4, snapshot.PooledCount);
    }

    [Fact]
    public void Enabled_ProactiveEvictStillClaimsIdleObjects()
    {
        var (_, builder) = NewPool(lockFree: true, minSize: 4, maxSize: 4);
        using var pool = builder.Build();

        var borrowed = new TestObject[4];
        for (var i = 0; i < borrowed.Length; i++) borrowed[i] = pool.Acquire();
        foreach (var item in borrowed) pool.Release(item);

        // Claim-and-remove is the same exchange a take performs, which is what makes the three Remove
        // call sites work unchanged on this backend.
        var evicted = pool.Evict(HayateEvictReason.Touched);

        Assert.Equal(4, evicted);
        Assert.Equal(0, pool.GetStats().PooledCount);
    }

    [Fact]
    public void Enabled_ClearLeavesNothingIdle()
    {
        // MinSize 2, so the two objects the case borrows are the whole pool: the count it asserts after
        // each return is the store's count and nothing else.
        var (_, builder) = NewPool(lockFree: true, minSize: 2, maxSize: 4);
        using var pool = builder.Build();

        var borrowed = new TestObject[2];
        borrowed[0] = pool.Acquire();
        borrowed[1] = pool.Acquire();
        foreach (var held in borrowed) pool.Release(held);
        Assert.Equal(2, pool.GetStats().PooledCount);

        pool.Clear();

        Assert.Equal(0, pool.GetStats().PooledCount);

        // The slots went back with the wrappers the drain retired, so the shard can fill again.
        var recycled = pool.Acquire();
        pool.Release(recycled);
        Assert.Equal(1, pool.GetStats().PooledCount);
    }

    [Fact]
    public void Enabled_ReloadCannotRaiseTheCeilingPastTheSlotArray()
    {
        var (_, builder) = NewPool(lockFree: true, minSize: 2, maxSize: 4);
        using var pool = builder.Build();

        // The slot array is sized when the shard is built, so raising the ceiling past it is refused
        // rather than accepted and then silently clamped.
        Assert.Throws<InvalidOperationException>(() => pool.ReloadConfig(o => o.MaxPoolSize = 8));
        Assert.Equal(4, pool.GetOptions().MaxPoolSize);

        // Lowering stays allowed: the store clamps to the smaller of the two.
        pool.ReloadConfig(o => o.MaxPoolSize = 3);
        Assert.Equal(3, pool.GetOptions().MaxPoolSize);
    }

    [Fact]
    public void Enabled_ReloadIsMeasuredPerShardAndNotPerPool()
    {
        // The store's ceiling is per shard while MaxPoolSize is per pool, so a reload has to be measured
        // by the per-shard maximum it implies. Eight objects over two shards is four slots each: reloading
        // to eight asks for exactly what the shards were built with, and a check written against the pool
        // total would refuse it — which would make a pool unable to reload to the size it already has.
        var policy = new CountingPolicy();
        using var pool = EngineBuilder(policy, lockFree: true, minSize: 4, maxSize: 8, shardCount: 2).Build();

        pool.ReloadConfig(o => o.MaxPoolSize = 8);
        Assert.Equal(8, pool.GetOptions().MaxPoolSize);

        // Nine objects over two shards is five per shard, one past the four slots each shard owns.
        Assert.Throws<InvalidOperationException>(() => pool.ReloadConfig(o => o.MaxPoolSize = 9));
        Assert.Equal(8, pool.GetOptions().MaxPoolSize);

        // Six is three per shard, well inside the arrays.
        pool.ReloadConfig(o => o.MaxPoolSize = 6);
        Assert.Equal(6, pool.GetOptions().MaxPoolSize);
    }

    [Fact]
    public void Enabled_ConcurrentBorrowAndReturnNeitherLosesNorDuplicates()
    {
        var (policy, builder) = NewPool(lockFree: true, minSize: 4, maxSize: 4);
        using var pool = builder.Build();

        // One id may be out on loan once at a time. A duplicate observable here would be the signature
        // of the classic slot-store failure: two threads exchanging the same wrapper out of a slot.
        var inFlight = new ConcurrentDictionary<int, int>();
        var duplicates = 0;
        const int threads = 4;
        const int iterations = 500;

        var tasks = new Task[threads];
        for (var t = 0; t < threads; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                for (var i = 0; i < iterations; i++)
                {
                    var item = pool.Acquire(TimeSpan.FromSeconds(10));
                    var concurrent = inFlight.AddOrUpdate(item.Id, 1, (_, count) => count + 1);
                    if (concurrent > 1) Interlocked.Increment(ref duplicates);

                    inFlight.AddOrUpdate(item.Id, 0, (_, count) => count - 1);
                    pool.Release(item);
                }
            });
        }

        Task.WaitAll(tasks);

        Assert.Equal(0, duplicates);
        Assert.Equal(4, policy.Created);
        Assert.Equal(4, pool.GetStats().PooledCount);
    }
}
