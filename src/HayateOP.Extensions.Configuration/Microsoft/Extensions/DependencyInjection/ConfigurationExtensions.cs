using DotNetCore.HayateOP;
using DotNetCore.HayateOP.Logging;
using DotNetCore.HayateOP.Metrics;
using DotNetCore.HayateOP.Policies;
using DotNetCore.HayateOP.Scaling;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Concurrent;
using System.Linq;

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

        services.Services.TryAddSingleton<IHayateObjectPolicy<T>, DefaultHayateObjectPolicy<T>>();

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
            var mergedOptions = MergeOptions(optionsMonitor.CurrentValue, poolConfigSection);
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
                .Configure(opt => mergedOptions.CopyTo(opt))
                .Build();

            // 注册配置变更监听，热更新池配置
            var changeToken = optionsMonitor.OnChange((newOptions, changedPoolName) =>
            {
                
                if (changedPoolName != poolName && changedPoolName != Options.Options.DefaultName) return;

                try
                {
                    // 合并最新配置
                    var latestOptions = MergeOptions(optionsMonitor.CurrentValue, poolConfigSection);
                    if (!latestOptions.IsValid())
                    {
                        logger?.LogWarning("HayatePool [{PoolName}] 热更新配置无效，已忽略", poolName);
                        return;
                    }

                    // 同步更新到池
                    pool.ReloadConfig(opt => latestOptions.CopyTo(opt));

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

    private static HayatePoolOptions MergeOptions(HayatePoolOptions globalOptions, IConfiguration poolSection)
    {
        var merged = new HayatePoolOptions();

        // 先应用全局配置
        globalOptions.CopyTo(merged);

        // 再应用池配置（覆盖全局）
        ApplyPoolOptionsOverrides(merged, poolSection);

        merged.ApplyFeatureSwitches();

        return merged;
    }

    private static void ApplyPoolOptionsOverrides(HayatePoolOptions target, IConfiguration poolSection)
    {
        // T13 修复（默认值误判）：原实现以「值 != C# 默认值」判断池配置是否显式设置，
        // 用户显式配置为默认值时（如 MaxPoolSize 配回默认 100）会被误判为"未覆盖"，
        // 导致全局配置意外生效。改为只应用配置节中真实出现的键：
        // 值来自 binder 对整个配置节的绑定结果（正确处理 int/bool/TimeSpan 等），
        // 缺席的键保持全局配置值。poolOptions 参数仅保留签名兼容用途。
        var properties = typeof(HayatePoolOptions).GetProperties()
            .Where(p => p.CanRead && p.CanWrite)
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var bound = new HayatePoolOptions();
        poolSection.Bind(bound);

        foreach (var child in poolSection.GetChildren())
        {
            if (!properties.TryGetValue(child.Key, out var prop)) continue;

            prop.SetValue(target, prop.GetValue(bound));
        }
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