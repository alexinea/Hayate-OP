using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace DotNetCore.HayateOP.Prometheus;

/// <summary>
/// Prometheus exporter configuration.
/// </summary>
public class HayatePrometheusOptions
{
    /// <summary>Metric-name prefix (namespace). Defaults to <c>hayateop</c>.</summary>
    public string Namespace { get; set; } = "hayateop";
}

/// <summary>
/// Serializes each pool's <see cref="HayatePoolStats"/> into the Prometheus text format.
/// <para>
/// The data source is the public <see cref="IHayateObjectPoolRegistry"/> + <see cref="IHayateObjectPool.GetStats"/>;
/// it never touches internal pool state, and a single pool failing to read does not affect the overall
/// output (that pool is skipped).
/// </para>
/// <para>
/// Exposed metric families (the prefix is configurable via <see cref="HayatePrometheusOptions.Namespace"/>):
/// <list type="bullet">
/// <item><description><c>hayateop_pool_size</c> (gauge) — live objects per pool (idle + borrowed)</description></item>
/// <item><description><c>hayateop_pool_available</c> (gauge) — current idle objects per pool</description></item>
/// <item><description><c>hayateop_pool_created_total</c> / <c>_released_total</c> / <c>_acquired_total</c> / <c>_missed_total</c> (counter)</description></item>
/// <item><description><c>hayateop_pool_leak_detected_total</c> / <c>_leak_suspected_total</c> (counter)</description></item>
/// <item><description><c>hayateop_pool_wait_average_milliseconds</c> / <c>_lease_average_milliseconds</c> (gauge)</description></item>
/// <item><description><c>hayateop_pool_acquire_allocated_bytes_average</c> / <c>_release_allocated_bytes_average</c> (gauge, only present on pools with allocation tracking enabled)</description></item>
/// </list>
/// </para>
/// </summary>
public sealed class HayatePrometheusExporter
{
    private readonly IHayateObjectPoolRegistry _registry;
    private readonly HayatePrometheusOptions _options;

    /// <summary>
    /// Creates the exporter.
    /// </summary>
    /// <param name="registry">The pool registry (data source). May be <c>null</c>, in which case the output is empty text.</param>
    /// <param name="options">Optional configuration (namespace prefix).</param>
    public HayatePrometheusExporter(
        IHayateObjectPoolRegistry registry = null,
        HayatePrometheusOptions options = null)
    {
        _registry = registry;
        _options = options ?? new HayatePrometheusOptions();
    }

    /// <summary>
    /// Scrapes and serializes to the Prometheus text format (reads live data on every call; no caching).
    /// </summary>
    public string Scrape()
    {
        var sb = new StringBuilder(1024);
        var pools = Collect();

        var ns = string.IsNullOrWhiteSpace(_options.Namespace) ? "hayateop" : _options.Namespace.Trim();

        HayatePrometheusSerializer.WriteGauge(sb, $"{ns}_pool_size",
            "Current number of live objects (idle + borrowed) in the pool.",
            Map(pools, s => s.CurrentSize));

        HayatePrometheusSerializer.WriteGauge(sb, $"{ns}_pool_available",
            "Current number of idle objects available for borrow.",
            Map(pools, s => s.PooledCount));

        HayatePrometheusSerializer.WriteCounter(sb, $"{ns}_pool_created_total",
            "Total number of objects created since pool initialization.",
            Map(pools, s => s.TotalCreated));

        HayatePrometheusSerializer.WriteCounter(sb, $"{ns}_pool_released_total",
            "Total number of objects returned to the pool.",
            Map(pools, s => s.TotalReleased));

        HayatePrometheusSerializer.WriteCounter(sb, $"{ns}_pool_acquired_total",
            "Total number of successful borrows.",
            Map(pools, s => s.TotalAcquired));

        HayatePrometheusSerializer.WriteCounter(sb, $"{ns}_pool_missed_total",
            "Total number of borrow attempts that required creating a new object.",
            Map(pools, s => s.TotalMissed));

        HayatePrometheusSerializer.WriteCounter(sb, $"{ns}_pool_leak_detected_total",
            "Total number of detected leaks (leak detection enabled).",
            Map(pools, s => s.LeakDetectedCount));

        HayatePrometheusSerializer.WriteCounter(sb, $"{ns}_pool_leak_suspected_total",
            "Total number of suspected leaks (leak detection disabled, retro-checked).",
            Map(pools, s => s.LeakSuspectedCount));

        HayatePrometheusSerializer.WriteGauge(sb, $"{ns}_pool_wait_average_milliseconds",
            "Average borrow wait time in milliseconds.",
            Map(pools, s => s.AverageWaitTimeMs));

        HayatePrometheusSerializer.WriteGauge(sb, $"{ns}_pool_lease_average_milliseconds",
            "Average lease (borrow-to-return) time in milliseconds.",
            Map(pools, s => s.AverageLeaseTimeMs));

        HayatePrometheusSerializer.WriteGauge(sb, $"{ns}_pool_acquire_allocated_bytes_average",
            "Average bytes allocated per borrow (allocation tracking; only pools with tracking enabled).",
            Map(pools, s => s.AllocationTrackingEnabled ? s.AverageAcquireAllocatedBytes : double.NaN, onlyWhen: s => s.AllocationTrackingEnabled));

        HayatePrometheusSerializer.WriteGauge(sb, $"{ns}_pool_release_allocated_bytes_average",
            "Average bytes allocated per return (allocation tracking; only pools with tracking enabled).",
            Map(pools, s => s.AllocationTrackingEnabled ? s.AverageReleaseAllocatedBytes : double.NaN, onlyWhen: s => s.AllocationTrackingEnabled));

        return sb.ToString();
    }

    /// <summary>
    /// Asynchronously writes the Prometheus text to the given writer (for endpoints to write directly
    /// to the response stream).
    /// </summary>
    /// <param name="writer">The text writer to write to. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> is null.</exception>
    public Task WriteAsync(TextWriter writer)
    {
        if (writer is null) throw new ArgumentNullException(nameof(writer));
        return writer.WriteAsync(Scrape());
    }

    private List<(string Name, HayatePoolStats Stats)> Collect()
    {
        var pools = new List<(string, HayatePoolStats)>();
        if (_registry is null) return pools;

        foreach (var name in _registry.Names)
        {
            if (!_registry.TryGet(name, out var pool)) continue;

            try
            {
                pools.Add((name, pool.GetStats()));
            }
            catch
            {
                // A single pool failing to read does not break the overall scrape (same strategy as HayateOtelMetrics).
            }
        }

        return pools;
    }

    private static IReadOnlyList<HayatePrometheusSample> Map(
        List<(string Name, HayatePoolStats Stats)> pools,
        Func<HayatePoolStats, double> selector,
        Func<HayatePoolStats, bool> onlyWhen = null)
    {
        var samples = new List<HayatePrometheusSample>(pools.Count);
        foreach (var (name, stats) in pools)
        {
            if (onlyWhen is not null && !onlyWhen(stats)) continue;
            samples.Add(new HayatePrometheusSample(name, selector(stats)));
        }

        return samples;
    }
}
