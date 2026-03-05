using System;
using System.Collections.Concurrent;
using DotNetCore.HayateOP.Metrics;
using DotNetCore.HayateOP.Policies;
using DotNetCore.HayateOP.Scaling;
using Microsoft.Extensions.Logging;

namespace DotNetCore.HayateOP;

public class HayateOpFactory : IHayateOpFactory
{
    private readonly ConcurrentDictionary<Type, object> _pools = new();

    public IHayateObjectPool<T> GetPool<T>() where T : class, new()
    {
        return (IHayateObjectPool<T>)_pools.GetOrAdd(typeof(T), _ =>
        {
            var policy = new DefaultHayateObjectPolicy<T>();
            return CreatePoolInternal(policy);
        });
    }

    public IHayateObjectPool<T> GetPool<T>(IHayateObjectPolicy<T> policy) where T : class, new()
    {
        return (IHayateObjectPool<T>)_pools.GetOrAdd(typeof(T), _ => CreatePoolInternal(policy));
    }

    public IHayateObjectPool<T> GetPool<T>(HayateOpOptions options) where T : class, new()
    {
        return (IHayateObjectPool<T>)_pools.GetOrAdd(typeof(T), _ => CreatePoolInternal<T>(policy: null, options: options, logger: null, metrics: null));
    }

    public IHayateObjectPool<T> GetPool<T>(IHayateObjectPolicy<T> policy, HayateOpOptions options) where T : class, new()
    {
        return (IHayateObjectPool<T>)_pools.GetOrAdd(typeof(T), _ => CreatePoolInternal(policy, options));
    }

    public IHayateObjectPool<T> GetPool<T>(IHayateObjectPolicy<T> policy, IHayateOpScalingStrategy scalingStrategy) where T : class, new()
    {
        return (IHayateObjectPool<T>)_pools.GetOrAdd(typeof(T), _ => CreatePoolInternal(policy, scalingStrategy: scalingStrategy));
    }

    public IHayateObjectPool<T> GetPool<T>(HayateOpOptions options, IHayateOpScalingStrategy scalingStrategy) where T : class, new()
    {
        return (IHayateObjectPool<T>)_pools.GetOrAdd(typeof(T), _ => CreatePoolInternal<T>(null, options, scalingStrategy));
    }

    public IHayateObjectPool<T> GetPool<T>(IHayateObjectPolicy<T> policy, HayateOpOptions options, IHayateOpScalingStrategy scalingStrategy) where T : class, new()
    {
        return (IHayateObjectPool<T>)_pools.GetOrAdd(typeof(T), _ => CreatePoolInternal(policy, options, scalingStrategy));
    }

    public IHayateObjectPool<T> GetPool<T>(IHayateObjectPolicy<T> policy, HayateOpOptions options, ILogger<HayateObjectPool<T>> logger, IHayateOpMetrics metrics) where T : class, new()
    {
        return (IHayateObjectPool<T>)_pools.GetOrAdd(typeof(T), _ => CreatePoolInternal(policy, options,null, logger, metrics));
    }

    public IHayateObjectPool<T> GetPool<T>(IHayateObjectPolicy<T> policy, HayateOpOptions options, IHayateOpScalingStrategy scalingStrategy, ILogger<HayateObjectPool<T>> logger, IHayateOpMetrics metrics) where T : class, new()
    {
        return (IHayateObjectPool<T>)_pools.GetOrAdd(typeof(T), _ => CreatePoolInternal(policy, options,scalingStrategy, logger, metrics));
    }

    private IHayateObjectPool<T> CreatePoolInternal<T>(
        IHayateObjectPolicy<T> policy,
        HayateOpOptions options = null,
        IHayateOpScalingStrategy scalingStrategy = null,
        ILogger<HayateObjectPool<T>> logger = null,
        IHayateOpMetrics metrics = null) where T : class, new()
    {
        policy = (policy ?? new DefaultHayateObjectPolicy<T>());
        return new HayateObjectPool<T>(policy, options, scalingStrategy, logger, metrics);
    }
}