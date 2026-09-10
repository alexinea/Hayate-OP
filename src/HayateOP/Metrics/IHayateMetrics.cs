namespace DotNetCore.HayateOP.Metrics;

/// <summary>
/// Diagnostic metrics interface for object pools.
/// </summary>
public interface IHayateMetrics
{
    /// <summary>
    /// Records the operation of taking an object from the pool.
    /// </summary>
    /// <param name="poolName">The object pool name.</param>
    /// <param name="item">The object taken from the pool.</param>
    /// <param name="elapsedMilliseconds">The elapsed time of the operation, in milliseconds.</param>
    void RecordObjectAcquired(string poolName, object item, double elapsedMilliseconds);

    /// <summary>
    /// Records the operation of returning an object to the pool.
    /// </summary>
    /// <param name="poolName">The object pool name.</param>
    /// <param name="item">The object returned to the pool.</param>
    /// <param name="isValid"><c>true</c> if the returned object passed validation; otherwise <c>false</c>.</param>
    void RecordObjectReleased(string poolName, object item, bool isValid);

    /// <summary>
    /// Records a pool miss (a new object had to be created).
    /// </summary>
    /// <param name="poolName">The object pool name.</param>
    void RecordObjectMiss(string poolName);

    /// <summary>
    /// Records a pool scale-up or scale-down operation.
    /// </summary>
    /// <param name="poolName">The object pool name.</param>
    /// <param name="action">The scaling action performed (e.g. "scale-up" / "scale-down").</param>
    /// <param name="oldSize">The pool size before scaling.</param>
    /// <param name="newSize">The pool size after scaling.</param>
    void RecordPoolScaled(string poolName, string action, int oldSize, int newSize);
}