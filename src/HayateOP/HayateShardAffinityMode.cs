namespace DotNetCore.HayateOP;

/// <summary>
/// The shard-affinity mode for the borrow path (2.5).
/// Determines the starting position when Acquire / AcquireAsync scan shards - after hitting the starting shard it continues scanning in a ring,
/// so no mode sacrifices availability; they only affect hit priority and locality.
/// </summary>
public enum HayateShardAffinityMode
{
    /// <summary>
    /// Sequential scan (default): starts from shard 0 and scans in index order, identical to the pre-2.5 behavior with zero extra overhead.
    /// </summary>
    None = 0,

    /// <summary>
    /// Thread affinity: stably maps the starting shard from the managed thread id (the same thread
    /// always prefers the same shard first), which helps reuse objects in the CPU cache / NUMA node /
    /// thread-local resources.
    /// </summary>
    Thread = 1,

    /// <summary>
    /// Custom delegate: the starting shard is decided by <see cref="HayatePoolOptions.CustomShardAffinity"/>.
    /// When the delegate returns null or an out-of-range value, the borrow falls back to sequential scanning (it does not throw, preserving borrow-path robustness).
    /// </summary>
    Custom = 2
}
