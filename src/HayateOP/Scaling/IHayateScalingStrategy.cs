namespace DotNetCore.HayateOP.Scaling;

/// <summary>
/// Auto-scaling strategy interface that computes the new pool size from the current pool state and options.
/// </summary>
public interface IHayateScalingStrategy
{
    /// <summary>
    /// Computes the new pool size from the current usage.
    /// </summary>
    /// <param name="currentSize">The current pool size.</param>
    /// <param name="idleCount">The number of currently idle (available) objects.</param>
    /// <param name="options">The pool options supplying thresholds, steps, and min/max bounds.</param>
    /// <returns>The computed new pool size, clamped to the configured bounds.</returns>
    /// <example>
    /// <code>
    /// var strategy = new ThresholdScalingStrategy();
    /// int next = strategy.CalculateNewSize(pool.CurrentSize, pool.AvailableSlots, pool.GetOptions());
    /// </code>
    /// </example>
    int CalculateNewSize(int currentSize, int idleCount, HayatePoolOptions options);
}