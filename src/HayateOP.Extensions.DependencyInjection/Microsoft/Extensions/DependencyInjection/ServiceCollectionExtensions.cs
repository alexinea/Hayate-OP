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
    /// Adds the core HayateOP services (scaling strategy and an empty metrics sink) so pools can be registered.
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

        services.Services.AddSingleton<IHayateObjectPool<T>>(sp =>
        {
            var options = sp.GetRequiredService<IOptionsSnapshot<HayatePoolOptions>>().Get(poolRegisterName);
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
                .WithPoolName(poolRegisterName)
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
                registry.Register(poolRegisterName, pool);
            }

            return pool;
        });

        return services;
    }
}
