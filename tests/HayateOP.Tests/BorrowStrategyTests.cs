using System;
using DotNetCore.HayateOP.Policies;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// Acceptance for the borrow-order switch (O8): the pool serves the end of its idle list that the
/// configured strategy names, and the switch is real behaviour rather than a knob that is accepted and
/// then ignored — the one combination that cannot be honoured (LIFO on the lean fast path) fails the
/// build instead of silently applying the default.
/// </summary>
public class BorrowStrategyTests
{
    /// <summary>
    /// A pooled object that can be told apart from its siblings by identity and by id. It keeps a
    /// parameterless constructor because <see cref="HayatePoolBuilder{T}"/> is generic over a type the
    /// pool could create itself.
    /// </summary>
    private sealed class TestObject
    {
        public int Id { get; set; }
    }

    /// <summary>Hands out distinguishable instances, so a borrow sequence can be asserted by identity.</summary>
    private sealed class NumberedPolicy : IHayateObjectPolicy<TestObject>
    {
        private int _next;

        public TestObject Create() => new() { Id = ++_next };
        public bool OnRelease(TestObject item) => true;
        public bool Validate(TestObject item) => true;
        public void OnAcquire(TestObject item) { }
        public void OnPassivate(TestObject item) { }
        public void OnDestroy(TestObject item) { }
    }

    /// <summary>
    /// A pool that holds exactly three objects and never evicts, so the three instances handed out below
    /// are its entire content and the order they are returned in is the order of its idle list.
    /// Sizing: one shard, because a per-shard capacity of <c>MaxPoolSize / ShardCount</c> would floor to
    /// zero with the default four shards, and no auto-scaling, so the ceiling stays where it was set.
    /// </summary>
    private static IHayateObjectPool<TestObject> BuildThreeObjectPool(HayateBorrowStrategy? strategy = null)
    {
        var builder = new HayatePoolBuilder<TestObject>()
            .WithPolicy(new NumberedPolicy())
            .WithShardCount(1)
            .WithEnableAutoScaling(false)
            .WithMinSize(0)
            .WithMaxSize(3)
            .WithEnableEviction(false)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand);

        if (strategy.HasValue) builder = builder.WithBorrowStrategy(strategy.Value);

        return builder.Build();
    }

    /// <summary>
    /// Borrows three objects and returns them in creation order, which leaves the idle list as
    /// <c>first, second, third</c> — <c>first</c> at the head (oldest returned), <c>third</c> at the tail
    /// (most recently returned) — whatever the borrow strategy is.
    /// </summary>
    private static (TestObject First, TestObject Second, TestObject Third) FillInReturnOrder(
        IHayateObjectPool<TestObject> pool)
    {
        var first = pool.Acquire();
        var second = pool.Acquire();
        var third = pool.Acquire();

        Assert.Equal(1, first.Id);
        Assert.Equal(2, second.Id);
        Assert.Equal(3, third.Id);

        pool.Release(first);
        pool.Release(second);
        pool.Release(third);

        return (first, second, third);
    }

    [Fact(Timeout = 30_000)]
    public void Default_ShouldBeFifo()
    {
        // No WithBorrowStrategy call at all: the default is the order every release before the switch
        // used, so a pool that never asks for anything keeps behaving exactly as it did.
        using var pool = BuildThreeObjectPool();

        Assert.Equal(HayateBorrowStrategy.Fifo, pool.GetOptions().BorrowStrategy);

        var (first, second, third) = FillInReturnOrder(pool);

        Assert.Same(first, pool.Acquire());
        Assert.Same(second, pool.Acquire());
        Assert.Same(third, pool.Acquire());
    }

    [Fact(Timeout = 30_000)]
    public void Fifo_ShouldServeTheOldestReturnedObjectFirst()
    {
        using var pool = BuildThreeObjectPool(HayateBorrowStrategy.Fifo);
        var (first, second, third) = FillInReturnOrder(pool);

        Assert.Same(first, pool.Acquire());
        Assert.Same(second, pool.Acquire());
        Assert.Same(third, pool.Acquire());
    }

    [Fact(Timeout = 30_000)]
    public void Lifo_ShouldServeTheMostRecentlyReturnedObjectFirst()
    {
        // The mirror image of the FIFO sequence above, from the same pool state: same list, same
        // contents, opposite end. That is what makes the two cases a discriminating pair.
        using var pool = BuildThreeObjectPool(HayateBorrowStrategy.Lifo);
        var (first, second, third) = FillInReturnOrder(pool);

        Assert.Same(third, pool.Acquire());
        Assert.Same(second, pool.Acquire());
        Assert.Same(first, pool.Acquire());
    }

    [Fact(Timeout = 30_000)]
    public void Lifo_ShouldHandBackTheObjectThatCameBackLast()
    {
        using var pool = BuildThreeObjectPool(HayateBorrowStrategy.Lifo);
        FillInReturnOrder(pool);

        var borrowed = pool.Acquire();
        pool.Release(borrowed);

        // The return put the object at the tail again, and the tail is the end LIFO serves, so the
        // steady-state borrow is the same instance — the hot object stays hot instead of the borrow
        // walking the whole idle set.
        Assert.Same(borrowed, pool.Acquire());
    }

    [Fact(Timeout = 30_000)]
    public void Lifo_ShouldBeVisibleThroughGetOptions()
    {
        using var pool = BuildThreeObjectPool(HayateBorrowStrategy.Lifo);
        Assert.Equal(HayateBorrowStrategy.Lifo, pool.GetOptions().BorrowStrategy);
    }

    [Fact]
    public void UnknownValue_ShouldFallBackToTheDefault()
    {
        // Robustness rule shared with the shard-affinity mode: an unrecognised value is normalized to the
        // default rather than leaving the shard with an end it cannot decide on.
        var options = new HayatePoolOptions { BorrowStrategy = (HayateBorrowStrategy)42 };
        options.ApplyFeatureSwitches();
        Assert.Equal(HayateBorrowStrategy.Fifo, options.BorrowStrategy);
    }

    [Fact]
    public void WithBorrowStrategy_ShouldRejectUnknownValue()
    {
        var builder = new HayatePoolBuilder<TestObject>();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => builder.WithBorrowStrategy((HayateBorrowStrategy)42));
    }

    [Fact]
    public void Lifo_ShouldBeRejectedOnTheLeanFastPath()
    {
        // The lean fast path keeps no ordered idle list (it has a fast lane plus a slot array), so a
        // borrow order is not something it can promise. Rewriting the request into FIFO would be the
        // "accepted but never applied" outcome this item exists to remove, so the build fails instead.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new HayatePoolBuilder<TestObject>()
                .WithLean()
                .WithBorrowStrategy(HayateBorrowStrategy.Lifo)
                .Build());

        Assert.Contains("lean", ex.Message);
    }

    [Fact(Timeout = 30_000)]
    public void DefaultStrategy_ShouldStillBuildOnTheLeanFastPath()
    {
        // The guard above must fire on a request, not on the default value: a lean pool that never asked
        // for a borrow order keeps building and pooling.
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithLean()
            .WithMaxSize(4)
            .Build();

        Assert.Equal(HayateBorrowStrategy.Fifo, pool.GetOptions().BorrowStrategy);

        var item = pool.Acquire();
        Assert.NotNull(item);
        pool.Release(item);
        Assert.Same(item, pool.Acquire());
    }
}
