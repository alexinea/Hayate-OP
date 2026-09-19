using DotNetCore.HayateOP;
using DotNetCore.HayateOP.Metrics;
using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using Xunit;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// Acceptance for the core package's own <see cref="System.Diagnostics.Metrics"/> sink
/// (<see cref="HayateMetricsMeter"/>, meter name <c>"HayateOP"</c>): the instrument contract read
/// through a <see cref="MeterListener"/>, the end-to-end wiring through a real pool, and the two
/// properties the design promises — a configurable meter name (so the OpenTelemetry package can
/// publish the same instrument set under its own name) and one sink per pool, so no event is
/// counted twice.
/// </summary>
public class DiagnosticMetricsTests
{
    private sealed class PooledResource
    {
        public int Id { get; set; }
    }

    private sealed class Point
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
    private static (MeterListener Listener, List<Point> Points) BuildListener(string meterName)
    {
        var points = new List<Point>();
        var listener = new MeterListener();

        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == meterName)
                l.EnableMeasurementEvents(instrument);
        };

        listener.SetMeasurementEventCallback<long>((inst, value, tags, _) =>
            points.Add(new Point { Name = inst.Name, Value = value, Tags = ToDict(tags) }));
        listener.SetMeasurementEventCallback<double>((inst, value, tags, _) =>
            points.Add(new Point { Name = inst.Name, Value = value, Tags = ToDict(tags) }));

        listener.Start();
        return (listener, points);
    }

    [Fact(Timeout = 30_000)]
    public void Meter_ShouldPublishUnderTheHayateOpName()
    {
        Assert.Equal("HayateOP", HayateMetricsMeter.MeterName);

        using var sink = new HayateMetricsMeter();
        Assert.Equal("HayateOP", sink.Name);

        var (listener, points) = BuildListener(HayateMetricsMeter.MeterName);
        sink.RecordObjectAcquired("probe", new PooledResource(), 0.5);

        // The event landed on the HayateOP meter, and nowhere else.
        var acquire = Assert.Single(points.Where(p => p.Name == "HayatePoolAcquire"));
        Assert.Equal(1, acquire.Value);
        Assert.Equal("probe", acquire.Tags[HayateMetricsMeter.TagPoolName]);

        listener.Dispose();
    }

    [Fact(Timeout = 30_000)]
    public void AcquireAndRelease_ShouldBeCountedWithPoolNameAndValidityTags()
    {
        var registry = new HayateObjectPoolRegistry();
        using var sink = new HayateMetricsMeter(registry);
        using var pool = new HayatePoolBuilder<PooledResource>()
            .WithPoolName("diag")
            .WithMinSize(1)
            .WithMaxSize(8)
            .WithShardCount(2)
            .WithEnableMetrics(true)
            .WithMetrics(sink)
            .Build();
        registry.Register("diag", pool);

        var (listener, points) = BuildListener(HayateMetricsMeter.MeterName);

        var item = pool.Acquire();
        pool.Release(item);

        var acquire = Assert.Single(points.Where(p => p.Name == "HayatePoolAcquire"));
        Assert.Equal(1, acquire.Value);
        Assert.Equal("diag", acquire.Tags[HayateMetricsMeter.TagPoolName]);

        var release = Assert.Single(points.Where(p => p.Name == "HayatePoolRelease"));
        Assert.Equal(1, release.Value);
        Assert.Equal("diag", release.Tags[HayateMetricsMeter.TagPoolName]);
        Assert.Equal(true, release.Tags[HayateMetricsMeter.TagValid]);

        // The borrow-path histogram carries the measured wait, not a placeholder.
        var wait = Assert.Single(points.Where(p => p.Name == "HayatePoolWaitTime"));
        Assert.True(wait.Value >= 0.0);

        listener.Dispose();
    }

    [Fact(Timeout = 30_000)]
    public void ObservableGauges_ShouldReportCapacityAndIdleCount()
    {
        var registry = new HayateObjectPoolRegistry();
        using var sink = new HayateMetricsMeter(registry);
        using var pool = new HayatePoolBuilder<PooledResource>()
            .WithPoolName("gauge")
            .WithMinSize(2)
            .WithMaxSize(8)
            .WithShardCount(2)
            .Build();
        registry.Register("gauge", pool);

        var (listener, points) = BuildListener(HayateMetricsMeter.MeterName);

        // Gauges are pull-based: nothing is emitted until a listener collects.
        Assert.Empty(points);
        listener.RecordObservableInstruments();

        var size = Assert.Single(points.Where(p => p.Name == "HayatePoolSize"));
        Assert.Equal("gauge", size.Tags[HayateMetricsMeter.TagPoolName]);
        Assert.True(size.Value >= 2.0, $"expected at least the pre-warmed capacity, got {size.Value}");

        var available = Assert.Single(points.Where(p => p.Name == "HayatePoolAvailable"));
        Assert.Equal("gauge", available.Tags[HayateMetricsMeter.TagPoolName]);
        Assert.True(available.Value > 0.0, "a fresh pool has idle objects");

        listener.Dispose();
    }

    [Fact(Timeout = 30_000)]
    public void MeterNameOverride_ShouldPublishUnderThatNameOnly()
    {
        // The OpenTelemetry package publishes the same instrument set under its own meter name by
        // passing it through; the core meter must stay silent in that configuration.
        using var sink = new HayateMetricsMeter(null, "DotNetCore.HayateOP");
        Assert.Equal("DotNetCore.HayateOP", sink.Name);

        var (listener, points) = BuildListener("DotNetCore.HayateOP");
        sink.RecordObjectMiss("probe");

        Assert.Single(points.Where(p => p.Name == "HayatePoolMiss"));

        // Nothing was published under the default name.
        var (defaultListener, defaultPoints) = BuildListener(HayateMetricsMeter.MeterName);
        sink.RecordObjectMiss("probe");
        Assert.Empty(defaultPoints);

        listener.Dispose();
        defaultListener.Dispose();
    }

    [Fact(Timeout = 30_000)]
    public void TwoSinksOnOnePool_ShouldCountIndependentlyPerMeter()
    {
        // One sink per pool is the documented configuration; the test pins the reason — each sink
        // owns its own meter and counter set, so subscribing to both meters yields two independent
        // series rather than one interleaved one.
        var registry = new HayateObjectPoolRegistry();
        using var coreSink = new HayateMetricsMeter(registry);
        using var bridgeSink = new HayateMetricsMeter(registry, "DotNetCore.HayateOP");

        var (coreListener, corePoints) = BuildListener(HayateMetricsMeter.MeterName);
        var (bridgeListener, bridgePoints) = BuildListener("DotNetCore.HayateOP");

        coreSink.RecordObjectAcquired("probe", new PooledResource(), 1.0);
        bridgeSink.RecordObjectAcquired("probe", new PooledResource(), 1.0);

        Assert.Single(corePoints.Where(p => p.Name == "HayatePoolAcquire"));
        Assert.Single(bridgePoints.Where(p => p.Name == "HayatePoolAcquire"));

        coreListener.Dispose();
        bridgeListener.Dispose();
    }

    [Fact(Timeout = 30_000)]
    public void Dispose_ShouldBeIdempotent()
    {
        var sink = new HayateMetricsMeter();
        sink.Dispose();
        sink.Dispose();
    }
}
