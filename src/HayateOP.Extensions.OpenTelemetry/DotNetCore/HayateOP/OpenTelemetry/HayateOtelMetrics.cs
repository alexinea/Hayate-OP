using DotNetCore.HayateOP.Metrics;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;

namespace DotNetCore.HayateOP.OpenTelemetry;

/// <summary>
/// OpenTelemetry bridge options.
/// </summary>
public class HayateOtelMetricsOptions
{
    /// <summary>
    /// The <see cref="Meter"/> name (Meter.Name). Defaults to "DotNetCore.HayateOP".
    /// </summary>
    public string MeterName { get; set; } = "DotNetCore.HayateOP";
}

/// <summary>
/// Bridges <see cref="IHayateMetrics"/> events to <see cref="System.Diagnostics.Metrics"/>
/// (the .NET built-in Metrics API and the native data source for the OpenTelemetry .NET SDK).
/// <para>
/// Metric inventory:
/// <list type="bullet">
/// <item><description>Counter <c>HayatePoolAcquire</c> — borrow count (tag: pool.name)</description></item>
/// <item><description>Counter <c>HayatePoolRelease</c> — return count (tag: pool.name, valid)</description></item>
/// <item><description>Counter <c>HayatePoolMiss</c> — miss count (object creation required) (tag: pool.name)</description></item>
/// <item><description>Counter <c>HayatePoolScaled</c> — scaling event count (tag: pool.name, action)</description></item>
/// <item><description>Histogram <c>HayatePoolWaitTime</c> — borrow wait time in milliseconds (tag: pool.name)</description></item>
/// <item><description>ObservableGauge <c>HayatePoolSize</c> — current capacity of each pool (tag: pool.name)</description></item>
/// <item><description>ObservableGauge <c>HayatePoolAvailable</c> — current available (idle) objects of each pool (tag: pool.name)</description></item>
/// </list>
/// </para>
/// <para>
/// This package has zero external NuGet dependencies: <see cref="Meter"/> is built into the
/// .NET 6+ base class library (System.Diagnostics.DiagnosticSource). Consumers bring their own
/// OpenTelemetry SDK + Exporter (OTLP / Prometheus / InMemory …) to subscribe to the
/// "DotNetCore.HayateOP" Meter; without an SDK, <c>dotnet-counters</c> can observe it directly.
/// </para>
/// <para>
/// Decoupling constraint: this class does not access any internal pool state — events come via the
/// <see cref="IHayateMetrics"/> callbacks, and gauges are read from the public
/// <see cref="IHayateObjectPoolRegistry"/> + <see cref="IHayateObjectPool.GetStats"/>.
/// </para>
/// </summary>
public sealed class HayateOtelMetrics : IHayateMetrics, IDisposable
{
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
    /// Creates the bridge instance.
    /// </summary>
    /// <param name="registry">
    /// The pool registry (gauge data source). May be <c>null</c> — the gauges are still created
    /// but no pool is observable (suitable when only the event counters matter).
    /// </param>
    /// <param name="options">Optional configuration (meter name, etc.).</param>
    public HayateOtelMetrics(IHayateObjectPoolRegistry? registry = null, HayateOtelMetricsOptions? options = null)
    {
        _registry = registry;
        var opts = options ?? new HayateOtelMetricsOptions();
        var version = typeof(HayateOtelMetrics).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

        _meter = new Meter(opts.MeterName, version);

        _acquire = _meter.CreateCounter<long>("HayatePoolAcquire", description: "Total number of objects acquired (HayateOP)");
        _release = _meter.CreateCounter<long>("HayatePoolRelease", description: "Total number of objects released (HayateOP)");
        _miss = _meter.CreateCounter<long>("HayatePoolMiss", description: "Total number of pool misses requiring a new object (HayateOP)");
        _scaled = _meter.CreateCounter<long>("HayatePoolScaled", description: "Total number of scaling events (HayateOP)");
        _waitTime = _meter.CreateHistogram<double>("HayatePoolWaitTime", unit: "ms", description: "Object acquire wait time in milliseconds (HayateOP)");

        // ObservableGauge: pulls GetStats() per pool at observation time. GetStats internally builds a
        // lightweight snapshot, and the callback is driven only by listeners (SDK collection period /
        // MeterListener.RecordObservableInstruments), never on the borrow/return hot path.
        _meter.CreateObservableGauge("HayatePoolSize", ObservePoolSize, unit: "{object}",
            description: "Current capacity of each object pool (HayateOP)");
        _meter.CreateObservableGauge("HayatePoolAvailable", ObservePoolAvailable, unit: "{object}",
            description: "Current available (idle) objects in each pool (HayateOP)");
    }

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
                Debug.WriteLine($"[HayateOtelMetrics] GetStats failed for pool '{name}': {ex.Message}");
                continue;
            }

            yield return new Measurement<double>(selector(stats), new KeyValuePair<string, object?>(TagPoolName, name));
        }
    }

    /// <summary>Disposes the Meter (unregisters all metrics; idempotent).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _meter.Dispose();
    }
}
