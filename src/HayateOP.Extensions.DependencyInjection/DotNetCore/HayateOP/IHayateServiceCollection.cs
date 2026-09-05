using Microsoft.Extensions.DependencyInjection;

namespace DotNetCore.HayateOP;

public interface IHayateServiceCollection
{
    IServiceCollection Services { get; }
}