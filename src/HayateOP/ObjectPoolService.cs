namespace DotNetCore.HayateOP;

public class ObjectPoolService<T> : ObjectPool<T> where T : class
{
    public ObjectPoolService(IPooledObjectPolicy<T> policy, int maxConcurrent, int maxPoolSize)
        : base(policy, maxConcurrent, maxPoolSize)
    {
    }
}