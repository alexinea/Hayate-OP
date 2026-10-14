using System;
using System.Threading;
using System.Threading.Tasks;
using DotNetCore.HayateOP;

namespace DotNetCore.HayateOP.Specialized;

/// <summary>
/// A ready-to-use pool of <see cref="CancellationTokenSource"/> instances, aligned with Reservoir's
/// <c>CancellationTokenSourcePool</c>: renting costs one line, <c>using</c> on the rented source returns
/// it, and <see cref="RentLinked(CancellationToken)"/> makes the rental follow an upstream token.
/// </summary>
/// <remarks>
/// A <see cref="CancellationTokenSource"/> is one of the most frequently pooled objects in asynchronous
/// code — the per-operation timeout or cancellation scope that would otherwise be allocated and collected
/// on every request — and it is also one of the few that cannot be reused naively: a used source carries
/// the timer and the callbacks its renter left behind, and
/// <c>CancellationTokenSource.TryReset()</c>, the one call that clears them, is documented as unsafe to
/// use while the source is still in use. The pool therefore owns the whole lifetime: a source is handed
/// out exclusively, and its reset happens only after the rental has ended, on the return path.<br />
/// <b>Reuse is runtime-dependent, and the pool says so.</b> <c>TryReset()</c> does not exist on .NET
/// Framework 4.8, and without it a returned source cannot be cleaned — its stale callbacks and timer would
/// fire during the next rental. Rather than fork its source per target framework, the pool locates the
/// method at run time (see <c>CancellationTokenSourceReset</c>): where it is missing,
/// <see cref="IsReuseSupported"/> is <c>false</c> and every returned source is destroyed instead of
/// parked, so the pool still hands out a correct source every time — it just creates a new one rather
/// than reusing. On .NET 6.0 and later reuse is on.<br />
/// The pool is built on the ordinary HayateOP engine, configured the way a small single-purpose pool
/// wants it: nothing is pre-created, a miss inside the pool size is served by creating a source on
/// demand, and the background features it has no use for (sharding, auto-scaling, eviction) are switched
/// off. Validation stays on, and is what keeps a canceled source out of the pool: a source whose
/// cancellation has been requested is destroyed on return rather than parked, because no reset can make
/// it usable again.<br />
/// Disposal routes exactly one return per rental, so <c>using</c> replaces the manual
/// <see cref="Release"/> without risking a double return — see <see cref="PooledCancellationTokenSource"/>.
/// </remarks>
/// <example>
/// <code>
/// // A per-operation cancellation scope that allocates nothing after the first few operations.
/// using var cts = CancellationTokenSourcePool.Instance.Rent();
/// cts.CancelAfter(TimeSpan.FromSeconds(5));
/// await httpClient.GetAsync(url, cts.Token);
///
/// // A rental that follows the caller's token, for as long as the rental lives.
/// using var linked = CancellationTokenSourcePool.Instance.RentLinked(requestAborted);
/// await Task.Delay(TimeSpan.FromSeconds(30), linked.Token);
/// </code>
/// </example>
public sealed class CancellationTokenSourcePool : IHayateObjectPool<PooledCancellationTokenSource>
{
    /// <summary>
    /// The default maximum pool size (16), the same value the other specialized pools use.
    /// </summary>
    public const int DefaultPoolMaximumSize = 16;

    /// <summary>
    /// A shared, thread-safe pool instance for callers that do not want to manage a pool's lifetime.
    /// </summary>
    public static CancellationTokenSourcePool Instance { get; } = new();

    /// <summary>
    /// Whether this runtime can reset a returned source and hand it out again.
    /// </summary>
    /// <remarks>
    /// <c>true</c> on .NET 6.0 and later, where <c>CancellationTokenSource.TryReset()</c> exists;
    /// <c>false</c> on .NET Framework 4.8, where the pool creates a fresh source for every rental and
    /// destroys it on return, so it retains nothing between rentals. The value is fixed for the process —
    /// it describes the runtime, not the pool — and is exposed so a caller can tell the two modes apart
    /// instead of inferring them from a source identity it happens to observe.
    /// </remarks>
    public static bool IsReuseSupported => CancellationTokenSourceReset.IsSupported;

    private readonly IHayateObjectPool<PooledCancellationTokenSource> _pool;

    /// <summary>
    /// Builds a pool holding at most <see cref="DefaultPoolMaximumSize"/> sources.
    /// </summary>
    public CancellationTokenSourcePool()
        : this(DefaultPoolMaximumSize)
    {
    }

    /// <summary>
    /// Builds a pool holding at most <paramref name="maxPoolSize"/> sources.
    /// </summary>
    /// <param name="maxPoolSize">The maximum number of sources the pool keeps and lends out.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxPoolSize"/> is not greater than
    /// zero.</exception>
    public CancellationTokenSourcePool(int maxPoolSize)
    {
        if (maxPoolSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPoolSize), maxPoolSize,
                "The pool size must be greater than zero.");
        }

        _pool = new HayatePoolBuilder<PooledCancellationTokenSource>()
            .WithPoolName(nameof(PooledCancellationTokenSource))
            .WithMinSize(0)
            .WithMaxSize(maxPoolSize)
            .WithPolicy(new CancellationTokenSourcePoolPolicy(this))
            // Lazy like every specialized pool: nothing is pre-created, and a miss inside the size is
            // served immediately by creating a source (the default wait-then-timeout policy would stall
            // every rental past the first until the background scaler caught up — and the scaler is
            // switched off here).
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand)
            // A small pool gains nothing from sharding, and a cancellation-source pool needs no background
            // features: an idle pool holds its sources and runs no timers of its own.
            .WithEnableSharding(false)
            .WithEnableAutoScaling(false)
            .WithEnableEviction(false)
            .WithEnableGenerationOptimization(false)
            .WithEnableLeakDetection(false)
            // Validation is what keeps a canceled source out of the pool; borrow-time validation catches a
            // source that was canceled while parked. Idle validation needs the background timer and is off
            // with the rest of them.
            .WithValidateOnBorrow(true)
            .WithValidateOnReturn(true)
            .WithValidateWhileIdle(false)
            .Build();
    }

    /// <summary>
    /// Rents a source that returns to this pool when disposed — the cancellation-source name for
    /// <see cref="Acquire()"/>.
    /// </summary>
    /// <returns>A source the caller owns until it is disposed, canceled, or released.</returns>
    public CancellationTokenSource Rent() => _pool.Acquire();

    /// <summary>
    /// Rents a source that is canceled when <paramref name="upstreamToken"/> is canceled, and that returns
    /// to this pool when disposed.
    /// </summary>
    /// <param name="upstreamToken">The token the rental follows. A token that cannot be canceled produces
    /// an ordinary rental.</param>
    /// <returns>A source the caller owns until it is disposed, canceled, or released.</returns>
    /// <remarks>
    /// The link is registered on the pooled source rather than by creating a source through
    /// <c>CancellationTokenSource.CreateLinkedTokenSource</c>, so the rental is still a pooled instance
    /// that returns to the pool on disposal. Disposal drops the link and waits for an upstream callback
    /// that is already running before the source can be handed out again, and an upstream token canceled
    /// after the return can no longer reach the reused source.<br />
    /// A rental whose upstream token is already canceled comes back canceled — and a canceled source is
    /// destroyed on return rather than parked, so it never reaches a later renter.
    /// </remarks>
    public CancellationTokenSource RentLinked(CancellationToken upstreamToken)
    {
        var source = _pool.Acquire();
        if (upstreamToken.CanBeCanceled)
        {
            source.RegisterUpstream(upstreamToken);
        }

        return source;
    }

    /// <inheritdoc />
    public PooledCancellationTokenSource Acquire() => _pool.Acquire();

    /// <inheritdoc />
    public PooledCancellationTokenSource Acquire(TimeSpan timeout) => _pool.Acquire(timeout);

    /// <inheritdoc />
    public bool TryAcquire(out PooledCancellationTokenSource item) => _pool.TryAcquire(out item);

    /// <inheritdoc />
    public bool TryAcquire(TimeSpan timeout, out PooledCancellationTokenSource item)
        => _pool.TryAcquire(timeout, out item);

    /// <inheritdoc />
    public Task<PooledCancellationTokenSource> AcquireAsync(CancellationToken cancellationToken = default)
        => _pool.AcquireAsync(cancellationToken);

    /// <inheritdoc />
    public Task<PooledCancellationTokenSource> AcquireAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        => _pool.AcquireAsync(timeout, cancellationToken);

    /// <summary>
    /// Returns a source to the pool it came from. Rentals normally return themselves when disposed; this is
    /// the explicit form, and it is what the engine's own contract requires.
    /// </summary>
    /// <inheritdoc />
    public void Release(PooledCancellationTokenSource item) => _pool.Release(item);

    /// <inheritdoc />
    public int Evict(HayateEvictReason reason) => _pool.Evict(reason);

    /// <inheritdoc />
    public int PreWarm(int count) => _pool.PreWarm(count);

    /// <inheritdoc />
    public HayatePoolStats GetStats() => _pool.GetStats();

    /// <inheritdoc />
    public HayatePoolSnapshot TakeSnapshot() => _pool.TakeSnapshot();

    /// <inheritdoc />
    public void ReloadConfig(Action<HayatePoolOptions> configure) => _pool.ReloadConfig(configure);

    /// <summary>
    /// Drops every source currently held by the pool, so the next rental creates a fresh one.
    /// </summary>
    /// <remarks>
    /// Rentals already handed out are not touched; they are destroyed when they come back, because the
    /// pool no longer tracks them. A dropped source has already been reset — its timer disarmed and its
    /// callbacks dropped — so nothing is left running and there is no resource to release: the object is
    /// simply collected.
    /// </remarks>
    /// <inheritdoc />
    public void Clear() => _pool.Clear();

    /// <inheritdoc />
    public HayatePoolOptions GetOptions() => _pool.GetOptions();

    /// <inheritdoc />
    public bool CheckAvailable() => _pool.CheckAvailable();

    /// <inheritdoc />
    public void SetUnavailable(string? reason = null) => _pool.SetUnavailable(reason);

    /// <inheritdoc />
    public void SetAvailable() => _pool.SetAvailable();

    /// <inheritdoc />
    public void Dispose() => _pool.Dispose();
}
