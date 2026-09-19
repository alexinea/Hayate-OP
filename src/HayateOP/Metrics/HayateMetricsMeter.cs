#if NET6_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace DotNetCore.HayateOP.Metrics;

/// <summary>
/// Publishes pool activity on the <c>"HayateOP"</c> <see cref="Meter"/>: an
/// <see cref="IHayateMetrics"/> sink built on <see cref="System.Diagnostics.Metrics"/>, the
/// instrumentation API the .NET runtime, <c>dotnet-counters</c> and the OpenTelemetry .NET SDK all
/// consume.
/// </summary>
/// <remarks>
/// This is the core package's own meter, so a consumer that references nothing but
/// <c>DotNetCore.HayateOP</c> can observe a pool without pulling in an exporter package:
/// <c>AddMeter("HayateOP")</c> or <c>dotnet-counters monitor --counters HayateOP</c> is enough. The
/// dedicated <c>DotNetCore.HayateOP.Extensions.OpenTelemetry</c> package publishes the same
/// instrument set under its own meter name and adds the DI/Prometheus wiring; pick one meter per
/// pool rather than attaching both sinks, which would count every event twice.<br />
/// The instruments are pull-based for the gauges (read on collection, never on the borrow path)
/// and push-based for the counters and the histogram, mirroring the layout of the libraries this
/// package is benchmarked against.<br />
/// <c>Meter</c> is part of the .NET 6+ base class library, so this type exists on net6.0 and above
/// only: on net48 / netstandard2.0 it is compiled out rather than dragging a NuGet dependency into
/// the core package. The lean fast path keeps no counters at all and therefore reports zeros — the
/// same rule as <see cref="HayatePoolStats.TotalCreated"/>.
/// </remarks>
/// <example>
/// <code>
/// var registry = new HayateObjectPoolRegistry();
/// var meter = new HayateMetricsMeter(registry);
/// var pool = new HayatePoolBuilder&lt;MyObject&gt;().WithMetrics(meter).Build();
/// // dotnet-counters monitor --counters HayateOP
/// </code>
/// </example>
public sealed class HayateMetricsMeter : IHayateMetrics, IDisposable
{
    /// <summary>The default meter name (<c>"HayateOP"</c>); see
    /// <see cref="HayateMetricsMeter(IHayateObjectPoolRegistry?, string?)"/> to override it.</summary>
    public const string MeterName = "HayateOP";

    /// <summary>Pool-name tag key (OTel convention: lowercase dotted).</summary>
    public const string TagPoolName = "pool.name";

    /// <summary>Return-validity tag key.</summary>
    public const string TagValid = "valid";

    /// <summary>Scaling-action tag key (expand / shrink).</summary>
    public const string TagAction = "action";

    private readonly Meter _meter;
    private readonly Counter<long> _acquire;
    private readonly Counter<long> _release;
    private readonly Counter<long> _miss;
    private readonly Counter<long> _scaled;
    private readonly Histogram<double> _waitTime;
    private readonly IHayateObjectPoolRegistry? _registry;
    private bool _disposed;

    /// <summary>
    /// Creates the sink and publishes its instruments.
    /// </summary>
    /// <param name="registry">
    /// The pool registry the observable gauges read from. May be <c>null</c> — the gauges are still
    /// created but report nothing, which suits a consumer that only cares about the event counters.
    /// </param>
    /// <param name="meterName">
    /// The meter name to publish under; <c>null</c>/blank uses <see cref="MeterName"/>. The
    /// OpenTelemetry package passes its own name through here, so both packages share one
    /// implementation of the instrument set.
    /// </param>
    public HayateMetricsMeter(IHayateObjectPoolRegistry? registry = null, string? meterName = null)
    {
        _registry = registry;
        var name = string.IsNullOrWhiteSpace(meterName) ? MeterName : meterName!;
        var version = typeof(HayateMetricsMeter).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

        _meter = new Meter(name, version);

        _acquire = _meter.CreateCounter<long>("HayatePoolAcquire", description: "Total number of objects acquired (HayateOP)");
        _release = _meter.CreateCounter<long>("HayatePoolRelease", description: "Total number of objects released (HayateOP)");
        _miss = _meter.CreateCounter<long>("HayatePoolMiss", description: "Total number of pool misses requiring a new object (HayateOP)");
        _scaled = _meter.CreateCounter<long>("HayatePoolScaled", description: "Total number of scaling events (HayateOP)");
        _waitTime = _meter.CreateHistogram<double>("HayatePoolWaitTime", unit: "ms", description: "Object acquire wait time in milliseconds (HayateOP)");

        // ObservableGauge: pulls GetStats() per pool at observation time. The callback runs only when a
        // listener collects, never on the borrow/return hot path.
        _meter.CreateObservableGauge("HayatePoolSize", ObservePoolSize, unit: "{object}",
            description: "Current capacity of each object pool (HayateOP)");
        _meter.CreateObservableGauge("HayatePoolAvailable", ObservePoolAvailable, unit: "{object}",
            description: "Current available (idle) objects in each pool (HayateOP)");
    }

    /// <summary>The meter name this sink publishes under.</summary>
    public string Name => _meter.Name;

    /// <inheritdoc />
    public void RecordObjectAcquired(string poolName, object item, double elapsedMilliseconds)
    {
        _acquire.Add(1, new KeyValuePair<string, object?>(TagPoolName, poolName));
        _waitTime.Record(elapsedMilliseconds, new KeyValuePair<string, object?>(TagPoolName, poolName));
    }

    /// <inheritdoc />
    public void RecordObjectReleased(string poolName, object item, bool isValid)
    {
        _release.Add(1,
            new KeyValuePair<string, object?>(TagPoolName, poolName),
            new KeyValuePair<string, object?>(TagValid, isValid));
    }

    /// <inheritdoc />
    public void RecordObjectMiss(string poolName)
    {
        _miss.Add(1, new KeyValuePair<string, object?>(TagPoolName, poolName));
    }

    /// <inheritdoc />
    public void RecordPoolScaled(string poolName, string action, int oldSize, int newSize)
    {
        _scaled.Add(1,
            new KeyValuePair<string, object?>(TagPoolName, poolName),
            new KeyValuePair<string, object?>(TagAction, action));
    }

    private IEnumerable<Measurement<double>> ObservePoolSize()
        => ObservePools(s => s.CurrentSize);

    private IEnumerable<Measurement<double>> ObservePoolAvailable()
        => ObservePools(s => s.PooledCount);

    private IEnumerable<Measurement<double>> ObservePools(Func<HayatePoolStats, double> selector)
    {
        if (_disposed || _registry is null)
            yield break;

        foreach (var name in _registry.Names)
        {
            if (!_registry.TryGet(name, out var pool))
                continue;

            // A single pool failure must not break the whole collection cycle.
            HayatePoolStats stats;
            try
            {
                stats = pool.GetStats();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HayateMetricsMeter] GetStats failed for pool '{name}': {ex.Message}");
                continue;
            }

            yield return new Measurement<double>(selector(stats), new KeyValuePair<string, object?>(TagPoolName, name));
        }
    }

    /// <summary>Disposes the meter (unregisters every instrument; idempotent).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _meter.Dispose();
    }
}
#endif
