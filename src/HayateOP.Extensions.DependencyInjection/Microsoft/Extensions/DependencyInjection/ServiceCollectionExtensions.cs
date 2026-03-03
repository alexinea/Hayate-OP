using DotNetCore.HayateOP;

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
    public static IServiceCollection AddObjectPool<T>(
        this IServiceCollection services,
        Action<ObjectPoolOptions>? configure = null)
        where T : class, new()
    {
        if(services == null) throw new ArgumentNullException(nameof(services));
        
        if (configure != null)
        {
            services.Configure(configure);
        }
        else
        {
            var defaultOptions = new ObjectPoolOptions();
            services.Configure<ObjectPoolOptions>(op =>
            {
                op.MaxConcurrent = defaultOptions.MaxConcurrent;
                op.MaxPoolSize = defaultOptions.MaxPoolSize;
            });
        }
        
        services.AddSingleton<IPooledObjectPolicy<T>, DefaultPooledObjectPolicy<T>>();
        services.AddSingleton<IObjectPool<T>, ObjectPoolService<T>>();
        return services;
    }
    
    /// <summary>
    /// 添加自定义策略的对象池
    /// </summary>
    /// <param name="services"></param>
    /// <param name="configure"></param>
    /// <typeparam name="T"></typeparam>
    /// <typeparam name="TPolicy"></typeparam>
    /// <returns></returns>
    public static IServiceCollection AddObjectPool<T, TPolicy>(
        this IServiceCollection services,
        Action<ObjectPoolOptions>? configure = null)
        where T : class
        where TPolicy : class, IPooledObjectPolicy<T>
    {
        if(services == null) throw new ArgumentNullException(nameof(services));
        
        if (configure != null)
        {
            services.Configure(configure);
        }
        else
        {
            var defaultOptions = new ObjectPoolOptions();
            services.Configure<ObjectPoolOptions>(op =>
            {
                op.MaxConcurrent = defaultOptions.MaxConcurrent;
                op.MaxPoolSize = defaultOptions.MaxPoolSize;
            });
        }

        services.AddSingleton<IPooledObjectPolicy<T>, TPolicy>();
        services.AddSingleton<IObjectPool<T>, ObjectPoolService<T>>();
        return services;
    }
}