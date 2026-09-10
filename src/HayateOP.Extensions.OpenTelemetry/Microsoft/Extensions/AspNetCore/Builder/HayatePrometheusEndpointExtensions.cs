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
/// ASP.NET Core mapping extension for the Prometheus scrape endpoint (compiled only for the net8.0+ target framework).
/// <para>
/// The package containing this file (<c>DotNetCore.HayateOP.Extensions.OpenTelemetry</c>) references
/// <c>Microsoft.AspNetCore.App</c> only on the net8.0+ target framework; the net6.0 / net7.0 targets
/// do not include this extension (the serializer and DI registration are available on all targets).
/// </para>
/// </summary>
public static class HayatePrometheusEndpointExtensions
{
    /// <summary>The default scrape path.</summary>
    public const string DefaultPattern = "/hayateop/metrics";

    /// <summary>
    /// Maps the Prometheus text scrape endpoint: GET returns the Prometheus text format of each
    /// pool's metrics (Content-Type <c>text/plain; version=0.0.4</c>).
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="pattern">The scrape path; defaults to <see cref="DefaultPattern"/>.</param>
    /// <returns>The endpoint convention builder (further chained calls such as <c>RequireAuthorization</c> are allowed).</returns>
    /// <remarks>
    /// Requires registering the exporter via <c>AddHayatePrometheusExporter()</c> first; if not
    /// registered, an exporter is built on the fly from the container's
    /// <see cref="IHayateObjectPoolRegistry"/> (still usable).
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="endpoints"/> or <paramref name="pattern"/> is null.</exception>
    /// <example>
    /// <code>
    /// app.UseHayatePrometheusExporter();   // exposes GET /hayateop/metrics
    /// // or with a custom path:
    /// app.UseHayatePrometheusExporter("/metrics");
    /// </code>
    /// </example>
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
