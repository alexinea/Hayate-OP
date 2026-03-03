using Microsoft.Extensions.DependencyInjection;

namespace HayateOP;

internal class MsdiHayateOpModuleServiceCollection : IHayateOpModuleServiceCollection
{
    private readonly IServiceCollection _services;

    public MsdiHayateOpModuleServiceCollection(IServiceCollection services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    public IServiceCollection ExposeServices() => _services;


    public IHayateOpModuleServiceCollection AddModule<TModule>() where TModule : class, IHayateOpModule
    {
        _services.AddSingleton<TModule>();

        return this;
    }

    public IHayateOpModuleServiceCollection AddModule<TModule>(TModule module) where TModule : class, IHayateOpModule
    {
        if (module is null) throw new ArgumentNullException(nameof(module));
        
        _services.AddSingleton(module);

        return this;
    }

    public IHayateOpModuleServiceCollection AddModule<TModule>(Func<TModule> func) where TModule : class, IHayateOpModule
    {
        if (func is null) throw new ArgumentNullException(nameof(func));
        
        var module = func.Invoke();
        
        if (module is null) throw new ArgumentNullException(nameof(module));

        _services.AddSingleton(module);

        return this;
    }
}