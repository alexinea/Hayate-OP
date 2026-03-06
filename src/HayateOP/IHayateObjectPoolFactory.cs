using System.Threading.Tasks;
using DotNetCore.HayateOP.Metrics;
using DotNetCore.HayateOP.Policies;
using DotNetCore.HayateOP.Scaling;
using Microsoft.Extensions.Logging;

namespace DotNetCore.HayateOP;

public interface IHayateObjectPoolFactory
{
    IHayateObjectPool<T> GetPool<T>() where T : class, new();
    IHayateObjectPool<T> GetPool<T>(IHayateObjectPolicy<T> policy) where T : class, new();
    IHayateObjectPool<T> GetPool<T>(HayatePoolOptions options) where T : class, new();
    IHayateObjectPool<T> GetPool<T>(IHayateObjectPolicy<T> policy, HayatePoolOptions options) where T : class, new();
    IHayateObjectPool<T> GetPool<T>(IHayateObjectPolicy<T> policy, IHayateScalingStrategy scalingStrategy) where T : class, new();
    IHayateObjectPool<T> GetPool<T>(HayatePoolOptions options, IHayateScalingStrategy scalingStrategy) where T : class, new();
    IHayateObjectPool<T> GetPool<T>(IHayateObjectPolicy<T> policy, HayatePoolOptions options, IHayateScalingStrategy scalingStrategy) where T : class, new();
    IHayateObjectPool<T> GetPool<T>(IHayateObjectPolicy<T> policy, HayatePoolOptions options, ILogger<HayateObjectPool<T>> logger, IHayateMetrics metrics) where T : class, new();
    IHayateObjectPool<T> GetPool<T>(IHayateObjectPolicy<T> policy, HayatePoolOptions options, IHayateScalingStrategy scalingStrategy, ILogger<HayateObjectPool<T>> logger, IHayateMetrics metrics) where T : class, new();
    
    Task<bool> PoolExistsAsync(string poolName);
    Task<HayatePoolOptions> GetPoolOptionsAsync(string poolName);
    Task<HayatePoolStats> GetPoolStatsAsync(string poolName);
    Task UpdatePoolOptionsAsync(string poolName, HayatePoolOptions newConfig);
    Task<HayatePoolSnapshot> GetPoolSnapshotAsync(string poolName);
    Task ClearPoolAsync(string poolName);
}