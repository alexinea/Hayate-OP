using Microsoft.Extensions.DependencyInjection;

namespace DotNetCore.HayateOP;

internal class MSDIHayateServiceCollection : IHayateServiceCollection
{
    private readonly IServiceCollection _services;

    public MSDIHayateServiceCollection(IServiceCollection services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }
    
    public IServiceCollection Services => _services;
}