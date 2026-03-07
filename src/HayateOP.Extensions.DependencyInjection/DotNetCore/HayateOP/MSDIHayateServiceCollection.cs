using DotNetCore.HayateOP.Modules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DotNetCore.HayateOP;

internal class MSDIHayateServiceCollection : IHayateServiceCollection
{
    private readonly IServiceCollection _services;

    public MSDIHayateServiceCollection(IServiceCollection services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    //public IHayateServiceCollection AddModule<TModule>() where TModule : class, IHayateOpModule
    //{
    //    _services.TryAddSingleton<TModule>();

    //    return this;
    //}

    //public IHayateServiceCollection AddModule<TModule>(TModule module) where TModule : class, IHayateOpModule
    //{
    //    if (module is null) throw new ArgumentNullException(nameof(module));

    //    _services.TryAddSingleton(module);

    //    return this;
    //}

    //public IHayateServiceCollection AddModule<TModule>(Func<TModule> func) where TModule : class, IHayateOpModule
    //{
    //    if (func is null) throw new ArgumentNullException(nameof(func));

    //    var module = func.Invoke();

    //    if (module is null) throw new ArgumentNullException(nameof(module));

    //    _services.TryAddSingleton(module);

    //    return this;
    //}

    //public IHayateServiceCollection AddModule(Action<IServiceCollection> action)
    //{
    //    if (action is null) throw new ArgumentNullException(nameof(action));

    //    action.Invoke(_services);

    //    return this;
    //}

    public IServiceCollection Services => _services;
}