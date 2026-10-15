using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DotNetCore.HayateOP.Policies;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// The capacity-adaptive slot layout (G-23 = <c>33-R-9</c>) on the lock-free shard store.
///
/// A compact slot array puts eight adjacent slots in one cache line on a 64-bit runtime, so two
/// threads exchanging two different slots ping-pong that one line between them. The layout spreads
/// the slots a cache line apart for a shard small enough to afford it, and keeps the compact layout
/// above the boundary — the spaced form holds one reference per stride position, and past a few
/// dozen slots that array stops paying for itself.
///
/// Both layouts run the same protocol, so almost every assertion here is about the two numbers that
/// describe the array rather than about behaviour: the stride between two slots and the physical
/// position of logical slot 0. The behaviour that does differ is guarded here too — the borrow scan
/// walks logical slots and maps each to its position, so a mapping that walked the wrong positions
/// would report an occupied store empty and hand out fresh objects instead of the ones it holds.
/// </summary>
public class CacheLineSlotLayoutTests
{
    /// <summary>A cache line, stated independently of the layout so the assertions measure it rather than copy it.</summary>
    private const int CacheLineSize = 64;

    /// <summary>The stride the spaced layout is expected to use: one cache line's worth of references.</summary>
    private static int SpacedStride => CacheLineSize / IntPtr.Size;

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
    /// An engine pool with configurable sharding — no auto-scaling and no background eviction, which
    /// keeps every case deterministic except the one that deliberately runs threads.
    /// </summary>
    private static HayatePoolBuilder<TestObject> EngineBuilder(
        CountingPolicy policy,
        bool lockFree,
        int minSize,
        int maxSize,
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

    private static IHayateObjectPool<TestObject> Build(bool lockFree, int minSize, int maxSize, int shardCount = 1)
    {
        var policy = new CountingPolicy();
        return EngineBuilder(policy, lockFree, minSize, maxSize, shardCount).Build();
    }

    /// <summary>
    /// The layout of every shard: the stride between two slots, the physical position of logical slot
    /// 0, the number of slots laid out, and the array's physical length. Read through reflection
    /// because the point of the layout is to be measured, not to become API the feature does not
    /// otherwise need — the same reasoning the lock-free store's take counter follows.
    /// </summary>
    private static List<(int Stride, int First, int Capacity, int Length)> SlotLayouts(object pool)
    {
        var shardsField = pool.GetType().GetField("_shards", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(shardsField);
        var shards = (Array)shardsField!.GetValue(pool)!;

        var layouts = new List<(int, int, int, int)>(shards.Length);
        foreach (var shard in shards)
        {
            layouts.Add((
                ReadInt(shard!, "SlotStride"),
                ReadInt(shard!, "SlotFirst"),
                ReadInt(shard!, "SlotCapacity"),
                ReadInt(shard!, "SlotArrayLength")));
        }

        return layouts;
    }

    private static int ReadInt(object shard, string property)
    {
        var info = shard.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(info);
        return (int)info!.GetValue(shard)!;
    }

    /// <summary>The pooled value a parked wrapper holds, read through reflection like the array itself.</summary>
    private static object? ParkedValue(object? wrapper)
    {
        if (wrapper is null) return null;
        var property = wrapper.GetType().GetProperty("Value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(property);
        return property!.GetValue(wrapper);
    }

    /// <summary>
    /// The shard's slot array itself, for the one case that observes where a wrapper is parked rather
    /// than which layout was declared. Read as <c>object[]</c> because the element type is internal:
    /// a reference-typed array is covariantly an <c>object[]</c> at run time.
    /// </summary>
    private static object?[] SlotArray(object pool)
    {
        var shardsField = pool.GetType().GetField("_shards", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(shardsField);
        var shard = ((Array)shardsField!.GetValue(pool)!).GetValue(0)!;
        var field = shard.GetType().GetField("_slots", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (object?[])field!.GetValue(shard)!;
    }

    [Fact(Timeout = 30_000)]
    public void SpacedLayoutPutsEachSlotOnItsOwnCacheLine()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var pool = Build(lockFree: true, minSize: 0, maxSize: 4);

        var layouts = SlotLayouts(pool);
        Assert.Single(layouts);
        var (stride, first, capacity, length) = layouts[0];

        // The whole of the claim: the distance between two slots is a cache line, so two threads
        // exchanging two different slots cannot share one.
        Assert.Equal(SpacedStride, stride);
        Assert.True(stride * IntPtr.Size >= CacheLineSize, "adjacent slots must sit at least a cache line apart");
        Assert.Equal(stride, first);                       // slot 0 starts one stride in, off the array header's line
        Assert.Equal(4, capacity);                         // logical slots, not array positions
        Assert.Equal(first + 4 * stride, length);          // offset + capacity x stride
    }

    [Fact(Timeout = 30_000)]
    public void TheBoundaryItselfIsSpaced()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        // The boundary is inclusive: a shard of exactly the boundary capacity still gets the layout.
        using var pool = Build(lockFree: true, minSize: 0, maxSize: 64);

        var (stride, first, capacity, length) = Assert.Single(SlotLayouts(pool));
        Assert.Equal(SpacedStride, stride);
        Assert.Equal(stride, first);
        Assert.Equal(64, capacity);
        Assert.Equal(first + 64 * stride, length);
    }

    [Fact(Timeout = 30_000)]
    public void CompactLayoutAboveTheBoundary()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        // One slot past the boundary: the spaced form would hold eight references per object, so the
        // compact layout stands. It is the identity — the layout every shard had before G-23.
        using var pool = Build(lockFree: true, minSize: 0, maxSize: 65);

        var (stride, first, capacity, length) = Assert.Single(SlotLayouts(pool));
        Assert.Equal(1, stride);
        Assert.Equal(0, first);
        Assert.Equal(65, capacity);
        Assert.Equal(65, length);
    }

    [Fact(Timeout = 30_000)]
    public void SpacingIsDecidedPerShard()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        // 257 over four shards is 65 / 64 / 64 / 64, so one shard takes the compact layout and three
        // take the spaced one in the same pool. Both layouts run the same protocol, which is what
        // makes the asymmetry harmless rather than a second code path to keep in step.
        using var pool = Build(lockFree: true, minSize: 4, maxSize: 257, shardCount: 4);

        var layouts = SlotLayouts(pool);
        Assert.Equal(4, layouts.Count);
        Assert.Single(layouts, l => l.Stride == 1);
        Assert.Equal(3, layouts.FindAll(l => l.Stride == SpacedStride).Count);
        Assert.True(layouts.Exists(l => l.Capacity == 65));
        Assert.True(layouts.Exists(l => l.Capacity == 64 && l.First == SpacedStride));

        // Whichever layout holds the object, the pool still lends and takes it back. The four the
        // shards were pre-warmed with are counted too, so the check is against the level the pool
        // started at rather than against a number the min-size happens to produce.
        var before = pool.GetStats().PooledCount;
        var borrowed = pool.Acquire();
        Assert.Equal(before - 1, pool.GetStats().PooledCount);
        pool.Release(borrowed);
        Assert.Equal(before, pool.GetStats().PooledCount);
    }

    [Fact(Timeout = 30_000)]
    public void ASmallPoolSpacesEveryShard()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        // The default pool is small, so every shard is under the boundary and all of them are spaced.
        using var pool = Build(lockFree: true, minSize: 5, maxSize: 50, shardCount: 4);

        var layouts = SlotLayouts(pool);
        Assert.Equal(4, layouts.Count);
        Assert.All(layouts, l => Assert.Equal(SpacedStride, l.Stride));
        Assert.Equal(50, layouts.ConvertAll(l => l.Capacity).Sum());
        Assert.All(layouts, l => Assert.True(l.Capacity <= 64, "a shard of the default pool is under the boundary"));
    }

    [Fact(Timeout = 30_000)]
    public void OffByDefaultKeepsTheEmptyArray()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        // With the store off the array is the shared empty singleton: no slots, no layout, and the
        // borrow / return path is exactly what it was.
        using var pool = Build(lockFree: false, minSize: 0, maxSize: 4);

        var (stride, first, capacity, length) = Assert.Single(SlotLayouts(pool));
        Assert.Equal(1, stride);
        Assert.Equal(0, first);
        Assert.Equal(0, capacity);
        Assert.Equal(0, length);

        var borrowed = pool.Acquire();
        pool.Release(borrowed);
        Assert.Equal(1, pool.GetStats().PooledCount);
    }

    [Fact(Timeout = 30_000)]
    public void SpacedStore_ParksEachWrapperAtItsMappedPosition()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        // The mapping is the one thing the layout facts above do not cover: a formula that ignored the
        // stride would still leave every wrapper somewhere, so the store would work and only the
        // cache-line benefit would be gone. This case observes the array itself, so the mapping is
        // pinned by where the wrappers actually land.
        //
        // Two objects are held at once, because a slot is assigned when an object is parked and kept
        // while it is borrowed: borrowing, returning and borrowing again would reuse the first slot,
        // and the test needs two wrappers in two different slots.
        using var pool = Build(lockFree: true, minSize: 0, maxSize: 4);
        var slots = SlotArray(pool);

        var first = pool.Acquire();
        var second = pool.Acquire();
        pool.Release(first);      // parked in logical slot 0 - the head of the free-slot stack
        pool.Release(second);     // parked in logical slot 1 - the next entry

        // Exactly the two mapped positions are occupied. Everything in between is the spacing, and
        // position 0 is the array header's, which stays empty.
        Assert.Null(slots[0]);
        Assert.Same(first, ParkedValue(slots[SpacedStride]));
        Assert.Null(slots[SpacedStride + 1]);
        Assert.Same(second, ParkedValue(slots[SpacedStride * 2]));
        Assert.Null(slots[SpacedStride * 3]);

        var occupied = 0;
        foreach (var slot in slots) if (slot is not null) occupied++;
        Assert.Equal(2, occupied);

        // A take reads the same mapping: the scan starts at logical slot 0, so the first wrapper is
        // the one that comes back, its position is cleared, and the other stays where it was.
        var again = pool.Acquire();
        Assert.Same(first, again);
        Assert.Null(slots[SpacedStride]);
        Assert.Same(second, ParkedValue(slots[SpacedStride * 2]));

        pool.Release(again);
        Assert.Same(first, ParkedValue(slots[SpacedStride]));
    }

    [Fact(Timeout = 30_000)]
    public void SpacedStore_ServesEveryObjectItHolds()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        // Every one of the eight objects has to come back out of the store. A mapping that walked the
        // wrong positions would report an occupied store empty, and create-on-miss would quietly hand
        // out a fresh object instead of the one the pool already holds.
        using var pool = Build(lockFree: true, minSize: 0, maxSize: 8);

        var first = new TestObject[8];
        for (var i = 0; i < first.Length; i++) first[i] = pool.Acquire();
        Assert.Equal(0, pool.GetStats().PooledCount);

        foreach (var held in first) pool.Release(held);
        Assert.Equal(8, pool.GetStats().PooledCount);

        var seen = new HashSet<int>();
        for (var i = 0; i < first.Length; i++)
        {
            var again = pool.Acquire();
            Assert.Contains(again, first);
            Assert.True(seen.Add(again.Id));
            pool.Release(again);
        }

        Assert.Equal(8, seen.Count);
    }

    [Fact(Timeout = 30_000)]
    public void CompactStore_ServesEveryObjectItHolds()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        // The compact layout is the rare path now, so it gets its own coverage rather than relying on
        // the existing store cases, which all sit under the boundary and therefore run spaced.
        using var pool = Build(lockFree: true, minSize: 0, maxSize: 65);

        var first = new TestObject[65];
        for (var i = 0; i < first.Length; i++) first[i] = pool.Acquire();
        Assert.Equal(0, pool.GetStats().PooledCount);

        foreach (var held in first) pool.Release(held);
        Assert.Equal(65, pool.GetStats().PooledCount);

        var seen = new HashSet<int>();
        for (var i = 0; i < first.Length; i++)
        {
            var again = pool.Acquire();
            Assert.Contains(again, first);
            Assert.True(seen.Add(again.Id));
            pool.Release(again);
        }

        Assert.Equal(65, seen.Count);
    }

    [Fact(Timeout = 30_000)]
    public void SpacedStore_EvictAndClearClaimEveryObject()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        // Remove, drain and the sample walk all go through the same mapping the take does, so the two
        // cold claims have to clear the whole store on the spaced layout too.
        using var pool = Build(lockFree: true, minSize: 0, maxSize: 8);

        var borrowed = new TestObject[6];
        for (var i = 0; i < borrowed.Length; i++) borrowed[i] = pool.Acquire();
        foreach (var held in borrowed) pool.Release(held);
        Assert.Equal(6, pool.GetStats().PooledCount);

        Assert.Equal(6, pool.Evict(HayateEvictReason.Touched));
        Assert.Equal(0, pool.GetStats().PooledCount);

        for (var i = 0; i < borrowed.Length; i++) borrowed[i] = pool.Acquire();
        foreach (var held in borrowed) pool.Release(held);
        Assert.Equal(6, pool.GetStats().PooledCount);

        pool.Clear();
        Assert.Equal(0, pool.GetStats().PooledCount);

        // The slots went back with the wrappers the drain retired, so the shard can fill again.
        var recycled = pool.Acquire();
        pool.Release(recycled);
        Assert.Equal(1, pool.GetStats().PooledCount);
    }

    [Fact(Timeout = 30_000)]
    public void SpacedStore_RefusesASecondReturnAndKeepsTheNextBorrowSound()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        // A second return is refused through the store's own claim, which is one of the accesses the
        // mapping had to move. If it claimed the wrong position the wrapper would stay reachable, and
        // the next borrow would hand out a destroyed object.
        using var pool = Build(lockFree: true, minSize: 0, maxSize: 8);

        var borrowed = pool.Acquire();
        Assert.Equal(1, borrowed.Id);

        pool.Release(borrowed);
        Assert.Equal(1, pool.GetStats().PooledCount);

        pool.Release(borrowed);
        Assert.Equal(0, pool.GetStats().PooledCount);

        var again = pool.Acquire();
        Assert.NotNull(again);
        Assert.Equal(2, again.Id);
        pool.Release(again);
        Assert.Equal(1, pool.GetStats().PooledCount);
    }

    [Fact(Timeout = 30_000)]
    public void SpacedStore_ConcurrentBorrowAndReturnNeitherLosesNorDuplicates()
    {
        // The layout exists to stop two threads ping-ponging one cache line; the same exercise is also
        // the assertion that the mapping is safe under concurrency. One id may be out on loan once at
        // a time, and nothing may be created past the eight the pool was given room for.
        // Pre-warmed to the ceiling, so the eight objects exist from the start and the concurrency
        // creates nothing: the count at the end is the count the pool was given, which is what makes
        // "nothing was lost" a hard number rather than a bound.
        using var pool = Build(lockFree: true, minSize: 8, maxSize: 8);

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
            }, TestContext.Current.CancellationToken);
        }

        Task.WaitAll(tasks, TestContext.Current.CancellationToken);

        Assert.Equal(0, duplicates);
        Assert.Equal(8, pool.GetStats().PooledCount);
    }
}
