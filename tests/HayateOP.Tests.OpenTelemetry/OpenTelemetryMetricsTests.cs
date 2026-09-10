using DotNetCore.HayateOP;
using DotNetCore.HayateOP.Metrics;
using DotNetCore.HayateOP.OpenTelemetry;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using Xunit;

namespace DotNetCore.HayateOP.Tests.OpenTelemetry;

/// <summary>
/// Acceptance: HayateOtelMetrics bridge contract validation.
/// <para>
/// Three layers of validation:
/// 1. MeterListener direct connection - contract for counter/histogram/gauge names, values, and tags;
/// 2. Real pool end-to-end - builder WithMetrics + registry; the gauge reflects pool capacity changes;
/// 3. OpenTelemetry SDK InMemory Exporter pipeline - shares the scraping pipeline with the OTLP Collector (only the terminal differs).
/// </para>
/// </summary>
public class OpenTelemetryMetricsTests
{
    private sealed class PooledResource
    {
        public int Id { get; set; }
        public void DoWork() { }
    }

    private sealed class TagCollector
    {
        public string Name { get; init; } = "";
        public double Value { get; init; }
        public Dictionary<string, object?> Tags { get; init; } = new();
    }

    /// <summary>LINQ ToDictionary cannot be used on a ReadOnlySpan (extension resolution ambiguity); convert manually.</summary>
    private static Dictionary<string, object?> ToDict(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var t in tags) dict[t.Key] = t.Value;
        return dict;
    }

    /// <summary>Build a MeterListener that observes the specified Meter, collecting all long/double measurements.</summary>
    private static (MeterListener Listener, List<TagCollector> Points) BuildListener(string meterName)
    {
        var points = new List<TagCollector>();
        var listener = new MeterListener();

        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == meterName)
                l.EnableMeasurementEvents(instrument);
        };

        listener.SetMeasurementEventCallback<long>((inst, value, tags, _) =>
            points.Add(new TagCollector { Name = inst.Name, Value = value, Tags = ToDict(tags) }));
        listener.SetMeasurementEventCallback<double>((inst, value, tags, _) =>
            points.Add(new TagCollector { Name = inst.Name, Value = value, Tags = ToDict(tags) }));

        listener.Start();
        return (listener, points);
    }

    // ─────────────────────────────────────────────────────────────
    // 1. Counter + histogram contract (MeterListener direct connection)
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void CountersAndHistogram_EmitWithExpectedNamesValuesAndTags()
    {
        using var metrics = new HayateOtelMetrics();
        var (listener, points) = BuildListener("DotNetCore.HayateOP");

        metrics.RecordObjectAcquired("poolA", new object(), 12.5);
        metrics.RecordObjectAcquired("poolB", new object(), 0.25);
        metrics.RecordObjectReleased("poolA", new object(), isValid: true);
        metrics.RecordObjectReleased("poolA", new object(), isValid: false);
        metrics.RecordObjectMiss("poolA");
        metrics.RecordPoolScaled("poolA", "expand", 5, 10);

        // Assert counter points
        var acquires = points.Where(p => p.Name == "HayatePoolAcquire").ToList();
        Assert.Equal(2, acquires.Count);
        Assert.Equal("poolA", acquires[0].Tags["pool.name"]);
        Assert.Equal("poolB", acquires[1].Tags["pool.name"]);

        var releases = points.Where(p => p.Name == "HayatePoolRelease").ToList();
        Assert.Equal(2, releases.Count);
        Assert.All(releases, p => Assert.Equal("poolA", p.Tags["pool.name"]));
        Assert.Equal(true, releases[0].Tags["valid"]);
        Assert.Equal(false, releases[1].Tags["valid"]);

        var miss = Assert.Single(points.Where(p => p.Name == "HayatePoolMiss"));
        Assert.Equal("poolA", miss.Tags["pool.name"]);

        var scaled = Assert.Single(points.Where(p => p.Name == "HayatePoolScaled"));
        Assert.Equal("expand", scaled.Tags["action"]);

        // Assert histogram points (per-measurement values)
        var waits = points.Where(p => p.Name == "HayatePoolWaitTime").ToList();
        Assert.Equal(2, waits.Count);
        Assert.Equal(12.5, waits[0].Value);
        Assert.Equal(0.25, waits[1].Value);

        listener.Dispose();
    }

    // ─────────────────────────────────────────────────────────────
    // 2. Gauge contract (registry data source + real pool end-to-end)
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void Gauges_ReflectLivePoolStatsViaRegistry()
    {
        var registry = new HayateObjectPoolRegistry();
        using var metrics = new HayateOtelMetrics(registry);
        var (listener, points) = BuildListener("DotNetCore.HayateOP");

        using var pool = new HayatePoolBuilder<PooledResource>()
            .WithPoolName("otel-gauge-pool")
            .WithMinSize(5)
            .WithMaxSize(10)
            .WithEnableAutoScaling(false)
            .WithEnableValidation(false)
            .WithEnableEviction(false)
            .WithEnableLeakDetection(false)
            .WithEnableMetrics(true)       // Enable the event gate: only then do borrow/return events reach the bridge instance
            .WithMetrics(metrics)
            .Build();

        registry.Register("otel-gauge-pool", pool);

        // Initial: Min=5 warm-up -> CurrentSize=5, PooledCount=5
        listener.RecordObservableInstruments();
        var size0 = points.Where(p => p.Name == "HayatePoolSize").Single(p => p.Tags["pool.name"] as string == "otel-gauge-pool");
        var avail0 = points.Where(p => p.Name == "HayatePoolAvailable").Single(p => p.Tags["pool.name"] as string == "otel-gauge-pool");
        Assert.Equal(5, size0.Value);
        Assert.Equal(5, avail0.Value);

        // Borrow 2 -> PooledCount=3, capacity unchanged; the Acquire counter should also fire once per borrow in the same window
        points.Clear();
        var a = pool.Acquire();
        var b = pool.Acquire();
        listener.RecordObservableInstruments();
        var size1 = points.Where(p => p.Name == "HayatePoolSize").Single(p => p.Tags["pool.name"] as string == "otel-gauge-pool");
        var avail1 = points.Where(p => p.Name == "HayatePoolAvailable").Single(p => p.Tags["pool.name"] as string == "otel-gauge-pool");
        Assert.Equal(5, size1.Value);
        Assert.Equal(3, avail1.Value);
        Assert.Equal(2, points.Count(p => p.Name == "HayatePoolAcquire"));
        Assert.All(points.Where(p => p.Name == "HayatePoolAcquire"), p => Assert.Equal("otel-gauge-pool", p.Tags["pool.name"]));

        // Return -> PooledCount back to 5; the Release counter fires twice in the same window
        points.Clear();
        pool.Release(a);
        pool.Release(b);
        listener.RecordObservableInstruments();
        var avail2 = points.Where(p => p.Name == "HayatePoolAvailable").Single(p => p.Tags["pool.name"] as string == "otel-gauge-pool");
        Assert.Equal(5, avail2.Value);
        Assert.Equal(2, points.Count(p => p.Name == "HayatePoolRelease"));

        listener.Dispose();
    }

    // ─────────────────────────────────────────────────────────────
    // 3. DI registration: overrides the default EmptyHayateMetrics
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void AddHayateOpenTelemetryMetrics_OverridesDefaultEmptyMetrics()
    {
        var services = new ServiceCollection();
        // AddHayatePoolSupport returns IHayateServiceCollection (a custom builder) and terminates the chain;
        // the OTel bridge extension is defined on IServiceCollection and must be called separately
        services.AddHayatePoolSupport();
        services.AddHayateOpenTelemetryMetrics();

        using var sp = services.BuildServiceProvider();
        var metrics = sp.GetRequiredService<IHayateMetrics>();

        Assert.IsType<HayateOtelMetrics>(metrics);
        Assert.NotSame(EmptyHayateMetrics.Instance, metrics);

        // Events are published normally through the DI-resolved bridge instance
        var (listener, points) = BuildListener("DotNetCore.HayateOP");
        metrics.RecordObjectMiss("di-pool");
        var miss = Assert.Single(points.Where(p => p.Name == "HayatePoolMiss"));
        Assert.Equal("di-pool", miss.Tags["pool.name"]);
        listener.Dispose();
    }

    // ─────────────────────────────────────────────────────────────
    // 4. OpenTelemetry SDK pipeline (InMemory Exporter, isomorphic to the OTLP scraping pipeline)
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void OpenTelemetrySdk_InMemoryExporter_ReceivesHayateMetrics()
    {
        var exported = new List<Metric>();
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter("DotNetCore.HayateOP")
            .AddInMemoryExporter(exported)
            .Build();

        using var metrics = new HayateOtelMetrics();
        metrics.RecordObjectAcquired("sdk-pool", new object(), 3.0);
        metrics.RecordObjectMiss("sdk-pool");

        meterProvider.ForceFlush();

        var acquireMetric = exported.Single(m => m.Name == "HayatePoolAcquire");
        var missMetric = exported.Single(m => m.Name == "HayatePoolMiss");
        var waitMetric = exported.Single(m => m.Name == "HayatePoolWaitTime");

        // Counter aggregated value
        var acquirePoints = new List<MetricPoint>();
        foreach (var p in acquireMetric.GetMetricPoints()) acquirePoints.Add(p);
        Assert.Equal(1, acquirePoints.Count);
        Assert.Equal(1, acquirePoints[0].GetSumLong());
        var tags = new Dictionary<string, object?>();
        foreach (var t in acquirePoints[0].Tags) tags[t.Key] = t.Value;
        Assert.Equal("sdk-pool", tags["pool.name"]);

        var missPoints = new List<MetricPoint>();
        foreach (var p in missMetric.GetMetricPoints()) missPoints.Add(p);
        Assert.Equal(1, missPoints[0].GetSumLong());

        // Histogram collected count=1, sum=3.0
        var waitPoints = new List<MetricPoint>();
        foreach (var p in waitMetric.GetMetricPoints()) waitPoints.Add(p);
        Assert.Equal(1, waitPoints[0].GetHistogramCount());
        Assert.Equal(3.0, waitPoints[0].GetHistogramSum());
    }
}
