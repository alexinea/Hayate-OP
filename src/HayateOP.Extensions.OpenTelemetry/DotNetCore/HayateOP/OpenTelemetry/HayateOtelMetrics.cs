using DotNetCore.HayateOP.Metrics;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;

namespace DotNetCore.HayateOP.OpenTelemetry;

/// <summary>
/// OpenTelemetry 桥接选项
/// </summary>
public class HayateOtelMetricsOptions
{
    /// <summary>
    /// <see cref="Meter"/> 名称（Meter.Name）。默认 "DotNetCore.HayateOP"。
    /// </summary>
    public string MeterName { get; set; } = "DotNetCore.HayateOP";
}

/// <summary>
/// T14：将 <see cref="IHayateMetrics"/> 事件桥接到 <see cref="System.Diagnostics.Metrics"/>（.NET 官方 Metrics API，
/// OpenTelemetry .NET SDK 的原生数据源）。
/// <para>
/// 指标清单：
/// <list type="bullet">
/// <item><description>Counter <c>HayatePoolAcquire</c> —— 借出次数（tag: pool.name）</description></item>
/// <item><description>Counter <c>HayatePoolRelease</c> —— 归还次数（tag: pool.name, valid）</description></item>
/// <item><description>Counter <c>HayatePoolMiss</c> —— 未命中（需创建新对象）次数（tag: pool.name）</description></item>
/// <item><description>Counter <c>HayatePoolScaled</c> —— 扩缩容事件次数（tag: pool.name, action）</description></item>
/// <item><description>Histogram <c>HayatePoolWaitTime</c> —— 借出等待时长（ms，tag: pool.name）</description></item>
/// <item><description>ObservableGauge <c>HayatePoolSize</c> —— 各池当前容量（tag: pool.name）</description></item>
/// <item><description>ObservableGauge <c>HayatePoolAvailable</c> —— 各池当前可用（空闲）对象数（tag: pool.name）</description></item>
/// </list>
/// </para>
/// <para>
/// 本包零外部 NuGet 依赖：<see cref="Meter"/> 由 .NET 6+ 基础类库内置（System.Diagnostics.DiagnosticSource），
/// 使用方按需自行引入 OpenTelemetry SDK + Exporter（OTLP / Prometheus / InMemory …）订阅
/// Meter "<c>DotNetCore.HayateOP</c>" 即可；无 SDK 时也可用 <c>dotnet-counters</c> 直接观测。
/// </para>
/// <para>
/// 解耦约束：本类不访问池的任何内部状态——事件经 <see cref="IHayateMetrics"/> 回调、
/// gauge 经公开的 <see cref="IHayateObjectPoolRegistry"/> + <see cref="IHayateObjectPool.GetStats"/> 取数。
/// </para>
/// </summary>
public sealed class HayateOtelMetrics : IHayateMetrics, IDisposable
{
    /// <summary>池名 tag 键（OTel 惯例小写点分）。</summary>
    public const string TagPoolName = "pool.name";

    /// <summary>归还有效性 tag 键。</summary>
    public const string TagValid = "valid";

    /// <summary>扩缩容动作 tag 键（expand / shrink）。</summary>
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
    /// 创建桥接实例。
    /// </summary>
    /// <param name="registry">
    /// 池注册表（gauge 数据源）。可为 <c>null</c>——此时 gauge 仍会被创建，
    /// 但观测不到任何池（适合仅关心事件计数器的场景）。
    /// </param>
    /// <param name="options">可选配置（Meter 名等）。</param>
    public HayateOtelMetrics(IHayateObjectPoolRegistry? registry = null, HayateOtelMetricsOptions? options = null)
    {
        _registry = registry;
        var opts = options ?? new HayateOtelMetricsOptions();
        var version = typeof(HayateOtelMetrics).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

        _meter = new Meter(opts.MeterName, version);

        _acquire = _meter.CreateCounter<long>("HayatePoolAcquire", description: "累计对象借出次数（HayateOP）");
        _release = _meter.CreateCounter<long>("HayatePoolRelease", description: "累计对象归还次数（HayateOP）");
        _miss = _meter.CreateCounter<long>("HayatePoolMiss", description: "累计池未命中（需创建新对象）次数（HayateOP）");
        _scaled = _meter.CreateCounter<long>("HayatePoolScaled", description: "累计扩缩容事件次数（HayateOP）");
        _waitTime = _meter.CreateHistogram<double>("HayatePoolWaitTime", unit: "ms", description: "对象借出等待时长（HayateOP）");

        // ObservableGauge：观测时逐池拉取 GetStats()。GetStats 内部为轻量快照构造，
        // 且回调仅由监听方（SDK 采集周期 / MeterListener.RecordObservableInstruments）驱动，不在借还热路径上。
        _meter.CreateObservableGauge("HayatePoolSize", ObservePoolSize, unit: "{object}",
            description: "各对象池当前容量（HayateOP）");
        _meter.CreateObservableGauge("HayatePoolAvailable", ObservePoolAvailable, unit: "{object}",
            description: "各对象池当前可用（空闲）对象数（HayateOP）");
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

            // 单池故障不应拖垮整个采集周期
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

    /// <summary>释放 Meter（注销全部指标；幂等）。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _meter.Dispose();
    }
}
