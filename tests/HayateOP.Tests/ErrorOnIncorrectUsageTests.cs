using System;
using System.Threading;
using System.Threading.Tasks;
using DotNetCore.HayateOP.Metrics;
using DotNetCore.HayateOP.Policies;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// Acceptance for the misuse-detection switch (G-14, research item 33-R-17): when
/// <see cref="HayatePoolOptions.ErrorOnIncorrectUsage"/> is on, the three caller mistakes on the
/// return path — a null object, an object this pool does not own, an object the pool already holds —
/// throw an <see cref="InvalidOperationException"/> instead of being logged, counted and handled.
/// The forgiving path is pinned first because "off" is the shipped behaviour, and the boundary is
/// pinned with it: a policy verdict and a validation failure are not caller mistakes and never
/// throw, and lean mode can only detect the null one, because its return path keeps no ownership
/// lookup.
/// </summary>
public class ErrorOnIncorrectUsageTests
{
    /// <summary>A pooled object that remembers whether the pool destroyed it.</summary>
    private sealed class TestObject : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    /// <summary>Counts the release outcomes the engine records, so a refused return can be seen in
    /// the metric even when the caller swallows the exception.</summary>
    private sealed class CountingMetrics : IHayateMetrics
    {
        public int Acquired;
        public int ReleasedAccepted;
        public int ReleasedRejected;

        public void RecordObjectAcquired(string poolName, object item, double elapsedMilliseconds)
            => Acquired++;

        public void RecordObjectReleased(string poolName, object item, bool isValid)
        {
            if (isValid) ReleasedAccepted++;
            else ReleasedRejected++;
        }

        public void RecordObjectMiss(string poolName)
        {
        }

        public void RecordPoolScaled(string poolName, string action, int oldSize, int newSize)
        {
        }
    }

    /// <summary>A hand-written policy whose two verdicts are chosen per test, so a caller mistake and
    /// an object verdict can be told apart.</summary>
    private sealed class RejectingPolicy : IHayateObjectPolicy<TestObject>
    {
        private readonly bool _accept;
        private readonly bool _valid;

        public RejectingPolicy(bool accept, bool valid)
        {
            _accept = accept;
            _valid = valid;
        }

        public TestObject Create() => new();

        public bool OnRelease(TestObject item) => _accept;

        public bool Validate(TestObject item) => _valid;

        public void OnAcquire(TestObject item)
        {
        }

        public void OnPassivate(TestObject item)
        {
        }

        public void OnDestroy(TestObject item)
        {
        }
    }

    /// <summary>The preparation strategy that calls everything ready, so the wrapper is transparent
    /// for a return-forwarding test.</summary>
    private sealed class AlwaysReadyStrategy : IHayatePreparationStrategy<TestObject>
    {
        public Task<bool> IsReadyAsync(TestObject item, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task PrepareAsync(TestObject item, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private static HayatePoolBuilder<TestObject> Builder(bool throwOnMisuse)
        => new HayatePoolBuilder<TestObject>()
            .WithFactory(() => new TestObject())
            .WithMinSize(0)
            .WithMaxSize(4)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand)
            .WithErrorOnIncorrectUsage(throwOnMisuse);

    [Fact(Timeout = 30_000)]
    public void Default_Path_KeepsTheForgivingBehaviour()
    {
        using var pool = Builder(throwOnMisuse: false).Build();

        // Null: warned and ignored.
        pool.Release(null!);

        // Foreign: warned and destroyed — the pool takes ownership of what was handed to it.
        var foreign = new TestObject();
        pool.Release(foreign);
        Assert.True(foreign.Disposed);

        // Already returned: the shard's append guard rejects it and the pooled object is destroyed
        // — the "the pool loses one object" behaviour docs/ownership.md records. The refusal leaves
        // the store consistent, so the pool reports the loss instead of keeping a dead object
        // countable (G-14-A), and the next borrow is a fresh object rather than the destroyed one.
        var held = pool.Acquire();
        pool.Release(held);
        Assert.Equal(1, pool.GetStats().PooledCount);
        pool.Release(held);
        Assert.True(held.Disposed);
        Assert.Equal(0, pool.GetStats().PooledCount);
        var replacement = pool.Acquire();
        Assert.NotNull(replacement);
        Assert.NotSame(held, replacement);
    }

    [Fact(Timeout = 30_000)]
    public void NullReturn_ThrowsWhenEnabled()
    {
        using var pool = Builder(throwOnMisuse: true).Build();

        var ex = Assert.Throws<InvalidOperationException>(() => pool.Release(null!));
        Assert.Contains("null object", ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(TestObject), ex.Message, StringComparison.Ordinal);

        // The pool is untouched by the refused return and still serves a borrow.
        var held = pool.Acquire();
        Assert.NotNull(held);
        pool.Release(held);
        Assert.Equal(1, pool.GetStats().PooledCount);
    }

    [Fact(Timeout = 30_000)]
    public void ForeignObject_ThrowsAndIsLeftWithTheCaller()
    {
        using var pool = Builder(throwOnMisuse: true).Build();

        var foreign = new TestObject();
        var ex = Assert.Throws<InvalidOperationException>(() => pool.Release(foreign));
        Assert.Contains("does not own", ex.Message, StringComparison.Ordinal);

        // The object never belonged to this pool, so a return the pool refuses leaves it with the
        // caller rather than being destroyed on the caller's behalf.
        Assert.False(foreign.Disposed);

        var held = pool.Acquire();
        Assert.NotNull(held);
        pool.Release(held);
        Assert.Equal(1, pool.GetStats().PooledCount);
    }

    [Fact(Timeout = 30_000)]
    public void AlreadyReturned_ThrowsAndThePoolKeepsTheObject()
    {
        using var pool = Builder(throwOnMisuse: true).Build();

        var held = pool.Acquire();
        pool.Release(held);
        Assert.Equal(1, pool.GetStats().PooledCount);

        var ex = Assert.Throws<InvalidOperationException>(() => pool.Release(held));
        Assert.Contains("already holds", ex.Message, StringComparison.Ordinal);

        // The throwing path changes nothing: the object is still in the pool exactly once, not
        // destroyed and not listed twice, and the very same object is handed out again.
        Assert.False(held.Disposed);
        Assert.Equal(1, pool.GetStats().PooledCount);
        var again = pool.Acquire();
        Assert.Same(held, again);
        pool.Release(again);
    }

    [Fact(Timeout = 30_000)]
    public void AlreadyReturned_CountsTheRefusedRelease()
    {
        var metrics = new CountingMetrics();
        using var pool = Builder(throwOnMisuse: true)
            .WithEnableMetrics(true)
            .WithMetrics(metrics)
            .Build();

        var held = pool.Acquire();
        pool.Release(held);
        Assert.Throws<InvalidOperationException>(() => pool.Release(held));

        // The refused release is counted the way the shard's append guard used to count it, so a
        // caller that swallows the exception still leaves a trace in the metric.
        Assert.Equal(1, metrics.Acquired);
        Assert.Equal(1, metrics.ReleasedAccepted);
        Assert.Equal(1, metrics.ReleasedRejected);

        // And the loan bookkeeping is intact: one object out and one back leaves the pool as it was.
        Assert.Equal(1, pool.GetStats().PooledCount);
    }

    [Fact(Timeout = 30_000)]
    public void AlreadyReturned_ParkedInTheFrontSlot_Throws()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithFactory(() => new TestObject())
            .WithMinSize(0)
            .WithMaxSize(4)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand)
            .WithErrorOnIncorrectUsage()
            .WithThreadLocalFrontSlot()
            .Build();

        // The borrow is what the front tier's Rents rule reads, so the return parks the object in
        // this thread's slot — where its location is still "borrowed" and only the tier knows the
        // object was already returned.
        var held = pool.Acquire();
        pool.Release(held);
        Assert.Equal(1, pool.GetStats().PooledCount);

        var ex = Assert.Throws<InvalidOperationException>(() => pool.Release(held));
        Assert.Contains("already holds", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, pool.GetStats().PooledCount);
    }

    [Fact(Timeout = 30_000)]
    public void Lean_NullReturn_ThrowsWhenEnabled()
    {
        using var pool = Builder(throwOnMisuse: true).WithLean().Build();

        var ex = Assert.Throws<InvalidOperationException>(() => pool.Release(null!));
        Assert.Contains("null object", ex.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = 30_000)]
    public void Lean_ForeignObject_IsStillAccepted()
    {
        // The lean return path keeps no ownership lookup — an object from elsewhere is pooled by
        // contract — so the switch has nothing to detect there and does not pretend to.
        using var pool = Builder(throwOnMisuse: true).WithLean().Build();

        var foreign = new TestObject();
        pool.Release(foreign);
        var again = pool.Acquire();
        Assert.Same(foreign, again);
    }

    [Fact(Timeout = 30_000)]
    public void Preparation_ForwardsTheThrow()
    {
        var inner = Builder(throwOnMisuse: true).Build();
        using var pool = inner.WithPreparation(new AlwaysReadyStrategy());

        var held = pool.Acquire();
        pool.Release(held);
        Assert.Throws<InvalidOperationException>(() => pool.Release(held));
        Assert.Equal(1, pool.GetStats().PooledCount);
    }

    [Fact(Timeout = 30_000)]
    public void TheSwitchIsOffByDefault_AndCanBeTurnedBackOff()
    {
        Assert.False(new HayatePoolOptions().ErrorOnIncorrectUsage);

        using var off = Builder(throwOnMisuse: false).Build();
        Assert.False(off.GetOptions().ErrorOnIncorrectUsage);
        off.Release(null!);

        // An explicit off after an explicit on is the same forgiving path.
        using var flipped = Builder(throwOnMisuse: true)
            .WithErrorOnIncorrectUsage(false)
            .Build();
        flipped.Release(null!);
    }

    [Fact(Timeout = 30_000)]
    public void PolicyRejection_DoesNotThrow()
    {
        // A verdict about the object is not a mistake by the caller: the switch leaves the
        // non-throwing refusal exactly where it was.
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPolicy(new RejectingPolicy(accept: false, valid: true))
            .WithMinSize(0)
            .WithMaxSize(4)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand)
            .WithErrorOnIncorrectUsage()
            .Build();

        var held = pool.Acquire();
        pool.Release(held);
        Assert.True(held.Disposed);
    }

    [Fact(Timeout = 30_000)]
    public void ValidateOnReturnFailure_DoesNotThrow()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPolicy(new RejectingPolicy(accept: true, valid: false))
            .WithValidateOnReturn(true)
            .WithMinSize(0)
            .WithMaxSize(4)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand)
            .WithErrorOnIncorrectUsage()
            .Build();

        var held = pool.Acquire();
        pool.Release(held);
        Assert.True(held.Disposed);
    }
}

/// <summary>
/// The store-consistency half of a double return on the forgiving path (G-14-A, found while
/// verifying G-14): the second return is refused by the shard's append guard and the object is
/// destroyed, and the destruction must leave nothing reachable from the store. Before the fix the
/// destroyed wrapper stayed linked — the free list kept counting it and the next borrow popped it
/// and handed out its null value. Pinned on both storage backends, because the lock-free store
/// holds the parked wrapper in a slot rather than in a list node.
/// </summary>
public class DoubleReturnStoreConsistencyTests
{
    private sealed class TestObject : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private static HayatePoolBuilder<TestObject> Builder(bool lockFree)
        => new HayatePoolBuilder<TestObject>()
            .WithFactory(() => new TestObject())
            .WithMinSize(0)
            .WithMaxSize(4)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand)
            .WithLockFreeStorage(lockFree);

    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public void TheRefusedReturn_NeverLeavesADestroyedObjectReachable(bool lockFree)
    {
        using var pool = Builder(lockFree).Build();

        var held = pool.Acquire();
        pool.Release(held);
        pool.Release(held);

        // The pool lost the object — that is the documented outcome — and the store no longer
        // counts or holds it.
        Assert.True(held.Disposed);
        Assert.Equal(0, pool.GetStats().PooledCount);

        // The next borrow is a fresh object: not null, and not the destroyed one.
        var replacement = pool.Acquire();
        Assert.NotNull(replacement);
        Assert.False(replacement!.Disposed);
        Assert.NotSame(held, replacement);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public void TheStoreKeepsServingObjects_AfterARefusedReturn(bool lockFree)
    {
        using var pool = Builder(lockFree).Build();

        var first = pool.Acquire();
        var second = pool.Acquire();
        pool.Release(first);
        pool.Release(first);

        // Two borrows after the refused return still produce two distinct live objects, so the
        // store recovered its capacity instead of shrinking by the destroyed one.
        var third = pool.Acquire();
        var fourth = pool.Acquire();
        Assert.NotNull(third);
        Assert.NotNull(fourth);
        Assert.NotSame(third, fourth);
        Assert.False(third!.Disposed);
        Assert.False(fourth!.Disposed);
        Assert.NotSame(second, third);
    }
}
