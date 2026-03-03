using Microsoft.Extensions.Options;

namespace DotNetCore.HayateOP;

public class ObjectPoolService<T> : ObjectPool<T> where T : class
{
    public ObjectPoolService(IPooledObjectPolicy<T> policy, IOptions<ObjectPoolOptions> options)
        : base(policy, options.Value.MaxConcurrent, options.Value.MaxPoolSize)
    {
    }
}