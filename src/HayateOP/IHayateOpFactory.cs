using DotNetCore.HayateOP.Metrics;
using DotNetCore.HayateOP.Policies;
using DotNetCore.HayateOP.Scaling;
using Microsoft.Extensions.Logging;

namespace DotNetCore.HayateOP;

public interface IHayateOpFactory
{
    IHayateObjectPool<T> GetPool<T>() where T : class, new();
    IHayateObjectPool<T> GetPool<T>(IHayateObjectPolicy<T> policy) where T : class, new();
    IHayateObjectPool<T> GetPool<T>(HayateOpOptions options) where T : class, new();
    IHayateObjectPool<T> GetPool<T>(IHayateObjectPolicy<T> policy, HayateOpOptions options) where T : class, new();
    IHayateObjectPool<T> GetPool<T>(IHayateObjectPolicy<T> policy, IHayateOpScalingStrategy scalingStrategy) where T : class, new();
    IHayateObjectPool<T> GetPool<T>(HayateOpOptions options, IHayateOpScalingStrategy scalingStrategy) where T : class, new();
    IHayateObjectPool<T> GetPool<T>(IHayateObjectPolicy<T> policy, HayateOpOptions options, IHayateOpScalingStrategy scalingStrategy) where T : class, new();
    IHayateObjectPool<T> GetPool<T>(IHayateObjectPolicy<T> policy, HayateOpOptions options, ILogger<HayateObjectPool<T>> logger, IHayateOpMetrics metrics) where T : class, new();
    IHayateObjectPool<T> GetPool<T>(IHayateObjectPolicy<T> policy, HayateOpOptions options, IHayateOpScalingStrategy scalingStrategy, ILogger<HayateObjectPool<T>> logger, IHayateOpMetrics metrics) where T : class, new();
}