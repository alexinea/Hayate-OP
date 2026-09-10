#if NET8_0_OR_GREATER
using DotNetCore.HayateOP;
using DotNetCore.HayateOP.Prometheus;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// M22：Prometheus 抓取端点的 ASP.NET Core 映射扩展（仅 net8.0+ 目标框架编译）。
/// <para>
/// 本文件所在包（<c>DotNetCore.HayateOP.Extensions.OpenTelemetry</c>）仅在 net8.0 及以上目标框架
/// 引用 <c>Microsoft.AspNetCore.App</c>；net6.0 / net7.0 目标框架不包含本扩展（序列化器与
/// DI 注册在全部目标框架可用）。
/// </para>
/// </summary>
public static class HayatePrometheusEndpointExtensions
{
    /// <summary>默认抓取路径。</summary>
    public const string DefaultPattern = "/hayateop/metrics";

    /// <summary>
    /// 映射 Prometheus 文本抓取端点：GET 返回各池指标的 Prometheus 文本格式
    /// （Content-Type <c>text/plain; version=0.0.4</c>）。
    /// </summary>
    /// <param name="endpoints">端点路由构建器。</param>
    /// <param name="pattern">抓取路径，默认 <see cref="DefaultPattern"/>。</param>
    /// <returns>端点约定构建器（可继续追加 <c>RequireAuthorization</c> 等）。</returns>
    /// <remarks>
    /// 需先通过 <c>AddHayatePrometheusExporter()</c> 注册导出器；未注册时会按容器中的
    /// <see cref="IHayateObjectPoolRegistry"/> 即时构建一个导出器（仍可用）。
    /// </remarks>
    public static IEndpointConventionBuilder UseHayatePrometheusExporter(
        this IEndpointRouteBuilder endpoints,
        string pattern = DefaultPattern)
    {
        if (endpoints is null) throw new ArgumentNullException(nameof(endpoints));
        if (string.IsNullOrWhiteSpace(pattern)) throw new ArgumentNullException(nameof(pattern));

        var exporter = endpoints.ServiceProvider.GetService<HayatePrometheusExporter>()
            ?? new HayatePrometheusExporter(endpoints.ServiceProvider.GetService<IHayateObjectPoolRegistry>());

        return endpoints.MapGet(pattern, (HttpContext context) =>
            Results.Text(exporter.Scrape(), HayatePrometheusSerializer.ContentType));
    }
}
#endif
