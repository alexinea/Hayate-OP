namespace DotNetCore.HayateOP;

/// <summary>
/// 对象池策略接口，定义了对象池创建对象的规则
/// </summary>
/// <typeparam name="T"></typeparam>
public interface IPooledObjectPolicy<T> where T : class
{
    T Create();
    bool Return(T item);
}