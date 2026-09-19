namespace DotNetCore.HayateOP;

/// <summary>
/// The rule every pool applies when no eviction policy is installed — the rule the background
/// eviction run used before the <see cref="IHayateEvictionPolicy{T}"/> extension point existed:
/// </summary>
/// <list type="bullet">
/// <item><description>the object has outlived its maximum lifetime
/// (<see cref="HayatePoolOptions.MaxLifeTime"/>) — it is expired regardless of how busy the pool is;</description></item>
/// <item><description>the object has been idle longer than <see cref="HayatePoolOptions.MaxIdleTime"/>;</description></item>
/// <item><description>or the object has been idle longer than
/// <see cref="HayatePoolOptions.SoftMinEvictableIdleTime"/> <em>and</em> its shard still holds more idle
/// objects than its share of <see cref="HayatePoolOptions.MinPoolSize"/>, so the pool shrinks back
/// towards its minimum instead of waiting the full idle time out.</description></item>
/// </list>
/// <remarks>
/// Stateless and therefore thread-safe and free to share: <see cref="Instance"/> is a single instance
/// per element type, and it is what a pool that was not given a policy holds. A pool recognizes this
/// instance and keeps the built-in evaluation for its log line, which is why installing it explicitly
/// is the same as installing nothing.
/// </remarks>
public sealed class HayateDefaultEvictionPolicy<T> : IHayateEvictionPolicy<T> where T : class
{
    /// <summary>
    /// The shared, stateless instance. Every pool that is not given another policy evaluates its
    /// eviction run through this one.
    /// </summary>
    public static HayateDefaultEvictionPolicy<T> Instance { get; } = new HayateDefaultEvictionPolicy<T>();

    /// <summary>
    /// Evaluates the three rules above, in the order the run has always applied them.
    /// </summary>
    /// <param name="candidate">The idle object under test.</param>
    /// <returns><c>true</c> when the object is expired, idle too long, or softly idle above the minimum.</returns>
    public bool ShouldEvict(in HayateEvictionCandidate<T> candidate)
    {
        // Lifetime first: an expired object goes whatever the load is, MinPoolSize included.
        if (candidate.Age > candidate.MaxLifeTime) return true;

        // Then the hard idle limit, which is likewise unconditional.
        if (candidate.IdleTime > candidate.MaxIdleTime) return true;

        // Soft minimum idleness is conditional on the pool being above its floor: shedding this
        // object is only allowed while the shard holds more idle objects than its share of MinPoolSize.
        return candidate.IdleTime > candidate.SoftMinEvictableIdleTime
               && candidate.IdleCount > candidate.MinIdleCount;
    }
}
