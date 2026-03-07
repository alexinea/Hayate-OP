using DotNetCore.HayateOP;
using DotNetCore.HayateOP.Metrics;

namespace Microsoft.Extensions.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IHayateServiceCollection RegisterDiagnostics<T>(this IHayateServiceCollection services)
        where T : class, new()
    {
        services.AddModule<HayateOpDiagnosticsService>();

        return services;
    }
}