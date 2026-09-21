using DotNetCore.HayateOP;
using DotNetCore.HealthChecks;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers a health check that reports on the pool of <typeparamref name="T"/> using the pool's own
    /// counters and circuit breaker.
    /// </summary>
    public static IHayateServiceCollection RegisterHealthChecks<T>(this IHayateServiceCollection services)
        where T : class
    {
        return services.RegisterHealthChecks<T>(probe: null);
    }

    /// <summary>
    /// Registers a health check that also asks a real pooled object whether it still works.
    /// </summary>
    /// <typeparam name="T">The pooled element type.</typeparam>
    /// <typeparam name="TProbe">The probe implementation; registered as scoped unless the host already
    /// registered it, so a probe with its own dependencies can be registered beforehand.</typeparam>
    /// <remarks>
    /// This is the overload to reach for when "the pool has a free connection" and "the connection works"
    /// are different questions — which, for anything holding a network resource, they are.
    /// </remarks>
    public static IHayateServiceCollection RegisterHealthChecks<T, TProbe>(this IHayateServiceCollection services)
        where T : class
        where TProbe : class, IHayateObjectHealthProbe<T>
    {
        services.Services.TryAddScoped<TProbe>();
        services.Services.AddScoped(sp => new HayateOpHealthCheck<T>(
            sp.GetRequiredService<IHayateObjectPool<T>>(), sp.GetRequiredService<TProbe>()));
        services.Services.AddHealthChecks().AddCheck<HayateOpHealthCheck<T>>($"HayateOpHealthCheck_{typeof(T).Name}");

        return services;
    }

    /// <summary>
    /// Registers a health check that asks the given <paramref name="probe"/> about a real pooled object.
    /// </summary>
    /// <param name="services">The Hayate service collection.</param>
    /// <param name="probe">The object-level probe, or <c>null</c> to register the counters-only check.</param>
    public static IHayateServiceCollection RegisterHealthChecks<T>(this IHayateServiceCollection services,
        IHayateObjectHealthProbe<T>? probe)
        where T : class
    {
        if (probe is null)
        {
            services.Services.AddScoped<HayateOpHealthCheck<T>>();
        }
        else
        {
            services.Services.AddScoped(sp => new HayateOpHealthCheck<T>(
                sp.GetRequiredService<IHayateObjectPool<T>>(), probe));
        }

        services.Services.AddHealthChecks().AddCheck<HayateOpHealthCheck<T>>($"HayateOpHealthCheck_{typeof(T).Name}");

        return services;
    }
}