using HayateOP;
using HayateOP.Metrics;
using HayateOP.Policies;

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
        Action<HayateOpOptions>? configure = null)
        where T : class, new()
    {
        if (services == null) throw new ArgumentNullException(nameof(services));

        if (configure != null)
        {
            services.Configure(configure);
        }
        else
        {
            var defaultOptions = new HayateOpOptions();
            services.Configure<HayateOpOptions>(op =>
            {
                op.MaxConcurrent = defaultOptions.MaxConcurrent;
                op.MaxPoolSize = defaultOptions.MaxPoolSize;
            });
        }

        services.AddLogging();

        services.AddSingleton<IHayateObjectPolicy<T>, DefaultHayateObjectPolicy<T>>();
        services.AddSingleton<IHayateOpMetrics, EmptyHayateOpMetrics>();
        services.AddSingleton<IHayateObjectPool<T>, HayateObjectPoolService<T>>();

        return new MsdiHayateServiceCollection<T>(services);
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
        Action<HayateOpOptions>? configure = null)
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
            var defaultOptions = new HayateOpOptions();
            services.Configure<HayateOpOptions>(op =>
            {
                op.MaxConcurrent = defaultOptions.MaxConcurrent;
                op.MaxPoolSize = defaultOptions.MaxPoolSize;
            });
        }

        services.AddLogging();

        services.AddSingleton<IHayateObjectPolicy<T>, TPolicy>();
        services.AddSingleton<IHayateOpMetrics, EmptyHayateOpMetrics>();
        services.AddSingleton<IHayateObjectPool<T>, HayateObjectPoolService<T>>();

        return new MsdiHayateServiceCollection<T>(services);
    }
}