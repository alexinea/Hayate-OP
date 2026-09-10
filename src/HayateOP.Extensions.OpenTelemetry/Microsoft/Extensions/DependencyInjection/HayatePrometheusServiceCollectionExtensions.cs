using DotNetCore.HayateOP;
using DotNetCore.HayateOP.Prometheus;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// M22：Prometheus 导出器的 DI 注册扩展。
/// </summary>
public static class HayatePrometheusServiceCollectionExtensions
{
    /// <summary>
    /// 注册 <see cref="HayatePrometheusExporter"/> 单例（数据源为容器中的
    /// <see cref="IHayateObjectPoolRegistry"/>，由 <c>RegisterHayatePool&lt;T&gt;</c> 填充）。
    /// <para>
    /// 若使用 ASP.NET Core，可在注册后调用
    /// <c>app.UseHayatePrometheusExporter()</c> 暴露 <c>/hayateop/metrics</c> 抓取端点（net8+）。
    /// </para>
    /// </summary>
    /// <param name="services">服务容器。</param>
    /// <param name="configure">可选配置（指标命名空间前缀等）。</param>
    /// <returns>服务容器（链式）。</returns>
    public static IServiceCollection AddHayatePrometheusExporter(
        this IServiceCollection services,
        Action<HayatePrometheusOptions> configure = null)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));

        services.TryAddSingleton(sp =>
        {
            var options = new HayatePrometheusOptions();
            configure?.Invoke(options);
            return new HayatePrometheusExporter(sp.GetService<IHayateObjectPoolRegistry>(), options);
        });

        return services;
    }
}
