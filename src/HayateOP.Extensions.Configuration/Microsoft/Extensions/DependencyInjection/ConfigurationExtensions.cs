using DotNetCore.HayateOP;
using DotNetCore.HayateOP.Metrics;
using DotNetCore.HayateOP.Policies;
using DotNetCore.HayateOP.Scaling;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using DotNetCore.HayateOP.Logging;

namespace Microsoft.Extensions.DependencyInjection;

public static class ConfigurationExtensions
{
    // 热更新令牌缓存，用于池释放时解绑事件，避免内存泄漏
    private static readonly ConcurrentDictionary<string, IDisposable> _changeTokenCache = new();

    public static IHayateServiceCollection RegisterGlobalConfig(this IHayateServiceCollection services,
        IConfiguration configuration,
        string configSectionPath = "HayatePool")
    {
        var globalConfig = configuration.GetSection($"{configSectionPath}:Global");
        services.Services.Configure<HayatePoolOptions>(globalConfig);

        services.Services.AddSingleton<HayatePoolConfigurationCleanup>();

        return services;
    }

    public static IHayateServiceCollection RegisterHayatePool<T>(this IHayateServiceCollection services,
        IConfiguration configuration,
        string poolName = null,
        string configSectionPath = "HayatePool")
        where T : class, new()
    {
        poolName ??= typeof(T).Name;
        var poolConfigSection = configuration.GetSection($"{configSectionPath}:Pools:{poolName}");

        // 注册池的命名配置（覆盖全局配置）
        services.Services.Configure<HayatePoolOptions>(poolName, poolConfigSection);

        // 注册实例
        services.Services.AddSingleton<IHayateObjectPool<T>>(sp =>
        {
            // 解析依赖
            var optionsMonitor = sp.GetRequiredService<IOptionsMonitor<HayatePoolOptions>>();
            var policy = sp.GetRequiredService<IHayateObjectPolicy<T>>();
            var scalingStrategy = sp.GetRequiredService<IHayateScalingStrategy>();
            var metrics = sp.GetRequiredService<IHayateMetrics>();
            var loggerFactory = sp.GetService<ILoggerFactory>();
            var logger = new HayateMicrosoftLoggerAdapter<T>(loggerFactory?.CreateLogger<T>());

            // 合并全局配置与池专属配置
            var mergedOptions = MergeOptions(optionsMonitor.CurrentValue, optionsMonitor.Get(poolName));
            if (!mergedOptions.IsValid())
            {
                throw new InvalidOperationException($"HayatePool [{poolName}] 配置无效");
            }

            // 池构建
            var pool = new HayatePoolBuilder<T>()
                .WithPoolName(poolName)
                .WithPolicy(policy)
                .WithScalingStrategy(scalingStrategy)
                .WithMetrics(metrics)
                .WithLogger(logger)
                .Configure(opt =>
                {
                    // 合并后的配置全量覆盖
                    opt.MinPoolSize = mergedOptions.MinPoolSize;
                    opt.MaxPoolSize = mergedOptions.MaxPoolSize;
                    opt.ShardCount = mergedOptions.ShardCount;
                    opt.UseFairMode = mergedOptions.UseFairMode;
                    opt.DefaultAcquireTimeout = mergedOptions.DefaultAcquireTimeout;
                    opt.ScaleUpThreshold = mergedOptions.ScaleUpThreshold;
                    opt.ScaleDownThreshold = mergedOptions.ScaleDownThreshold;
                    opt.ScaleUpCooldownSeconds = mergedOptions.ScaleUpCooldownSeconds;
                    opt.ScaleDownCooldownSeconds = mergedOptions.ScaleDownCooldownSeconds;
                    opt.ScaleUpStep = mergedOptions.ScaleUpStep;
                    opt.ValidateOnBorrow = mergedOptions.ValidateOnBorrow;
                    opt.ValidateOnReturn = mergedOptions.ValidateOnReturn;
                    opt.ValidateWhileIdle = mergedOptions.ValidateWhileIdle;
                    opt.ValidateIntervalMs = mergedOptions.ValidateIntervalMs;
                    opt.OldGenerationValidationInterval = mergedOptions.OldGenerationValidationInterval;
                    opt.GenerationThresholdMs = mergedOptions.GenerationThresholdMs;
                    opt.MaxLifeTime = mergedOptions.MaxLifeTime;
                    opt.MaxIdleTime = mergedOptions.MaxIdleTime;
                    opt.SoftMinEvictableIdleTime = mergedOptions.SoftMinEvictableIdleTime;
                    opt.EvictionIntervalMs = mergedOptions.EvictionIntervalMs;
                    opt.NumTestsPerEvictionRun = mergedOptions.NumTestsPerEvictionRun;
                    opt.CreationRetryCount = mergedOptions.CreationRetryCount;
                    opt.CreationRetryDelay = mergedOptions.CreationRetryDelay;
                    opt.LeakDetectionThreshold = mergedOptions.LeakDetectionThreshold;
                    opt.EnableLeakDetection = mergedOptions.EnableLeakDetection;
                    opt.RejectPolicy = mergedOptions.RejectPolicy;
                })
                .Build();

            // 注册配置变更监听，热更新池配置
            var changeToken = optionsMonitor.OnChange((newOptions, changedPoolName) =>
            {
                if (changedPoolName != poolName && changedPoolName != "") return;

                try
                {
                    // 合并最新配置
                    var latestOptions = MergeOptions(optionsMonitor.CurrentValue, optionsMonitor.Get(poolName));
                    if (!latestOptions.IsValid())
                    {
                        logger?.LogWarning("HayatePool [{PoolName}] 热更新配置无效，已忽略", poolName);
                        return;
                    }

                    // 同步更新到池
                    pool.ReloadConfig(opt =>
                    {
                        opt.MinPoolSize = latestOptions.MinPoolSize;
                        opt.MaxPoolSize = latestOptions.MaxPoolSize;
                        opt.ShardCount = latestOptions.ShardCount;
                        opt.UseFairMode = latestOptions.UseFairMode;
                        opt.DefaultAcquireTimeout = latestOptions.DefaultAcquireTimeout;
                        opt.ScaleUpThreshold = latestOptions.ScaleUpThreshold;
                        opt.ScaleDownThreshold = latestOptions.ScaleDownThreshold;
                        opt.ScaleUpCooldownSeconds = latestOptions.ScaleUpCooldownSeconds;
                        opt.ScaleDownCooldownSeconds = latestOptions.ScaleDownCooldownSeconds;
                        opt.ScaleUpStep = latestOptions.ScaleUpStep;
                        opt.ValidateOnBorrow = latestOptions.ValidateOnBorrow;
                        opt.ValidateOnReturn = latestOptions.ValidateOnReturn;
                        opt.ValidateWhileIdle = latestOptions.ValidateWhileIdle;
                        opt.ValidateIntervalMs = latestOptions.ValidateIntervalMs;
                        opt.OldGenerationValidationInterval = latestOptions.OldGenerationValidationInterval;
                        opt.GenerationThresholdMs = latestOptions.GenerationThresholdMs;
                        opt.MaxLifeTime = latestOptions.MaxLifeTime;
                        opt.MaxIdleTime = latestOptions.MaxIdleTime;
                        opt.SoftMinEvictableIdleTime = latestOptions.SoftMinEvictableIdleTime;
                        opt.EvictionIntervalMs = latestOptions.EvictionIntervalMs;
                        opt.NumTestsPerEvictionRun = latestOptions.NumTestsPerEvictionRun;
                        opt.CreationRetryCount = latestOptions.CreationRetryCount;
                        opt.CreationRetryDelay = latestOptions.CreationRetryDelay;
                        opt.LeakDetectionThreshold = latestOptions.LeakDetectionThreshold;
                        opt.EnableLeakDetection = latestOptions.EnableLeakDetection;
                        opt.RejectPolicy = latestOptions.RejectPolicy;
                    });

                    logger?.LogInformation("HayatePool [{PoolName}] 配置热更新成功", poolName);
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "HayatePool [{PoolName}] 配置热更新失败", poolName);
                }
            });

            // 缓存变更令牌，池释放时用于解绑事件
            _changeTokenCache.TryAdd(poolName, changeToken);

            return pool;
        });


        return services;
    }

    private static HayatePoolOptions MergeOptions(HayatePoolOptions globalOptions, HayatePoolOptions poolOptions)
    {
        var merged = new HayatePoolOptions();

        // 先应用全局配置
        ApplyOptions(globalOptions, merged);

        // 再应用池配置（覆盖全局）
        ApplyOptions(poolOptions, merged, overrideOnly: true);

        return merged;
    }

    private static void ApplyOptions(HayatePoolOptions source, HayatePoolOptions target, bool overrideOnly = false)
    {
        // 基础配置
        if (!overrideOnly || source.MinPoolSize != 5) target.MinPoolSize = source.MinPoolSize;
        if (!overrideOnly || source.MaxPoolSize != 50) target.MaxPoolSize = source.MaxPoolSize;
        if (!overrideOnly || source.ShardCount != 4) target.ShardCount = source.ShardCount;
        if (!overrideOnly || source.UseFairMode != true) target.UseFairMode = source.UseFairMode;

        // 超时配置
        if (!overrideOnly || source.DefaultAcquireTimeout != TimeSpan.FromSeconds(5))
            target.DefaultAcquireTimeout = source.DefaultAcquireTimeout;

        // 扩缩容配置
        if (!overrideOnly || source.ScaleUpThreshold != 0.8) target.ScaleUpThreshold = source.ScaleUpThreshold;
        if (!overrideOnly || source.ScaleDownThreshold != 0.2) target.ScaleDownThreshold = source.ScaleDownThreshold;
        if (!overrideOnly || source.ScaleUpCooldownSeconds != 3) target.ScaleUpCooldownSeconds = source.ScaleUpCooldownSeconds;
        if (!overrideOnly || source.ScaleDownCooldownSeconds != 15) target.ScaleDownCooldownSeconds = source.ScaleDownCooldownSeconds;
        if (!overrideOnly || source.ScaleUpStep != 5) target.ScaleUpStep = source.ScaleUpStep;

        // 验证配置
        if (!overrideOnly || source.ValidateOnBorrow != true) target.ValidateOnBorrow = source.ValidateOnBorrow;
        if (!overrideOnly || source.ValidateOnReturn != true) target.ValidateOnReturn = source.ValidateOnReturn;
        if (!overrideOnly || source.ValidateWhileIdle != true) target.ValidateWhileIdle = source.ValidateWhileIdle;
        if (!overrideOnly || source.ValidateIntervalMs != 30000) target.ValidateIntervalMs = source.ValidateIntervalMs;
        if (!overrideOnly || source.OldGenerationValidationInterval != 3) target.OldGenerationValidationInterval = source.OldGenerationValidationInterval;
        if (!overrideOnly || source.GenerationThresholdMs != 30000) target.GenerationThresholdMs = source.GenerationThresholdMs;

        // 驱逐配置
        if (!overrideOnly || source.MaxLifeTime != TimeSpan.FromMinutes(10)) target.MaxLifeTime = source.MaxLifeTime;
        if (!overrideOnly || source.MaxIdleTime != TimeSpan.FromMinutes(5)) target.MaxIdleTime = source.MaxIdleTime;
        if (!overrideOnly || source.SoftMinEvictableIdleTime != TimeSpan.FromMinutes(2)) target.SoftMinEvictableIdleTime = source.SoftMinEvictableIdleTime;
        if (!overrideOnly || source.EvictionIntervalMs != 30000) target.EvictionIntervalMs = source.EvictionIntervalMs;
        if (!overrideOnly || source.NumTestsPerEvictionRun != 10) target.NumTestsPerEvictionRun = source.NumTestsPerEvictionRun;

        // 创建配置
        if (!overrideOnly || source.CreationRetryCount != 3) target.CreationRetryCount = source.CreationRetryCount;
        if (!overrideOnly || source.CreationRetryDelay != TimeSpan.FromMilliseconds(100)) target.CreationRetryDelay = source.CreationRetryDelay;

        // 泄漏检测
        if (!overrideOnly || source.LeakDetectionThreshold != TimeSpan.FromSeconds(30)) target.LeakDetectionThreshold = source.LeakDetectionThreshold;
        if (!overrideOnly || source.EnableLeakDetection != true) target.EnableLeakDetection = source.EnableLeakDetection;

        // 拒绝策略
        if (!overrideOnly || source.RejectPolicy != HayatePoolRejectPolicy.BlockTimeout) target.RejectPolicy = source.RejectPolicy;
    }

    internal class HayatePoolConfigurationCleanup : IDisposable
    {
        public void Dispose()
        {
            // 释放所有热更新令牌，避免内存泄漏
            foreach (var token in _changeTokenCache.Values)
            {
                try
                {
                    token.Dispose();
                }
                catch
                {
                    // ignore
                }
            }
            _changeTokenCache.Clear();
        }
    }
}