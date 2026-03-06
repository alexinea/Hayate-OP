using System;

namespace DotNetCore.HayateOP.Scaling;

public class ThresholdScalingStrategy : IHayateScalingStrategy
{
    public int CalculateNewSize(int currentSize, int poolCount, HayatePoolOptions options)
    {
        double usage = (double)poolCount / currentSize;

        if (usage > options.ScaleUpThreshold)
            return Math.Min(currentSize + 5, options.MaxPoolSize);

        if (usage < options.ScaleDownThreshold)
            return Math.Max(currentSize - 5, options.MinPoolSize);

        return currentSize;
    }
}