namespace DotNetCore.HayateOP.Policies;

/// <summary>
/// 默认对象策略池
/// </summary>
/// <typeparam name="T"></typeparam>
public class DefaultHayateObjectPolicy<T> : IHayateObjectPolicy<T> where T : class, new()
{
    public T Create() => new T();

    public bool OnRelease(T item)
    {
        if (item is IHayateResettable r) r.Reset();
        return true;
    }

    public bool Validate(T item)
    {
        if (item is IHayateValidatable v) return v.IsValid();
        return true;
    }

    public void OnAcquire(T item)
    {
    }

    public void OnPassivate(T item)
    {
    }

    public void OnDestroy(T item)
    {
    }
}