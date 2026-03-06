namespace DotNetCore.HayateOP.Policies;

/// <summary>
/// 对象池策略接口，定义了对象池创建对象的规则
/// </summary>
/// <typeparam name="T"></typeparam>
public interface IHayateObjectPolicy<T> where T : class
{
    T Create();
    bool Return(T item);
    bool Validate(T item);
    void ActivateObject(T item);
    void PassivateObject(T item);
    void DestroyObject(T item);
}