using DotNetCore.HayateOP;
using DotNetCore.HayateOP.Metrics;

namespace Microsoft.Extensions.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IHayateServiceCollection<T> AddDiagnostics<T>(this IHayateServiceCollection<T> services)
        where T : class, new()
    {
        services.AddModule<HayateOpDiagnosticsService>();

        return services;
    }
}