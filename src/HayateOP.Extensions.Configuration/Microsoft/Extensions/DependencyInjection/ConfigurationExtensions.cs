using DotNetCore.HayateOP;
using DotNetCore.HayateOP.DependencyInjection;
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
    // Hot-reload change-token cache; unbound on pool disposal to avoid memory leaks.
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

    /// <summary>
    /// Registers a HayateOP pool driven by configuration.
    /// </summary>
    /// <param name="services">The Hayate service collection.</param>
    /// <param name="configuration">The configuration root used to read pool options.</param>
    /// <param name="poolName">Optional logical pool name; defaults to the type name of <typeparamref name="T"/>.</param>
    /// <param name="configSectionPath">Configuration section path; defaults to "HayatePool".</param>
    /// <typeparam name="T">The pooled object type.</typeparam>
    /// <exception cref="InvalidOperationException">Thrown when the merged pool configuration is invalid.</exception>
    /// <example>
    /// <code>
    /// services.RegisterHayatePool&lt;MyConnection&gt;(configuration);
    /// // or with an explicit name and section:
    /// services.RegisterHayatePool&lt;MyConnection&gt;(configuration, poolName: "db", configSectionPath: "Pools");
    /// </code>
    /// </example>
    public static IHayateServiceCollection RegisterHayatePool<T>(this IHayateServiceCollection services,
        IConfiguration configuration,
        string? poolName = null,
        string configSectionPath = "HayatePool")
        where T : class
    {
        poolName ??= typeof(T).Name;
        var poolConfigSection = configuration.GetSection($"{configSectionPath}:Pools:{poolName}");

        // Register the pool's named configuration (overrides the global configuration).
        services.Services.Configure<HayatePoolOptions>(poolName, poolConfigSection);

        services.Services.TryAddSingleton<IHayateObjectPolicy<T>>(sp => HayateObjectPolicies.Default<T>());

        // Register the pool instance.
        services.Services.AddSingleton<IHayateObjectPool<T>>(sp =>
            BuildConfiguredPool<T>(sp, poolName, poolConfigSection, registerInRegistry: false));

        return services;
    }

    /// <summary>
    /// Registers a <b>named</b> pool of type <typeparamref name="T"/> driven by configuration: the
    /// configuration-driven counterpart of <c>AddNamedPool&lt;T&gt;(name)</c>.
    /// </summary>
    /// <param name="services">The Hayate service collection.</param>
    /// <param name="configuration">The configuration root used to read pool options.</param>
    /// <param name="name">The logical pool name; also the name of the per-pool configuration section
    /// (<c>{configSectionPath}:Pools:{name}</c>).</param>
    /// <param name="configSectionPath">Configuration section path; defaults to "HayatePool".</param>
    /// <typeparam name="T">The pooled object type.</typeparam>
    /// <exception cref="InvalidOperationException">Thrown when the merged pool configuration is invalid.</exception>
    /// <remarks>
    /// The pool is registered under the canonical name <c>{typeof(T).Name}:{name}</c> and resolved
    /// through <see cref="IHayateNamedPoolAccessor"/>, so a configuration-driven named pool coexists
    /// with the unnamed pool of the same type and with any other name — exactly as a pool declared
    /// with <c>AddNamedPool</c> does. Section merging and hot reload behave as they do for
    /// <see cref="RegisterHayatePool{T}(IHayateServiceCollection, IConfiguration, string?, string)"/>:
    /// the global section is applied first and only the keys present in the pool's own section
    /// override it.
    /// </remarks>
    /// <example>
    /// <code>
    /// // appsettings.json:  "HayatePool": { "Pools": { "replica": { "MaxPoolSize": 16 } } }
    /// services.RegisterNamedHayatePool&lt;MyConnection&gt;(configuration, "replica");
    /// </code>
    /// </example>
    public static IHayateServiceCollection RegisterNamedHayatePool<T>(this IHayateServiceCollection services,
        IConfiguration configuration,
        string name,
        string configSectionPath = "HayatePool")
        where T : class
    {
        var key = HayateServiceKey.Create<T>(name);
        var poolConfigSection = configuration.GetSection($"{configSectionPath}:Pools:{name}");

        // Options are keyed by the canonical registry name so two pools of the same type never share
        // an options entry; the configuration section keeps the plain logical name, which is what a
        // user writes in appsettings.
        services.Services.Configure<HayatePoolOptions>(key.RegistryName, poolConfigSection);
        services.Services.TryAddSingleton<IHayateObjectPoolRegistry, HayateObjectPoolRegistry>();
        services.Services.TryAddSingleton<IHayateObjectPolicy<T>>(sp => HayateObjectPolicies.Default<T>());
        services.Services.AddSingleton<HayatePoolConfigurationCleanup>();

        services.Services.TryAddSingleton<HayateNamedPoolCollection<T>>();
        services.Services.AddSingleton(new HayateNamedPoolRegistration<T>(key,
            sp => BuildConfiguredPool<T>(sp, key.RegistryName, poolConfigSection, registerInRegistry: true)));
        services.Services.TryAddSingleton<IHayateNamedPoolAccessor>(sp =>
            new HayateNamedPoolAccessor(sp, sp.GetService<IHayateObjectPoolRegistry>()));

        return services;
    }

    /// <summary>
    /// Builds the pool for <paramref name="poolName"/> by merging the global configuration with the
    /// pool's own section, subscribes it to configuration hot reload, and — for named pools —
    /// registers it into the pool registry.
    /// </summary>
    private static IHayateObjectPool<T> BuildConfiguredPool<T>(IServiceProvider sp, string poolName,
        IConfiguration poolConfigSection, bool registerInRegistry)
        where T : class
    {
        // Resolve dependencies.
        var optionsMonitor = sp.GetRequiredService<IOptionsMonitor<HayatePoolOptions>>();
        var policy = sp.GetRequiredService<IHayateObjectPolicy<T>>();
        var scalingStrategy = sp.GetRequiredService<IHayateScalingStrategy>();
        var metrics = sp.GetRequiredService<IHayateMetrics>();
        var loggerFactory = sp.GetService<ILoggerFactory>();

        // The pool logs under its own MEL category. The builder resolves the same category through the
        // factory at Build time; this instance is the one the hot-reload callback below keeps writing to.
        // L3: as on the DI path, a null ILoggerFactory falls back to the built-in logger inside the
        // factory rather than to a silent one.
        var hayateLoggerFactory = new HayateMicrosoftLoggerFactory(loggerFactory);
        var logger = hayateLoggerFactory.CreateLogger(poolName);

        // Merge the global and pool-specific configuration.
        var mergedOptions = MergeOptions(optionsMonitor.CurrentValue, poolConfigSection);
        if (!mergedOptions.IsValid())
        {
            throw new InvalidOperationException($"HayatePool [{poolName}] configuration is invalid");
        }

        // Build the pool.
        // Only attach the DI-registered custom metrics when the metrics master switch is enabled;
        // otherwise keep the v2.1 semantics (the custom instance has no effect) to avoid Build()
        // fast-failing and wrongly affecting Configuration users.
        var builder = new HayatePoolBuilder<T>()
            .WithPoolName(poolName)
            .WithPolicy(policy)
            .WithScalingStrategy(scalingStrategy)
            .WithLoggerFactory(hayateLoggerFactory);

        if (mergedOptions.EnableMetrics)
        {
            builder.WithMetrics(metrics);
        }

        var pool = builder
            .Configure(opt => mergedOptions.CopyTo(opt))
            .Build();

        if (registerInRegistry)
        {
            var registry = sp.GetService<IHayateObjectPoolRegistry>();
            registry?.Register(poolName, pool);
        }

        // Subscribe to configuration change tokens to hot-reload the pool configuration.
        var changeToken = optionsMonitor.OnChange((newOptions, changedPoolName) =>
        {
            if (changedPoolName != poolName && changedPoolName != Options.Options.DefaultName) return;

            try
            {
                // Merge the latest configuration.
                var latestOptions = MergeOptions(optionsMonitor.CurrentValue, poolConfigSection);
                if (!latestOptions.IsValid())
                {
                    logger?.LogWarning("HayatePool [{PoolName}] hot-reload configuration is invalid; ignored", poolName);
                    return;
                }

                // Sync the update to the pool.
                pool.ReloadConfig(opt => latestOptions.CopyTo(opt));

                logger?.LogInformation("HayatePool [{PoolName}] configuration hot-reloaded successfully", poolName);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "HayatePool [{PoolName}] configuration hot-reload failed", poolName);
            }
        });

        // Cache the change token so it can be unbound when the pool is disposed.
        _changeTokenCache.TryAdd(poolName, changeToken!);

        return pool;
    }

    private static HayatePoolOptions MergeOptions(HayatePoolOptions globalOptions, IConfiguration poolSection)
    {
        var merged = new HayatePoolOptions();

        // Apply the global configuration first.
        globalOptions.CopyTo(merged);

        // Then apply the pool configuration (overrides the global one).
        ApplyPoolOptionsOverrides(merged, poolSection);

        merged.ApplyFeatureSwitches();

        return merged;
    }

    private static void ApplyPoolOptionsOverrides(HayatePoolOptions target, IConfiguration poolSection)
    {
        // Fix for default-value misjudgment: the original implementation treated a value differing
        // from the C# default as "explicitly set". When a user explicitly set a value back to its
        // default (e.g. MaxPoolSize back to the default 100) it was misjudged as "not overridden",
        // causing the global configuration to take effect unexpectedly. Now only keys that actually
        // appear in the configuration section are applied: the bound values come from the binder's
        // result for the whole section (correctly handling int/bool/TimeSpan, etc.), and absent keys
        // keep the global configuration value. The poolOptions parameter is retained only for
        // signature compatibility.
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
            // Dispose all hot-reload tokens to avoid memory leaks.
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
