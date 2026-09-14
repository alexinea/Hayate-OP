using System;
using System.Threading;
using System.Threading.Tasks;
using DotNetCore.HayateOP;

namespace DotNetCore.HayateOP.Specialized;

/// <summary>
/// A ready-to-use pool of <see cref="PooledMemoryStream"/> instances, aligned with P89OP's
/// <c>MemoryStreamPool</c>: borrowing costs one line, and <c>using</c> on the borrowed stream returns it.
/// </summary>
/// <remarks>
/// The pool is built on the ordinary HayateOP engine, configured the way a specialized stream pool
/// wants it: nothing is pre-created, a miss inside the pool size is served by creating a stream on
/// demand, and the two background features a stream pool has no use for (auto-scaling, eviction) are
/// switched off — so an idle pool holds its streams and runs no timers. Validation stays on: it is what
/// enforces the capacity window below, on every return.<br />
/// Streams are created with <see cref="MinimumMemoryStreamCapacity"/> bytes and may grow while borrowed;
/// a stream that comes back with a capacity outside the <see cref="MinimumMemoryStreamCapacity"/> /
/// <see cref="MaximumMemoryStreamCapacity"/> window is destroyed instead of being parked, so buffers
/// never linger larger than the configuration allows. Returned streams are reset to empty (position and
/// length zero) before the next borrow.<br />
/// Tightening the window clears the pool, matching P89OP: raising the minimum or lowering the maximum
/// destroys every parked stream, because none of them can pass the new window. Widening keeps them.
/// </remarks>
/// <example>
/// <code>
/// using var stream = MemoryStreamPool.Instance.GetObject();
/// stream.Write(payload, 0, payload.Length);   // returned to the pool at the end of the block
/// </code>
/// </example>
public sealed class MemoryStreamPool : IHayateObjectPool<PooledMemoryStream>
{
    /// <summary>
    /// The default maximum pool size (16), the same value P89OP's <c>ObjectPool.DefaultPoolMaximumSize</c>
    /// uses for its specialized pools.
    /// </summary>
    public const int DefaultPoolMaximumSize = 16;

    /// <summary>
    /// The default minimum memory stream capacity: 4KB, the P89OP default.
    /// </summary>
    public const int DefaultMinimumMemoryStreamCapacity = 4 * 1024;

    /// <summary>
    /// The default maximum memory stream capacity: 512KB, the P89OP default.
    /// </summary>
    public const int DefaultMaximumMemoryStreamCapacity = 512 * 1024;

    /// <summary>
    /// A shared, thread-safe pool instance for callers that do not want to manage a pool's lifetime.
    /// </summary>
    public static MemoryStreamPool Instance { get; } = new();

    private readonly IHayateObjectPool<PooledMemoryStream> _pool;
    private readonly MemoryStreamPoolPolicy _policy;
    private int _minimumCapacity = DefaultMinimumMemoryStreamCapacity;
    private int _maximumCapacity = DefaultMaximumMemoryStreamCapacity;

    /// <summary>
    /// Builds a pool holding at most <see cref="DefaultPoolMaximumSize"/> streams.
    /// </summary>
    public MemoryStreamPool()
        : this(DefaultPoolMaximumSize)
    {
    }

    /// <summary>
    /// Builds a pool holding at most <paramref name="maxPoolSize"/> streams.
    /// </summary>
    /// <param name="maxPoolSize">The maximum number of streams the pool keeps and lends out.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxPoolSize"/> is not greater than
    /// zero.</exception>
    public MemoryStreamPool(int maxPoolSize)
    {
        if (maxPoolSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPoolSize), maxPoolSize,
                "The pool size must be greater than zero.");
        }

        _policy = new MemoryStreamPoolPolicy(this);

        _pool = new HayatePoolBuilder<PooledMemoryStream>()
            .WithPoolName(nameof(PooledMemoryStream))
            .WithMinSize(0)
            .WithMaxSize(maxPoolSize)
            .WithPolicy(_policy)
            // Lazy like every specialized pool: nothing is pre-created, and a miss inside the size is
            // served immediately by creating a stream (the default wait-then-timeout policy would stall
            // every borrow past the first until the background scaler caught up — and the scaler is
            // switched off here).
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand)
            // A small pool gains nothing from sharding, and stream pooling needs no background features:
            // an idle pool holds its streams and runs no timers.
            .WithEnableSharding(false)
            .WithEnableAutoScaling(false)
            .WithEnableEviction(false)
            .WithEnableGenerationOptimization(false)
            .WithEnableLeakDetection(false)
            // Validation is what enforces the capacity window on every return; borrow-time validation
            // destroys a stream a borrower managed to break while idle. Idle validation needs the
            // background timer and is off with the rest of them.
            .WithValidateOnBorrow(true)
            .WithValidateOnReturn(true)
            .WithValidateWhileIdle(false)
            .Build();
    }

    /// <summary>
    /// The minimum capacity a stream is created with; it is also the smallest capacity a returned stream
    /// may have to be accepted back into the pool. Defaults to <see cref="DefaultMinimumMemoryStreamCapacity"/>.
    /// </summary>
    /// <remarks>
    /// Raising this value clears the pool: parked streams created under the old minimum cannot pass the
    /// new one.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The assigned value is not greater than zero.</exception>
    public int MinimumMemoryStreamCapacity
    {
        get => _minimumCapacity;
        set
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value,
                    "The minimum memory stream capacity must be greater than zero.");
            }

            var oldValue = _minimumCapacity;
            _minimumCapacity = value;
            if (oldValue < value)
            {
                _pool.Clear();
            }
        }
    }

    /// <summary>
    /// The maximum capacity a returned stream may have to be accepted back into the pool; larger streams
    /// are destroyed on return. Defaults to <see cref="DefaultMaximumMemoryStreamCapacity"/>.
    /// </summary>
    /// <remarks>
    /// Lowering this value clears the pool: parked streams above the new maximum cannot pass it.
    /// </remarks>
    public int MaximumMemoryStreamCapacity
    {
        get => _maximumCapacity;
        set
        {
            var oldValue = _maximumCapacity;
            _maximumCapacity = value;
            if (oldValue > value)
            {
                _pool.Clear();
            }
        }
    }

    /// <summary>
    /// Acquires a stream from the pool. Synonym of <see cref="Acquire()"/>, named as P89OP names it.
    /// </summary>
    public PooledMemoryStream GetObject() => Acquire();

    /// <summary>
    /// Acquires a stream from the pool, blocking up to the given timeout. Synonym of
    /// <see cref="Acquire(TimeSpan)"/>, named as P89OP names it.
    /// </summary>
    public PooledMemoryStream GetObject(TimeSpan timeout) => Acquire(timeout);

    /// <inheritdoc />
    public PooledMemoryStream Acquire() => _pool.Acquire();

    /// <inheritdoc />
    public PooledMemoryStream Acquire(TimeSpan timeout) => _pool.Acquire(timeout);

    /// <inheritdoc />
    public Task<PooledMemoryStream> AcquireAsync(CancellationToken cancellationToken = default)
        => _pool.AcquireAsync(cancellationToken);

    /// <inheritdoc />
    public Task<PooledMemoryStream> AcquireAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        => _pool.AcquireAsync(timeout, cancellationToken);

    /// <inheritdoc />
    public void Release(PooledMemoryStream item) => _pool.Release(item);

    /// <inheritdoc />
    public HayatePoolStats GetStats() => _pool.GetStats();

    /// <inheritdoc />
    public HayatePoolSnapshot TakeSnapshot() => _pool.TakeSnapshot();

    /// <inheritdoc />
    public void ReloadConfig(Action<HayatePoolOptions> configure) => _pool.ReloadConfig(configure);

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
    public int Evict(HayateEvictReason reason) => _pool.Evict(reason);

    /// <inheritdoc />
    public void Dispose() => _pool.Dispose();
}
