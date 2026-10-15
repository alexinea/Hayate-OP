using System;
using System.Threading.Tasks;
using DotNetCore.HayateOP.Policies;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// Acceptance for the borrow-path active-object ceiling (G-5): a pool that keeps <c>MaxPoolSize</c>
/// objects but lends out no more than <c>MaxActiveObjects</c> of them at a time. Zero (the default)
/// must behave exactly like a pool without the switch; at the ceiling the outcome is the one the
/// reject policy already gives a caller that missed, except that the two creation policies wait
/// instead of creating, because an object created to satisfy the request would itself be out on loan.
/// The values that could never take effect (above the hard ceiling, negative, combined with lean
/// mode) are rejected rather than accepted as a knob that silently does nothing.
/// </summary>
public class ActiveObjectCeilingTests
{
    /// <summary>A pooled object distinguishable by id, so a test can tell a reuse from a fresh instance.</summary>
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
    /// An engine pool whose two ceilings stay where they were set: one shard (the per-shard capacity is
    /// <c>MaxPoolSize / ShardCount</c>), no auto-scaling, no eviction. It is pre-warmed to
    /// <paramref name="minSize"/> idle objects in the constructor, because the reject policies are only
    /// exercised on a pool that already holds something: under <c>Abort</c> a borrow from an empty pool
    /// is refused for want of any object at all, which would make an assertion about the ceiling unable
    /// to tell the two refusals apart. <paramref name="maxActiveObjects"/> of <c>null</c> leaves the
    /// switch at its default.
    /// </summary>
    private static HayatePoolBuilder<TestObject> EngineBuilder(
        CountingPolicy policy,
        int? maxActiveObjects,
        HayatePoolRejectPolicy rejectPolicy = HayatePoolRejectPolicy.CreateOnDemand,
        int minSize = 1,
        int maxSize = 4)
    {
        var builder = new HayatePoolBuilder<TestObject>()
            .WithPolicy(policy)
            .WithShardCount(1)
            .WithEnableAutoScaling(false)
            .WithEnableEviction(false)
            .WithMinSize(minSize)
            .WithMaxSize(maxSize)
            .WithRejectPolicy(rejectPolicy);

        if (maxActiveObjects.HasValue) builder = builder.WithMaxActiveObjects(maxActiveObjects.Value);

        return builder;
    }

    [Fact(Timeout = 30_000)]
    public void ZeroDefault_LendsTheWholePoolAndNeverThrottles()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var policy = new CountingPolicy();
        using var pool = EngineBuilder(policy, maxActiveObjects: null).Build();

        // Without a ceiling every one of the four objects may be out at the same time — the behaviour
        // of every release before the switch existed.
        var items = new TestObject[4];
        for (var i = 0; i < 4; i++) items[i] = pool.Acquire();

        Assert.Equal(new[] { 1, 2, 3, 4 }, SortedIds(items));
        Assert.Equal(0, pool.GetStats().ActiveLimitThrottledCount);

        foreach (var item in items) pool.Release(item);
    }

    [Fact(Timeout = 30_000)]
    public void Abort_RefusesOnceTheCeilingIsReached_WithoutCreating()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var policy = new CountingPolicy();
        // Two objects are held, so one is still idle while the single slot is out: a plain miss could
        // therefore have been served, and the refusal can only be the ceiling's.
        using var pool = EngineBuilder(policy, maxActiveObjects: 1, HayatePoolRejectPolicy.Abort, minSize: 2).Build();

        var first = pool.Acquire();

        // The single slot is out on loan, so the next borrow is refused outright — and refused before
        // any object is created for it, which is what makes the ceiling a ceiling rather than a hint.
        Assert.Throws<InvalidOperationException>(() => pool.Acquire());
        Assert.Equal(2, policy.Created);
        Assert.Equal(1, pool.GetStats().ActiveLimitThrottledCount);

        pool.Release(first);

        // The return frees the slot, so the ceiling lets the next borrow through — served by the object
        // that is still idle, with nothing new created.
        var second = pool.Acquire();
        Assert.NotNull(second);
        Assert.Equal(2, policy.Created);
        Assert.Equal(1, pool.GetStats().ActiveLimitThrottledCount);

        pool.Release(second);
    }

    [Fact(Timeout = 30_000)]
    public void CreateOnDemand_WaitsInsteadOfCreatingAtTheCeiling()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var policy = new CountingPolicy();
        using var pool = EngineBuilder(policy, maxActiveObjects: 1).Build();

        var first = pool.Acquire();

        // CreateOnDemand normally serves a miss by growing the pool. At the ceiling it must not: the
        // object it would create is itself out on loan, so the borrow gives up on the timeout instead.
        Assert.Throws<TimeoutException>(() => pool.Acquire(TimeSpan.FromMilliseconds(150)));
        Assert.Equal(1, policy.Created);
        Assert.True(pool.GetStats().ActiveLimitThrottledCount > 0);

        pool.Release(first);
    }

    [Fact(Timeout = 30_000)]
    public void BlockTimeout_ResumesAsSoonAsAReturnFreesTheSlot()
    {
        var policy = new CountingPolicy();
        using var pool = EngineBuilder(policy, maxActiveObjects: 1, HayatePoolRejectPolicy.BlockTimeout).Build();

        var first = pool.Acquire();

        TestObject? second = null;
        var borrower = Task.Run(() => { second = pool.Acquire(TimeSpan.FromSeconds(20)); }, TestContext.Current.CancellationToken);

        // The ceiling holds the second borrow back while the slot is out.
        Assert.False(borrower.Wait(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken));
        Assert.True(pool.GetStats().ActiveLimitThrottledCount > 0);

        pool.Release(first);

        Assert.True(borrower.Wait(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));
        Assert.NotNull(second);
        Assert.Equal(1, second!.Id);   // it is the object that came back, not a freshly created one
        Assert.Equal(1, policy.Created);

        pool.Release(second!);
    }

    [Fact(Timeout = 30_000)]
    public void CeilingIsRestoredByClear_SoAnUnpairedLoanCannotWedgeThePool()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var policy = new CountingPolicy();
        // Two objects held, one slot: the second borrow must be refused by the ceiling while an idle
        // object is still sitting in the pool.
        using var pool = EngineBuilder(policy, maxActiveObjects: 1, HayatePoolRejectPolicy.BlockTimeout, minSize: 2).Build();

        var borrowed = pool.Acquire();
        Assert.Throws<TimeoutException>(() => pool.Acquire(TimeSpan.FromMilliseconds(150)));
        Assert.True(pool.GetStats().ActiveLimitThrottledCount > 0);

        var createdBeforeClear = policy.Created;

        // Clear drops the registry entries of the borrowed object, so a later return of it would be
        // destroyed as a foreign object and could no longer free the slot. The ceiling is reset with the
        // pool rather than left counting a loan that will never be given back.
        pool.Clear();

        var afterClear = pool.Acquire(TimeSpan.FromMilliseconds(500));
        Assert.NotNull(afterClear);
        // The cleared pool holds nothing that can be handed over, so the borrow had to build a fresh
        // object — which is only reachable because the ceiling stopped counting the lost loan.
        Assert.Equal(createdBeforeClear + 1, policy.Created);

        pool.Release(afterClear);
        GC.KeepAlive(borrowed);
    }

    [Fact(Timeout = 30_000)]
    public void IsValid_RejectsCeilingsThatCouldNeverTakeEffect()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        // Above the hard ceiling: the pool could never have that many objects out at once.
        Assert.False(new HayatePoolOptions { MinPoolSize = 0, MaxPoolSize = 4, MaxActiveObjects = 5 }.IsValid());
        // Negative: not a meaningful ceiling.
        Assert.False(new HayatePoolOptions { MinPoolSize = 0, MaxPoolSize = 4, MaxActiveObjects = -1 }.IsValid());
        // The lean fast path keeps no borrow bookkeeping, so it cannot honour a ceiling either.
        Assert.False(new HayatePoolOptions { MinPoolSize = 0, MaxPoolSize = 4, MaxActiveObjects = 2, EnableLean = true }.IsValid());
        // A live ceiling inside the bounds passes, and zero stays the "disabled" default.
        Assert.True(new HayatePoolOptions { MinPoolSize = 0, MaxPoolSize = 4, MaxActiveObjects = 2 }.IsValid());
        Assert.True(new HayatePoolOptions { MinPoolSize = 0, MaxPoolSize = 4, MaxActiveObjects = 4 }.IsValid());
        Assert.True(new HayatePoolOptions { MinPoolSize = 0, MaxPoolSize = 4, MaxActiveObjects = 0 }.IsValid());
    }

    [Fact(Timeout = 30_000)]
    public void Builder_RejectsNegativeMaxActiveObjects()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new HayatePoolBuilder<TestObject>().WithMaxActiveObjects(-1));
    }

    [Fact(Timeout = 30_000)]
    public void CeilingSurvivesACopyOfTheOptions()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var options = new HayatePoolOptions { MinPoolSize = 0, MaxPoolSize = 4, MaxActiveObjects = 2 };

        Assert.Equal(2, options.CopyTo().MaxActiveObjects);
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
