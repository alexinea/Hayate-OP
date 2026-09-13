using System;
using System.Threading;
using System.Threading.Tasks;

namespace DotNetCore.HayateOP;

/// <summary>
/// <c>using</c>-friendly borrows for every <see cref="IHayateObjectPool{T}"/>: the synchronous and
/// asynchronous ways to take an object together with the lease that puts it back.
/// </summary>
/// <remarks>
/// These are extension methods, so every pool implementation gets them without implementing anything, and
/// every existing call site keeps compiling unchanged.<br />
/// Each method borrows one object and hands it over to the returned <see cref="HayatePoolScope{T}"/>. The
/// lease returns the object when it is disposed — once per lease, on the exception paths included.<br />
/// These calls add nothing to the borrow itself: they delegate to the ordinary <c>Acquire</c> /
/// <c>AcquireAsync</c> path, so the acquire timeout, cancellation, validation and every other configured
/// semantics apply unchanged. Only the return side is automated, at the cost of one lease allocation.
/// </remarks>
/// <example>
/// <code>
/// using var lease = pool.AcquireScoped();
/// lease.Value.DoWork();
///
/// var asyncLease = await pool.AcquireScopeAsync(ct);
/// using (asyncLease) { asyncLease.Value.DoWork(); }
/// </code>
/// </example>
public static class HayatePoolScopeExtensions
{
    /// <summary>
    /// Borrows an object together with the lease that returns it, so a <c>using</c> block replaces the
    /// manual <c>try</c>/<c>finally</c>.
    /// </summary>
    /// <param name="pool">The pool to borrow from.</param>
    /// <returns>A lease holding the borrowed object; disposing it returns the object to the pool.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pool"/> is <c>null</c>.</exception>
    /// <exception cref="TimeoutException">The configured acquire timeout elapsed before an object became
    /// available, under a reject policy that waits. Nothing is borrowed in that case, so there is no lease
    /// to dispose.</exception>
    /// <remarks>
    /// Blocks for up to the pool's configured acquire timeout, exactly like <c>Acquire</c>.
    /// </remarks>
    /// <example>
    /// <code>
    /// using var lease = pool.AcquireScoped();
    /// lease.Value.DoWork();
    /// </code>
    /// </example>
    public static HayatePoolScope<T> AcquireScoped<T>(this IHayateObjectPool<T> pool) where T : class
    {
        if (pool is null) throw new ArgumentNullException(nameof(pool));

        // Nothing between Acquire and wrapping it in the lease can throw that leaves the borrow dangling:
        // either Acquire returns an object, or it throws and nothing was borrowed.
        var item = pool.Acquire();
        return new HayatePoolScope<T>(pool, item);
    }

    /// <summary>
    /// Borrows an object with an explicit wait bound, together with the lease that returns it.
    /// </summary>
    /// <param name="pool">The pool to borrow from.</param>
    /// <param name="timeout">The maximum wait time, overriding the pool's configured acquire timeout.</param>
    /// <returns>A lease holding the borrowed object; disposing it returns the object to the pool.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pool"/> is <c>null</c>.</exception>
    /// <exception cref="TimeoutException"><paramref name="timeout"/> elapsed before an object became
    /// available. Nothing is borrowed in that case.</exception>
    /// <example>
    /// <code>
    /// using var lease = pool.AcquireScoped(TimeSpan.FromSeconds(2));
    /// lease.Value.DoWork();
    /// </code>
    /// </example>
    public static HayatePoolScope<T> AcquireScoped<T>(this IHayateObjectPool<T> pool, TimeSpan timeout) where T : class
    {
        if (pool is null) throw new ArgumentNullException(nameof(pool));

        var item = pool.Acquire(timeout);
        return new HayatePoolScope<T>(pool, item);
    }

    /// <summary>
    /// Asynchronously borrows an object together with the lease that returns it, waiting without blocking a
    /// thread.
    /// </summary>
    /// <param name="pool">The pool to borrow from.</param>
    /// <param name="cancellationToken">A token that can interrupt the wait.</param>
    /// <returns>A lease holding the borrowed object; disposing it returns the object to the pool.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pool"/> is <c>null</c>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled
    /// before an object became available, or the configured acquire timeout elapsed. Nothing is borrowed in
    /// those cases.</exception>
    /// <remarks>
    /// Await this and dispose the resulting lease, so cancellation during the wait cannot leave a borrowed
    /// object unowned: when the wait fails, no object was taken at all.<br />
    /// The lease implements <see cref="IDisposable"/>, so it is used with <c>using</c> after the await —
    /// not <c>await using</c>, which would require <c>IAsyncDisposable</c> (unavailable on the
    /// <c>net48</c> and <c>netstandard2.0</c> targets). Disposal does no asynchronous work anyway.
    /// </remarks>
    /// <example>
    /// <code>
    /// using var lease = await pool.AcquireScopeAsync(cancellationToken);
    /// lease.Value.DoWork();
    /// </code>
    /// </example>
    public static async Task<HayatePoolScope<T>> AcquireScopeAsync<T>(this IHayateObjectPool<T> pool,
        CancellationToken cancellationToken = default) where T : class
    {
        if (pool is null) throw new ArgumentNullException(nameof(pool));

        var item = await pool.AcquireAsync(cancellationToken).ConfigureAwait(false);
        return new HayatePoolScope<T>(pool, item);
    }

    /// <summary>
    /// Asynchronously borrows an object with an explicit wait bound, together with the lease that returns it.
    /// </summary>
    /// <param name="pool">The pool to borrow from.</param>
    /// <param name="timeout">The maximum wait time.</param>
    /// <param name="cancellationToken">A token that can interrupt the wait.</param>
    /// <returns>A lease holding the borrowed object; disposing it returns the object to the pool.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pool"/> is <c>null</c>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.
    /// Nothing is borrowed in that case.</exception>
    /// <exception cref="TimeoutException"><paramref name="timeout"/> elapsed before an object became
    /// available, while external cancellation propagates as <see cref="OperationCanceledException"/> — the
    /// same split as the underlying asynchronous acquire. Nothing is borrowed either way.</exception>
    /// <example>
    /// <code>
    /// var lease = await pool.AcquireScopeAsync(TimeSpan.FromSeconds(3), cancellationToken);
    /// using (lease) { lease.Value.DoWork(); }
    /// </code>
    /// </example>
    public static async Task<HayatePoolScope<T>> AcquireScopeAsync<T>(this IHayateObjectPool<T> pool,
        TimeSpan timeout, CancellationToken cancellationToken = default) where T : class
    {
        if (pool is null) throw new ArgumentNullException(nameof(pool));

        var item = await pool.AcquireAsync(timeout, cancellationToken).ConfigureAwait(false);
        return new HayatePoolScope<T>(pool, item);
    }
}
