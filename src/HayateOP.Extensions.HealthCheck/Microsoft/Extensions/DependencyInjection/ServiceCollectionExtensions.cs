using DotNetCore.HayateOP;
using DotNetCore.HealthChecks;

namespace Microsoft.Extensions.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IHayateServiceCollection RegisterHealthChecks<T>(this IHayateServiceCollection services)
        where T : class, new()
    {
        services.Services.AddHealthChecks().AddCheck<HayateOpHealthCheck<T>>($"HayateOpHealthCheck_{typeof(T).Name}");
        services.Services.AddScoped<HayateOpHealthCheck<T>>();

        return services;
    }
}