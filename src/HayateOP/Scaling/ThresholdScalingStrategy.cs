using System;

namespace DotNetCore.HayateOP.Scaling;

public class ThresholdScalingStrategy : IHayateScalingStrategy
{
    /// <summary>
    /// Computes the next pool size from the current usage ratio. Scales up when usage exceeds the
    /// configured scale-up threshold, scales down when below the scale-down threshold, and clamps the
    /// result to the configured min/max pool size.
    /// </summary>
    /// <param name="currentSize">The current pool size.</param>
    /// <param name="idleCount">The number of currently idle (available) objects.</param>
    /// <param name="options">The pool options supplying thresholds, steps, and bounds.</param>
    /// <returns>The computed next pool size (may equal <paramref name="currentSize"/> when no scaling occurs).</returns>
    public int CalculateNewSize(int currentSize, int idleCount, HayatePoolOptions options)
    {
        // Guard: when currentSize <= 0, return it directly to avoid a divide-by-zero NaN from polluting downstream calculations.
        if (currentSize <= 0) return currentSize;

        double usage = (double)(currentSize - idleCount) / currentSize;

        if (usage > options.ScaleUpThreshold)
            return Math.Min(currentSize + options.ScaleUpStep, options.MaxPoolSize);

        if (usage < options.ScaleDownThreshold)
            return Math.Max(currentSize - options.ScaleDownStep, options.MinPoolSize);

        return currentSize;
    }
}