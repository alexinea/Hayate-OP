using System;
using DotNetCore.HayateOP.Policies;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// Acceptance for the builder's delegate policy surface (G-13, research item 33-O-10): a pool whose
/// creation, reset and return decision are described by lambdas instead of a policy class, on the full
/// builder rather than on the one-call <see cref="HayatePool.Simple{T}"/> alone. The three setters are
/// last-one-wins against <c>WithPolicy</c> in both directions, they combine with every option the
/// builder exposes, and the two hooks they carry run on the engine's own return path — the reset before
/// the return is accepted, the predicate deciding whether it is.
/// </summary>
public class BuilderDelegatePolicyTests
{
    /// <summary>A pooled object carrying the name of whatever created it, so a test can tell which
    /// factory or policy built it.</summary>
    private sealed class TestObject
    {
        public string CreatedBy { get; set; } = string.Empty;

        public int Resets { get; set; }
    }

    /// <summary>The minimal hand-written policy the delegate chain replaces, kept identical to what the
    /// lambdas express so the two can be compared behaviour by behaviour.</summary>
    private sealed class ClassPolicy : IHayateObjectPolicy<TestObject>
    {
        private int _next;

        public TestObject Create() => new() { CreatedBy = "class" };

        public bool OnRelease(TestObject item) => true;

        public bool Validate(TestObject item) => true;

        public void OnAcquire(TestObject item)
        {
        }

        public void OnPassivate(TestObject item) => item.Resets++;

        public void OnDestroy(TestObject item)
        {
        }
    }

    /// <summary>Creates objects stamped with a name, counting how many it built.</summary>
    private sealed class CountingFactory
    {
        private int _built;

        public int Built => _built;

        public TestObject Build(string name)
        {
            _built++;
            return new TestObject { CreatedBy = name };
        }
    }

    [Fact]
    public void FactoryOnly_ReusesLikeThePolicyItReplaces()
    {
        var factory = new CountingFactory();
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithFactory(() => factory.Build("delegate"))
            .WithMinSize(0)
            .WithMaxSize(4)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand)
            .Build();

        // Two borrows create two objects; both come back and are handed out again rather than
        // rebuilt — the reuse a hand-written policy produces with the same lambdas.
        var first = pool.Acquire();
        var second = pool.Acquire();
        Assert.Equal("delegate", first.CreatedBy);
        Assert.Equal("delegate", second.CreatedBy);
        Assert.Equal(2, factory.Built);

        pool.Release(first);
        pool.Release(second);

        var third = pool.Acquire();
        var fourth = pool.Acquire();
        Assert.Equal(2, factory.Built);
        Assert.True(ReferenceEquals(second, third) || ReferenceEquals(first, third));
        Assert.True(ReferenceEquals(first, fourth) || ReferenceEquals(second, fourth));
        Assert.False(ReferenceEquals(third, fourth));

        pool.Release(third);
        pool.Release(fourth);
    }

    [Fact]
    public void Reset_RunsOnTheWayIntoTheIdleState()
    {
        var resets = 0;
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithFactory(() => new TestObject())
            .WithReset(item => { item.Resets++; resets++; })
            .WithMinSize(0)
            .WithMaxSize(2)
            .Build();

        var held = pool.Acquire();
        Assert.Equal(0, resets);

        // The passivate hook runs on the return path, before the return is accepted, so a borrower that
        // gets the object back later finds it already reset.
        pool.Release(held);
        Assert.Equal(1, resets);
        Assert.Equal(1, held.Resets);

        // Borrowing and returning the same object resets it once more per cycle.
        var again = pool.Acquire();
        Assert.Equal(1, resets);
        Assert.Same(held, again);
        pool.Release(again);
        Assert.Equal(2, resets);
        Assert.Equal(2, again.Resets);
    }

    [Fact]
    public void Reset_RunsEvenWhenOrderedBeforeTheFactory()
    {
        var resets = 0;
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithReset(item => { item.Resets++; resets++; })
            .WithFactory(() => new TestObject())
            .WithMinSize(0)
            .WithMaxSize(2)
            .Build();

        var held = pool.Acquire();
        pool.Release(held);
        Assert.Equal(1, resets);
        Assert.Equal(1, held.Resets);
    }

    [Fact]
    public void ReturnPredicateFalse_DestroysInsteadOfParking()
    {
        var factory = new CountingFactory();
        var accepted = 0;
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithFactory(() => factory.Build("delegate"))
            .WithReturnPredicate(item => { accepted++; return false; })
            .WithMinSize(0)
            .WithMaxSize(4)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand)
            .Build();

        var first = pool.Acquire();
        Assert.Equal(1, factory.Built);

        // A refused return is not parked, so the next borrow pays for a fresh object instead of
        // receiving the one that was handed back.
        pool.Release(first);
        Assert.Equal(1, accepted);

        var second = pool.Acquire();
        Assert.Equal(2, factory.Built);
        Assert.False(ReferenceEquals(first, second));

        pool.Release(second);
        Assert.Equal(2, accepted);
    }

    [Fact]
    public void ReturnPredicate_RunsAfterTheReset()
    {
        var order = new System.Collections.Generic.List<string>();
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithFactory(() => new TestObject())
            .WithReset(item => order.Add("reset"))
            .WithReturnPredicate(item => { order.Add("predicate"); return true; })
            .WithMinSize(0)
            .WithMaxSize(2)
            .Build();

        var held = pool.Acquire();
        pool.Release(held);

        // The object is reset before the pool decides whether it is worth keeping, so a predicate that
        // inspects the object sees the reset state rather than the borrower's residue.
        Assert.Equal(new[] { "reset", "predicate" }, order);
    }

    [Fact]
    public void Delegates_CombineWithTheWholeOptionSurface()
    {
        var factory = new CountingFactory();
        var predicateCalls = 0;
        var pool = new HayatePoolBuilder<TestObject>()
            .WithFactory(() => factory.Build("delegate"))
            .WithReset(item => item.Resets++)
            .WithReturnPredicate(item => { predicateCalls++; return true; })
            .WithMinSize(2)
            .WithMaxSize(2)
            .WithRejectPolicy(HayatePoolRejectPolicy.BlockTimeout)
            .WithEnableMetrics(true)
            .Build();

        using (pool)
        {
            // Every option on the chain still reaches the pool the delegates describe.
            var options = pool.GetOptions();
            Assert.Equal(2, options.MinPoolSize);
            Assert.Equal(2, options.MaxPoolSize);
            Assert.Equal(HayatePoolRejectPolicy.BlockTimeout, options.RejectPolicy);
            Assert.True(options.EnableMetrics);

            // The minimum is pre-warmed through the delegate factory, not through a policy class.
            Assert.Equal(2, factory.Built);
            var first = pool.Acquire();
            var second = pool.Acquire();
            Assert.Equal(2, factory.Built);

            // Both objects are out on loan and the size is reached, so the wait policy refuses the
            // third borrow after its budget instead of creating — the option surface is live, not
            // merely reported.
            Assert.False(pool.TryAcquire(TimeSpan.FromMilliseconds(150), out var extra));
            Assert.Null(extra);

            pool.Release(first);
            Assert.Equal(1, first.Resets);
            Assert.Equal(1, predicateCalls);

            // The freed object is handed back out, and the delegate hooks run on every cycle.
            Assert.True(pool.TryAcquire(out var served));
            Assert.Same(first, served);
            pool.Release(served);
            Assert.Equal(2, first.Resets);
            Assert.Equal(2, predicateCalls);
            pool.Release(second);
            Assert.Equal(1, second.Resets);
        }
    }

    [Fact]
    public void LastCallWins_FromFactoryToPolicy()
    {
        var factory = new CountingFactory();
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithFactory(() => factory.Build("delegate"))
            .WithPolicy(new ClassPolicy())
            .WithMinSize(0)
            .WithMaxSize(2)
            .Build();

        var held = pool.Acquire();
        Assert.Equal("class", held.CreatedBy);
        Assert.Equal(0, factory.Built);

        // The policy class also owns the reset, so the delegate chain left nothing behind.
        pool.Release(held);
        Assert.Equal(1, held.Resets);
    }

    [Fact]
    public void LastCallWins_FromPolicyToFactory()
    {
        var factory = new CountingFactory();
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPolicy(new ClassPolicy())
            .WithFactory(() => factory.Build("delegate"))
            .WithMinSize(0)
            .WithMaxSize(2)
            .Build();

        var held = pool.Acquire();
        Assert.Equal("delegate", held.CreatedBy);
        Assert.Equal(1, factory.Built);
    }

    [Fact]
    public void LastCallWins_WithinTheDelegates()
    {
        var first = new CountingFactory();
        var second = new CountingFactory();
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithFactory(() => first.Build("first"))
            .WithReset(item => item.Resets += 100)
            .WithFactory(() => second.Build("second"))
            .WithReset(item => item.Resets++)
            .WithMinSize(0)
            .WithMaxSize(2)
            .Build();

        var held = pool.Acquire();
        Assert.Equal("second", held.CreatedBy);
        Assert.Equal(0, first.Built);
        Assert.Equal(1, second.Built);

        pool.Release(held);
        Assert.Equal(1, held.Resets);
    }

    [Fact]
    public void NullDelegates_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => new HayatePoolBuilder<TestObject>().WithFactory(null!));
        Assert.Throws<ArgumentNullException>(() => new HayatePoolBuilder<TestObject>().WithReset(null!));
        Assert.Throws<ArgumentNullException>(() => new HayatePoolBuilder<TestObject>().WithReturnPredicate(null!));
    }

    [Fact]
    public void HookWithoutFactory_FailsAtBuild()
    {
        // A pool that cannot create is a builder mistake, reported next to the other build-time
        // rejections rather than at the first borrow.
        var ex = Assert.Throws<InvalidOperationException>(
            () => new HayatePoolBuilder<TestObject>().WithReset(item => { }).Build());
        Assert.Contains("WithFactory", ex.Message);

        ex = Assert.Throws<InvalidOperationException>(
            () => new HayatePoolBuilder<TestObject>().WithReturnPredicate(item => true).Build());
        Assert.Contains("WithFactory", ex.Message);

        // The positive control for the same chain: once a factory is supplied it builds.
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithReset(item => { })
            .WithFactory(() => new TestObject())
            .Build();
        Assert.NotNull(pool.Acquire());
    }

    [Fact]
    public void FactoryReturningNull_StillFailsAtCreation()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithFactory(() => null!)
            .WithMinSize(0)
            .WithMaxSize(2)
            .Build();

        // The creation boundary keeps its invariant: a null never reaches the pool, so the failure is
        // the factory's own rather than a null the borrow paths would each have to defend against. The
        // creation retry wraps the factory's error, so neither layer can hide the other.
        var ex = Assert.Throws<InvalidOperationException>(() => pool.Acquire());
        Assert.Contains("Failed to create object after retries", ex.Message);
        var inner = Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Contains("returned null", inner.Message);
    }
}
