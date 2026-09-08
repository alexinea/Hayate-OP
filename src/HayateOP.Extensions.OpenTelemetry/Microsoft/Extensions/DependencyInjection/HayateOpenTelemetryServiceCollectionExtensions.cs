using DotNetCore.HayateOP;
using DotNetCore.HayateOP.Metrics;
using DotNetCore.HayateOP.OpenTelemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// T14：<see cref="HayateOtelMetrics"/> 的 DI 注册扩展。
/// </summary>
public static class HayateOpenTelemetryServiceCollectionExtensions
{
    /// <summary>
    /// 注册 OpenTelemetry 桥接指标（覆盖 <c>AddHayatePoolSupport()</c> 默认的
    /// <c>EmptyHayateMetrics</c>），并令其通过容器中的
    /// <see cref="IHayateObjectPoolRegistry"/>（由 <c>RegisterHayatePool&lt;T&gt;</c> 填充）
    /// 观测各池容量。
    /// <para>
    /// 本方法只负责把指标发布到 System.Diagnostics.Metrics；
    /// 消费端（OpenTelemetry SDK + Exporter / dotnet-counters）由使用方自行接入，
    /// 例如：<c>builder.Services.AddOpenTelemetry().WithMetrics(m =&gt; m.AddMeter("DotNetCore.HayateOP"))</c>。
    /// </para>
    /// </summary>
    /// <param name="services">服务容器。</param>
    /// <param name="configure">可选的桥接配置（Meter 名等）。</param>
    /// <returns>服务容器（链式）。</returns>
    public static IServiceCollection AddHayateOpenTelemetryMetrics(
        this IServiceCollection services,
        Action<HayateOtelMetricsOptions>? configure = null)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));

        // AddSingleton（非 TryAdd）：后注册覆盖 AddHayatePoolSupport 预置的 EmptyHayateMetrics
        services.AddSingleton<HayateOtelMetrics>(sp =>
        {
            var options = new HayateOtelMetricsOptions();
            configure?.Invoke(options);
            var registry = sp.GetService<IHayateObjectPoolRegistry>();
            return new HayateOtelMetrics(registry, options);
        });
        services.AddSingleton<IHayateMetrics>(sp => sp.GetRequiredService<HayateOtelMetrics>());

        return services;
    }
}
