using System;
using System.Linq;
using System.Threading;
using DotNetCore.HayateOP.Policies;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// A3a — borrow-side lifetime rotation (<see cref="HayatePoolOptions.EnableLifetimeRotationOnBorrow"/>).
/// </summary>
/// <remarks>
/// Acceptance: an object that has outlived <see cref="HayatePoolOptions.MaxLifeTime"/> is replaced on the
/// next borrow instead of being handed out; the rotation can never surface as a rejection; it cannot spin;
/// it is decoupled from the validation and generational switches; and with the switch off — the default —
/// the borrow path behaves exactly as it did before the feature existed.
/// </remarks>
public class LifetimeRotationTests
{
    /// <summary>A pooled object that records its own disposal, so a rotation can be observed.</summary>
    private sealed class TrackedObject : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class PlainObject { }

    /// <summary>Counts the validation calls the pool makes, for the generational-decoupling checks.</summary>
    private sealed class CountingPolicy : IHayateObjectPolicy<PlainObject>
    {
        private int _validateCalls;

        public int ValidateCalls => Volatile.Read(ref _validateCalls);

        public PlainObject Create() => new PlainObject();

        public bool OnRelease(PlainObject item) => true;

        public bool Validate(PlainObject item)
        {
            Interlocked.Increment(ref _validateCalls);
            return true;
        }

        public void OnAcquire(PlainObject item) { }
        public void OnPassivate(PlainObject item) { }
        public void OnDestroy(PlainObject item) { }
    }

    private static IHayateObjectPool<TrackedObject> Build(TimeSpan lifeTime, bool rotation,
        HayatePoolRejectPolicy rejectPolicy = HayatePoolRejectPolicy.CreateNew, int maxSize = 1,
        bool metrics = false)
    {
        var builder = new HayatePoolBuilder<TrackedObject>()
            .WithMinSize(1)
            .WithMaxSize(maxSize)
            .WithEnableEviction(false)
            .WithEnableAutoScaling(false)
            .WithMaxLifeTime(lifeTime)
            .WithRejectPolicy(rejectPolicy)
            .WithEnableLifetimeRotationOnBorrow(rotation);

        // TotalCreated is only counted while metrics are enabled, so the tests that assert on it ask for them.
        if (metrics) builder = builder.WithEnableMetrics();

        return builder.Build();
    }

    // ── U1 ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Rotation_ReplacesAnObjectThatOutlivedMaxLifeTime()
    {
        // Metrics on so TotalCreated is populated — it is only counted while metrics are enabled.
        using var pool = Build(TimeSpan.FromMilliseconds(50), rotation: true, metrics: true);

        var first = pool.Acquire();
        Thread.Sleep(150);                  // held well past MaxLifeTime
        pool.Release(first);
        var createdBefore = pool.GetStats().TotalCreated;

        var second = pool.Acquire();

        Assert.NotSame(first, second);
        Assert.True(first.Disposed, "the expired object should have been destroyed, not handed back");
        Assert.True(pool.GetStats().TotalCreated > createdBefore, "a replacement should have been created");
        Assert.Equal(1, pool.GetStats().LifetimeRotatedCount);

        pool.Release(second);
    }

    // ── U2 ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BackgroundEviction_NeverTouchesABorrowedObjectEvenWhenItIsExpired()
    {
        using var pool = new HayatePoolBuilder<TrackedObject>()
            .WithMinSize(2)
            .WithMaxSize(4)
            .WithEnableEviction(true)
            .WithEvictionInterval(1000)         // the builder's floor
            .WithEnableAutoScaling(false)
            .WithMaxLifeTime(TimeSpan.FromMilliseconds(50))
            .WithEnableLifetimeRotationOnBorrow()
            .Build();

        var held = pool.Acquire();
        Thread.Sleep(2500);                 // several eviction runs, far past MaxLifeTime

        // The regression the rotation must not introduce: an expired object that is currently borrowed is
        // still nobody's business but its borrower's.
        Assert.False(held.Disposed);
        Assert.Equal(1, pool.TakeSnapshot().BorrowedCount);

        pool.Release(held);
    }

    // ── U3 ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Rotation_WithAnImmediatelyExpiringLifetime_ReturnsInsteadOfSpinning()
    {
        // A lifetime shorter than the time it takes to create an object must not turn a borrow into an
        // endless create/destroy cycle: at most one object is retired per borrow and the replacement is
        // handed out without being re-checked.
        using var pool = Build(TimeSpan.FromTicks(1), rotation: true, maxSize: 2);

        for (var i = 0; i < 5; i++)
        {
            var obj = pool.Acquire();
            pool.Release(obj);
        }

        Assert.True(pool.GetStats().LifetimeRotatedCount > 0,
            "the rotation should be observable in the counters");
    }

    // ── U4 ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Rotation_UnderAbort_DoesNotTurnALifetimeEventIntoARejection()
    {
        using var pool = Build(TimeSpan.FromMilliseconds(50), rotation: true,
            rejectPolicy: HayatePoolRejectPolicy.Abort);

        var first = pool.Acquire();
        Thread.Sleep(150);
        pool.Release(first);

        // Abort rejects a borrow when no object is available. Rotating the only object destroys it, so the
        // replacement has to be created through the same reservation path — otherwise a lifetime event
        // would be amplified into an InvalidOperationException.
        var second = pool.Acquire();

        Assert.NotNull(second);
        Assert.NotSame(first, second);
        Assert.Equal(1, pool.GetStats().LifetimeRotatedCount);

        pool.Release(second);
    }

    // ── U5 ──────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rotation_DoesNotChangeGenerationalBehaviour(bool generationOptimization)
    {
        var without = RunBorrows(generationOptimization, rotation: false);
        var with = RunBorrows(generationOptimization, rotation: true);

        // The rotation is gated on its own switch only: it is not nested inside the generational branch and
        // not tied to the validation switch, so enabling it leaves both the validation count and the
        // generational promotion untouched.
        Assert.Equal(without.ValidateCalls, with.ValidateCalls);
        Assert.Equal(without.Promoted, with.Promoted);
        Assert.Equal(0, with.Rotated);

        // …and the generational optimization itself still behaves as documented, so the comparison above
        // is not vacuous.
        if (generationOptimization)
        {
            Assert.True(with.Promoted > 0);
            Assert.True(with.ValidateCalls < with.Borrows);
        }
        else
        {
            Assert.Equal(0, with.Promoted);
            Assert.Equal(with.Borrows, with.ValidateCalls);
        }
    }

    private static (int ValidateCalls, int Promoted, long Rotated, int Borrows) RunBorrows(
        bool generationOptimization, bool rotation)
    {
        const int borrows = 6;
        var policy = new CountingPolicy();

        using var pool = new HayatePoolBuilder<PlainObject>()
            .WithMinSize(1)
            .WithMaxSize(1)
            .WithEnableEviction(false)
            .WithEnableSharding(false)          // one shard, so the single object is the one borrowed
            .WithEnableAutoScaling(false)
            .WithEnableValidation(true)
            .WithValidateOnBorrow(true)
            .WithEnableGenerationOptimization(generationOptimization)
            .WithGenerationThreshold(1000)      // the builder's floor
            .WithOldGenerationValidationInterval(2)
            .WithMaxLifeTime(TimeSpan.FromMinutes(10))   // never expires during this test
            .WithEnableLifetimeRotationOnBorrow(rotation)
            .WithPolicy(policy)
            .Build();

        // Let the pre-warmed object age past GenerationThreshold, so the first borrow promotes it.
        Thread.Sleep(1200);

        for (var i = 0; i < borrows; i++)
        {
            var obj = pool.Acquire();
            pool.Release(obj);
        }

        var promoted = pool.TakeSnapshot().ObjectDetails.Count(d => d.Generation == 1);
        return (policy.ValidateCalls, promoted, pool.GetStats().LifetimeRotatedCount, borrows);
    }

    // ── U6 ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Rotation_IsOffByDefault_SoAnExpiredObjectIsStillHandedBack()
    {
        using var pool = Build(TimeSpan.FromMilliseconds(50), rotation: false);

        var first = pool.Acquire();
        Thread.Sleep(150);
        pool.Release(first);

        var second = pool.Acquire();

        // The pre-2.9 behaviour, unchanged: MaxLifeTime bounds idle objects only.
        Assert.Same(first, second);
        Assert.False(first.Disposed);
        Assert.Equal(0, pool.GetStats().LifetimeRotatedCount);

        pool.Release(second);
    }

    [Fact]
    public void EnableLifetimeRotationOnBorrow_DefaultsToFalse()
    {
        Assert.False(new HayatePoolOptions().EnableLifetimeRotationOnBorrow);

        using var pool = new HayatePoolBuilder<PlainObject>().Build();
        Assert.False(pool.GetOptions().EnableLifetimeRotationOnBorrow);
    }

    // ── U7 ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LeanWithRotation_IsRejectedRatherThanSilentlyIgnored()
    {
        var combined = new HayatePoolOptions
        {
            EnableLean = true,
            EnableLifetimeRotationOnBorrow = true
        };

        // The lean fast path keeps no per-object timestamps, so it cannot evaluate an age. Silently
        // switching the rotation off would leave an owner believing objects are rotated when they are not.
        Assert.False(combined.IsValid());

        // Rejected in either builder order — unlike normalizing it away, which would make call order matter.
        Assert.Throws<InvalidOperationException>(() => new HayatePoolBuilder<PlainObject>()
            .WithLean()
            .WithEnableLifetimeRotationOnBorrow()
            .Build());

        Assert.Throws<InvalidOperationException>(() => new HayatePoolBuilder<PlainObject>()
            .WithEnableLifetimeRotationOnBorrow()
            .WithLean()
            .Build());

        // Each of the two on its own is still legal.
        Assert.True(new HayatePoolOptions { EnableLean = true }.IsValid());
        Assert.True(new HayatePoolOptions { EnableLifetimeRotationOnBorrow = true }.IsValid());
    }

    // ── U8 ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ExpiryVerdict_AgreesBetweenManualEvictionAndBorrowRotation()
    {
        // Not yet expired: neither entry treats the object as expired.
        using (var pool = Build(TimeSpan.FromMinutes(10), rotation: true))
        {
            var first = pool.Acquire();
            pool.Release(first);

            Assert.Equal(0, pool.Evict(HayateEvictReason.Expired));

            var second = pool.Acquire();
            Assert.Same(first, second);
            Assert.Equal(0, pool.GetStats().LifetimeRotatedCount);

            pool.Release(second);
        }

        // Past the lifetime the manual eviction removes it…
        using (var pool = Build(TimeSpan.FromMilliseconds(50), rotation: true))
        {
            Thread.Sleep(150);
            Assert.Equal(1, pool.Evict(HayateEvictReason.Expired));
        }

        // …and the borrow path rotates it, from the same tick bound.
        using (var pool = Build(TimeSpan.FromMilliseconds(50), rotation: true))
        {
            Thread.Sleep(150);

            var obj = pool.Acquire();

            Assert.Equal(1, pool.GetStats().LifetimeRotatedCount);
            pool.Release(obj);
        }
    }
}
