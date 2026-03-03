namespace DotNetCore.HayateOP.Policies;

/// <summary>
/// 默认对象策略池
/// </summary>
/// <typeparam name="T"></typeparam>
public class DefaultHayateObjectPolicy<T> : IHayateObjectPolicy<T> where T : class, new()
{
    public T Create() => new T();

    public bool Return(T item)
    {
        if (item is IHayateOpResettable resettable)
        {
            resettable.Reset();
        }

        return true;
    }
}