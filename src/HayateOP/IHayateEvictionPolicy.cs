namespace DotNetCore.HayateOP;

/// <summary>
/// Decides whether an idle pooled object should be destroyed by the background eviction run — the
/// pluggable form of the rule the pool otherwise applies on its own (the counterpart of CHOPIN's
/// <c>EvictionPolicyClassName</c>).
/// </summary>
/// <typeparam name="T">The pooled element type.</typeparam>
/// <remarks>
/// Install a policy with <see cref="HayatePoolBuilder{T}.WithEvictionPolicy"/>. A pool that installs
/// none asks <see cref="HayateDefaultEvictionPolicy{T}.Instance"/>, whose verdict is exactly the rule
/// the pool applied before this extension point existed: lifetime exceeded, idle time exceeded, or
/// soft-idle past the shard's share of <see cref="HayatePoolOptions.MinPoolSize"/>.
/// <para>
/// The policy is the whole rule for the run — it is asked about every candidate and its answer
/// decides. A policy that never returns <c>true</c> therefore turns idle eviction off, which is a
/// supported configuration (a pool whose objects are expensive and safe to keep); the pool still
/// reclaims abandoned objects through K2 and still honours an explicit
/// <see cref="IHayateObjectPool{T}.Evict(HayateEvictReason)"/> call, neither of which goes through
/// this interface.
/// </para>
/// <para>
/// Only idle objects are offered. An object that is currently borrowed never reaches the policy, and
/// no verdict can make one evictable: the shard re-checks ownership in its claim protocol, so an
/// object handed out between the test and the removal is skipped rather than destroyed under the
/// borrower. Each shard contributes at most <see cref="HayatePoolOptions.NumTestsPerEvictionRun"/>
/// objects per run, taken from the head of its idle list — a policy sees the oldest idles first, not
/// the whole pool.
/// </para>
/// <para>
/// The policy runs on the maintenance timer, so it must return quickly and must not block that
/// thread. It is called concurrently from the shards' runs and should be stateless (or thread-safe);
/// an exception it throws is caught by the run and logged, like any other background-eviction failure.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// private sealed class RetireWellUsedConnections : IHayateEvictionPolicy&lt;Connection&gt;
/// {
///     public bool ShouldEvict(in HayateEvictionCandidate&lt;Connection&gt; candidate)
///         =&gt; candidate.LeaseCount &gt;= 1_000
///            || candidate.Age &gt; candidate.MaxLifeTime
///            || candidate.IdleTime &gt; candidate.MaxIdleTime;
/// }
///
/// var pool = new HayatePoolBuilder&lt;Connection&gt;()
///     .WithEvictionPolicy(new RetireWellUsedConnections())
///     .WithMaxLifeTime(TimeSpan.FromMinutes(30))
///     .Build();
/// </code>
/// </example>
public interface IHayateEvictionPolicy<T> where T : class
{
    /// <summary>
    /// Returns <c>true</c> when the tested object must be destroyed.
    /// </summary>
    /// <param name="candidate">
    /// The idle object under test and the timings, thresholds and idle count the run measured for it.
    /// </param>
    /// <returns><c>true</c> to evict the object, <c>false</c> to leave it in the pool.</returns>
    bool ShouldEvict(in HayateEvictionCandidate<T> candidate);
}
