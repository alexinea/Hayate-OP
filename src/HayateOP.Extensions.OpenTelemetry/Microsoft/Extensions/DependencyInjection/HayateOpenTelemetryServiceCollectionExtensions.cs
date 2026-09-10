using DotNetCore.HayateOP;
using DotNetCore.HayateOP.Metrics;
using DotNetCore.HayateOP.OpenTelemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// DI registration extension for <see cref="HayateOtelMetrics"/>.
/// </summary>
public static class HayateOpenTelemetryServiceCollectionExtensions
{
    /// <summary>
    /// Registers the OpenTelemetry bridge metrics (overriding the <c>EmptyHayateMetrics</c> default
    /// from <c>AddHayatePoolSupport()</c>) and lets it observe each pool's capacity through the
    /// container's <see cref="IHayateObjectPoolRegistry"/> (populated by <c>RegisterHayatePool&lt;T&gt;</c>).
    /// <para>
    /// This method only publishes the metrics to System.Diagnostics.Metrics; the consumer side
    /// (OpenTelemetry SDK + Exporter / dotnet-counters) is wired up by the caller, e.g.:
    /// <c>builder.Services.AddOpenTelemetry().WithMetrics(m =&gt; m.AddMeter("DotNetCore.HayateOP"))</c>.
    /// </para>
    /// </summary>
    /// <param name="services">The service container.</param>
    /// <param name="configure">Optional bridge configuration (meter name, etc.).</param>
    /// <returns>The service container (for chaining).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    /// <example>
    /// <code>
    /// services.AddHayateOpenTelemetryMetrics();
    /// // or with configuration:
    /// services.AddHayateOpenTelemetryMetrics(o =&gt; o.MeterName = "MyApp.Pools");
    /// </code>
    /// </example>
    public static IServiceCollection AddHayateOpenTelemetryMetrics(
        this IServiceCollection services,
        Action<HayateOtelMetricsOptions>? configure = null)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));

        // AddSingleton (not TryAdd): a later registration overrides the EmptyHayateMetrics preset by AddHayatePoolSupport.
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
