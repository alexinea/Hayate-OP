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
    
    /// <summary>
    /// 借出前激活 <br />
    ///  激活对象，在对象被借出之前调用，可以用于执行一些初始化操作，例如重置对象状态、清理资源等。这个方法的目的是确保对象在被借出时处于一个干净、可用的状态，从而提高对象池的效率和可靠性。
    /// </summary>
    /// <param name="item"></param>
    void ActivateObject(T item);
    
    /// <summary>
    /// 归还后纯化 <br />
    ///  失活对象，在对象被归还之前调用，可以用于执行一些清理操作，例如释放资源、重置对象状态等。这个方法的目的是确保对象在被归还时处于一个干净、可用的状态，从而提高对象池的效率和可靠性。
    /// </summary>
    /// <param name="item"></param>
    void PassivateObject(T item);
    
    /// <summary>
    /// 销毁前清理 <br />
    ///  销毁对象，在对象被销毁之前调用，可以用于执行一些清理操作，例如释放资源、关闭连接等。这个方法的目的是确保对象在被销毁时能够正确地释放资源，避免资源泄漏和其他潜在问题，从而提高系统的稳定性和性能。
    /// </summary>
    /// <param name="item"></param>
    void DestroyObject(T item);
}