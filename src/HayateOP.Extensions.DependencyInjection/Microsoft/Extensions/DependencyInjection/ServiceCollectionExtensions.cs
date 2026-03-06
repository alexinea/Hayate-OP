using DotNetCore.HayateOP;
using DotNetCore.HayateOP.Metrics;
using DotNetCore.HayateOP.Policies;
using DotNetCore.HayateOP.Scaling;

namespace Microsoft.Extensions.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 添加默认对象池
    /// </summary>
    /// <param name="services"></param>
    /// <param name="configure"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static IHayateServiceCollection<T> AddHayateObjectPool<T>(
        this IServiceCollection services,
        Action<HayatePoolOptions>? configure = null)
        where T : class, new()
    {
        if (services == null) throw new ArgumentNullException(nameof(services));

        if (configure != null)
        {
            services.Configure(configure);
        }
        else
        {
            var defaultOptions = new HayatePoolOptions();
            services.Configure<HayatePoolOptions>(op =>
            {
                op.MaxConcurrent = defaultOptions.MaxConcurrent;
                op.MinPoolSize = defaultOptions.MinPoolSize;
                op.MaxPoolSize = defaultOptions.MaxPoolSize;
                op.EnableMetrics= defaultOptions.EnableMetrics;
                op.ScalingIntervalMs = defaultOptions.ScalingIntervalMs;
                op.ScaleUpThreshold = defaultOptions.ScaleUpThreshold;
                op.ScaleDownThreshold = defaultOptions.ScaleDownThreshold;
                op.ValidateOnBorrow = defaultOptions.ValidateOnBorrow;
                op.ValidateOnReturn = defaultOptions.ValidateOnReturn;
                op.ValidateWhileIdle = defaultOptions.ValidateWhileIdle;
                op.ValidateIntervalMs= defaultOptions.ValidateIntervalMs;
                op.MaxLifeTime= defaultOptions.MaxLifeTime;
                op.MaxIdleTime= defaultOptions.MaxIdleTime;
                op.SoftMinEvictableIdleTime= defaultOptions.SoftMinEvictableIdleTime;
                op.EvictionIntervalMs= defaultOptions.EvictionIntervalMs;
                op.NumTestsPerEvictionRun = defaultOptions.NumTestsPerEvictionRun;
                op.DefaultGetTimeout = defaultOptions.DefaultGetTimeout;
                op.UseFairSemaphore = defaultOptions.UseFairSemaphore;
                op.LeakDetectionThreshold= defaultOptions.LeakDetectionThreshold;
                op.EnableLeakDetection = defaultOptions.EnableLeakDetection;
                op.RejectPolicy = defaultOptions.RejectPolicy;
                op.CreationRetryCount = defaultOptions.CreationRetryCount;
                op.CreationRetryDelay = defaultOptions.CreationRetryDelay;
                op.ShardCount = defaultOptions.ShardCount;
                op.GenerationThresholdMs= defaultOptions.GenerationThresholdMs;
                op.OldGenerationValidationInterval = defaultOptions.OldGenerationValidationInterval;
            });
        }

        services.AddLogging();

        services.AddSingleton<IHayateScalingStrategy, ThresholdScalingStrategy>();
        services.AddSingleton<IHayateObjectPolicy<T>, DefaultHayateObjectPolicy<T>>();
        services.AddSingleton<IHayateMetrics, EmptyHayateMetrics>();
        services.AddSingleton<IHayateObjectPoolFactory, HayateObjectPoolFactory>();
        services.AddSingleton<IHayateObjectPool<T>, HayateObjectPoolService<T>>();

        return new MSDIHayateServiceCollection<T>(services);
    }

    /// <summary>
    /// 添加自定义策略的对象池
    /// </summary>
    /// <param name="services"></param>
    /// <param name="configure"></param>
    /// <typeparam name="T"></typeparam>
    /// <typeparam name="TPolicy"></typeparam>
    /// <returns></returns>
    public static IHayateServiceCollection<T> AddHayateObjectPool<T, TPolicy>(
        this IServiceCollection services,
        Action<HayatePoolOptions>? configure = null)
        where T : class, new()
        where TPolicy : class, IHayateObjectPolicy<T>
    {
        if (services == null) throw new ArgumentNullException(nameof(services));

        if (configure != null)
        {
            services.Configure(configure);
        }
        else
        {
            var defaultOptions = new HayatePoolOptions();
            services.Configure<HayatePoolOptions>(op =>
            {
                op.MaxConcurrent = defaultOptions.MaxConcurrent;
                op.MinPoolSize = defaultOptions.MinPoolSize;
                op.MaxPoolSize = defaultOptions.MaxPoolSize;
                op.EnableMetrics = defaultOptions.EnableMetrics;
                op.ScalingIntervalMs = defaultOptions.ScalingIntervalMs;
                op.ScaleUpThreshold = defaultOptions.ScaleUpThreshold;
                op.ScaleDownThreshold = defaultOptions.ScaleDownThreshold;
                op.ValidateOnBorrow = defaultOptions.ValidateOnBorrow;
                op.ValidateOnReturn = defaultOptions.ValidateOnReturn;
                op.ValidateWhileIdle = defaultOptions.ValidateWhileIdle;
                op.ValidateIntervalMs = defaultOptions.ValidateIntervalMs;
                op.MaxLifeTime = defaultOptions.MaxLifeTime;
                op.MaxIdleTime = defaultOptions.MaxIdleTime;
                op.SoftMinEvictableIdleTime = defaultOptions.SoftMinEvictableIdleTime;
                op.EvictionIntervalMs = defaultOptions.EvictionIntervalMs;
                op.NumTestsPerEvictionRun = defaultOptions.NumTestsPerEvictionRun;
                op.DefaultGetTimeout = defaultOptions.DefaultGetTimeout;
                op.UseFairSemaphore = defaultOptions.UseFairSemaphore;
                op.LeakDetectionThreshold = defaultOptions.LeakDetectionThreshold;
                op.EnableLeakDetection = defaultOptions.EnableLeakDetection;
                op.RejectPolicy = defaultOptions.RejectPolicy;
                op.CreationRetryCount = defaultOptions.CreationRetryCount;
                op.CreationRetryDelay = defaultOptions.CreationRetryDelay;
                op.ShardCount = defaultOptions.ShardCount;
                op.GenerationThresholdMs = defaultOptions.GenerationThresholdMs;
                op.OldGenerationValidationInterval = defaultOptions.OldGenerationValidationInterval;
            });
        }

        services.AddLogging();

        services.AddSingleton<IHayateScalingStrategy, ThresholdScalingStrategy>();
        services.AddSingleton<IHayateObjectPolicy<T>, TPolicy>();
        services.AddSingleton<IHayateMetrics, EmptyHayateMetrics>();
        services.AddSingleton<IHayateObjectPoolFactory, HayateObjectPoolFactory>();
        services.AddSingleton<IHayateObjectPool<T>, HayateObjectPoolService<T>>();

        return new MSDIHayateServiceCollection<T>(services);
    }
}