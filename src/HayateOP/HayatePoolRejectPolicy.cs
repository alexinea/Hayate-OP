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
    ///  先等待归还、等满 acquire timeout 后才创建新对象（并非名字暗示的「立即绕过池建新」）。
    ///  创建的对象会登记进池、归还时可回池复用（T15）；空池冷启动首次调用最多付出一个完整超时延迟。
    /// </summary>
    CreateNew
}