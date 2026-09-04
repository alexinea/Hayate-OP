using System;

namespace DotNetCore.HayateOP.Scaling;

public class ThresholdScalingStrategy : IHayateScalingStrategy
{
    public int CalculateNewSize(int currentSize, int idleCount, HayatePoolOptions options)
    {
        // 防御：currentSize<=0 时直接返回，避免除零产生的 NaN 继续污染下游
        if (currentSize <= 0) return currentSize;

        double usage = (double)(currentSize - idleCount) / currentSize;

        if (usage > options.ScaleUpThreshold)
            return Math.Min(currentSize + options.ScaleUpStep, options.MaxPoolSize);

        if (usage < options.ScaleDownThreshold)
            return Math.Max(currentSize - options.ScaleDownStep, options.MinPoolSize);

        return currentSize;
    }
}