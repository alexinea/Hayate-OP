using System;
using System.Collections.Generic;
using System.Reflection;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// The named configuration presets. A preset names one of the shapes the option surface can take, so the
/// common workloads cost one word instead of forty settings — and the point of these cases is that the
/// word costs the caller nothing in control.
/// Acceptance: every preset lands exactly the configuration the caller could have written by hand; a
/// preset owns its documented field set and nothing else, so it composes; every preset is still refinable
/// by a later feature call; the three entry points agree; and an unrecognised preset is rejected instead
/// of silently leaving a configuration that would claim to be it.
/// </summary>
public class PoolPresetTests
{
    private sealed class TestObject { }

    private sealed class ValidatableItem : IHayateValidatable
    {
        public bool Healthy { get; set; } = true;

        public bool IsValid() => Healthy;
    }

    public static IEnumerable<object[]> AllPresets()
    {
        foreach (var preset in Enum.GetValues(typeof(HayatePoolPreset)))
        {
            yield return new object[] { preset };
        }
    }

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

    /// <summary>
    /// Fills every writable option with a value other than the shipped default, so that "the preset did not
    /// touch this" is a claim with a witness behind it rather than a comparison of defaults with defaults.
    /// </summary>
    private static void PrimeWithNonDefaultValues(HayatePoolOptions options)
    {
        foreach (var property in typeof(HayatePoolOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanWrite) continue;
            property.SetValue(options, NonDefaultValue(property));
        }
    }

    private static object? NonDefaultValue(PropertyInfo property)
    {
        var current = property.GetValue(new HayatePoolOptions());
        var type = property.PropertyType;

        if (type == typeof(bool)) return !(bool)current!;
        if (type == typeof(int)) return (int)current! + 1;
        if (type == typeof(double)) return (double)current! + 0.25;
        if (type == typeof(TimeSpan)) return ((TimeSpan)current!) + TimeSpan.FromSeconds(1);
        if (type == typeof(HayateCircuitBreakerOptions)) return new HayateCircuitBreakerOptions { FailureThreshold = 7 };
        if (type == typeof(Func<int>)) return new Func<int>(() => 0);
        if (type == typeof(Action<HayatePoolCapacityAlarmEventArgs>))
        {
            return new Action<HayatePoolCapacityAlarmEventArgs>(_ => { });
        }
        if (type == typeof(Action<HayatePoolAvailabilityEventArgs>))
        {
            return new Action<HayatePoolAvailabilityEventArgs>(_ => { });
        }

        if (type.IsEnum)
        {
            foreach (var value in Enum.GetValues(type))
            {
                if (!Equals(value, current)) return value;
            }

            throw new InvalidOperationException($"Option {property.Name}: enum {type.Name} has no second member to switch to");
        }

        throw new InvalidOperationException(
            $"Option {property.Name} has type {type.Name}, which the preset probe does not handle yet; " +
            "add it to NonDefaultValue so the ownership contract stays checkable.");
    }

    private static void AssertUntouched(PropertyInfo property, object? primed, object? actual)
    {
        // A class-valued option is expected back by identity: "left alone" means the preset never assigned
        // it, not that it assigned an equal copy.
        if (property.PropertyType.IsValueType || property.PropertyType == typeof(string))
        {
            Assert.Equal(primed, actual);
        }
        else
        {
            Assert.Same(primed, actual);
        }
    }

    /// <summary>
    /// The field set each preset documents as its own. Kept as names rather than as values so the check
    /// fails when a preset quietly starts touching an option that is not part of its identity — which is
    /// the failure that would make composition unsafe for callers.
    /// </summary>
    private static string[] OwnedProperties(HayatePoolPreset preset)
    {
        switch (preset)
        {
            case HayatePoolPreset.Default:
                // The one preset that owns everything: "the shipped behaviour" is the whole surface.
                var all = new List<string>();
                foreach (var property in typeof(HayatePoolOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (property.CanWrite) all.Add(property.Name);
                }

                return all.ToArray();

            case HayatePoolPreset.Lean:
                return new[]
                {
                    "EnableLean", "EnableSharding", "EnableAutoScaling",
                    "EnableValidation", "ValidateOnBorrow", "ValidateOnReturn", "ValidateWhileIdle",
                    "EnableEviction", "EnableGenerationOptimization", "EnableLeakDetection",
                    "RemoveAbandonedOnBorrow", "RemoveAbandonedOnMaintenance",
                    "EnableDiagnostics", "EnableMetrics", "EnableAllocationTracking",
                    "WarnAtRatio", "CriticalAtRatio", "ShardAffinityMode"
                };

            case HayatePoolPreset.Full:
                return new[]
                {
                    "EnableLean", "EnableSharding", "EnableAutoScaling",
                    "EnableValidation", "EnableEviction", "EnableGenerationOptimization",
                    "EnableLeakDetection", "EnableDiagnostics", "EnableMetrics",
                    "EnableAllocationTracking"
                };

            case HayatePoolPreset.HighThroughput:
                return new[]
                {
                    "EnableLean", "EnableSharding",
                    "EnableAutoScaling", "ScalingIntervalMs", "ScaleUpThreshold", "ScaleUpStep",
                    "ScaleUpCooldownSeconds", "ScaleDownThreshold", "ScaleDownStep", "ScaleDownCooldownSeconds",
                    "EnableEviction", "MaxLifeTime", "MaxIdleTime", "SoftMinEvictableIdleTime",
                    "EnableGenerationOptimization",
                    "EnableValidation", "ValidateOnBorrow", "ValidateOnReturn", "ValidateWhileIdle",
                    "EnableLeakDetection", "EnableDiagnostics", "EnableMetrics", "EnableAllocationTracking"
                };

            case HayatePoolPreset.LowLatency:
                return new[]
                {
                    "EnableLean", "EnableSharding", "EnableAutoScaling",
                    "EnableValidation", "ValidateOnBorrow", "ValidateOnReturn", "ValidateWhileIdle",
                    "EnableEviction", "EnableGenerationOptimization", "EnableLeakDetection",
                    "EnableDiagnostics", "EnableMetrics", "EnableAllocationTracking",
                    "WaitForWarmup"
                };

            case HayatePoolPreset.MemoryConstrained:
                return new[]
                {
                    "EnableLean", "EnableSharding", "MinPoolSize", "RejectPolicy", "EnableAutoScaling",
                    "EnableValidation", "ValidateOnBorrow", "ValidateOnReturn", "ValidateWhileIdle",
                    "EnableEviction", "MaxLifeTime", "MaxIdleTime", "SoftMinEvictableIdleTime",
                    "EvictionIntervalMs", "NumTestsPerEvictionRun",
                    "EnableGenerationOptimization", "EnableLeakDetection",
                    "EnableDiagnostics", "EnableMetrics", "EnableAllocationTracking"
                };

            case HayatePoolPreset.ConnectionPool:
                return new[]
                {
                    "EnableLean", "EnableSharding", "EnableAutoScaling",
                    "EnableValidation", "ValidateOnBorrow", "ValidateOnReturn", "ValidateWhileIdle",
                    "ValidateIntervalMs",
                    "EnableEviction", "MaxLifeTime", "MaxIdleTime", "SoftMinEvictableIdleTime",
                    "EvictionIntervalMs", "NumTestsPerEvictionRun",
                    "EnableGenerationOptimization",
                    "EnableLeakDetection", "LeakDetectionThreshold",
                    "RemoveAbandonedOnBorrow", "RemoveAbandonedOnMaintenance", "RemoveAbandonedTimeout",
                    "LogAbandoned", "RemoveAbandonedIntervalMs",
                    "EnableCircuitBreaker",
                    "EnableDiagnostics", "EnableMetrics", "EnableAllocationTracking",
                    "WaitForWarmup"
                };

            case HayatePoolPreset.BatchProcessing:
                return new[]
                {
                    "EnableLean", "EnableSharding",
                    "EnableAutoScaling", "ScalingIntervalMs", "ScaleUpThreshold", "ScaleUpStep",
                    "ScaleUpCooldownSeconds", "ScaleDownThreshold", "ScaleDownStep", "ScaleDownCooldownSeconds",
                    "EnableEviction", "MaxLifeTime", "MaxIdleTime", "SoftMinEvictableIdleTime",
                    "EvictionIntervalMs", "NumTestsPerEvictionRun",
                    "EnableValidation", "ValidateOnBorrow", "ValidateOnReturn", "ValidateWhileIdle",
                    "EnableGenerationOptimization",
                    "EnableLeakDetection", "LeakDetectionThreshold",
                    "EnableDiagnostics", "EnableMetrics", "EnableAllocationTracking"
                };

            case HayatePoolPreset.Deterministic:
                return new[]
                {
                    "EnableLean", "EnableSharding", "EnableAutoScaling",
                    "EnableValidation", "ValidateOnBorrow", "ValidateOnReturn", "ValidateWhileIdle",
                    "EnableEviction", "EnableGenerationOptimization", "EnableLeakDetection",
                    "RemoveAbandonedOnBorrow", "RemoveAbandonedOnMaintenance",
                    "EnableDiagnostics", "EnableMetrics", "EnableAllocationTracking",
                    "WarnAtRatio", "CriticalAtRatio", "ShardAffinityMode",
                    "WaitForWarmup", "EnableAutoDisposeWithSystem"
                };

            default:
                throw new ArgumentOutOfRangeException(nameof(preset), preset, "No ownership contract declared for this preset");
        }
    }

    /// <summary>
    /// The configuration each preset promises, written out by hand. Default, Lean and Full are expressed
    /// through the profile calls they reuse, so this case pins the five presets that carry new logic.
    /// </summary>
    private static HayatePoolOptions HandWritten(HayatePoolPreset preset)
    {
        switch (preset)
        {
            case HayatePoolPreset.Default:
                return new HayatePoolOptions();

            case HayatePoolPreset.Lean:
                return new HayatePoolOptions().UseLeanProfile();

            case HayatePoolPreset.Full:
                return new HayatePoolOptions().UseFullProfile();

            case HayatePoolPreset.HighThroughput:
                return new HayatePoolOptions
                {
                    EnableLean = false,
                    EnableSharding = true,
                    EnableAutoScaling = true,
                    ScalingIntervalMs = 2000,
                    ScaleUpThreshold = 0.7,
                    ScaleUpStep = 8,
                    ScaleUpCooldownSeconds = 1,
                    ScaleDownThreshold = 0.2,
                    ScaleDownStep = 5,
                    ScaleDownCooldownSeconds = 30,
                    EnableEviction = true,
                    MaxLifeTime = TimeSpan.FromMinutes(30),
                    MaxIdleTime = TimeSpan.FromMinutes(15),
                    SoftMinEvictableIdleTime = TimeSpan.FromMinutes(5),
                    EnableGenerationOptimization = true,
                    EnableValidation = true,
                    ValidateOnBorrow = false,
                    ValidateOnReturn = false,
                    ValidateWhileIdle = false,
                    EnableLeakDetection = false,
                    EnableDiagnostics = false,
                    EnableMetrics = false,
                    EnableAllocationTracking = false
                };

            case HayatePoolPreset.LowLatency:
                return new HayatePoolOptions
                {
                    EnableLean = false,
                    EnableSharding = true,
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
                    WaitForWarmup = true
                };

            case HayatePoolPreset.MemoryConstrained:
                return new HayatePoolOptions
                {
                    EnableLean = false,
                    EnableSharding = false,
                    MinPoolSize = 0,
                    RejectPolicy = HayatePoolRejectPolicy.CreateOnDemand,
                    EnableAutoScaling = false,
                    EnableValidation = false,
                    ValidateOnBorrow = false,
                    ValidateOnReturn = false,
                    ValidateWhileIdle = false,
                    EnableEviction = true,
                    MaxLifeTime = TimeSpan.FromMinutes(10),
                    MaxIdleTime = TimeSpan.FromMinutes(1),
                    SoftMinEvictableIdleTime = TimeSpan.FromSeconds(30),
                    EvictionIntervalMs = 10000,
                    NumTestsPerEvictionRun = 20,
                    EnableGenerationOptimization = false,
                    EnableLeakDetection = true,
                    EnableDiagnostics = false,
                    EnableMetrics = false,
                    EnableAllocationTracking = false
                };

            case HayatePoolPreset.ConnectionPool:
                return new HayatePoolOptions
                {
                    EnableLean = false,
                    EnableSharding = true,
                    EnableAutoScaling = false,
                    EnableValidation = true,
                    ValidateOnBorrow = true,
                    ValidateOnReturn = false,
                    ValidateWhileIdle = true,
                    ValidateIntervalMs = 30000,
                    EnableEviction = true,
                    MaxLifeTime = TimeSpan.FromMinutes(30),
                    MaxIdleTime = TimeSpan.FromMinutes(5),
                    SoftMinEvictableIdleTime = TimeSpan.FromMinutes(2),
                    EvictionIntervalMs = 30000,
                    NumTestsPerEvictionRun = 10,
                    EnableGenerationOptimization = true,
                    EnableLeakDetection = true,
                    LeakDetectionThreshold = TimeSpan.FromSeconds(30),
                    RemoveAbandonedOnBorrow = true,
                    RemoveAbandonedOnMaintenance = true,
                    RemoveAbandonedTimeout = TimeSpan.FromSeconds(60),
                    LogAbandoned = true,
                    RemoveAbandonedIntervalMs = 30000,
                    EnableCircuitBreaker = true,
                    EnableDiagnostics = true,
                    EnableMetrics = true,
                    EnableAllocationTracking = false,
                    WaitForWarmup = true
                };

            case HayatePoolPreset.BatchProcessing:
                return new HayatePoolOptions
                {
                    EnableLean = false,
                    EnableSharding = true,
                    EnableAutoScaling = true,
                    ScalingIntervalMs = 1000,
                    ScaleUpThreshold = 0.6,
                    ScaleUpStep = 10,
                    ScaleUpCooldownSeconds = 1,
                    ScaleDownThreshold = 0.2,
                    ScaleDownStep = 10,
                    ScaleDownCooldownSeconds = 15,
                    EnableEviction = true,
                    MaxLifeTime = TimeSpan.FromMinutes(15),
                    MaxIdleTime = TimeSpan.FromMinutes(2),
                    SoftMinEvictableIdleTime = TimeSpan.FromMinutes(1),
                    EvictionIntervalMs = 10000,
                    NumTestsPerEvictionRun = 10,
                    EnableValidation = true,
                    ValidateOnBorrow = false,
                    ValidateOnReturn = false,
                    ValidateWhileIdle = false,
                    EnableGenerationOptimization = true,
                    EnableLeakDetection = true,
                    LeakDetectionThreshold = TimeSpan.FromMinutes(5),
                    EnableDiagnostics = true,
                    EnableMetrics = false,
                    EnableAllocationTracking = false
                };

            case HayatePoolPreset.Deterministic:
                var deterministic = new HayatePoolOptions().UseLeanProfile();
                deterministic.WaitForWarmup = false;
                deterministic.EnableAutoDisposeWithSystem = false;
                return deterministic;

            default:
                throw new ArgumentOutOfRangeException(nameof(preset), preset, "No hand-written form declared for this preset");
        }
    }

    [Theory(Timeout = 30_000)]
    [MemberData(nameof(AllPresets))]
    public void Presets_ShouldLandTheConfigurationTheyDocument(HayatePoolPreset preset)
    {
        AssertOptionsEquivalent(HandWritten(preset), HayatePoolPresets.Create(preset));
    }

    [Theory(Timeout = 30_000)]
    [MemberData(nameof(AllPresets))]
    public void Presets_ShouldOwnOnlyTheFieldSetTheyDocument(HayatePoolPreset preset)
    {
        var pristine = new HayatePoolOptions().UsePreset(preset);

        var primed = new HayatePoolOptions();
        PrimeWithNonDefaultValues(primed);

        // Snapshot the primed values by reference before the preset runs: a CopyTo snapshot would deep-copy
        // the nested settings object and turn "left alone" into a comparison of two distinct instances.
        var before = new Dictionary<string, object?>();
        var properties = typeof(HayatePoolOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (var property in properties)
        {
            if (!property.CanRead || !property.CanWrite) continue;
            before[property.Name] = property.GetValue(primed);
        }

        primed.UsePreset(preset);

        var owned = new HashSet<string>(OwnedProperties(preset));

        foreach (var property in properties)
        {
            if (!property.CanRead || !property.CanWrite) continue;

            if (owned.Contains(property.Name))
            {
                // Owned: the preset decides, so the prior value must not influence the result.
                Assert.Equal(property.GetValue(pristine), property.GetValue(primed));
            }
            else
            {
                // Not owned: the caller decides, so the prior value must survive untouched.
                AssertUntouched(property, before[property.Name], property.GetValue(primed));
            }
        }
    }

    [Theory(Timeout = 30_000)]
    [MemberData(nameof(AllPresets))]
    public void Presets_ShouldAgreeAcrossEveryEntryPoint(HayatePoolPreset preset)
    {
        var expected = HayatePoolPresets.Create(preset);

        AssertOptionsEquivalent(expected, new HayatePoolOptions().UsePreset(preset));

        // The builder route is observed through the pool, which reports the normalized configuration, so
        // the expectation is normalized the same way before comparing.
        _ = expected.IsValid();

        using var pool = new HayatePoolBuilder<TestObject>().WithPreset(preset).Build();
        AssertOptionsEquivalent(expected, pool.GetOptions());
    }

    [Theory(Timeout = 30_000)]
    [MemberData(nameof(AllPresets))]
    public void Presets_ShouldBeRefinableByALaterFeatureCall(HayatePoolPreset preset)
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPreset(preset)
            .WithMaxSize(200)
            .WithMinSize(3)
            .Build();

        var options = pool.GetOptions();
        Assert.Equal(200, options.MaxPoolSize);
        Assert.Equal(3, options.MinPoolSize);

        // And the preset's own identity survives the refinement.
        var expected = HayatePoolPresets.Create(preset);
        expected.MaxPoolSize = 200;
        expected.MinPoolSize = 3;
        _ = expected.IsValid();
        AssertOptionsEquivalent(expected, options);
    }

    [Fact(Timeout = 30_000)]
    public void Presets_ShouldBeComposableAcrossEachOther()
    {
        // The second preset owns its own field set only: the zero floor and the reject policy that go with
        // it come from the first preset and stay, while leak detection — owned by both — follows the last
        // one applied.
        var options = new HayatePoolOptions()
            .UsePreset(HayatePoolPreset.MemoryConstrained)
            .UsePreset(HayatePoolPreset.HighThroughput);

        Assert.Equal(0, options.MinPoolSize);
        Assert.Equal(HayatePoolRejectPolicy.CreateOnDemand, options.RejectPolicy);
        Assert.False(options.EnableLeakDetection);
        Assert.True(options.EnableAutoScaling);

        // Default owns everything, so applying it last leaves a pool with the shipped behaviour rather
        // than a mixture of the two presets.
        AssertOptionsEquivalent(new HayatePoolOptions(), options.UsePreset(HayatePoolPreset.Default));
    }

    [Fact(Timeout = 30_000)]
    public void Presets_ShouldHandOutIndependentInstances()
    {
        var first = HayatePoolPresets.Create(HayatePoolPreset.HighThroughput);
        first.MaxPoolSize = 123;
        first.EnableLeakDetection = true;

        var second = HayatePoolPresets.Create(HayatePoolPreset.HighThroughput);

        Assert.NotSame(first, second);
        Assert.Equal(50, second.MaxPoolSize);
        Assert.False(second.EnableLeakDetection);
    }

    [Fact(Timeout = 30_000)]
    public void Presets_ShouldRejectAnUnknownValue()
    {
        var unknown = (HayatePoolPreset)99;

        Assert.Throws<ArgumentOutOfRangeException>(() => { HayatePoolPresets.Create(unknown); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { HayatePoolPresets.Apply(unknown, new HayatePoolOptions()); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { new HayatePoolOptions().UsePreset(unknown); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { new HayatePoolBuilder<TestObject>().WithPreset(unknown); });
    }

    [Fact(Timeout = 30_000)]
    public void Presets_ShouldRejectANullCustomPreset()
    {
        Assert.Throws<ArgumentNullException>(() =>
        {
            new HayatePoolBuilder<TestObject>().WithPreset((HayatePoolOptions)null!);
        });
        Assert.Throws<ArgumentNullException>(() => { HayatePoolPresets.Apply(HayatePoolPreset.Lean, null!); });
    }

    [Fact(Timeout = 30_000)]
    public void CustomPreset_ShouldBeAppliedLikeACatalogueEntry()
    {
        var custom = new HayatePoolOptions { MaxPoolSize = 128 }.UseFullProfile();

        using var pool = new HayatePoolBuilder<TestObject>().WithPreset(custom).Build();

        var expected = custom.CopyTo();
        _ = expected.IsValid();
        AssertOptionsEquivalent(expected, pool.GetOptions());

        // The preset is copied, so mutating it afterwards cannot reconfigure a pool that was built from it.
        custom.MaxPoolSize = 7;
        Assert.Equal(128, pool.GetOptions().MaxPoolSize);
    }

    [Fact(Timeout = 30_000)]
    public void MemoryConstrained_ShouldRoundTripWithAZeroFloor()
    {
        // The zero floor and CreateOnDemand are a pair: on its own the floor would leave the second
        // borrower waiting out the acquire timeout instead of being served by a fresh object.
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPreset(HayatePoolPreset.MemoryConstrained)
            .WithMaxSize(4)
            .Build();

        var options = pool.GetOptions();
        Assert.Equal(0, options.MinPoolSize);
        Assert.Equal(HayatePoolRejectPolicy.CreateOnDemand, options.RejectPolicy);

        for (var i = 0; i < 8; i++)
        {
            var item = pool.Acquire();
            Assert.NotNull(item);
            pool.Release(item);
        }

        Assert.Equal(1, pool.GetStats().PooledCount);
    }

    [Fact(Timeout = 30_000)]
    public void ConnectionPool_ShouldHandOutOnlyAHandleThatValidates()
    {
        using var pool = new HayatePoolBuilder<ValidatableItem>()
            .WithPreset(HayatePoolPreset.ConnectionPool)
            .WithMinSize(0)
            .WithMaxSize(2)
            .Build();

        var first = pool.Acquire();
        first.Healthy = false;   // the handle died while it was out
        pool.Release(first);     // ValidateOnReturn is off, so it goes back into the idle list

        var second = pool.Acquire();

        Assert.NotSame(first, second);
        Assert.True(second.Healthy);
    }
}
