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

    public static IHayateServiceCollection RegisterHayatePool<T>(this IHayateServiceCollection services,
        Action<HayatePoolOptions> configure = null)
        where T : class, new()
    {
        var poolRegisterName = typeof(T).Name;

        services.Services.Configure<HayatePoolOptions>(poolRegisterName, configure ?? (_ => { }));

        // T05：注册池注册表（单例）。池在解析时把自己的非泛型 IHayateObjectPool 注册进去，
        // 供管理端点 / 诊断按「逻辑池名」查找，替代 Type.GetType 反射寻址。
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

            var pool = new HayatePoolBuilder<T>()
                .WithPoolName(poolRegisterName)
                .WithPolicy(policy)
                .WithScalingStrategy(scalingStrategy)
                .WithMetrics(metrics)
                .WithLogger(logger)
                .Configure(opt => options.CopyTo(opt))
                .Build();

            // 把池的非泛型面注册进注册表，端点通过池名即可寻址（T05）
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