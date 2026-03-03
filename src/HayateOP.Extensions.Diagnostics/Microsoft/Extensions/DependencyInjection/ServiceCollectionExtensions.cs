using HayateOP;
using HayateOP.Metrics;

namespace Microsoft.Extensions.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IHayateOpModuleServiceCollection AddDiagnostics(this IHayateOpModuleServiceCollection services)
    {
        services.AddModule<HayateOpDiagnostics>();
        
        return services;
    }
}