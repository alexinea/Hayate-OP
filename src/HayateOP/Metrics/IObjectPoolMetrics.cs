namespace DotNetCore.HayateOP.Metrics;

/// <summary>
/// 对象池诊断指标接口
/// </summary>
public interface IObjectPoolMetrics
{
    /// <summary>
    /// 记录对象从池中取出的操作
    /// </summary>
    /// <param name="poolName">对象池名称</param>
    /// <param name="success">是否获取成功</param>
    /// <param name="item">对象</param>
    /// <param name="elapsedMilliseconds">耗时（毫秒）</param>
    void RecordObjectAcquired(string poolName, object item, bool success, double elapsedMilliseconds);

    /// <summary>
    /// 记录对象归还到池中的操作
    /// </summary>
    /// <param name="poolName"></param>
    /// <param name="item"></param>
    /// <param name="isValid"></param>
    void RecordObjectReturned(string poolName, object item, bool isValid);

    /// <summary>
    /// 记录对象池创建新对象的操作
    /// </summary>
    /// <param name="poolName"></param>
    /// <param name="item"></param>
    void RecordObjectCreated(string poolName, object item);
}