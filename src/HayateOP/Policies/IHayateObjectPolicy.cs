namespace DotNetCore.HayateOP.Policies;

/// <summary>
/// 对象池策略接口，定义了对象池创建对象的规则
/// </summary>
/// <typeparam name="T"></typeparam>
public interface IHayateObjectPolicy<T> where T : class
{
    T Create();
    bool OnRelease(T item);
    bool Validate(T item);
    void OnAcquire(T item);
    void OnPassivate(T item);
    void OnDestroy(T item);
}