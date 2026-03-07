namespace DotNetCore.HayateOP.Metrics;

/// <summary>
/// 对象池诊断指标接口
/// </summary>
public interface IHayateMetrics
{
    /// <summary>
    /// 记录对象从池中取出的操作
    /// </summary>
    /// <param name="poolName">对象池名称</param>
    /// <param name="item">对象</param>
    /// <param name="elapsedMilliseconds">耗时（毫秒）</param>
    void RecordObjectAcquired(string poolName, object item, double elapsedMilliseconds);

    /// <summary>
    /// 记录对象归还到池中的操作
    /// </summary>
    /// <param name="poolName"></param>
    /// <param name="item"></param>
    /// <param name="isValid"></param>
    void RecordObjectReleased(string poolName, object item, bool isValid);

    /// <summary>
    /// 记录对象池创建新对象的操作
    /// </summary>
    /// <param name="poolName"></param>
    /// <param name="item"></param>
    void RecordObjectMiss(string poolName, object item);

    /// <summary>
    /// 记录对象池扩缩容的操作
    /// </summary>
    /// <param name="poolName"></param>
    /// <param name="action"></param>
    /// <param name="oldSize"></param>
    /// <param name="newSize"></param>
    void RecordPoolScaled(string poolName, string action, int oldSize, int newSize);
}