using System;
using System.Threading;
using System.Threading.Tasks;

namespace DotNetCore.HayateOP;

public interface IHayateObjectPool : IDisposable
{
    /// <summary>Returns a point-in-time snapshot of pool statistics (counts, timings, allocation tracking).</summary>
    HayatePoolStats GetStats();
    /// <summary>Returns a snapshot of every pooled object and its current state.</summary>
    HayatePoolSnapshot TakeSnapshot();
    /// <summary>Reloads pool configuration by applying the supplied mutator to the live options.</summary>
    /// <param name="configure">A callback that mutates the current <see cref="HayatePoolOptions"/>.</param>
    void ReloadConfig(Action<HayatePoolOptions> configure);
    /// <summary>Destroys every object currently held by the pool.</summary>
    void Clear();
    /// <summary>Returns the live configuration options for this pool.</summary>
    /// <returns>The pool's <see cref="HayatePoolOptions"/>.</returns>
    HayatePoolOptions GetOptions();

    /// <summary>
    /// Reports whether the pool is currently able to serve borrow requests.
    /// </summary>
    /// <returns><c>true</c> when the pool is available; <c>false</c> while the pool-level circuit breaker
    /// is open.</returns>
    /// <remarks>
    /// Always <c>true</c> when <see cref="HayatePoolOptions.EnableCircuitBreaker"/> is off, because nothing
    /// can take such a pool out of service. Cheap enough to call on a request path: a single volatile read
    /// when the feature is on, and a constant when it is off.
    /// </remarks>
    /// <example>
    /// <code>
    /// if (pool.CheckAvailable()) { /* use the pool */ }
    /// </code>
    /// </example>
    bool CheckAvailable();

    /// <summary>
    /// Reports that the dependency behind the pool failed, and takes the pool out of service once
    /// <see cref="HayateCircuitBreakerOptions.FailureThreshold"/> consecutive failures have been reported.
    /// </summary>
    /// <param name="reason">An optional description of the failure, surfaced through
    /// <see cref="HayatePoolOptions.OnUnavailable"/> and the logs.</param>
    /// <remarks>
    /// The intended caller is the application code that discovers the failure — a connection attempt that
    /// timed out, a remote call that failed — not the pool. Calling this on a pool whose circuit breaker is
    /// disabled does nothing, so instrumentation can call it unconditionally.<br />
    /// Each call counts as one consecutive failure; an explicit <see cref="SetAvailable"/> (or a successful
    /// probe) resets the streak, so sporadic failures that survive a recovery are counted from scratch again,
    /// and an application that knows the dependency is fine can call <see cref="SetAvailable"/> at any time
    /// to clear a pending streak before it trips. Once the threshold is reached the pool becomes unavailable,
    /// a further failure report changes nothing, and recovery happens either through the configured probe or
    /// through <see cref="SetAvailable"/>.
    /// </remarks>
    /// <example>
    /// <code>
    /// try { database.Ping(); }
    /// catch (Exception ex) { pool.SetUnavailable(ex.Message); }
    /// </code>
    /// </example>
    void SetUnavailable(string? reason = null);

    /// <summary>
    /// Reports that the dependency behind the pool is healthy again, and brings the pool back into service.
    /// </summary>
    /// <remarks>
    /// Closes the circuit breaker and raises <see cref="HayatePoolOptions.OnAvailable"/> — but only when the
    /// pool was actually unavailable; calling it on a pool that is already available just clears the pending
    /// failure streak and raises nothing. This is the recovery path when no
    /// <see cref="HayateCircuitBreakerOptions.Probe"/> is configured. Calling it on a pool whose circuit
    /// breaker is disabled does nothing.
    /// </remarks>
    /// <example>
    /// <code>
    /// pool.SetAvailable();   // the dependency is back; resume serving
    /// </code>
    /// </example>
    void SetAvailable();
}

/// <summary>
/// The generic object-pool contract for acquiring and releasing instances of <typeparamref name="T"/>.
/// </summary>
/// <typeparam name="T">The pooled object type.</typeparam>
public interface IHayateObjectPool<T> : IHayateObjectPool where T : class
{
    /// <summary>Acquires an object from the pool, blocking until one becomes available.</summary>
    /// <returns>A pooled object of type <typeparamref name="T"/>.</returns>
    T Acquire();
    /// <summary>Acquires an object from the pool, blocking up to the given timeout.</summary>
    /// <param name="timeout">The maximum wait time.</param>
    /// <returns>A pooled object of type <typeparamref name="T"/>.</returns>
    /// <exception cref="TimeoutException">The timeout elapsed before an object became available.</exception>
    T Acquire(TimeSpan timeout);
    Task<T> AcquireAsync(CancellationToken cancellationToken = default);
    /// <summary>
    /// Asynchronously acquires an object from the pool with a timeout boundary (aligned with the
    /// synchronous <see cref="Acquire(TimeSpan)"/> semantics): a timeout throws
    /// <see cref="TimeoutException"/>, while external cancellation propagates a
    /// <see cref="TaskCanceledException"/>.
    /// </summary>
    /// <param name="timeout">The maximum wait time.</param>
    /// <param name="cancellationToken">An external cancellation token that can interrupt the wait at any time.</param>
    /// <example>
    /// <code>
    /// var obj = await pool.AcquireAsync(TimeSpan.FromSeconds(3), cancellationToken);
    /// try { /* use obj */ }
    /// finally { pool.Release(obj); }
    /// </code>
    /// </example>
    Task<T> AcquireAsync(TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns an object to the pool so it can be reused.
    /// </summary>
    /// <param name="item">The object previously obtained from <c>Acquire</c>; must not be <c>null</c>.</param>
    /// <example>
    /// <code>
    /// pool.Release(obj);
    /// </code>
    /// </example>
    void Release(T item);

    /// <summary>
    /// Proactively evicts idle objects by category and returns the number actually evicted.
    /// Only affects <b>idle</b> objects (borrowed objects are never touched). Eviction reuses the same
    /// shard atomic-claim + idempotent destroy CAS as background eviction, so it can run concurrently
    /// with other eviction / borrow-return paths.
    /// The default background eviction (<c>EvictionCallback</c>) is unchanged — this API is an
    /// additional, on-demand operational outlet.
    /// </summary>
    /// <param name="reason">The eviction basis (Touched = evict if used, Idle = exceeds MaxIdleTime, Expired = exceeds MaxLifeTime).</param>
    /// <example>
    /// <code>
    /// int evicted = pool.Evict(HayateEvictReason.Idle);
    /// </code>
    /// </example>
    int Evict(HayateEvictReason reason);

    /// <summary>
    /// Creates idle objects until the pool holds at least <paramref name="count"/> of them, and returns how
    /// many objects this call created.
    /// </summary>
    /// <param name="count">The number of idle — immediately borrowable — objects the pool should hold; must
    /// not be negative.</param>
    /// <returns>The number of objects created by this call: <c>0</c> when the pool already holds
    /// <paramref name="count"/> idle objects or more, and <c>0</c> for a pool model that retains no idle
    /// objects at all.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    /// <exception cref="InvalidOperationException">An object could not be created after the configured
    /// number of attempts.</exception>
    /// <remarks>
    /// The count is a floor on <i>idle</i> objects, not on total objects: objects currently lent out do not
    /// count towards it, so calling this after borrowing creates replacements. The pool's own ceiling still
    /// applies — the maximum pool size in the sharded engine, the retained-object limit in the unbounded
    /// model — and a request above that ceiling warms up to the ceiling instead of failing.<br />
    /// This is the explicit counterpart of the construction-time warm-up switch: that one warms the
    /// configured minimum during construction (synchronously, or in the background and awaited by the first
    /// borrow when the wait is enabled), while this call warms any target afterwards — after raising the
    /// minimum through a configuration reload, or to pay the creation cost up front without making the first
    /// borrow wait for it. Warming beyond the configured minimum is <b>not</b> a retention promise: the extra
    /// objects age like every other idle object and are reclaimed by the idle timeout and by background
    /// scale-down.<br />
    /// The call is safe alongside concurrent borrows and returns, and concurrent calls never take the pool
    /// past its ceiling. A creation failure is reported rather than swallowed — unlike during construction,
    /// here the caller asked for the objects explicitly.
    /// </remarks>
    /// <example>
    /// <code>
    /// pool.PreWarm(32);   // the next 32 borrows need not create anything
    /// </code>
    /// </example>
    int PreWarm(int count);
}