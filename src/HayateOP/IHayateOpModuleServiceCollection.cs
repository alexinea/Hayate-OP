using System;

namespace HayateOP;

public interface IHayateOpModuleServiceCollection
{
    IHayateOpModuleServiceCollection AddModule<TModule>() where TModule : class, IHayateOpModule;
    IHayateOpModuleServiceCollection AddModule<TModule>(TModule module) where TModule : class, IHayateOpModule;
    IHayateOpModuleServiceCollection AddModule<TModule>(Func<TModule> func) where TModule : class, IHayateOpModule;
}