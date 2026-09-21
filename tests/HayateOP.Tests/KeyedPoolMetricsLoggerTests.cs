using System;
using System.Threading;
using DotNetCore.HayateOP.Logging;
using DotNetCore.HayateOP.Metrics;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// B3 — a keyed pool accepts the metrics sink and the logger its sub-pools should use.
/// </summary>
/// <remarks>
/// Before 2.9 the sub-pool builder hard-coded the empty sink and the built-in no-op logger, so a keyed
/// pool was invisible to OpenTelemetry and to structured logging however it was configured — the one
/// configuration where per-key visibility matters most, a pool keyed by connection string. Both are now
/// optional constructor arguments, defaulting to the two that were hard-coded, so an unchanged caller sees
/// exactly what it saw before.
/// </remarks>
public class KeyedPoolMetricsLoggerTests
{
    private sealed class Pooled { }

    private sealed class RecordingMetrics : IHayateMetrics
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public void RecordObjectAcquired(string poolName, object item, double elapsedMilliseconds)
            => Interlocked.Increment(ref _calls);

        public void RecordObjectReleased(string poolName, object item, bool isValid)
            => Interlocked.Increment(ref _calls);

        public void RecordObjectMiss(string poolName) => Interlocked.Increment(ref _calls);

        public void RecordPoolScaled(string poolName, string action, int oldSize, int newSize)
            => Interlocked.Increment(ref _calls);
    }

    private sealed class RecordingLogger : IHayateLogger
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public void LogDebug(string message, params object[] args) => Interlocked.Increment(ref _calls);
        public void LogInformation(string message, params object[] args) => Interlocked.Increment(ref _calls);
        public void LogWarning(string message, params object[] args) => Interlocked.Increment(ref _calls);
        public void LogError(Exception ex, string message, params object[] args) => Interlocked.Increment(ref _calls);
    }

    [Fact(Timeout = 30_000)]
    public void SubPools_ShouldUseTheInjectedMetricsAndLogger()
    {
        var metrics = new RecordingMetrics();
        var logger = new RecordingLogger();

        using var pools = new ParameterizedHayatePool<string, Pooled>(
            _ => new Pooled(),
            maxSizePerKey: 2,
            configure: (_, o) => o.EnableMetrics = true,   // counters are off by default
            name: "keyed",
            metrics: metrics,
            logger: logger);

        var borrowed = pools.GetObject("tenant-a");
        pools.ReturnObject("tenant-a", borrowed);

        Assert.True(metrics.Calls > 0, "the injected sink should receive the sub-pool's counters");
        Assert.True(logger.Calls > 0, "the injected logger should receive the sub-pool's trace");
    }

    [Fact(Timeout = 30_000)]
    public void WithoutASink_TheKeyedPoolKeepsItsPreviousBehaviour()
    {
        using var pools = new ParameterizedHayatePool<string, Pooled>(
            _ => new Pooled(), maxSizePerKey: 2, name: "keyed");

        var borrowed = pools.GetObject("tenant-a");
        pools.ReturnObject("tenant-a", borrowed);

        Assert.NotNull(borrowed);
        Assert.Equal(1, pools.KeysInPoolCount);
    }

    [Fact(Timeout = 30_000)]
    public void TheSubPoolFactoryOverload_StillDecidesForItself()
    {
        // The escape hatch is unchanged and still lets a caller give each key whatever it likes — here one
        // shared sink attached to a pool the caller built, which is what the new arguments cannot express
        // (they are per keyed pool, not per key).
        var metrics = new RecordingMetrics();

        using var pools = new ParameterizedHayatePool<string, Pooled>(
            _ => new HayatePoolBuilder<Pooled>()
                .WithMinSize(0)             // the default minimum (5) would exceed a small max size
                .WithMaxSize(2)
                .WithEnableMetrics()
                .WithMetrics(metrics)
                .Build(),
            name: "keyed");

        var borrowed = pools.GetObject("tenant-a");
        pools.ReturnObject("tenant-a", borrowed);

        Assert.True(metrics.Calls > 0, "a sub-pool built by the caller keeps the sink the caller gave it");
    }
}
