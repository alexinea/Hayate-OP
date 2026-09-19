using System;

namespace DotNetCore.HayateOP;

/// <summary>
/// One idle object offered to an <see cref="IHayateEvictionPolicy{T}"/>: the pooled instance plus the
/// timings and thresholds the background eviction run measured for it.
/// </summary>
/// <typeparam name="T">The pooled element type.</typeparam>
/// <remarks>
/// The value is a snapshot — the pool builds it when the object is tested and the policy only reads
/// it. The object itself is handed over as <see cref="Item"/> so a policy can inspect its real state
/// (an open socket, a connection that answered a probe) rather than deciding on age alone; the
/// bookkeeping wrapper the pool keeps around it stays internal, so a policy cannot corrupt the
/// lifecycle metadata by writing to it.<br />
/// Every duration is already measured in <see cref="TimeSpan"/> and every threshold is the value the
/// pool is actually running with (including the defaults it filled in), so a policy never has to
/// reach back into <see cref="HayatePoolOptions"/> — and it keeps working when the options are hot
/// reloaded.
/// </remarks>
public readonly struct HayateEvictionCandidate<T> where T : class
{
    /// <summary>
    /// Creates a candidate. The pool builds these on every eviction run; the constructor is public so
    /// a policy can be unit-tested without a pool.
    /// </summary>
    /// <param name="item">The idle object under test.</param>
    /// <param name="age">Time since the object was created (the <see cref="MaxLifeTime"/> basis).</param>
    /// <param name="idleTime">Time since the object was last returned (the <see cref="MaxIdleTime"/> basis).</param>
    /// <param name="leaseCount">Number of times the object has been borrowed.</param>
    /// <param name="idleCount">Idle objects in the shard holding this object, this one included.</param>
    /// <param name="minIdleCount">The shard's share of the minimum pool size (see <see cref="MinIdleCount"/>).</param>
    /// <param name="maxLifeTime">The configured maximum lifetime.</param>
    /// <param name="maxIdleTime">The configured maximum idle time.</param>
    /// <param name="softMinEvictableIdleTime">The configured soft minimum evictable idle time.</param>
    /// <param name="shardIndex">Index of the shard the object currently belongs to.</param>
    public HayateEvictionCandidate(T item, TimeSpan age, TimeSpan idleTime, int leaseCount, int idleCount,
        int minIdleCount, TimeSpan maxLifeTime, TimeSpan maxIdleTime, TimeSpan softMinEvictableIdleTime,
        int shardIndex)
    {
        Item = item;
        Age = age;
        IdleTime = idleTime;
        LeaseCount = leaseCount;
        IdleCount = idleCount;
        MinIdleCount = minIdleCount;
        MaxLifeTime = maxLifeTime;
        MaxIdleTime = maxIdleTime;
        SoftMinEvictableIdleTime = softMinEvictableIdleTime;
        ShardIndex = shardIndex;
    }

    /// <summary>The idle pooled instance the run is testing. Never <c>null</c>: only idle objects are candidates.</summary>
    public T Item { get; }

    /// <summary>Time since <see cref="Item"/> was created — the quantity <see cref="MaxLifeTime"/> bounds.</summary>
    public TimeSpan Age { get; }

    /// <summary>Time since <see cref="Item"/> was last returned — the quantity <see cref="MaxIdleTime"/> bounds.</summary>
    public TimeSpan IdleTime { get; }

    /// <summary>
    /// How many times <see cref="Item"/> has been borrowed so far. Unlike the other measurements this
    /// is a lifetime total, so it is the axis to use when the object's freshness is what matters
    /// (retire a pooled object after a fixed number of leases) rather than the wall clock.
    /// </summary>
    public int LeaseCount { get; }

    /// <summary>
    /// Idle objects in the shard at the moment of the test, this candidate included — the quantity the
    /// soft minimum is compared against.
    /// </summary>
    public int IdleCount { get; }

    /// <summary>
    /// The shard's share of <see cref="HayatePoolOptions.MinPoolSize"/> —
    /// <c>MinPoolSize / ShardCount</c>, integer division, exactly the figure the run has always
    /// compared <see cref="IdleCount"/> against. Idle objects above this count are the ones the pool is
    /// allowed to shed, so a soft-idle verdict should always be combined with
    /// <c>IdleCount &gt; MinIdleCount</c>.
    /// </summary>
    public int MinIdleCount { get; }

    /// <summary>The configured maximum lifetime (<see cref="HayatePoolOptions.MaxLifeTime"/>).</summary>
    public TimeSpan MaxLifeTime { get; }

    /// <summary>The configured maximum idle time (<see cref="HayatePoolOptions.MaxIdleTime"/>).</summary>
    public TimeSpan MaxIdleTime { get; }

    /// <summary>The configured soft minimum evictable idle time (<see cref="HayatePoolOptions.SoftMinEvictableIdleTime"/>).</summary>
    public TimeSpan SoftMinEvictableIdleTime { get; }

    /// <summary>Index of the shard the object currently belongs to (diagnostics only).</summary>
    public int ShardIndex { get; }
}
