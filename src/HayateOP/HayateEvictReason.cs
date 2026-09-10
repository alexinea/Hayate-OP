namespace DotNetCore.HayateOP;

/// <summary>
/// The categorization basis for <see cref="IHayateObjectPool{T}.Evict(HayateEvictReason)"/>.
/// </summary>
public enum HayateEvictReason
{
    /// <summary>
    /// An idle object that has been borrowed before (LeaseCount &gt; 0) -- "used once, then cleared", suited to objects whose cohesive state
    /// is hard to reset reliably.
    /// </summary>
    Touched = 0,

    /// <summary>
    /// An object whose idle time exceeds <see cref="HayatePoolOptions.MaxIdleTime"/> (same criterion as the background eviction's
    /// idle-too-long check).
    /// </summary>
    Idle = 1,

    /// <summary>
    /// An object whose lifetime exceeds <see cref="HayatePoolOptions.MaxLifeTime"/> (same criterion as the background eviction's
    /// expired check).
    /// </summary>
    Expired = 2
}
