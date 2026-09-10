using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace DotNetCore.HayateOP.Prometheus;

/// <summary>
/// M22：Prometheus 导出器配置。
/// </summary>
public class HayatePrometheusOptions
{
    /// <summary>指标名前缀（命名空间）。默认 <c>hayateop</c>。</summary>
    public string Namespace { get; set; } = "hayateop";
}

/// <summary>
/// M22：把各池的 <see cref="HayatePoolStats"/> 序列化为 Prometheus 文本格式。
/// <para>
/// 数据源为公开的 <see cref="IHayateObjectPoolRegistry"/> + <see cref="IHayateObjectPool.GetStats"/>，
/// 不触碰池内部状态；单个池取数失败不影响整体输出（跳过该池）。
/// </para>
/// <para>
/// 暴露的指标族（前缀可经 <see cref="HayatePrometheusOptions.Namespace"/> 配置）：
/// <list type="bullet">
/// <item><description><c>hayateop_pool_size</c>（gauge）—— 各池存活对象数（空闲 + 借出）</description></item>
/// <item><description><c>hayateop_pool_available</c>（gauge）—— 各池当前空闲对象数</description></item>
/// <item><description><c>hayateop_pool_created_total</c> / <c>_released_total</c> / <c>_acquired_total</c> / <c>_missed_total</c>（counter）</description></item>
/// <item><description><c>hayateop_pool_leak_detected_total</c> / <c>_leak_suspected_total</c>（counter）</description></item>
/// <item><description><c>hayateop_pool_wait_average_milliseconds</c> / <c>_lease_average_milliseconds</c>（gauge）</description></item>
/// <item><description><c>hayateop_pool_acquire_allocated_bytes_average</c> / <c>_release_allocated_bytes_average</c>（gauge，仅在启用 M3 分配追踪的池上出现）</description></item>
/// </list>
/// </para>
/// </summary>
public sealed class HayatePrometheusExporter
{
    private readonly IHayateObjectPoolRegistry _registry;
    private readonly HayatePrometheusOptions _options;

    /// <summary>
    /// 创建导出器。
    /// </summary>
    /// <param name="registry">池注册表（数据源）。可为 <c>null</c>——此时输出空文本。</param>
    /// <param name="options">可选配置（命名空间前缀）。</param>
    public HayatePrometheusExporter(
        IHayateObjectPoolRegistry registry = null,
        HayatePrometheusOptions options = null)
    {
        _registry = registry;
        _options = options ?? new HayatePrometheusOptions();
    }

    /// <summary>
    /// 采集并序列化为 Prometheus 文本（每次调用即时取数，无缓存）。
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
            "Average bytes allocated per borrow (M3 allocation tracking; only pools with tracking enabled).",
            Map(pools, s => s.AllocationTrackingEnabled ? s.AverageAcquireAllocatedBytes : double.NaN, onlyWhen: s => s.AllocationTrackingEnabled));

        HayatePrometheusSerializer.WriteGauge(sb, $"{ns}_pool_release_allocated_bytes_average",
            "Average bytes allocated per return (M3 allocation tracking; only pools with tracking enabled).",
            Map(pools, s => s.AllocationTrackingEnabled ? s.AverageReleaseAllocatedBytes : double.NaN, onlyWhen: s => s.AllocationTrackingEnabled));

        return sb.ToString();
    }

    /// <summary>
    /// 异步把 Prometheus 文本写入指定 writer（供端点直接回写响应流）。
    /// </summary>
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
                // 单池取数失败不拖垮整体采集（与 HayateOtelMetrics 同一策略）
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
