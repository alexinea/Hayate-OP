using DotNetCore.HayateOP;
using DotNetCore.HealthChecks;

namespace Microsoft.Extensions.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IHayateServiceCollection<T> AddHealthChecks<T>(this IHayateServiceCollection<T> services)
        where T : class, new()
    {
        var a = 0;
        services.AddModule(s =>
        {
            s.AddHealthChecks().AddCheck<HayateOpHealthCheck<T>>($"HayateOpHealthCheck_{typeof(T).Name}");
            s.AddScoped<HayateOpHealthCheck<T>>();
        });

        //services.ExposeServices().AddHealthChecks().AddCheck<HayateOpHealthCheck<T>>($"HayateOpHealthCheck_{typeof(T).Name}");
        //services.ExposeServices().AddScoped<HayateOpHealthCheck<T>>();
        
        return services;
    }
}