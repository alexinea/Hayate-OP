namespace DotNetCore.HayateOP.Specialized;

/// <summary>
/// The capacity ladder the specialized pools route declared-capacity borrows through: a request within
/// the pool's minimum creation capacity is served by the base tier, anything larger by the smallest
/// bucket on the exponential ladder <c>minimum × 2ⁿ</c> that covers it — the same bucketing shape
/// <c>ArrayPool</c> uses, anchored at the pool's own minimum so buckets stay few and aligned.
/// </summary>
internal static class PoolCapacityTiers
{
    /// <summary>
    /// Resolves the tier bucket a declared capacity is served from: <c>0</c> for the base tier when the
    /// request fits the minimum creation capacity, otherwise the bucket capacity (always at least the
    /// request, never more than twice it), clamped to <see cref="int.MaxValue"/> if the ladder
    /// overflows.
    /// </summary>
    public static int Bucket(int minimumCapacity, int requestedCapacity)
    {
        if (requestedCapacity <= minimumCapacity)
        {
            return 0;
        }

        var bucket = minimumCapacity;
        while (bucket < requestedCapacity)
        {
            var doubled = bucket << 1;
            if (doubled <= 0)
            {
                // The ladder ran past int.MaxValue: serve everything larger from a single top tier
                // instead of wrapping into negative capacities.
                return int.MaxValue;
            }

            bucket = doubled;
        }

        return bucket;
    }
}
