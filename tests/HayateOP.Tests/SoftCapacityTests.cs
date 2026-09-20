using System;
using DotNetCore.HayateOP.Policies;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// Acceptance for the return-path soft capacity (T-R): a pool whose soft ceiling is lower than its
/// hard ceiling still lends out the whole hard ceiling, but stops <i>retaining</i> returns once the
/// ceiling is reached — a return that arrives with the retained set full is disposed instead of
/// stored. Zero (the default) must behave exactly like a pool without the switch, and the values
/// that could never take effect (below the floor, above the hard ceiling, negative) are rejected
/// rather than accepted as a knob that silently does nothing.
/// </summary>
public class SoftCapacityTests
{
    /// <summary>
    /// A pooled object distinguishable by id, so a burst sequence can be asserted by identity — the
    /// objects a follow-up burst receives say exactly which returns were retained and which were
    /// dropped and recreated.
    /// </summary>
    private sealed class TestObject
    {
        public int Id { get; set; }
    }

    /// <summary>Hands out numbered instances and records every destruction, so tests can observe drops.</summary>
    private sealed class CountingPolicy : IHayateObjectPolicy<TestObject>
    {
        private int _next;

        public int Destroyed { get; private set; }

        public TestObject Create() => new() { Id = ++_next };
        public bool OnRelease(TestObject item) => true;
        public bool Validate(TestObject item) => true;
        public void OnAcquire(TestObject item) { }
        public void OnPassivate(TestObject item) { }
        public void OnDestroy(TestObject item) => Destroyed++;
    }

    /// <summary>
    /// An engine pool that holds at most four objects and never evicts: one shard (the per-shard
    /// capacity is <c>MaxPoolSize / ShardCount</c>), no auto-scaling, so the ceilings stay where they
    /// were set. <paramref name="softCapacity"/> of <c>null</c> leaves the switch at its default.
    /// </summary>
    private static HayatePoolBuilder<TestObject> EngineBuilder(CountingPolicy policy, int? softCapacity)
    {
        var builder = new HayatePoolBuilder<TestObject>()
            .WithPolicy(policy)
            .WithShardCount(1)
            .WithEnableAutoScaling(false)
            .WithEnableEviction(false)
            .WithMinSize(0)
            .WithMaxSize(4)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand);

        if (softCapacity.HasValue) builder = builder.WithSoftCapacity(softCapacity.Value);

        return builder;
    }

    /// <summary>Borrows four objects (the pool's whole hard ceiling) and returns them in order.</summary>
    private static TestObject[] BorrowFour(IHayateObjectPool<TestObject> pool)
    {
        var items = new TestObject[4];
        for (var i = 0; i < 4; i++) items[i] = pool.Acquire();
        foreach (var item in items) pool.Release(item);
        return items;
    }

    [Fact(Timeout = 30_000)]
    public void EnginePool_DropsReturnsOnceSoftCapacityIsReached()
    {
        var policy = new CountingPolicy();
        using var pool = EngineBuilder(policy, softCapacity: 2).Build();

        BorrowFour(pool);

        var stats = pool.GetStats();
        Assert.Equal(2, stats.PooledCount); // the first two returns fill the retained set
        Assert.Equal(2, policy.Destroyed);  // the last two returns are disposed instead of stored
    }

    [Fact(Timeout = 30_000)]
    public void EnginePool_ZeroDefault_RetainsEveryReturn()
    {
        var policy = new CountingPolicy();
        using var pool = EngineBuilder(policy, softCapacity: null).Build();

        BorrowFour(pool);

        var stats = pool.GetStats();
        Assert.Equal(4, stats.PooledCount);
        Assert.Equal(0, policy.Destroyed);
    }

    [Fact(Timeout = 30_000)]
    public void EnginePool_PoolStillLendsFullCeiling_AfterDroppingExcess()
    {
        var policy = new CountingPolicy();
        using var pool = EngineBuilder(policy, softCapacity: 2).Build();

        var firstBurst = BorrowFour(pool);
        Assert.Equal(new[] { 1, 2, 3, 4 }, SortedIds(firstBurst));

        // The two dropped objects must come back as fresh instances, not as misses: the pool keeps
        // lending its whole hard ceiling, only its retained set is smaller now.
        var secondBurst = new TestObject[4];
        for (var i = 0; i < 4; i++) secondBurst[i] = pool.Acquire();

        Assert.Equal(new[] { 1, 2, 5, 6 }, SortedIds(secondBurst));

        foreach (var item in secondBurst) pool.Release(item);

        // The second burst's overflow is dropped the same way (2 from each burst).
        Assert.Equal(4, policy.Destroyed);
    }

    [Fact(Timeout = 30_000)]
    public void LeanPool_HonoursSoftCapacityThroughItsSlotLimit()
    {
        var policy = new CountingPolicy();
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPolicy(policy)
            .WithLean()
            .WithMinSize(0)
            .WithMaxSize(4)
            .WithSoftCapacity(2)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand)
            .Build();

        BorrowFour(pool);

        var stats = pool.GetStats();
        Assert.Equal(2, stats.PooledCount); // fast lane + one slot: the structural ceiling
        Assert.Equal(2, policy.Destroyed);
    }

    [Fact(Timeout = 30_000)]
    public void IsValid_RejectsSoftCapacityThatCouldNeverTakeEffect()
    {
        // Above the hard ceiling: the check could never fire.
        Assert.False(new HayatePoolOptions { MinPoolSize = 0, MaxPoolSize = 4, SoftCapacity = 5 }.IsValid());
        // Below the floor: the pool could not hold the minimum it promises.
        Assert.False(new HayatePoolOptions { MinPoolSize = 2, MaxPoolSize = 4, SoftCapacity = 1 }.IsValid());
        // Negative: not a meaningful ceiling.
        Assert.False(new HayatePoolOptions { MinPoolSize = 0, MaxPoolSize = 4, SoftCapacity = -1 }.IsValid());
        // A live ceiling inside the bounds passes, and zero stays the "disabled" default.
        Assert.True(new HayatePoolOptions { MinPoolSize = 0, MaxPoolSize = 4, SoftCapacity = 2 }.IsValid());
        Assert.True(new HayatePoolOptions { MinPoolSize = 0, MaxPoolSize = 4, SoftCapacity = 0 }.IsValid());
    }

    [Fact(Timeout = 30_000)]
    public void Builder_RejectsNegativeSoftCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new HayatePoolBuilder<TestObject>().WithSoftCapacity(-1));
    }

    [Fact(Timeout = 30_000)]
    public void ReloadConfig_RejectsSoftCapacityChange_AndRestoresTheOriginalValue()
    {
        using var pool = EngineBuilder(new CountingPolicy(), softCapacity: 2).Build();

        Assert.Throws<InvalidOperationException>(() => pool.ReloadConfig(o => o.SoftCapacity = 3));
        Assert.Equal(2, pool.GetOptions().SoftCapacity);
    }

    /// <summary>The burst's object ids in sorted order — the borrow order across a refill is not part of the contract.</summary>
    private static int[] SortedIds(TestObject[] items)
    {
        var ids = new int[items.Length];
        for (var i = 0; i < items.Length; i++) ids[i] = items[i].Id;
        Array.Sort(ids);
        return ids;
    }
}
