using Microsoft.Extensions.DependencyInjection;

namespace HayateOP;

internal class MsdiHayateServiceCollection<T> : IHayateServiceCollection<T> where T : class, new()
{
    private readonly IServiceCollection _services;

    public MsdiHayateServiceCollection(IServiceCollection services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    public IServiceCollection ExposeServices() => _services;

    public IHayateServiceCollection<T> AddModule<TModule>() where TModule : class, IHayateOpModule
    {
        _services.AddSingleton<TModule>();

        return this;
    }

    public IHayateServiceCollection<T> AddModule<TModule>(TModule module) where TModule : class, IHayateOpModule
    {
        if (module is null) throw new ArgumentNullException(nameof(module));

        _services.AddSingleton(module);

        return this;
    }

    public IHayateServiceCollection<T> AddModule<TModule>(Func<TModule> func) where TModule : class, IHayateOpModule
    {
        if (func is null) throw new ArgumentNullException(nameof(func));

        var module = func.Invoke();

        if (module is null) throw new ArgumentNullException(nameof(module));

        _services.AddSingleton(module);

        return this;
    }

    public IHayateServiceCollection<T> AddModule(Action<IServiceCollection> action)
    {
        if (action is null) throw new ArgumentNullException(nameof(action));

        action.Invoke(_services);

        return this;
    }
}