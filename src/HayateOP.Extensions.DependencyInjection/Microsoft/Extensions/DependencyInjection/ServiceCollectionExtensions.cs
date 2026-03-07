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
    public static IHayateServiceCollection AddHayatePoolSupport(this IServiceCollection services)
    {
        services.TryAddSingleton<IHayateScalingStrategy, ThresholdScalingStrategy>();
        services.TryAddSingleton<IHayateMetrics, EmptyHayateMetrics>();

        return new MSDIHayateServiceCollection(services);
    }

    public static IHayateServiceCollection RegisterHayatePool<T>(this IHayateServiceCollection services, Action<HayatePoolOptions> configure = null)
        where T : class, new()
    {
        if (configure != null)
            services.Services.Configure<HayatePoolOptions>(typeof(T).Name, configure);

        services.Services.AddSingleton<IHayateObjectPolicy<T>, DefaultHayateObjectPolicy<T>>();

        services.Services.AddSingleton<IHayateObjectPool<T>>(sp =>
        {
            var options = sp.GetRequiredService<IOptionsSnapshot<HayatePoolOptions>>().Get(typeof(T).Name);
            var policy = sp.GetRequiredService<IHayateObjectPolicy<T>>();
            var scalingStrategy = sp.GetRequiredService<IHayateScalingStrategy>();
            var metrics = sp.GetRequiredService<IHayateMetrics>();
            var loggerFactory = sp.GetService<ILoggerFactory>();
            var logger = new HayateMicrosoftLoggerAdapter<T>(loggerFactory?.CreateLogger<T>());

            return new HayatePoolBuilder<T>()
                .WithPoolName(typeof(T).Name)
                .WithPolicy(policy)
                .WithScalingStrategy(scalingStrategy)
                .WithMetrics(metrics)
                .WithLogger(logger)
                .Configure(opt =>
                {
                    opt.MinPoolSize = options.MinPoolSize;
                    opt.MaxPoolSize = options.MaxPoolSize;
                    opt.ShardCount = options.ShardCount;
                    opt.UseFairMode = options.UseFairMode;
                    opt.DefaultAcquireTimeout = options.DefaultAcquireTimeout;
                    opt.ScaleUpThreshold = options.ScaleUpThreshold;
                    opt.ScaleDownThreshold = options.ScaleDownThreshold;
                    opt.ScaleUpCooldownSeconds = options.ScaleUpCooldownSeconds;
                    opt.ScaleDownCooldownSeconds = options.ScaleDownCooldownSeconds;
                    opt.ScaleUpStep = options.ScaleUpStep;
                    opt.ValidateOnBorrow = options.ValidateOnBorrow;
                    opt.ValidateOnReturn = options.ValidateOnReturn;
                    opt.ValidateWhileIdle = options.ValidateWhileIdle;
                    opt.ValidateIntervalMs = options.ValidateIntervalMs;
                    opt.OldGenerationValidationInterval = options.OldGenerationValidationInterval;
                    opt.GenerationThresholdMs = options.GenerationThresholdMs;
                    opt.MaxLifeTime = options.MaxLifeTime;
                    opt.MaxIdleTime = options.MaxIdleTime;
                    opt.SoftMinEvictableIdleTime = options.SoftMinEvictableIdleTime;
                    opt.EvictionIntervalMs = options.EvictionIntervalMs;
                    opt.NumTestsPerEvictionRun = options.NumTestsPerEvictionRun;
                    opt.CreationRetryCount = options.CreationRetryCount;
                    opt.CreationRetryDelay = options.CreationRetryDelay;
                    opt.LeakDetectionThreshold = options.LeakDetectionThreshold;
                    opt.EnableLeakDetection = options.EnableLeakDetection;
                    opt.RejectPolicy = options.RejectPolicy;
                })
                .Build();
        });

        return services;
    }
}