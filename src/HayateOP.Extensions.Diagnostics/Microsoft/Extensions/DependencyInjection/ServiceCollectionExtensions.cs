using DotNetCore.HayateOP;
using DotNetCore.HayateOP.Metrics;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IHayateServiceCollection RegisterDiagnostics<T>(this IHayateServiceCollection services)
        where T : class, new()
    {
        services.Services.TryAddSingleton<HayateOpDiagnosticsService>();

        return services;
    }
}