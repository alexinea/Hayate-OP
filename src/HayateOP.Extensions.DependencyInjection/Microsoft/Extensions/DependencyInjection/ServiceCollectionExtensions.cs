using System;
using System.Linq;
using DotNetCore.HayateOP;
using DotNetCore.HayateOP.Logging;
using DotNetCore.HayateOP.Metrics;
using DotNetCore.HayateOP.Policies;
using DotNetCore.HayateOP.Scaling;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the core HayateOP services (scaling strategy, an empty metrics sink and the run-time pool
    /// factory) so pools can be registered.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The Hayate service collection for further configuration.</returns>
    /// <example>
    /// <code>
    /// services.AddHayatePoolSupport();
    /// services.RegisterHayatePool&lt;MyConnection&gt;();
    /// </code>
    /// </example>
    public static IHayateServiceCollection AddHayatePoolSupport(this IServiceCollection services)
    {
        services.TryAddSingleton<IHayateScalingStrategy, ThresholdScalingStrategy>();
        services.TryAddSingleton<IHayateMetrics, EmptyHayateMetrics>();

        // The run-time factory registers the pools it builds, but only when a registry exists: it is
        // registered late by the pool registrations below, and asking for it here would freeze a
        // "no registry" answer into the factory.
        services.TryAddSingleton<IHayatePoolFactory>(sp =>
            new HayatePoolFactory(sp.GetService<IHayateObjectPoolRegistry>()));

        return new MSDIHayateServiceCollection(services);
    }

    /// <summary>
    /// Registers a HayateOP pool of type <typeparamref name="T"/> with the DI container.
    /// </summary>
    /// <param name="services">The Hayate service collection.</param>
    /// <param name="configure">Optional callback to configure the pool options.</param>
    /// <typeparam name="T">The pooled object type.</typeparam>
    /// <returns>The Hayate service collection for chaining.</returns>
    /// <example>
    /// <code>
    /// services.AddHayatePoolSupport();
    /// services.RegisterHayatePool&lt;MyConnection&gt;(opt =&gt; opt.MaxPoolSize = 64);
    /// </code>
    /// </example>
    public static IHayateServiceCollection RegisterHayatePool<T>(this IHayateServiceCollection services,
        Action<HayatePoolOptions>? configure = null)
        where T : class, new()
    {
        var poolRegisterName = typeof(T).Name;

        services.Services.Configure<HayatePoolOptions>(poolRegisterName, configure ?? (_ => { }));

        // Register the pool registry (singleton). Each pool registers its non-generic
        // IHayateObjectPool into it on resolution, so the management endpoints / diagnostics can
        // look it up by logical pool name instead of using Type.GetType reflection.
        services.Services.TryAddSingleton<IHayateObjectPoolRegistry, HayateObjectPoolRegistry>();

        services.Services.AddSingleton<IHayateObjectPolicy<T>, DefaultHayateObjectPolicy<T>>();

        services.Services.AddSingleton<IHayateObjectPool<T>>(sp => BuildPool<T>(sp, poolRegisterName));

        return services;
    }

    /// <summary>
    /// Registers a <b>named</b> pool of type <typeparamref name="T"/>: an independently configured
    /// pool that coexists with the unnamed pool of the same type and with any other name.
    /// </summary>
    /// <param name="services">The Hayate service collection.</param>
    /// <param name="name">The logical pool name; cannot be empty and cannot contain ':'.</param>
    /// <param name="configure">Optional callback to configure the pool options.</param>
    /// <typeparam name="T">The pooled object type.</typeparam>
    /// <returns>The Hayate service collection for chaining.</returns>
    /// <remarks>
    /// The pool is registered under the canonical name <c>{typeof(T).Name}:{name}</c> — see
    /// <see cref="HayateServiceKey"/> — so the management endpoints can tell the pools apart in logs,
    /// statistics and snapshots. It is built on first use rather than at registration time, and it is
    /// resolved through <see cref="IHayateNamedPoolAccessor"/> (or injected into a typed client via
    /// <see cref="AddPool{T, TClient}(IHayateServiceCollection, string, Action{HayatePoolOptions})"/>)
    /// rather than as <see cref="IHayateObjectPool{T}"/>, which stays reserved for the unnamed
    /// pool.<br />
    /// Declaring the same name twice keeps the last declaration, as repeated DI registrations do.
    /// </remarks>
    /// <example>
    /// <code>
    /// services.AddHayatePoolSupport()
    ///         .AddNamedPool&lt;MyConnection&gt;("primary", o =&gt; o.MaxPoolSize = 64)
    ///         .AddNamedPool&lt;MyConnection&gt;("replica", o =&gt; o.MaxPoolSize = 16);
    /// </code>
    /// </example>
    public static IHayateServiceCollection AddNamedPool<T>(this IHayateServiceCollection services,
        string name, Action<HayatePoolOptions>? configure = null)
        where T : class, new()
    {
        var key = HayateServiceKey.Create<T>(name);

        services.Services.Configure<HayatePoolOptions>(key.RegistryName, configure ?? (_ => { }));
        services.Services.TryAddSingleton<IHayateObjectPoolRegistry, HayateObjectPoolRegistry>();
        services.Services.TryAddSingleton<IHayateObjectPolicy<T>, DefaultHayateObjectPolicy<T>>();

        // One collection per element type gathers every declaration; the accessor reads them by name.
        services.Services.TryAddSingleton<HayateNamedPoolCollection<T>>();
        services.Services.AddSingleton(new HayateNamedPoolRegistration<T>(key, sp => BuildPool<T>(sp, key.RegistryName)));

        services.Services.TryAddSingleton<IHayateNamedPoolAccessor>(sp =>
            new HayateNamedPoolAccessor(sp, sp.GetService<IHayateObjectPoolRegistry>()));

        return services;
    }

    /// <summary>
    /// Registers a typed client <typeparamref name="TClient"/> that receives the named pool of
    /// <typeparamref name="T"/> in its constructor.
    /// </summary>
    /// <param name="services">The Hayate service collection.</param>
    /// <param name="name">The logical pool name the client is bound to.</param>
    /// <param name="configure">Optional callback to configure the pool options.</param>
    /// <typeparam name="T">The pooled object type.</typeparam>
    /// <typeparam name="TClient">The client type; resolved as a transient and constructed with the
    /// named pool.</typeparam>
    /// <returns>The Hayate service collection for chaining.</returns>
    /// <remarks>
    /// The client keeps a plain constructor taking <see cref="IHayateObjectPool{T}"/> and needs no
    /// HayateOP attribute: the pool it is bound to is decided here, at registration. Two clients of
    /// the same type can therefore be bound to different pools of the same element type by giving
    /// them different names — the same shape <c>AddHttpClient&lt;TClient&gt;()</c> has.
    /// </remarks>
    /// <example>
    /// <code>
    /// services.AddHayatePoolSupport()
    ///         .AddPool&lt;MyConnection, PrimaryClient&gt;("primary")
    ///         .AddPool&lt;MyConnection, ReplicaClient&gt;("replica");
    /// </code>
    /// </example>
    public static IHayateServiceCollection AddPool<T, TClient>(this IHayateServiceCollection services,
        string name, Action<HayatePoolOptions>? configure = null)
        where T : class, new()
        where TClient : class
    {
        services.AddNamedPool<T>(name, configure);

        services.Services.AddTransient<TClient>(sp =>
        {
            var accessor = sp.GetRequiredService<IHayateNamedPoolAccessor>();
            return ActivatorUtilities.CreateInstance<TClient>(sp, accessor.GetPool<T>(name));
        });

        return services;
    }

    /// <summary>
    /// Registers a typed client <typeparamref name="TClient"/> that receives the unnamed pool of
    /// <typeparamref name="T"/> — registering that pool first when it is not registered yet.
    /// </summary>
    /// <param name="services">The Hayate service collection.</param>
    /// <param name="configure">Optional callback to configure the pool options; applied only when this
    /// call registers the pool.</param>
    /// <typeparam name="T">The pooled object type.</typeparam>
    /// <typeparam name="TClient">The client type; resolved as a transient and constructed with the
    /// pool.</typeparam>
    /// <returns>The Hayate service collection for chaining.</returns>
    /// <example>
    /// <code>
    /// services.AddHayatePoolSupport()
    ///         .AddPool&lt;MyConnection, MyClient&gt;(o =&gt; o.MaxPoolSize = 64);
    /// </code>
    /// </example>
    public static IHayateServiceCollection AddPool<T, TClient>(this IHayateServiceCollection services,
        Action<HayatePoolOptions>? configure = null)
        where T : class, new()
        where TClient : class
    {
        // The unnamed pool is a singleton registration of IHayateObjectPool<T>; register it only when
        // nothing has yet, so a pool configured elsewhere is not silently replaced.
        if (!services.Services.Any(d => d.ServiceType == typeof(IHayateObjectPool<T>)))
        {
            services.RegisterHayatePool<T>(configure);
        }

        services.Services.AddTransient<TClient>();
        return services;
    }

    /// <summary>
    /// Builds the pool for <paramref name="poolName"/> from the container's named options and
    /// services, and registers it into the pool registry when one is present.
    /// </summary>
    private static IHayateObjectPool<T> BuildPool<T>(IServiceProvider sp, string poolName)
        where T : class, new()
    {
        var options = sp.GetRequiredService<IOptionsSnapshot<HayatePoolOptions>>().Get(poolName);
        var policy = sp.GetRequiredService<IHayateObjectPolicy<T>>();
        var scalingStrategy = sp.GetRequiredService<IHayateScalingStrategy>();
        var metrics = sp.GetRequiredService<IHayateMetrics>();
        var loggerFactory = sp.GetService<ILoggerFactory>();
        var logger = new HayateMicrosoftLoggerAdapter<T>(loggerFactory?.CreateLogger<T>());

        // Only attach the DI-registered custom metrics when the diagnostic surface is open — both the
        // master switch (O11) and the metrics sub-switch; otherwise keep the v2.1 semantics (the custom
        // instance has no effect) to avoid Build() fast-failing and wrongly affecting DI users. Note
        // that the options instance resolved here is not normalized (ApplyFeatureSwitches runs inside
        // Build), so the master switch has to be consulted explicitly: with diagnostics off, attaching
        // the sink would make Build() reject a configuration the user chose deliberately.
        var builder = new HayatePoolBuilder<T>()
            .WithPoolName(poolName)
            .WithPolicy(policy)
            .WithScalingStrategy(scalingStrategy)
            .WithLogger(logger);

        if (options.EnableDiagnostics && options.EnableMetrics)
        {
            builder.WithMetrics(metrics);
        }

        var pool = builder
            .Configure(opt => options.CopyTo(opt))
            .Build();

        // Register the pool's non-generic face into the registry so endpoints can address it by name.
        var registry = sp.GetService<IHayateObjectPoolRegistry>();
        if (registry != null)
        {
            registry.Register(poolName, pool);
        }

        return pool;
    }
}
