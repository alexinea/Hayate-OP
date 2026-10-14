using System;
using System.Diagnostics;
using DotNetCore.HayateOP.Policies;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// Acceptance for the borrow-order switch (O8, extended by G-21): the pool serves the idle object the
/// configured strategy names — an end of its idle list for Fifo/Lifo, a node picked by a walk of that
/// list for Lru and Priority — and the switch is real behaviour rather than a knob that is accepted and
/// then ignored: every combination the engine cannot honour (any order but the default on the lean fast
/// path, on the lock-free store or alongside the per-shard front tier, and Priority with no comparer)
/// fails the build instead of silently applying the default.
/// </summary>
public class BorrowStrategyTests
{
    /// <summary>
    /// A pooled object that can be told apart from its siblings by identity, by id and by rank. It keeps a
    /// parameterless constructor because <see cref="HayatePoolBuilder{T}"/> is generic over a type the
    /// pool could create itself.
    /// </summary>
    private sealed class TestObject
    {
        public int Id { get; set; }

        /// <summary>The value the Priority comparers below order by. Deliberately not the same as
        /// <see cref="Id"/>: a case can then ask for an order that is neither the creation order nor its
        /// reverse, which is what tells a comparer-driven walk apart from an end read.</summary>
        public int Rank { get; set; }
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
    /// A builder for a pool that holds exactly three objects and never evicts, so the three instances a
    /// case borrows are its entire content and the order they are returned in is the order of its idle
    /// list. Sizing: one shard, because a per-shard capacity of <c>MaxPoolSize / ShardCount</c> would floor
    /// to zero with the default four shards, and no auto-scaling, so the ceiling stays where it was set.
    /// </summary>
    private static HayatePoolBuilder<TestObject> ThreeObjectBuilder()
    {
        return new HayatePoolBuilder<TestObject>()
            .WithPolicy(new NumberedPolicy())
            .WithShardCount(1)
            .WithEnableAutoScaling(false)
            .WithMinSize(0)
            .WithMaxSize(3)
            .WithEnableEviction(false)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand);
    }

    /// <summary>
    /// The same pool with a borrow order applied. <paramref name="strategy"/> goes through
    /// <c>WithBorrowStrategy</c> and <paramref name="priority"/> through <c>WithBorrowPriority</c> — which
    /// selects Priority by itself — so a case can exercise either route, and both at once to pin the
    /// "last call wins" rule between them.
    /// </summary>
    private static IHayateObjectPool<TestObject> BuildThreeObjectPool(
        HayateBorrowStrategy? strategy = null, IComparer<TestObject>? priority = null)
    {
        var builder = ThreeObjectBuilder();
        if (strategy.HasValue) builder = builder.WithBorrowStrategy(strategy.Value);
        if (priority is not null) builder = builder.WithBorrowPriority(priority);

        return builder.Build();
    }

    /// <summary>
    /// The bare builder with the requested order installed and nothing else, so a case about one of the
    /// backend guards trips that guard and no other. Priority is installed through
    /// <c>WithBorrowPriority</c>, because selecting it without a comparer would trip its own guard first.
    /// </summary>
    private static HayatePoolBuilder<TestObject> WithOrder(HayateBorrowStrategy strategy)
    {
        return strategy == HayateBorrowStrategy.Priority
            ? new HayatePoolBuilder<TestObject>().WithBorrowPriority(HigherRankFirst())
            : new HayatePoolBuilder<TestObject>().WithBorrowStrategy(strategy);
    }

    /// <summary>Orders by rank ascending, which is the direction Priority reads: the pool borrows the
    /// object the comparer ranks highest, so a positive result means "borrow this one first".</summary>
    private static IComparer<TestObject> HigherRankFirst() =>
        Comparer<TestObject>.Create((x, y) => x.Rank.CompareTo(y.Rank));

    /// <summary>Calls every pair equal, so only the tie rule can decide the order.</summary>
    private static IComparer<TestObject> AlwaysEqual() =>
        Comparer<TestObject>.Create((x, y) => 0);

    /// <summary>
    /// Spins until <see cref="Stopwatch.GetTimestamp"/> has moved on, so two borrows taken back to back
    /// carry different <c>LastBorrowedAt</c> values. Lru orders by that stamp, and without this the two
    /// borrows could share a tick — the case would then be pinning which of two equally-old objects the
    /// walk happens to keep rather than the order itself. The clock ticks at 100 ns on Windows and 1 ns
    /// elsewhere, so this is a handful of iterations; the cap is only there so a coarse clock cannot turn
    /// the helper into an infinite loop.
    /// </summary>
    private static void WaitForDistinctTimestamp()
    {
        var start = Stopwatch.GetTimestamp();
        var spins = 0;
        while (Stopwatch.GetTimestamp() == start)
        {
            if (++spins > 1_000_000)
            {
                Thread.Sleep(1);
                return;
            }
        }
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

    [Theory(Timeout = 30_000)]
    [InlineData(HayateBorrowStrategy.Lifo)]
    [InlineData(HayateBorrowStrategy.Lru)]
    [InlineData(HayateBorrowStrategy.Priority)]
    public void NonFifoOrder_ShouldBeRejectedOnTheLeanFastPath(HayateBorrowStrategy strategy)
    {
        // The lean fast path keeps no ordered idle list (it has a fast lane plus a slot array), so no
        // borrow order is something it can promise. Rewriting the request into FIFO would be the
        // "accepted but never applied" outcome this item exists to remove, so the build fails instead.
        // Lifo stays in the set so the case this guard was originally written for keeps being covered.
        var ex = Assert.Throws<InvalidOperationException>(() => WithOrder(strategy).WithLean().Build());

        Assert.Contains("lean", ex.Message);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(HayateBorrowStrategy.Lifo)]
    [InlineData(HayateBorrowStrategy.Lru)]
    [InlineData(HayateBorrowStrategy.Priority)]
    public void NonFifoOrder_ShouldBeRejectedOnTheLockFreeStore(HayateBorrowStrategy strategy)
    {
        // The lock-free store is a slot array: a wrapper owns its slot for as long as it is parked, so
        // the backend keeps no order at all and cannot honour one.
        var ex = Assert.Throws<InvalidOperationException>(
            () => WithOrder(strategy).WithLockFreeStorage().Build());

        Assert.Contains("lock-free", ex.Message);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(HayateBorrowStrategy.Lifo)]
    [InlineData(HayateBorrowStrategy.Lru)]
    [InlineData(HayateBorrowStrategy.Priority)]
    public void NonFifoOrder_ShouldBeRejectedAlongsideTheFrontTier(HayateBorrowStrategy strategy)
    {
        // The per-shard front tier is consulted before the shard, so it hands back whatever this thread
        // parked last without the selection ever running: the order would hold on a tier miss only.
        // Half-applied is the outcome every guard here exists to prevent, so the combination is refused
        // rather than documented as approximately true.
        var ex = Assert.Throws<InvalidOperationException>(
            () => WithOrder(strategy).WithThreadLocalFrontSlot().Build());

        Assert.Contains("front tier", ex.Message);
    }

    [Fact(Timeout = 30_000)]
    public void DefaultOrder_ShouldStillBuildOnEveryBackend()
    {
        // The three guards above must fire on a request, not on the default value: a pool that never
        // asked for an order keeps building and pooling on each backend.
        var backends = new[]
        {
            ThreeObjectBuilder().WithLean(),
            ThreeObjectBuilder().WithLockFreeStorage(),
            ThreeObjectBuilder().WithThreadLocalFrontSlot()
        };

        foreach (var backend in backends)
        {
            using var pool = backend.Build();
            Assert.Equal(HayateBorrowStrategy.Fifo, pool.GetOptions().BorrowStrategy);

            var item = pool.Acquire();
            pool.Release(item);
            Assert.Same(item, pool.Acquire());
        }
    }

    #region G-21: Lru and Priority

    [Fact(Timeout = 30_000)]
    public void Lru_ShouldServeTheObjectBorrowedLongestAgo()
    {
        // The discriminating pair with the FIFO case below. The two borrows are taken a tick apart and
        // returned in the opposite order, so the idle list and the borrow stamps disagree: the list head
        // is the object borrowed *last*, the smallest stamp belongs to the object borrowed *first*. FIFO
        // reads the head, Lru reads the stamp, and the two therefore hand out different instances from
        // one and the same pool state.
        using var pool = BuildThreeObjectPool(HayateBorrowStrategy.Lru);

        var borrowedFirst = pool.Acquire();
        WaitForDistinctTimestamp();
        var borrowedLast = pool.Acquire();

        pool.Release(borrowedLast);   // returned first -> list head (oldest returned)
        pool.Release(borrowedFirst);  // returned last  -> list tail (most recently returned)

        Assert.Same(borrowedFirst, pool.Acquire());
    }

    [Fact(Timeout = 30_000)]
    public void Fifo_ShouldServeTheOldestReturnedObjectEvenWhenItWasBorrowedLast()
    {
        // The mirror arm of the case above, from the same pool state: FIFO serves the head, which here is
        // the object that was borrowed last. Without this arm the Lru assertion above would also pass for
        // a pool that ignored the strategy and kept serving the head.
        using var pool = BuildThreeObjectPool(HayateBorrowStrategy.Fifo);

        var borrowedFirst = pool.Acquire();
        WaitForDistinctTimestamp();
        var borrowedLast = pool.Acquire();

        pool.Release(borrowedLast);
        pool.Release(borrowedFirst);

        Assert.Same(borrowedLast, pool.Acquire());
    }

    [Fact(Timeout = 30_000)]
    public void Lru_ShouldKeepServingTheRemainingIdleObjectsInBorrowOrder()
    {
        // With three objects the walk has to pick a node in the middle of the list, so this case also pins
        // that the unlink removed the node it chose rather than an end: the sequence is neither the list
        // order nor its reverse.
        using var pool = BuildThreeObjectPool(HayateBorrowStrategy.Lru);

        var first = pool.Acquire();
        WaitForDistinctTimestamp();
        var second = pool.Acquire();
        WaitForDistinctTimestamp();
        var third = pool.Acquire();

        // Returned in reverse borrow order, so the idle list is [third, second, first] — exactly the
        // reverse of the order Lru serves.
        pool.Release(third);
        pool.Release(second);
        pool.Release(first);

        Assert.Same(first, pool.Acquire());
        Assert.Same(second, pool.Acquire());
        Assert.Same(third, pool.Acquire());
    }

    [Fact(Timeout = 30_000)]
    public void Lru_ShouldBeVisibleThroughGetOptions()
    {
        using var pool = BuildThreeObjectPool(HayateBorrowStrategy.Lru);
        Assert.Equal(HayateBorrowStrategy.Lru, pool.GetOptions().BorrowStrategy);
    }

    [Fact(Timeout = 30_000)]
    public void Priority_ShouldServeTheHighestRankedObject()
    {
        // Ranks are assigned so the order the comparer asks for is neither the creation order nor its
        // reverse — which is what makes this discriminate a comparer-driven walk from either end read.
        using var pool = BuildThreeObjectPool(HayateBorrowStrategy.Priority, HigherRankFirst());

        var low = pool.Acquire();
        low.Rank = 10;
        var high = pool.Acquire();
        high.Rank = 30;
        var middle = pool.Acquire();
        middle.Rank = 20;

        pool.Release(low);
        pool.Release(high);
        pool.Release(middle);

        Assert.Same(high, pool.Acquire());
        Assert.Same(middle, pool.Acquire());
        Assert.Same(low, pool.Acquire());
    }

    [Fact(Timeout = 30_000)]
    public void Priority_ShouldServeEqualRanksOldestReturnedFirst()
    {
        // A comparer that calls every pair equal leaves only the tie rule: the walk keeps the first winner
        // it meets, and it starts at the head. The sequence must therefore be FIFO's, object for object,
        // which is what makes the tie rule — and the end the walk starts from — checkable.
        using var pool = BuildThreeObjectPool(HayateBorrowStrategy.Priority, AlwaysEqual());
        var (first, second, third) = FillInReturnOrder(pool);

        Assert.Same(first, pool.Acquire());
        Assert.Same(second, pool.Acquire());
        Assert.Same(third, pool.Acquire());
    }

    [Fact(Timeout = 30_000)]
    public void WithBorrowPriority_ShouldSelectTheStrategyByItself()
    {
        // The comparer cannot be supplied and then ignored: installing one selects Priority, so the order
        // below is the comparer's and GetOptions reports the strategy that goes with it.
        using var pool = BuildThreeObjectPool(priority: HigherRankFirst());

        Assert.Equal(HayateBorrowStrategy.Priority, pool.GetOptions().BorrowStrategy);

        var low = pool.Acquire();
        low.Rank = 1;
        var high = pool.Acquire();
        high.Rank = 2;

        pool.Release(low);
        pool.Release(high);

        Assert.Same(high, pool.Acquire());
    }

    [Fact]
    public void WithBorrowPriority_ShouldRejectANullComparer()
    {
        Assert.Throws<ArgumentNullException>(
            () => new HayatePoolBuilder<TestObject>().WithBorrowPriority(null!));
    }

    [Fact(Timeout = 30_000)]
    public void WithBorrowStrategy_ShouldDropAnEarlierPriorityComparer()
    {
        // The single-mechanism rule: the last call describes the order. A comparer left behind by an
        // earlier call would make Build refuse a chain that ended somewhere else entirely.
        using var pool = ThreeObjectBuilder()
            .WithBorrowPriority(HigherRankFirst())
            .WithBorrowStrategy(HayateBorrowStrategy.Fifo)
            .Build();

        Assert.Equal(HayateBorrowStrategy.Fifo, pool.GetOptions().BorrowStrategy);

        var first = pool.Acquire();
        first.Rank = 1;
        var second = pool.Acquire();
        second.Rank = 99;

        pool.Release(first);
        pool.Release(second);

        // FIFO, not the comparer: the object returned first is the one that comes back out.
        Assert.Same(first, pool.Acquire());
    }

    [Fact(Timeout = 30_000)]
    public void WithBorrowPriority_ShouldWinOverAnEarlierStrategy()
    {
        // The other direction of the same rule.
        using var pool = ThreeObjectBuilder()
            .WithBorrowStrategy(HayateBorrowStrategy.Fifo)
            .WithBorrowPriority(HigherRankFirst())
            .Build();

        Assert.Equal(HayateBorrowStrategy.Priority, pool.GetOptions().BorrowStrategy);
    }

    [Fact]
    public void Priority_ShouldRequireAComparer()
    {
        // Selecting the strategy alone cannot express which object should be borrowed first, so the build
        // fails rather than quietly serving some default order.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new HayatePoolBuilder<TestObject>()
                .WithBorrowStrategy(HayateBorrowStrategy.Priority)
                .Build());

        Assert.Contains("WithBorrowPriority", ex.Message);
    }

    [Fact]
    public void Configure_ShouldNotBeAbleToStrandAComparer()
    {
        // WithBorrowStrategy drops the comparer when it selects another order, but Configure writes
        // straight into the options and never goes through that setter — so this is the only shape in
        // which a comparer the pool would never consult can still be handed over, and it is refused.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new HayatePoolBuilder<TestObject>()
                .WithBorrowPriority(HigherRankFirst())
                .Configure(o => o.BorrowStrategy = HayateBorrowStrategy.Fifo)
                .Build());

        Assert.Contains("Configure", ex.Message);
    }

    [Theory]
    [InlineData(HayateBorrowStrategy.Lru)]
    [InlineData(HayateBorrowStrategy.Priority)]
    public void RecognisedOrder_ShouldSurviveNormalization(HayateBorrowStrategy strategy)
    {
        // The whitelist that turns an unrecognised value back into the default is the one place a new
        // member can be swallowed before Build ever sees it — accepted by the builder, reported by
        // GetOptions, and then served as FIFO. Normalization has to leave a value the engine can perform
        // alone.
        var options = new HayatePoolOptions { BorrowStrategy = strategy };
        options.ApplyFeatureSwitches();

        Assert.Equal(strategy, options.BorrowStrategy);
    }

    [Fact(Timeout = 30_000)]
    public void ReloadConfig_ShouldRefuseABorrowOrderChange()
    {
        // Every shard snapshots the order when it is constructed, so a reload cannot move it: accepting
        // the change would leave the shards serving the order they were built with while GetOptions
        // reported the new one. Refused with the original value restored, the same rule SoftCapacity
        // follows.
        using var pool = BuildThreeObjectPool(HayateBorrowStrategy.Lifo);
        Assert.Equal(HayateBorrowStrategy.Lifo, pool.GetOptions().BorrowStrategy);

        Assert.Throws<InvalidOperationException>(
            () => pool.ReloadConfig(o => o.BorrowStrategy = HayateBorrowStrategy.Fifo));

        Assert.Equal(HayateBorrowStrategy.Lifo, pool.GetOptions().BorrowStrategy);

        // ... and the pool still serves the order it was built with.
        var (first, second, third) = FillInReturnOrder(pool);
        Assert.Same(third, pool.Acquire());
    }

    [Fact(Timeout = 30_000)]
    public void ReloadConfig_ShouldStillAcceptARuntimeSetting()
    {
        // The guard above is specific to the borrow order: the settings a reload is meant to move keep
        // moving.
        using var pool = BuildThreeObjectPool(HayateBorrowStrategy.Fifo);

        pool.ReloadConfig(o => o.DefaultAcquireTimeout = TimeSpan.FromSeconds(2));

        Assert.Equal(TimeSpan.FromSeconds(2), pool.GetOptions().DefaultAcquireTimeout);
    }

    #endregion
}
