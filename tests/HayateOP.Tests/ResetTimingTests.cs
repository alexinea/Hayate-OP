using System;
using DotNetCore.HayateOP.Policies;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// Acceptance for the reset-timing switch (3.1, G-7): an <see cref="IHayateResettable"/> object is reset
/// where the configuration says it is, and the two switches are real behaviour rather than a knob that is
/// accepted and then ignored. Three things make that claim checkable — the default timing is unchanged for
/// a pool that never asks, moving the reset to the borrow side leaves the object dirty across the return,
/// and the one mode that cannot keep a borrow-side reset (lean) pins the timing back to the return side
/// rather than dropping the reset altogether.
/// </summary>
public class ResetTimingTests
{
    /// <summary>
    /// A pooled object that remembers how often the pool reset it and whether its borrower left anything
    /// in it. <see cref="ResetCount"/> is the observable the timing assertions read: it distinguishes "reset
    /// on the way in" from "reset on the way out" without needing to watch the object at the moment it moves.
    /// </summary>
    private sealed class ResettableBuffer : IHayateResettable
    {
        public int ResetCount;
        public int Payload;

        public void Reset()
        {
            ResetCount++;
            Payload = 0;
        }
    }

    /// <summary>
    /// A policy that resets in its own <c>OnRelease</c>, standing in for any custom policy. The reset
    /// switches govern the pool-driven <see cref="IHayateResettable"/> reset only, so this policy must keep
    /// resetting whatever the options say — that is the jurisdiction boundary, and the case below asserts it
    /// rather than assuming it.
    /// </summary>
    private sealed class SelfResettingPolicy : IHayateObjectPolicy<ResettableBuffer>
    {
        public ResettableBuffer Create() => new ResettableBuffer();

        public bool OnRelease(ResettableBuffer item)
        {
            item.Reset();
            return true;
        }

        public bool Validate(ResettableBuffer item) => true;
        public void OnAcquire(ResettableBuffer item) { }
        public void OnPassivate(ResettableBuffer item) { }
        public void OnDestroy(ResettableBuffer item) { }
    }

    /// <summary>
    /// A small single-shard pool that creates on demand and never retires anything, so the object a case
    /// borrows is the object it gets back: one shard because a per-shard capacity of
    /// <c>MaxPoolSize / ShardCount</c> would floor to zero with the default four, no auto-scaling so the
    /// ceiling stays put, no eviction so nothing is destroyed between the two halves of a cycle, and
    /// <c>CreateOnDemand</c> so an empty pool hands out a new object instead of making the case wait out a
    /// timeout for a return that will never come.
    /// </summary>
    private static HayatePoolBuilder<ResettableBuffer> Builder()
    {
        return new HayatePoolBuilder<ResettableBuffer>()
            .WithShardCount(1)
            .WithEnableAutoScaling(false)
            .WithEnableEviction(false)
            .WithMinSize(0)
            .WithMaxSize(4)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand);
    }

    /// <summary>
    /// Borrows, dirties and returns one object, then borrows again — the full cycle both timings are about.
    /// The second borrow is asserted to be the same instance, so the reset counts a case reads afterwards
    /// belong to one object's round trip and not to two different objects.
    /// </summary>
    private static ResettableBuffer RunOneCycle(IHayateObjectPool<ResettableBuffer> pool)
    {
        var buffer = pool.Acquire();
        buffer.Payload = 7;
        pool.Release(buffer);

        var again = pool.Acquire();
        Assert.Same(buffer, again);
        return again;
    }

    // ── Defaults ─────────────────────────────────────────────

    [Fact]
    public void Defaults_ShouldBeTheHistoricalTiming()
    {
        var options = new HayatePoolOptions();

        Assert.True(options.ResetOnReturn);
        Assert.False(options.ResetOnBorrow);
    }

    [Fact(Timeout = 30_000)]
    public void Default_ShouldResetOnReturnAndNotOnBorrow()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        // The configuration every pool before 3.1 had: the object is already clean when it comes back out,
        // and the borrow path does no reset work at all.
        using var pool = Builder().Build();

        var first = pool.Acquire();
        Assert.Equal(0, first.ResetCount);

        first.Payload = 7;
        pool.Release(first);

        Assert.Equal(1, first.ResetCount);
        Assert.Equal(0, first.Payload);

        var second = pool.Acquire();
        Assert.Same(first, second);
        Assert.Equal(1, second.ResetCount);
    }

    // ── The switch itself ────────────────────────────────────

    [Fact(Timeout = 30_000)]
    public void ResetOnBorrow_ShouldLeaveTheObjectDirtyAcrossTheReturn()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        // The discriminating case: with the reset moved to the borrow side, a returned object still holds
        // what its borrower left in it. A switch that was accepted and then ignored would show a clean
        // object here.
        using var pool = Builder()
            .WithResetOnReturn(false)
            .WithResetOnBorrow(true)
            .Build();

        var first = pool.Acquire();
        // The pool runs its acquire hook on a create-on-miss hand-out too, so a brand-new object is reset
        // once before its first borrower sees it.
        Assert.Equal(1, first.ResetCount);

        first.Payload = 7;
        pool.Release(first);

        Assert.Equal(1, first.ResetCount);
        Assert.Equal(7, first.Payload);

        var second = pool.Acquire();
        Assert.Same(first, second);
        Assert.Equal(2, second.ResetCount);
        Assert.Equal(0, second.Payload);
    }

    [Fact(Timeout = 30_000)]
    public void BothSwitches_ShouldResetOnBothSidesOfTheCycle()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        // Legal on purpose: a borrower that may hand back an object it has already dirtied, and a next
        // borrower that must not see it either, is a real configuration — the price is two resets a cycle.
        using var pool = Builder()
            .WithResetOnReturn(true)
            .WithResetOnBorrow(true)
            .Build();

        var buffer = RunOneCycle(pool);

        // One on the create-on-miss hand-out, one on the return, one on the reuse borrow.
        Assert.Equal(3, buffer.ResetCount);
        Assert.Equal(0, buffer.Payload);
    }

    [Fact(Timeout = 30_000)]
    public void NeitherSwitch_ShouldNeverReset()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        // Both off is not rejected either: it is what a pool of stateless payloads wants, and the object
        // keeps the state its borrower left in it all the way round.
        using var pool = Builder()
            .WithResetOnReturn(false)
            .WithResetOnBorrow(false)
            .Build();

        var buffer = RunOneCycle(pool);

        Assert.Equal(0, buffer.ResetCount);
        Assert.Equal(7, buffer.Payload);
    }

    [Fact(Timeout = 30_000)]
    public void Switches_ShouldBeVisibleThroughGetOptions()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var pool = Builder()
            .WithResetOnReturn(false)
            .WithResetOnBorrow(true)
            .Build();

        var options = pool.GetOptions();

        Assert.False(options.ResetOnReturn);
        Assert.True(options.ResetOnBorrow);
    }

    // ── Jurisdiction boundary ────────────────────────────────

    [Fact(Timeout = 30_000)]
    public void CustomPolicy_ShouldKeepItsOwnResetWhateverTheSwitchesSay()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        // A custom IHayateObjectPolicy<T> owns OnRelease and OnAcquire. The switches reach the pool-driven
        // IHayateResettable reset only, so this policy's own reset survives ResetOnReturn = false — and the
        // object is reset exactly once, not once by the policy and once by the pool.
        using var pool = Builder()
            .WithPolicy(new SelfResettingPolicy())
            .WithResetOnReturn(false)
            .WithResetOnBorrow(true)
            .Build();

        var buffer = RunOneCycle(pool);

        Assert.Equal(1, buffer.ResetCount);
        Assert.Equal(0, buffer.Payload);
    }

    [Fact]
    public void DefaultPolicy_ParameterlessConstructor_ShouldKeepTheHistoricalTiming()
    {
        // The constructor every standalone user reaches, and the one HayateObjectPolicies.Default<T>()
        // hands out: neither may move, or adopting the switch would change pools that never asked for it.
        foreach (var policy in new IHayateObjectPolicy<ResettableBuffer>[]
                 {
                     new DefaultHayateObjectPolicy<ResettableBuffer>(),
                     HayateObjectPolicies.Default<ResettableBuffer>()
                 })
        {
            var buffer = new ResettableBuffer { Payload = 5 };

            policy.OnAcquire(buffer);
            Assert.Equal(0, buffer.ResetCount);

            policy.OnRelease(buffer);
            Assert.Equal(1, buffer.ResetCount);
            Assert.Equal(0, buffer.Payload);
        }
    }

    [Fact]
    public void DefaultPolicy_TwoFlagConstructor_ShouldMoveTheReset()
    {
        var policy = new DefaultHayateObjectPolicy<ResettableBuffer>(resetOnReturn: false, resetOnBorrow: true);
        var buffer = new ResettableBuffer { Payload = 5 };

        Assert.True(policy.OnRelease(buffer));
        Assert.Equal(0, buffer.ResetCount);
        Assert.Equal(5, buffer.Payload);

        policy.OnAcquire(buffer);
        Assert.Equal(1, buffer.ResetCount);
        Assert.Equal(0, buffer.Payload);
    }

    // ── Lean: pinned to the return side, not dropped ─────────

    [Fact]
    public void Lean_ShouldPinTheResetToTheReturnSide()
    {
        // Lean keeps no borrow-side work beyond handing the value out, so a borrow-side reset is normalized
        // away. The point of pinning both halves is the caller below: one who had moved the reset to the
        // borrow side must land on the return side, not on "no reset at all".
        var normalized = new HayatePoolOptions
        {
            EnableLean = true,
            ResetOnReturn = false,
            ResetOnBorrow = true
        };
        normalized.ApplyFeatureSwitches();

        Assert.True(normalized.ResetOnReturn);
        Assert.False(normalized.ResetOnBorrow);

        var profiled = new HayatePoolOptions { ResetOnReturn = false, ResetOnBorrow = true }.UseLeanProfile();

        Assert.True(profiled.ResetOnReturn);
        Assert.False(profiled.ResetOnBorrow);
    }

    [Fact]
    public void NonLean_ShouldLeaveTheResetTimingAlone()
    {
        // The pinning above is lean-scoped. A general-engine pool that asks for the borrow-side reset keeps
        // it, which is what makes the switch a switch rather than a suggestion.
        var options = new HayatePoolOptions { ResetOnReturn = false, ResetOnBorrow = true };
        options.ApplyFeatureSwitches();

        Assert.False(options.ResetOnReturn);
        Assert.True(options.ResetOnBorrow);
    }

    [Fact(Timeout = 30_000)]
    public void Lean_ShouldResetOnReturnWhateverTheCallOrder()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        // Same normalized result from both orders: the mode wins at build time, not at each setter, so
        // WithResetOnBorrow before WithLean is not a way to smuggle a borrow-side reset into a lean pool.
        foreach (var pool in new[]
                 {
                     new HayatePoolBuilder<ResettableBuffer>()
                         .WithLean().WithMinSize(0).WithMaxSize(4).WithResetOnBorrow(true).Build(),
                     new HayatePoolBuilder<ResettableBuffer>()
                         .WithResetOnBorrow(true).WithLean().WithMinSize(0).WithMaxSize(4).Build()
                 })
        {
            using (pool)
            {
                Assert.True(pool.GetOptions().ResetOnReturn);
                Assert.False(pool.GetOptions().ResetOnBorrow);

                var buffer = pool.Acquire();
                Assert.Equal(0, buffer.ResetCount);

                buffer.Payload = 7;
                pool.Release(buffer);

                Assert.Equal(1, buffer.ResetCount);
                Assert.Equal(0, buffer.Payload);
            }
        }
    }

    // ── The second pool model ────────────────────────────────

    [Fact(Timeout = 30_000)]
    public void UnboundedPool_ShouldAlwaysResetOnReturn()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        // HayateUnboundedPool<T> takes no options object, so the switches have no route into it and its
        // timing is fixed. The defaults GetOptions() reports are a true statement about that behaviour.
        using var pool = new HayateUnboundedPool<ResettableBuffer>(4);

        var options = pool.GetOptions();
        Assert.True(options.ResetOnReturn);
        Assert.False(options.ResetOnBorrow);

        var buffer = pool.Acquire();
        Assert.Equal(0, buffer.ResetCount);

        buffer.Payload = 7;
        pool.Release(buffer);

        Assert.Equal(1, buffer.ResetCount);
        Assert.Equal(0, buffer.Payload);
        Assert.Same(buffer, pool.Acquire());
        Assert.Equal(1, buffer.ResetCount);
    }
}
