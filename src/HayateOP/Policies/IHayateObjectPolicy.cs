namespace DotNetCore.HayateOP.Policies;

/// <summary>
/// 对象池策略接口，定义了对象池创建对象的规则
/// </summary>
/// <typeparam name="T">池化对象类型</typeparam>
public interface IHayateObjectPolicy<T> where T : class
{
    /// <summary>
    /// 创建对象
    /// </summary>
    T Create();
    /// <summary>
    /// 归还对象时触发
    /// </summary>
    bool OnRelease(T item);
    /// <summary>
    /// 验证对象有效性
    /// </summary>
    bool Validate(T item);
    /// <summary>
    /// 借出对象时触发
    /// </summary>
    void OnAcquire(T item);
    /// <summary>
    /// 对象归还到池前触发
    /// </summary>
    void OnPassivate(T item);
    /// <summary>
    /// 对象销毁时触发
    /// </summary>
    void OnDestroy(T item);
}