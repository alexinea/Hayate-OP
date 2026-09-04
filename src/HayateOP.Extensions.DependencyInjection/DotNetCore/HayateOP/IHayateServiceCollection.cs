using Microsoft.Extensions.DependencyInjection;

namespace DotNetCore.HayateOP;

public interface IHayateServiceCollection
{
    //IHayateServiceCollection AddModule<TModule>() where TModule : class, IHayateOpModule;
    //IHayateServiceCollection AddModule<TModule>(TModule module) where TModule : class, IHayateOpModule;
    //IHayateServiceCollection AddModule<TModule>(Func<TModule> func) where TModule : class, IHayateOpModule;
    //IHayateServiceCollection AddModule(Action<IServiceCollection> action);
    IServiceCollection Services { get; }
}