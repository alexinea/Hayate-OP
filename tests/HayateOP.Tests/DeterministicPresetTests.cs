using System;
using System.Threading;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// Acceptance for the deterministic preset (O-G): a pool built for hosts where nothing may run without
/// the caller asking for it — AOT / IL2CPP runtimes and other deterministic environments. The preset is
/// the lean fast path plus the two switches Lean leaves open that could still move work off the calling
/// thread, both closed, so the built pool touches nothing except on an explicit Acquire / Release: no
/// maintenance timer, no event subscription, no background task.
/// </summary>
public class DeterministicPresetTests
{
    private sealed class TestObject { }

    private static HayatePoolOptions DeterministicOptions() => HayatePoolPresets.Create(HayatePoolPreset.Deterministic);

    [Fact(Timeout = 30_000)]
    public void Deterministic_IsLeanPlusEveryBackgroundPathClosed()
    {
        var options = DeterministicOptions();

        // The lean fast path itself: no background pass, no per-object bookkeeping.
        Assert.True(options.EnableLean);
        Assert.False(options.EnableEviction);
        Assert.False(options.EnableAutoScaling);
        Assert.False(options.EnableValidation);
        Assert.False(options.ValidateWhileIdle);
        Assert.False(options.RemoveAbandonedOnMaintenance);

        // The two switches Lean leaves open that a deterministic host cannot tolerate.
        Assert.False(options.WaitForWarmup);              // no background pre-warm task
        Assert.False(options.EnableAutoDisposeWithSystem); // no process-exit subscription
    }

    [Fact(Timeout = 30_000)]
    public void Deterministic_BuiltPool_NormalizesToTheSameGuarantees()
    {
        // The shared maintenance timer starts only when one of its four concerns is enabled, so the
        // normalized configuration of the built pool is the observable no-timer condition.
        using var pool = new HayatePoolBuilder<TestObject>().WithPreset(HayatePoolPreset.Deterministic).Build();

        var options = pool.GetOptions();
        Assert.True(options.EnableLean);
        Assert.False(options.EnableEviction);
        Assert.False(options.EnableAutoScaling);
        Assert.False(options.EnableValidation);
        Assert.False(options.RemoveAbandonedOnMaintenance);
        Assert.False(options.WaitForWarmup);
        Assert.False(options.EnableAutoDisposeWithSystem);
    }

    [Fact(Timeout = 30_000)]
    public void Deterministic_PoolServesBorrowReturnOnTheFastPath()
    {
        using var pool = new HayatePoolBuilder<TestObject>().WithPreset(HayatePoolPreset.Deterministic).Build();

        var first = pool.Acquire();
        pool.Release(first);

        // The lean fast lane hands the same instance back: the preset is a working pool, not a stub.
        var second = pool.Acquire();
        Assert.Same(first, second);
        pool.Release(second);
    }

    [Fact(Timeout = 30_000)]
    public void Deterministic_IsRefinableByALaterFeatureCall()
    {
        // Composition works both ways: sizing stays the caller's, and re-enabling a background concern
        // after the preset reintroduces that work by choice rather than by surprise.
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPreset(HayatePoolPreset.Deterministic)
            .WithMaxSize(32)
            .Build();

        var options = pool.GetOptions();
        Assert.Equal(32, options.MaxPoolSize);
        Assert.True(options.EnableLean);
        Assert.False(options.WaitForWarmup);
    }
}
