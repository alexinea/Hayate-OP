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
/// T14 验收：HayateOtelMetrics 桥接契约验证。
/// <para>
/// 三层验证：
/// 1. MeterListener 直连 —— 计数器/直方图/gauge 的名称、数值、tag 契约；
/// 2. 真实池端到端 —— builder WithMetrics + registry，gauge 反映池容量变化；
/// 3. OpenTelemetry SDK InMemory Exporter 管线 —— 与 OTLP Collector 共享采集管线（仅末端不同）。
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

    /// <summary>ReadOnlySpan 上不能用 LINQ ToDictionary（扩展解析歧义），手动转换。</summary>
    private static Dictionary<string, object?> ToDict(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var t in tags) dict[t.Key] = t.Value;
        return dict;
    }

    /// <summary>构建监听指定 Meter 的 MeterListener，收集所有 long/double 测量值。</summary>
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
    // 1. 计数器 + 直方图契约（MeterListener 直连）
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

        // 断言计数器点
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

        // 断言直方图点（逐次测量值）
        var waits = points.Where(p => p.Name == "HayatePoolWaitTime").ToList();
        Assert.Equal(2, waits.Count);
        Assert.Equal(12.5, waits[0].Value);
        Assert.Equal(0.25, waits[1].Value);

        listener.Dispose();
    }

    // ─────────────────────────────────────────────────────────────
    // 2. Gauge 契约（registry 数据源 + 真实池端到端）
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
            .WithEnableMetrics(true)       // 打开事件门控：借/还事件才会到达桥接实例
            .WithMetrics(metrics)
            .Build();

        registry.Register("otel-gauge-pool", pool);

        // 初始：Min=5 预热 → CurrentSize=5, PooledCount=5
        listener.RecordObservableInstruments();
        var size0 = points.Where(p => p.Name == "HayatePoolSize").Single(p => p.Tags["pool.name"] as string == "otel-gauge-pool");
        var avail0 = points.Where(p => p.Name == "HayatePoolAvailable").Single(p => p.Tags["pool.name"] as string == "otel-gauge-pool");
        Assert.Equal(5, size0.Value);
        Assert.Equal(5, avail0.Value);

        // 借出 2 个 → PooledCount=3，容量不变；同一窗口内 Acquire 计数器也应各发 1 次
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

        // 归还 → PooledCount 回到 5；同一窗口内 Release 计数器发 2 次
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
    // 3. DI 注册：覆盖默认 EmptyHayateMetrics
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void AddHayateOpenTelemetryMetrics_OverridesDefaultEmptyMetrics()
    {
        var services = new ServiceCollection();
        // AddHayatePoolSupport 返回 IHayateServiceCollection（自定义 builder），链式终止；
        // OTel 桥接扩展定义在 IServiceCollection 上，须单独调用
        services.AddHayatePoolSupport();
        services.AddHayateOpenTelemetryMetrics();

        using var sp = services.BuildServiceProvider();
        var metrics = sp.GetRequiredService<IHayateMetrics>();

        Assert.IsType<HayateOtelMetrics>(metrics);
        Assert.NotSame(EmptyHayateMetrics.Instance, metrics);

        // 事件经 DI 解析的桥接实例正常发布
        var (listener, points) = BuildListener("DotNetCore.HayateOP");
        metrics.RecordObjectMiss("di-pool");
        var miss = Assert.Single(points.Where(p => p.Name == "HayatePoolMiss"));
        Assert.Equal("di-pool", miss.Tags["pool.name"]);
        listener.Dispose();
    }

    // ─────────────────────────────────────────────────────────────
    // 4. OpenTelemetry SDK 管线（InMemory Exporter，与 OTLP 采集管线同构）
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

        // Counter 聚合值
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

        // Histogram 采集到 count=1、sum=3.0
        var waitPoints = new List<MetricPoint>();
        foreach (var p in waitMetric.GetMetricPoints()) waitPoints.Add(p);
        Assert.Equal(1, waitPoints[0].GetHistogramCount());
        Assert.Equal(3.0, waitPoints[0].GetHistogramSum());
    }
}
