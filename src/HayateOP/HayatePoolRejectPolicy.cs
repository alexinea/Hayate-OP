namespace DotNetCore.HayateOP;

/// <summary>
///  拒绝策略枚举，定义了当请求被拒绝时的处理方式
/// </summary>
public enum HayatePoolRejectPolicy
{
    /// <summary>
    ///  直接抛出异常，拒绝请求
    /// </summary>
    Abort,
    
    /// <summary>
    ///  阻塞等待，直到有可用对象或超时，拒绝请求
    /// </summary>
    Block,
    
    /// <summary>
    ///  默认策略，阻塞等待，直到有可用对象或超时，拒绝请求，并且在超时后抛出异常
    /// </summary>
    BlockTimeout,
    
    /// <summary>
    ///  直接创建一个新的对象，绕过池的限制，拒绝请求
    /// </summary>
    CreateNew
}