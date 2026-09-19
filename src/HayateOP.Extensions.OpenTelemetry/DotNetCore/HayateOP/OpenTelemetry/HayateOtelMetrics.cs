using DotNetCore.HayateOP.Metrics;
using System;

namespace DotNetCore.HayateOP.OpenTelemetry;

/// <summary>
/// OpenTelemetry bridge options.
/// </summary>
public class HayateOtelMetricsOptions
{
    /// <summary>
    /// The <see cref="System.Diagnostics.Metrics.Meter"/> name (Meter.Name). Defaults to "DotNetCore.HayateOP".
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
/// This package has zero external NuGet dependencies: <see cref="System.Diagnostics.Metrics.Meter"/> is built into the
/// .NET 6+ base class library (System.Diagnostics.DiagnosticSource). Consumers bring their own
/// OpenTelemetry SDK + Exporter (OTLP / Prometheus / InMemory …) to subscribe to the
/// "DotNetCore.HayateOP" Meter; without an SDK, <c>dotnet-counters</c> can observe it directly.
/// </para>
/// <para>
/// The instrument set itself — names, units, tags, descriptions, and the pull-based gauges — lives in
/// <see cref="HayateMetricsMeter"/> in the core package, and this class is the meter-name and
/// packaging front for it. Both packages therefore publish one and the same instrument set, and a
/// consumer picks whichever meter name it subscribes to rather than attaching both sinks to one pool
/// (which would count every event twice). The core meter (<c>"HayateOP"</c>) is the shorter name, kept
/// aligned in style with the reference libraries in the benchmark matrix.
/// </para>
/// <para>
/// Decoupling constraint: neither the bridge nor the core meter accesses internal pool state — events
/// come via the <see cref="IHayateMetrics"/> callbacks, and gauges are read from the public
/// <see cref="IHayateObjectPoolRegistry"/> + <see cref="IHayateObjectPool.GetStats"/>.
/// </para>
/// </summary>
public sealed class HayateOtelMetrics : IHayateMetrics, IDisposable
{
    /// <summary>Pool-name tag key (OTel convention: lowercase dotted).</summary>
    public const string TagPoolName = HayateMetricsMeter.TagPoolName;

    /// <summary>Return-validity tag key.</summary>
    public const string TagValid = HayateMetricsMeter.TagValid;

    /// <summary>Scaling-action tag key (expand / shrink).</summary>
    public const string TagAction = HayateMetricsMeter.TagAction;

    private readonly HayateMetricsMeter _meter;

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
        var opts = options ?? new HayateOtelMetricsOptions();
        _meter = new HayateMetricsMeter(registry, opts.MeterName);
    }

    /// <inheritdoc />
    public void RecordObjectAcquired(string poolName, object item, double elapsedMilliseconds)
        => _meter.RecordObjectAcquired(poolName, item, elapsedMilliseconds);

    /// <inheritdoc />
    public void RecordObjectReleased(string poolName, object item, bool isValid)
        => _meter.RecordObjectReleased(poolName, item, isValid);

    /// <inheritdoc />
    public void RecordObjectMiss(string poolName)
        => _meter.RecordObjectMiss(poolName);

    /// <inheritdoc />
    public void RecordPoolScaled(string poolName, string action, int oldSize, int newSize)
        => _meter.RecordPoolScaled(poolName, action, oldSize, newSize);

    /// <summary>Disposes the Meter (unregisters all metrics; idempotent).</summary>
    public void Dispose() => _meter.Dispose();
}
