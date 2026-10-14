using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using DotNetCore.HayateOP.Policies;
using DotNetCore.HayateOP.Specialized;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// Acceptance for the non-throwing borrow (G-17, research item V8): where <c>Acquire</c> reports a
/// pool that cannot serve the request right now by throwing, <c>TryAcquire</c> reports it by
/// returning <c>false</c>. The timeout form is a hard bound under every reject policy — including
/// <c>Block</c>, whose <c>Acquire</c> waits without a limit — and faults (a negative budget, a
/// factory that throws, an unknown policy) stay exceptions so a bug is never read as "nothing
/// available". The statistics sites the throwing form touches are touched by the try form as well,
/// so the two borrow forms cannot drift apart observably.
/// </summary>
public class TryAcquireTests
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

    /// <summary>Refuses to build anything, to prove a failing factory is not swallowed into a false.</summary>
    private sealed class ExplodingPolicy : IHayateObjectPolicy<TestObject>
    {
        public TestObject Create() => throw new InvalidOperationException("the factory was asked to build an object and refused");

        public bool OnRelease(TestObject item) => true;
        public bool Validate(TestObject item) => true;
        public void OnAcquire(TestObject item) { }
        public void OnPassivate(TestObject item) { }
        public void OnDestroy(TestObject item) { }
    }

    /// <summary>Reports every object ready, so the preparation chain never has to repair one.</summary>
    private sealed class AlwaysReadyStrategy : IHayatePreparationStrategy<TestObject>
    {
        public Task<bool> IsReadyAsync(TestObject item, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task PrepareAsync(TestObject item, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    /// <summary>Cannot repair anything, to prove a prepare fault stays an exception under a try borrow.</summary>
    private sealed class AlwaysBrokenStrategy : IHayatePreparationStrategy<TestObject>
    {
        public Task<bool> IsReadyAsync(TestObject item, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task PrepareAsync(TestObject item, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("the reconnect handshake failed");
    }

    /// <summary>
    /// An engine pool whose two ceilings stay where they were set: one shard, no auto-scaling, no
    /// eviction. It is pre-warmed to <paramref name="minSize"/> idle objects in the constructor,
    /// because the wait policies are only exercised on a pool that already tracks something: a pool
    /// that tracks nothing is cold-booted on a miss, which would answer a try with a fresh object
    /// before any policy decision is reached.
    /// </summary>
    private static HayatePoolBuilder<TestObject> EngineBuilder(
        IHayateObjectPolicy<TestObject> policy,
        HayatePoolRejectPolicy rejectPolicy = HayatePoolRejectPolicy.CreateOnDemand,
        int minSize = 1,
        int maxSize = 2)
        => new HayatePoolBuilder<TestObject>()
            .WithPolicy(policy)
            .WithShardCount(1)
            .WithEnableAutoScaling(false)
            .WithEnableEviction(false)
            .WithMinSize(minSize)
            .WithMaxSize(maxSize)
            .WithRejectPolicy(rejectPolicy);

    [Fact]
    public void IdlePool_ServesBothForms()
    {
        var policy = new CountingPolicy();
        using var pool = EngineBuilder(policy).Build();

        Assert.True(pool.TryAcquire(out var zeroWait));
        Assert.NotNull(zeroWait);

        Assert.True(pool.TryAcquire(TimeSpan.FromSeconds(5), out var bounded));
        Assert.NotNull(bounded);

        pool.Release(zeroWait);
        pool.Release(bounded);
        Assert.Equal(2, policy.Created);
    }

    [Fact]
    public void Abort_EmptyPool_ReportsFalseWithoutWaiting()
    {
        var policy = new CountingPolicy();
        using var pool = EngineBuilder(policy, HayatePoolRejectPolicy.Abort, minSize: 0)
            .WithEnableMetrics(true) // the miss counter is gated by metrics, so it must be on to assert
            .Build();

        // An Abort pool that holds nothing has no object to give and creates none on a miss: the
        // refusal the throwing form reports as InvalidOperationException.
        var sw = Stopwatch.StartNew();
        Assert.False(pool.TryAcquire(out var zeroWait));
        Assert.False(pool.TryAcquire(TimeSpan.FromSeconds(5), out var bounded));
        sw.Stop();

        Assert.Null(zeroWait);
        Assert.Null(bounded);

        // Neither form waits: a five-second budget must not cost five seconds under Abort.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"the calls waited {sw.Elapsed}");
        Assert.Equal(0, policy.Created);

        // The rejection is still recorded, so the statistics do not depend on the borrow form.
        Assert.True(pool.GetStats().TotalMissed > 0);
    }

    [Fact]
    public void Block_EmptyOfIdle_NeverParksPastItsBudget()
    {
        var policy = new CountingPolicy();
        using var pool = EngineBuilder(policy, HayatePoolRejectPolicy.Block).Build();

        // The pool tracks its one object, so a cold boot cannot answer: the only way forward is a
        // wait, and Block has no timeout of its own on the throwing path — this same call would park
        // until a return that never comes.
        var held = pool.Acquire();

        var sw = Stopwatch.StartNew();
        Assert.False(pool.TryAcquire(TimeSpan.FromMilliseconds(150), out var bounded));
        var boundedElapsed = sw.Elapsed;

        Assert.False(pool.TryAcquire(out var zeroWait));
        sw.Stop();

        Assert.Null(bounded);
        Assert.Null(zeroWait);

        // The budget bounded the wait, and the zero-budget form did not park at all.
        Assert.True(boundedElapsed >= TimeSpan.FromMilliseconds(100), $"bounded wait={boundedElapsed}");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"total={sw.Elapsed}");

        pool.Release(held);

        // With an idle object back, the borrow succeeds through the same policy.
        Assert.True(pool.TryAcquire(out var afterReturn));
        Assert.NotNull(afterReturn);
        pool.Release(afterReturn);
    }

    [Fact]
    public void BlockTimeout_ReportsTimeoutAsFalse()
    {
        var policy = new CountingPolicy();
        var pool = EngineBuilder(policy, HayatePoolRejectPolicy.BlockTimeout)
            .WithEnableMetrics(true) // the miss counter is gated by metrics, so it must be on to assert
            .Build();

        using (pool)
        {
            var held = pool.Acquire();

            var sw = Stopwatch.StartNew();
            Assert.False(pool.TryAcquire(TimeSpan.FromMilliseconds(150), out var item));
            sw.Stop();

            Assert.Null(item);
            Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(100), $"wait={sw.Elapsed}");
            Assert.True(pool.GetStats().TotalMissed > 0);

            pool.Release(held);
        }
    }

    [Fact]
    public void BlockTimeout_WaitsForAReturnWithinTheBudget()
    {
        var policy = new CountingPolicy();
        using var pool = EngineBuilder(policy, HayatePoolRejectPolicy.BlockTimeout).Build();

        var held = pool.Acquire();
        var returner = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(120));
            pool.Release(held);
        });

        var sw = Stopwatch.StartNew();
        Assert.True(pool.TryAcquire(TimeSpan.FromSeconds(10), out var item));
        sw.Stop();

        Assert.NotNull(item);

        // It waited for the return instead of answering at once: the refusal it would have reported
        // was still recoverable inside the budget.
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(50), $"wait={sw.Elapsed}");

        pool.Release(item);
        returner.Wait(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void CreateOnDemand_EmptyPool_ServesTheTryWithAFreshObject()
    {
        var policy = new CountingPolicy();
        using var pool = EngineBuilder(policy, HayatePoolRejectPolicy.CreateOnDemand, minSize: 0).Build();

        // Creating is not waiting: a policy that serves a miss by growing the pool answers a
        // zero-budget try with an object.
        Assert.True(pool.TryAcquire(out var item));
        Assert.NotNull(item);
        Assert.Equal(1, policy.Created);

        pool.Release(item);
    }

    [Fact]
    public void ActiveCeiling_RefusesInsteadOfThrowing()
    {
        var policy = new CountingPolicy();
        var pool = EngineBuilder(policy, HayatePoolRejectPolicy.BlockTimeout, minSize: 2, maxSize: 2)
            .WithMaxActiveObjects(1)
            .Build();

        using (pool)
        {
            var held = pool.Acquire();

            // The ceiling is the one place the create policies wait instead of creating, so the try
            // reports false after the budget — the outcome the throwing form reaches the same way.
            Assert.False(pool.TryAcquire(TimeSpan.FromMilliseconds(150), out var item));
            Assert.Null(item);
            Assert.True(pool.GetStats().ActiveLimitThrottledCount > 0);

            pool.Release(held);
        }
    }

    [Fact]
    public void ActiveCeiling_AbortPolicy_RefusesInsteadOfThrowing()
    {
        var policy = new CountingPolicy();
        var pool = EngineBuilder(policy, HayatePoolRejectPolicy.Abort, minSize: 2, maxSize: 2)
            .WithMaxActiveObjects(1)
            .Build();

        using (pool)
        {
            var held = pool.Acquire();

            // Abort reports the ceiling the way it reports a miss: the try answers false at once,
            // with or without a budget, and the throttle counter moves either way.
            Assert.False(pool.TryAcquire(out var zeroWait));
            Assert.False(pool.TryAcquire(TimeSpan.FromMilliseconds(150), out var bounded));
            Assert.Null(zeroWait);
            Assert.Null(bounded);
            Assert.True(pool.GetStats().ActiveLimitThrottledCount > 0);

            // The contrast that makes the false meaningful: the same ceiling still throws on Acquire.
            Assert.Throws<InvalidOperationException>(() => pool.Acquire());

            pool.Release(held);

            // With the slot freed, both forms serve again.
            Assert.True(pool.TryAcquire(out var served));
            Assert.NotNull(served);
            pool.Release(served);
        }
    }

    [Fact]
    public void NegativeBudget_StillThrows()
    {
        var policy = new CountingPolicy();
        using var pool = EngineBuilder(policy).Build();

        // A negative budget is a caller bug, not a pool state: it stays an exception so it cannot be
        // read as "nothing available".
        Assert.Throws<ArgumentOutOfRangeException>(
            () => pool.TryAcquire(TimeSpan.FromSeconds(-1), out _));
    }

    [Fact]
    public void FactoryFailure_Propagates()
    {
        using var pool = EngineBuilder(new ExplodingPolicy(), HayatePoolRejectPolicy.CreateOnDemand, minSize: 0).Build();

        // A factory that fails is a fault: it must not be swallowed into a false that reads as
        // "the pool was busy". The creation retry wraps the factory's own error, preserving it as
        // the inner exception, so neither layer can hide the other.
        var ex = Assert.Throws<InvalidOperationException>(() => pool.TryAcquire(out _));
        Assert.Contains("Failed to create object after retries", ex.Message);
        var inner = Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Contains("the factory was asked to build an object and refused", inner.Message);
    }

    [Fact]
    public void OpenBreaker_ReportsFalse()
    {
        var policy = new CountingPolicy();
        var pool = EngineBuilder(policy)
            .WithCircuitBreaker(1, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5), () => true)
            .Build();

        using (pool)
        {
            // The breaker takes the whole pool out of service: the throwing path raises
            // HayatePoolUnavailableException, the try path reports false before touching any shard.
            pool.SetUnavailable("the dependency is down");

            Assert.False(pool.TryAcquire(out var zeroWait));
            Assert.False(pool.TryAcquire(TimeSpan.FromSeconds(5), out var bounded));
            Assert.Null(zeroWait);
            Assert.Null(bounded);

            // The contrast that makes the false meaningful: the same pool still throws on Acquire.
            Assert.Throws<HayatePoolUnavailableException>(() => pool.Acquire());

            // Recovery is visible to the try form too.
            pool.SetAvailable();
            Assert.True(pool.TryAcquire(out var recovered));
            Assert.NotNull(recovered);
            pool.Release(recovered);
        }
    }

    [Fact]
    public void Lean_EmptyOfIdle_ReportsFalseAndServesWhenIdle()
    {
        var policy = new CountingPolicy();
        var builder = new HayatePoolBuilder<TestObject>()
            .WithPolicy(policy)
            .WithMinSize(1)
            .WithMaxSize(1)
            .WithEnableAutoScaling(false)
            .WithEnableEviction(false)
            .WithRejectPolicy(HayatePoolRejectPolicy.Block)
            .WithLean();
        using var pool = builder.Build();

        var held = pool.Acquire();

        // Lean keeps its 100 ms slice on the throwing path; the try form still never parks past its
        // budget, and the zero-budget form answers at once instead of spinning through slices.
        var sw = Stopwatch.StartNew();
        Assert.False(pool.TryAcquire(out var zeroWait));
        Assert.False(pool.TryAcquire(TimeSpan.FromMilliseconds(150), out var bounded));
        sw.Stop();

        Assert.Null(zeroWait);
        Assert.Null(bounded);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"the calls took {sw.Elapsed}");

        pool.Release(held);

        // With an idle object back in the buffer, the fast path serves the try directly.
        Assert.True(pool.TryAcquire(out var hit));
        Assert.NotNull(hit);
        pool.Release(hit);
    }

    [Fact]
    public void Lean_Timeout_ReportsFalseWithinBudget()
    {
        var policy = new CountingPolicy();
        var builder = new HayatePoolBuilder<TestObject>()
            .WithPolicy(policy)
            .WithMinSize(1)
            .WithMaxSize(1)
            .WithEnableAutoScaling(false)
            .WithEnableEviction(false)
            .WithRejectPolicy(HayatePoolRejectPolicy.BlockTimeout)
            .WithLean();
        using var pool = builder.Build();

        var held = pool.Acquire();

        // At the fixed buffer's ceiling the timeout branch is the only refusal left: the try reports
        // it, and the zero-budget form reports it without parking through a single 100 ms slice.
        Assert.False(pool.TryAcquire(out var zeroWait));
        Assert.False(pool.TryAcquire(TimeSpan.FromMilliseconds(150), out var bounded));
        Assert.Null(zeroWait);
        Assert.Null(bounded);

        // The same pool still raises the timeout on the throwing form.
        Assert.Throws<TimeoutException>(() => pool.Acquire(TimeSpan.FromMilliseconds(150)));

        pool.Release(held);

        // With the object back in the buffer the fast path serves the try directly.
        Assert.True(pool.TryAcquire(out var hit));
        Assert.NotNull(hit);
        pool.Release(hit);
    }

    [Fact]
    public void Unbounded_AlwaysSucceeds()
    {
        using var pool = new HayateUnboundedPool<TestObject>(maxIdle: 4, factory: () => new TestObject());

        // An unbounded pool never waits and never refuses, so both try forms answer true — the same
        // outcome its Acquire always has.
        Assert.True(pool.TryAcquire(out var zeroWait));
        Assert.True(pool.TryAcquire(TimeSpan.Zero, out var bounded));
        Assert.NotNull(zeroWait);
        Assert.NotNull(bounded);

        pool.Release(zeroWait);
        pool.Release(bounded);
    }

    [Fact]
    public void Preparation_ForwardsTheRefusal_KeepsPrepareFailuresLoud()
    {
        var policy = new CountingPolicy();
        using var inner = EngineBuilder(policy, HayatePoolRejectPolicy.BlockTimeout, minSize: 1, maxSize: 1).Build();
        using var pool = inner.WithPreparation(new AlwaysReadyStrategy());

        var held = pool.Acquire();

        // The wrapper reports the inner pool's refusal as false: an exhausted inner pool that would
        // have thrown TimeoutException on the Acquire path.
        Assert.False(pool.TryAcquire(TimeSpan.FromMilliseconds(150), out var refused));
        Assert.Null(refused);

        pool.Release(held);

        // With the inner pool able to serve, the same call runs the prepare chain and delivers.
        Assert.True(pool.TryAcquire(out var ready));
        Assert.NotNull(ready);
        pool.Release(ready);
    }

    [Fact]
    public void Preparation_FailingStrategy_StillThrows()
    {
        var policy = new CountingPolicy();
        using var inner = EngineBuilder(policy, HayatePoolRejectPolicy.CreateOnDemand, minSize: 0, maxSize: 2).Build();
        using var pool = inner.WithPreparation(new AlwaysBrokenStrategy());

        // A broken object is a fault, not a pool state: the try borrow keeps the prepare failure an
        // exception rather than answering "not available" for it.
        Assert.Throws<HayatePoolPreparationException>(() => pool.TryAcquire(out _));
    }

    [Fact]
    public void SpecializedPools_ForwardTheTry()
    {
        // The three specialized pools delegate their whole borrow surface to the inner pool, and the
        // try form is no exception.
        Assert.True(CancellationTokenSourcePool.Instance.TryAcquire(out var cts));
        Assert.NotNull(cts);
        cts.Dispose();

        using var streamPool = new MemoryStreamPool();
        Assert.True(streamPool.TryAcquire(out var stream));
        Assert.NotNull(stream);
        stream.Dispose();

        using var builderPool = new StringBuilderPool(4);
        Assert.True(builderPool.TryAcquire(out var builder));
        Assert.NotNull(builder);
        builder.Dispose();
    }
}
