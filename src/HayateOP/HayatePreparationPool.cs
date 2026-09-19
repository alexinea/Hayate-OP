using System;
using System.Threading;
using System.Threading.Tasks;

namespace DotNetCore.HayateOP;

/// <summary>
/// Wraps any <see cref="IHayateObjectPool{T}"/> with an <see cref="IHayatePreparationStrategy{T}"/>
/// (N3): every borrow runs the strategy's ready-check and, when the object is not ready, its
/// asynchronous prepare (reconnect) — so the caller receives a usable object or an exception, never
/// a silently broken one.
/// </summary>
/// <remarks>
/// The wrapper is transparent for everything else: <see cref="Release"/>, statistics, snapshots and
/// every other member delegate to the inner pool unchanged, and objects that pass the ready-check
/// cost exactly one asynchronous no-op round-trip over a plain borrow.<br />
/// <b>Failure handling.</b> When <see cref="IHayatePreparationStrategy{T}.PrepareAsync"/> fails
/// (throws), the object is handed to the constructor's <c>onDiscard</c> callback — give it the
/// object's real disposal (a connection's <c>Dispose</c>) so a broken connection does not linger —
/// and the borrow retries with another object. When the retry budget (<c>maxPrepareAttempts</c>,
/// default 3) is exhausted, the last exception propagates to the caller. A retry borrows again from the inner
/// pool, which may create a fresh object; the strategy then sees that object and typically reports
/// it ready or prepares it with the initial handshake.<br />
/// <b>Asynchronous borrows.</b> <see cref="HayatePreparationPool{T}.AcquireAsync(CancellationToken)"/>
/// awaits the inner pool's own asynchronous borrow, and
/// <see cref="HayatePreparationPool{T}.AcquireAsync(TimeSpan, CancellationToken)"/> forwards its
/// <c>timeout</c> to it as well. A borrow that has to wait therefore suspends the borrowing context
/// instead of occupying a thread, and the wait converges on the inner pool's own timeout contract
/// (the inner pool's <see cref="TimeoutException"/>, or its cancellation when the token is
/// cancelled).<br />
/// <b>Sync over async.</b> The synchronous <see cref="HayatePreparationPool{T}.Acquire()"/> runs the same chain by blocking on
/// it (<c>GetAwaiter().GetResult()</c>), and <see cref="HayatePreparationPool{T}.Acquire(TimeSpan)"/>
/// forwards its <c>timeout</c> to the inner pool. That keeps the semantics identical across the sync
/// and async paths (like HikariCP's <c>getConnection</c> testing connections under the hood), but a
/// strategy doing real I/O will block the calling thread, and blocking on tasks under a
/// <see cref="SynchronizationContext"/> can deadlock — under such a host, borrow asynchronously.
/// </remarks>
/// <example>
/// <code>
/// var pool = new HayatePoolBuilder&lt;SmtpConnection&gt;()
///     .WithMaxPoolSize(10)
///     .Build()
///     .WithPreparation(
///         new SmtpReconnectStrategy(),
///         onDiscard: conn =&gt; conn.Dispose());
///
/// var conn = await pool.AcquireAsync();   // ready or repaired, or the reconnect error throws
/// </code>
/// </example>
/// <typeparam name="T">The pooled object type.</typeparam>
public sealed class HayatePreparationPool<T> : IHayateObjectPool<T> where T : class
{
    private readonly IHayateObjectPool<T> _inner;
    private readonly IHayatePreparationStrategy<T> _strategy;
    private readonly Action<T>? _onDiscard;
    private readonly int _maxPrepareAttempts;

    /// <summary>
    /// Wraps <paramref name="inner"/> with the given preparation strategy.
    /// </summary>
    /// <param name="inner">The pool providing the objects.</param>
    /// <param name="strategy">The ready-check / prepare strategy run on every borrow.</param>
    /// <param name="onDiscard">Invoked with an object whose preparation failed, before the borrow
    /// retries; typically the object's real disposal. <c>null</c> leaves the object to the
    /// GC.</param>
    /// <param name="maxPrepareAttempts">How many objects a single borrow may run through the
    /// prepare chain before the last failure propagates; must be greater than zero.</param>
    /// <exception cref="ArgumentNullException"><paramref name="inner"/> or
    /// <paramref name="strategy"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxPrepareAttempts"/> is not
    /// greater than zero.</exception>
    public HayatePreparationPool(
        IHayateObjectPool<T> inner,
        IHayatePreparationStrategy<T> strategy,
        Action<T>? onDiscard = null,
        int maxPrepareAttempts = 3)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
        if (maxPrepareAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPrepareAttempts), maxPrepareAttempts,
                "The prepare attempt limit must be greater than zero.");
        }

        _onDiscard = onDiscard;
        _maxPrepareAttempts = maxPrepareAttempts;
    }

    /// <summary>
    /// Acquires an object from the inner pool and runs the preparation chain (ready-check, then
    /// prepare if needed) before delivering it, blocking on the asynchronous chain.
    /// </summary>
    /// <inheritdoc />
    public T Acquire() => AcquireAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Acquires an object from the inner pool, waiting at most <paramref name="timeout"/> for one to
    /// become available, and runs the preparation chain before delivering it — blocking on the
    /// asynchronous chain.
    /// </summary>
    /// <inheritdoc />
    public T Acquire(TimeSpan timeout) => AcquireAsync(timeout).GetAwaiter().GetResult();

    /// <summary>
    /// Acquires an object from the inner pool and runs the preparation chain (ready-check, then
    /// prepare if needed) before delivering it.
    /// </summary>
    /// <remarks>
    /// Each attempt borrows from the inner pool: a ready object is delivered immediately; a
    /// not-ready object goes through <see cref="IHayatePreparationStrategy{T}.PrepareAsync"/>; a
    /// failed prepare discards the object and retries with another borrow. When the attempt budget
    /// is exhausted the last prepare failure propagates, so a caller never receives a silently
    /// broken object — it either gets a usable one or the reconnect error.<br />
    /// The borrow itself is awaited asynchronously, so a borrow that has to wait suspends the caller
    /// rather than blocking a thread for the whole of the inner pool's acquire timeout.
    /// </remarks>
    /// <inheritdoc />
    public Task<T> AcquireAsync(CancellationToken cancellationToken = default)
        => AcquireAsyncCore(null, cancellationToken);

    /// <inheritdoc />
    public Task<T> AcquireAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        => AcquireAsyncCore(timeout, cancellationToken);

    /// <summary>
    /// Runs the borrow-and-prepare chain, forwarding the optional <paramref name="timeout"/> to the
    /// inner pool's borrow.
    /// </summary>
    private async Task<T> AcquireAsyncCore(TimeSpan? timeout, CancellationToken cancellationToken)
    {
        Exception? lastFailure = null;
        for (var attempt = 1; attempt <= _maxPrepareAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The inner pool's asynchronous borrow, with the timeout forwarded when there is one: an
            // exhausted pool suspends this context (no thread held for the duration of the wait), and a
            // borrow that never completes within the timeout surfaces the inner pool's TimeoutException.
            // Nothing is held yet, so an unsuccessful borrow simply propagates — there is no object to
            // discard.
            var item = timeout.HasValue
                ? await _inner.AcquireAsync(timeout.Value, cancellationToken).ConfigureAwait(false)
                : await _inner.AcquireAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (await _strategy.IsReadyAsync(item, cancellationToken).ConfigureAwait(false))
                {
                    return item;
                }

                await _strategy.PrepareAsync(item, cancellationToken).ConfigureAwait(false);
                return item;
            }
            catch (OperationCanceledException)
            {
                Discard(item);
                throw;
            }
            catch (Exception ex)
            {
                lastFailure = ex;
                Discard(item);
            }
        }

        throw new HayatePoolPreparationException(
            $"The preparation strategy failed for {_maxPrepareAttempts} consecutive objects; the last failure is attached.",
            lastFailure);
    }

    /// <summary>
    /// Returns an object to the inner pool; delegated unchanged.
    /// </summary>
    /// <inheritdoc />
    public void Release(T item) => _inner.Release(item);

    /// <inheritdoc />
    public HayatePoolStats GetStats() => _inner.GetStats();

    /// <inheritdoc />
    public HayatePoolSnapshot TakeSnapshot() => _inner.TakeSnapshot();

    /// <summary>
    /// The inner pool is reconfigured; the preparation strategy is unaffected.
    /// </summary>
    /// <inheritdoc />
    public void ReloadConfig(Action<HayatePoolOptions> configure) => _inner.ReloadConfig(configure);

    /// <inheritdoc />
    public void Clear() => _inner.Clear();

    /// <inheritdoc />
    public HayatePoolOptions GetOptions() => _inner.GetOptions();

    /// <inheritdoc />
    public bool CheckAvailable() => _inner.CheckAvailable();

    /// <inheritdoc />
    public void SetUnavailable(string? reason = null) => _inner.SetUnavailable(reason);

    /// <inheritdoc />
    public void SetAvailable() => _inner.SetAvailable();

    /// <inheritdoc />
    public int Evict(HayateEvictReason reason) => _inner.Evict(reason);

    /// <summary>
    /// Forwards the warm-up to the inner pool.
    /// </summary>
    /// <remarks>
    /// Warming creates objects only: every borrow still runs the preparation chain, so a warmed object is not
    /// a ready one, and the first borrows pay the readiness check (and any repair) exactly as before. What the
    /// warm-up removes is the creation cost, the same as it does for an undecorated pool.
    /// </remarks>
    /// <inheritdoc />
    public int PreWarm(int count) => _inner.PreWarm(count);

    /// <summary>
    /// Disposes the inner pool. The preparation strategy carries no resources of its own.
    /// </summary>
    /// <inheritdoc />
    public void Dispose() => _inner.Dispose();

    private void Discard(T item)
    {
        try
        {
            _onDiscard?.Invoke(item);
        }
        catch
        {
            // The discard callback must not mask the preparation failure it is invoked for.
        }
    }
}

/// <summary>
/// Thrown by <see cref="HayatePreparationPool{T}.AcquireAsync(System.Threading.CancellationToken)"/> when the preparation strategy
/// failed for every object the borrow ran through the prepare chain; the last strategy failure is
/// attached as the inner exception.
/// </summary>
public sealed class HayatePoolPreparationException : Exception
{
    /// <summary>
    /// Builds the exception with the attempt count in the message and the last strategy failure
    /// attached.
    /// </summary>
    public HayatePoolPreparationException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Install helpers for <see cref="IHayatePreparationStrategy{T}"/> (N3).
/// </summary>
public static class HayatePreparationPoolExtensions
{
    /// <summary>
    /// Wraps this pool with <paramref name="strategy"/>, so every borrow runs the ready-check and,
    /// when the object is not ready, the asynchronous prepare (reconnect) before the caller sees
    /// it. See <see cref="HayatePreparationPool{T}"/> for the failure handling and the sync-over-
    /// async caveat of the synchronous borrows.
    /// </summary>
    /// <typeparam name="T">The pooled object type.</typeparam>
    /// <param name="pool">The pool providing the objects.</param>
    /// <param name="strategy">The ready-check / prepare strategy run on every borrow.</param>
    /// <param name="onDiscard">Invoked with an object whose preparation failed; typically the
    /// object's real disposal.</param>
    /// <param name="maxPrepareAttempts">How many objects a single borrow may run through the
    /// prepare chain before the last failure propagates.</param>
    /// <returns>A pool whose borrows deliver ready-or-repaired objects.</returns>
    public static HayatePreparationPool<T> WithPreparation<T>(
        this IHayateObjectPool<T> pool,
        IHayatePreparationStrategy<T> strategy,
        Action<T>? onDiscard = null,
        int maxPrepareAttempts = 3)
        where T : class
        => new(pool, strategy, onDiscard, maxPrepareAttempts);
}
