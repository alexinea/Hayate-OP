using DotNetCore.HayateOP;
using DotNetCore.HayateOP.Prometheus;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// DI registration extension for the Prometheus exporter.
/// </summary>
public static class HayatePrometheusServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="HayatePrometheusExporter"/> singleton (data source is the
    /// container's <see cref="IHayateObjectPoolRegistry"/>, populated by <c>RegisterHayatePool&lt;T&gt;</c>).
    /// <para>
    /// With ASP.NET Core, after registration call
    /// <c>app.UseHayatePrometheusExporter()</c> to expose the <c>/hayateop/metrics</c> scrape
    /// endpoint (net8+).
    /// </para>
    /// </summary>
    /// <param name="services">The service container.</param>
    /// <param name="configure">Optional configuration (metric namespace prefix, etc.).</param>
    /// <returns>The service container (for chaining).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    /// <example>
    /// <code>
    /// services.AddHayatePrometheusExporter();
    /// // or with a custom namespace prefix:
    /// services.AddHayatePrometheusExporter(o =&gt; o.Namespace = "myapp");
    /// </code>
    /// </example>
    public static IServiceCollection AddHayatePrometheusExporter(
        this IServiceCollection services,
        Action<HayatePrometheusOptions>? configure = null)
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
