using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DotNetCore.HayateOP;

public class ObjectPoolService<T> : ObjectPool<T> where T : class
{
    public ObjectPoolService(
        string name,
        IPooledObjectPolicy<T> policy,
        IOptions<ObjectPoolOptions> options,
        ILogger<ObjectPool<T>> logger)
        : base(name, policy, logger, options, null, options.Value.MaxConcurrent, options.Value.MaxPoolSize)
    {
    }
}