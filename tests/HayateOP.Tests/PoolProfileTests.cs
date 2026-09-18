using System;
using System.Reflection;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// One-call configuration profiles -- the lean profile collapses the pool to the wrapper-free fast path
/// with every bookkeeping feature off, the full profile switches every optional feature on.
/// Acceptance: each profile lands exactly the configuration the caller would have written by hand; the
/// profiles are order-deterministic against each other; sizing, timeouts and the reject policy are left
/// untouched by both; and a profile can still be refined with an ordinary feature call afterwards.
/// </summary>
public class PoolProfileTests
{
    private sealed class TestObject { }

    /// <summary>
    /// Compares two option objects field by field through reflection, so the equivalence claim covers every
    /// option on the type rather than a hand-picked subset that could silently fall behind.
    /// </summary>
    private static void AssertOptionsEquivalent(HayatePoolOptions expected, HayatePoolOptions actual)
    {
        foreach (var property in typeof(HayatePoolOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead) continue;
            Assert.Equal(property.GetValue(expected), property.GetValue(actual));
        }
    }

    [Fact(Timeout = 30_000)]
    public void LeanProfile_ShouldLandTheExplicitConfiguration()
    {
        // The same options written out by hand.
        var handWritten = new HayatePoolOptions
        {
            EnableLean = true,
            EnableSharding = false,
            EnableAutoScaling = false,
            EnableValidation = false,
            ValidateOnBorrow = false,
            ValidateOnReturn = false,
            ValidateWhileIdle = false,
            EnableEviction = false,
            EnableGenerationOptimization = false,
            EnableLeakDetection = false,
            EnableDiagnostics = false,
            EnableMetrics = false,
            EnableAllocationTracking = false,
            WarnAtRatio = 0,
            CriticalAtRatio = 0,
            ShardAffinityMode = HayateShardAffinityMode.None
        };

        var profiled = new HayatePoolOptions().UseLeanProfile();

        AssertOptionsEquivalent(handWritten, profiled);
    }

    [Fact(Timeout = 30_000)]
    public void LeanProfile_ShouldMatchTheExplicitBuilderChain()
    {
        using var profiled = new HayatePoolBuilder<TestObject>().WithLeanProfile().Build();
        using var explicitChain = new HayatePoolBuilder<TestObject>()
            .WithLean()
            .WithEnableSharding(false)
            .WithEnableAutoScaling(false)
            .WithEnableValidation(false)
            .WithEnableEviction(false)
            .WithEnableGenerationOptimization(false)
            .WithEnableLeakDetection(false)
            .WithEnableMetrics(false)
            .WithEnableAllocationTracking(false)
            .Build();

        AssertOptionsEquivalent(explicitChain.GetOptions(), profiled.GetOptions());
    }

    [Fact(Timeout = 30_000)]
    public void LeanProfile_ShouldAlsoMatchTheChainInTheOppositeCallOrder()
    {
        // Lean is a mode, not a knob: feature toggles enabled before the profile are normalized away by the
        // profile itself, exactly as ApplyFeatureSwitches would do for EnableLean on its own.
        using var profiled = new HayatePoolBuilder<TestObject>().WithLeanProfile().Build();
        using var togglesFirst = new HayatePoolBuilder<TestObject>()
            .WithEnableSharding(true)
            .WithEnableAutoScaling(true)
            .WithEnableValidation(true)
            .WithEnableEviction(true)
            .WithEnableLeakDetection(true)
            .WithEnableMetrics(true)
            .WithCapacityAlarm(0.8, 0.95)
            .WithLeanProfile()
            .Build();

        AssertOptionsEquivalent(profiled.GetOptions(), togglesFirst.GetOptions());
    }

    [Fact(Timeout = 30_000)]
    public void FullProfile_ShouldTurnEveryFeatureSwitchOn()
    {
        var options = new HayatePoolOptions().UseFullProfile();

        Assert.False(options.EnableLean);
        Assert.True(options.EnableSharding);
        Assert.True(options.EnableAutoScaling);
        Assert.True(options.EnableValidation);
        Assert.True(options.EnableEviction);
        Assert.True(options.EnableGenerationOptimization);
        Assert.True(options.EnableLeakDetection);
        Assert.True(options.EnableDiagnostics);
        Assert.True(options.EnableMetrics);
        Assert.True(options.EnableAllocationTracking);
    }

    [Fact(Timeout = 30_000)]
    public void Profiles_ShouldLeaveSizingTimeoutAndRejectPolicyAlone()
    {
        var options = new HayatePoolOptions
        {
            MinPoolSize = 7,
            MaxPoolSize = 123,
            DefaultAcquireTimeout = TimeSpan.FromSeconds(2),
            RejectPolicy = HayatePoolRejectPolicy.CreateOnDemand
        };

        options.UseLeanProfile();

        Assert.Equal(7, options.MinPoolSize);
        Assert.Equal(123, options.MaxPoolSize);
        Assert.Equal(TimeSpan.FromSeconds(2), options.DefaultAcquireTimeout);
        Assert.Equal(HayatePoolRejectPolicy.CreateOnDemand, options.RejectPolicy);

        options.UseFullProfile();

        Assert.Equal(7, options.MinPoolSize);
        Assert.Equal(123, options.MaxPoolSize);
        Assert.Equal(TimeSpan.FromSeconds(2), options.DefaultAcquireTimeout);
        Assert.Equal(HayatePoolRejectPolicy.CreateOnDemand, options.RejectPolicy);
    }

    [Fact(Timeout = 30_000)]
    public void Profiles_ShouldBeOrderDeterministicAgainstEachOther()
    {
        // Whichever profile is applied last wins, in both directions.
        var fullLast = new HayatePoolOptions().UseLeanProfile().UseFullProfile();
        Assert.False(fullLast.EnableLean);
        Assert.True(fullLast.EnableSharding);
        Assert.True(fullLast.EnableDiagnostics);
        Assert.True(fullLast.EnableMetrics);

        var leanLast = new HayatePoolOptions().UseFullProfile().UseLeanProfile();
        Assert.True(leanLast.EnableLean);
        Assert.False(leanLast.EnableSharding);
        Assert.False(leanLast.EnableDiagnostics);
        Assert.False(leanLast.EnableMetrics);
    }

    [Fact(Timeout = 30_000)]
    public void FullProfile_ShouldBeRefinableByALaterFeatureCall()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithFullProfile()
            .WithEnableAllocationTracking(false)
            .WithEnableMetrics(false)
            .Build();

        var options = pool.GetOptions();
        Assert.True(options.EnableValidation);
        Assert.True(options.EnableEviction);
        Assert.False(options.EnableAllocationTracking);
        Assert.False(options.EnableMetrics);
    }

    [Fact(Timeout = 30_000)]
    public void LeanProfile_ShouldBehaveAsTheLeanFastPath()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithLeanProfile()
            .WithMinSize(0)
            .WithMaxSize(8)
            .Build();

        var obj = pool.Acquire();
        Assert.NotNull(obj);
        pool.Release(obj);

        // The lean path keeps no cumulative counters, so the round-trip is observable only through the
        // instantaneous pooled count.
        var stats = pool.GetStats();
        Assert.Equal(1, stats.PooledCount);
        Assert.Equal(0, stats.TotalAcquired);
        Assert.Equal(0, stats.TotalCreated);

        // Per-object eviction state does not exist on the fast path; it fails loudly instead of returning 0.
        Assert.Throws<InvalidOperationException>(() => { pool.Evict(HayateEvictReason.Idle); });
    }

    [Fact(Timeout = 30_000)]
    public void FullProfile_Pool_ShouldRecordStatistics()
    {
        using var pool = new HayatePoolBuilder<TestObject>().WithFullProfile().WithMinSize(0).Build();

        var obj = pool.Acquire();
        pool.Release(obj);

        var stats = pool.GetStats();
        Assert.Equal(1, stats.TotalAcquired);
        Assert.Equal(1, stats.TotalReleased);
        Assert.Equal(1, stats.TotalCreated);
        Assert.Equal(1, stats.PooledCount);
    }
}
