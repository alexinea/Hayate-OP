using System;

namespace DotNetCore.HayateOP.Scaling;

public class ThresholdScalingStrategy : IHayateScalingStrategy
{
    public int CalculateNewSize(int currentSize, int idleCount, HayatePoolOptions options)
    {
        double usage = (double)(currentSize - idleCount) / currentSize;

        if (usage > options.ScaleUpThreshold)
            return Math.Min(currentSize + options.ScaleUpStep, options.MaxPoolSize);

        if (usage < options.ScaleDownThreshold)
            return Math.Max(currentSize - options.ScaleUpStep, options.MinPoolSize);

        return currentSize;
    }
}