using DotNetCore.HayateOP.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetCore.HayateOP;

public interface IHayateServiceCollection<T> where T : class, new()
{
    IHayateServiceCollection<T> AddModule<TModule>() where TModule : class, IHayateOpModule;
    IHayateServiceCollection<T> AddModule<TModule>(TModule module) where TModule : class, IHayateOpModule;
    IHayateServiceCollection<T> AddModule<TModule>(Func<TModule> func) where TModule : class, IHayateOpModule;
    IHayateServiceCollection<T> AddModule(Action<IServiceCollection> action);
}