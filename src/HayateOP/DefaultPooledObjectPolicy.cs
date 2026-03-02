namespace DotNetCore.HayateOP;

/// <summary>
/// 默认对象策略池
/// </summary>
/// <typeparam name="T"></typeparam>
public class DefaultPooledObjectPolicy<T> : IPooledObjectPolicy<T> where T : class, new()
{
    public T Create() => new T();

    public bool Return(T item)
    {
        if (item is IResettable resettable)
        {
            resettable.Reset();
        }

        return true;
    }
}