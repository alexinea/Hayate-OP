using DotNetCore.HayateOP;

namespace Microsoft.Extensions.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 添加默认对象池
    /// </summary>
    /// <param name="services"></param>
    /// <param name="maxConcurrent"></param>
    /// <param name="maxPoolSize"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static IServiceCollection AddObjectPool<T>(this IServiceCollection services,
        int maxConcurrent = 10, int maxPoolSize = 20) where T : class, new()
    {
        services.AddSingleton<IPooledObjectPolicy<T>, DefaultPooledObjectPolicy<T>>();
        services.AddSingleton<IObjectPool<T>, ObjectPoolService<T>>(sp =>
        {
            var policy = sp.GetRequiredService<IPooledObjectPolicy<T>>();
            return new ObjectPoolService<T>(policy, maxConcurrent, maxPoolSize);
        });
        return services;
    }

    /// <summary>
    /// 添加自定义策略的对象池
    /// </summary>
    /// <param name="services"></param>
    /// <param name="maxConcurrent"></param>
    /// <param name="maxPoolSize"></param>
    /// <typeparam name="T"></typeparam>
    /// <typeparam name="TPolicy"></typeparam>
    /// <returns></returns>
    public static IServiceCollection AddObjectPool<T, TPolicy>(this IServiceCollection services,
        int maxConcurrent = 10, int maxPoolSize = 20)
        where T : class
        where TPolicy : class, IPooledObjectPolicy<T>
    {
        services.AddSingleton<IPooledObjectPolicy<T>, TPolicy>();
        services.AddSingleton<IObjectPool<T>, ObjectPoolService<T>>(sp =>
        {
            var policy = sp.GetRequiredService<IPooledObjectPolicy<T>>();
            return new ObjectPoolService<T>(policy, maxConcurrent, maxPoolSize);
        });
        return services;
    }
}