using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using DotNetCore.HayateOP.Metrics;
using DotNetCore.HayateOP.Policies;
using DotNetCore.HayateOP.Scaling;
using Microsoft.Extensions.Logging;

namespace DotNetCore.HayateOP;

public class HayateObjectPoolFactory : IHayateObjectPoolFactory
{
    private readonly ConcurrentDictionary<Type, object> _poolCache = new();
    private readonly ConcurrentDictionary<string, Type> _poolNameToType = new();

    public IHayateObjectPool<T> GetPool<T>() where T : class, new()
    {
        var type = typeof(T);

        var pool = (IHayateObjectPool<T>)_poolCache.GetOrAdd(typeof(T), _ =>
        {
            var policy = new DefaultHayateObjectPolicy<T>();
            var poolInstance = CreatePoolInternal(policy);
            _poolNameToType.TryAdd(type.Name, type);
            return poolInstance;
        });

        return pool;
    }

    public IHayateObjectPool<T> GetPool<T>(IHayateObjectPolicy<T> policy) where T : class, new()
    {
        var type = typeof(T);

        var pool = (IHayateObjectPool<T>)_poolCache.GetOrAdd(typeof(T), _ =>
        {
            var poolInstance = CreatePoolInternal(policy);
            _poolNameToType.TryAdd(type.Name, type);
            return poolInstance;
        });

        return pool;
    }

    public IHayateObjectPool<T> GetPool<T>(HayatePoolOptions options) where T : class, new()
    {
        var type = typeof(T);

        var pool = (IHayateObjectPool<T>)_poolCache.GetOrAdd(typeof(T), _ =>
        {
            var poolInstance = CreatePoolInternal<T>(policy: null, options: options, logger: null, metrics: null);
            _poolNameToType.TryAdd(type.Name, type);
            return poolInstance;
        });

        return pool;
    }

    public IHayateObjectPool<T> GetPool<T>(IHayateObjectPolicy<T> policy, HayatePoolOptions options) where T : class, new()
    {
        var type = typeof(T);

        var pool = (IHayateObjectPool<T>)_poolCache.GetOrAdd(typeof(T), _ =>
        {
            var poolInstance = CreatePoolInternal(policy, options);
            _poolNameToType.TryAdd(type.Name, type);
            return poolInstance;
        });

        return pool;
    }

    public IHayateObjectPool<T> GetPool<T>(IHayateObjectPolicy<T> policy, IHayateScalingStrategy scalingStrategy) where T : class, new()
    {
        var type = typeof(T);

        var pool = (IHayateObjectPool<T>)_poolCache.GetOrAdd(typeof(T), _ =>
        {
            var poolInstance = CreatePoolInternal(policy, scalingStrategy: scalingStrategy);
            _poolNameToType.TryAdd(type.Name, type);
            return poolInstance;
        });

        return pool;
    }

    public IHayateObjectPool<T> GetPool<T>(HayatePoolOptions options, IHayateScalingStrategy scalingStrategy) where T : class, new()
    {
        var type = typeof(T);

        var pool = (IHayateObjectPool<T>)_poolCache.GetOrAdd(typeof(T), _ =>
        {
            var poolInstance = CreatePoolInternal<T>(null, options, scalingStrategy);
            _poolNameToType.TryAdd(type.Name, type);
            return poolInstance;
        });

        return pool;
    }

    public IHayateObjectPool<T> GetPool<T>(IHayateObjectPolicy<T> policy, HayatePoolOptions options, IHayateScalingStrategy scalingStrategy) where T : class, new()
    {
        var type = typeof(T);

        var pool = (IHayateObjectPool<T>)_poolCache.GetOrAdd(typeof(T), _ =>
        {
            var poolInstance = CreatePoolInternal(policy, options, scalingStrategy);
            _poolNameToType.TryAdd(type.Name, type);
            return poolInstance;
        });

        return pool;
    }

    public IHayateObjectPool<T> GetPool<T>(IHayateObjectPolicy<T> policy, HayatePoolOptions options, ILogger<HayateObjectPool<T>> logger, IHayateMetrics metrics) where T : class, new()
    {
        var type = typeof(T);

        var pool = (IHayateObjectPool<T>)_poolCache.GetOrAdd(typeof(T), _ =>
        {
            var poolInstance = CreatePoolInternal(policy, options, null, logger, metrics);
            _poolNameToType.TryAdd(type.Name, type);
            return poolInstance;
        });

        return pool;
    }

    public IHayateObjectPool<T> GetPool<T>(IHayateObjectPolicy<T> policy, HayatePoolOptions options, IHayateScalingStrategy scalingStrategy, ILogger<HayateObjectPool<T>> logger, IHayateMetrics metrics) where T : class, new()
    {
        var type = typeof(T);

        var pool = (IHayateObjectPool<T>)_poolCache.GetOrAdd(typeof(T), _ =>
        {
            var poolInstance = CreatePoolInternal(policy, options, scalingStrategy, logger, metrics);
            _poolNameToType.TryAdd(type.Name, type);
            return poolInstance;
        });

        return pool;
    }

    public async Task<bool> PoolExistsAsync(string poolName)
    {
        await Task.CompletedTask;
        return _poolNameToType.ContainsKey(poolName);
    }

    public async Task<HayatePoolOptions> GetPoolOptionsAsync(string poolName)
    {
        await Task.CompletedTask;
        if (!_poolNameToType.TryGetValue(poolName, out var type))
            throw new KeyNotFoundException($"Pool '{poolName}' not found");

        // 反射获取池实例并返回配置
        var getPoolMethod = typeof(HayateObjectPoolFactory)
            .GetMethod(nameof(GetPool), BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static, Type.EmptyTypes)
            ?.MakeGenericMethod(type);
        var pool = (IHayateObjectPool)getPoolMethod?.Invoke(this, null);
        return pool?.GetOptions();
    }

    public async Task<HayatePoolStats> GetPoolStatsAsync(string poolName)
    {
        await Task.CompletedTask;
        if (!_poolNameToType.TryGetValue(poolName, out var type))
            throw new KeyNotFoundException($"Pool '{poolName}' not found");

        var getPoolMethod = typeof(HayateObjectPoolFactory)
            .GetMethod(nameof(GetPool), BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static, Type.EmptyTypes)
            ?.MakeGenericMethod(type);
        var pool = (IHayateObjectPool)getPoolMethod?.Invoke(this, null);
        return pool?.GetStats();
    }

    public async Task UpdatePoolOptionsAsync(string poolName, HayatePoolOptions newConfig)
    {
        await Task.CompletedTask;
        if (!newConfig.IsValid())
            throw new ArgumentException("Invalid pool configuration", nameof(newConfig));

        if (!_poolNameToType.TryGetValue(poolName, out var type))
            throw new KeyNotFoundException($"Pool '{poolName}' not found");

        var getPoolMethod = typeof(HayateObjectPoolFactory)
            .GetMethod(nameof(GetPool), BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static, Type.EmptyTypes)
            ?.MakeGenericMethod(type);
        var pool = (IHayateObjectPool)getPoolMethod?.Invoke(this, null);
        pool?.ReloadConfig(opt =>
        {
            opt.MinPoolSize = newConfig.MinPoolSize;
            opt.MaxPoolSize = newConfig.MaxPoolSize;
            opt.MaxConcurrent = newConfig.MaxConcurrent;
            opt.EnableMetrics = newConfig.EnableMetrics;
            opt.ScalingIntervalMs = newConfig.ScalingIntervalMs;
            opt.ScaleUpThreshold = newConfig.ScaleUpThreshold;
            opt.ScaleDownThreshold = newConfig.ScaleDownThreshold;
            opt.ValidateOnBorrow = newConfig.ValidateOnBorrow;
            opt.ValidateOnReturn = newConfig.ValidateOnReturn;
            opt.ValidateWhileIdle = newConfig.ValidateWhileIdle;
            opt.ValidateIntervalMs = newConfig.ValidateIntervalMs;
            opt.MaxLifeTime = newConfig.MaxLifeTime;
            opt.MaxIdleTime = newConfig.MaxIdleTime;
            opt.SoftMinEvictableIdleTime = newConfig.SoftMinEvictableIdleTime;
            opt.EvictionIntervalMs = newConfig.EvictionIntervalMs;
            opt.NumTestsPerEvictionRun = newConfig.NumTestsPerEvictionRun;
            opt.DefaultGetTimeout = newConfig.DefaultGetTimeout;
            opt.UseFairSemaphore = newConfig.UseFairSemaphore;
            opt.LeakDetectionThreshold = newConfig.LeakDetectionThreshold;
            opt.EnableLeakDetection = newConfig.EnableLeakDetection;
            opt.RejectPolicy = newConfig.RejectPolicy;
            opt.CreationRetryCount = newConfig.CreationRetryCount;
            opt.CreationRetryDelay = newConfig.CreationRetryDelay;
            opt.ShardCount = newConfig.ShardCount;
            opt.GenerationThresholdMs = newConfig.GenerationThresholdMs;
            opt.OldGenerationValidationInterval = newConfig.OldGenerationValidationInterval;
        });
    }

    public async Task<HayatePoolSnapshot> GetPoolSnapshotAsync(string poolName)
    {
        await Task.CompletedTask;
        if (!_poolNameToType.TryGetValue(poolName, out var type))
            throw new KeyNotFoundException($"Pool '{poolName}' not found");

        var getPoolMethod = typeof(HayateObjectPoolFactory)
            .GetMethod(nameof(GetPool), BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static, Type.EmptyTypes)
            ?.MakeGenericMethod(type);
        var pool = (IHayateObjectPool)getPoolMethod?.Invoke(this, null);
        return pool?.TakeSnapshot();
    }

    public async Task ClearPoolAsync(string poolName)
    {
        await Task.CompletedTask;
        if (!_poolNameToType.TryGetValue(poolName, out var type))
            throw new KeyNotFoundException($"Pool '{poolName}' not found");

        var getPoolMethod = typeof(HayateObjectPoolFactory)
            .GetMethod(nameof(GetPool), BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static, Type.EmptyTypes)
            ?.MakeGenericMethod(type);
        var pool = (IHayateObjectPool)getPoolMethod?.Invoke(this, null);
        pool?.Clear();
    }

    private IHayateObjectPool<T> CreatePoolInternal<T>(
        IHayateObjectPolicy<T> policy,
        HayatePoolOptions options = null,
        IHayateScalingStrategy scalingStrategy = null,
        ILogger<HayateObjectPool<T>> logger = null,
        IHayateMetrics metrics = null) where T : class, new()
    {
        policy = (policy ?? new DefaultHayateObjectPolicy<T>());
        return new HayateObjectPool<T>(policy, options, scalingStrategy, logger, metrics);
    }
}