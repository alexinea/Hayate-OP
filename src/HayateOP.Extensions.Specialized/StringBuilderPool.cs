using System;
using System.Threading;
using System.Threading.Tasks;
using DotNetCore.HayateOP;

namespace DotNetCore.HayateOP.Specialized;

/// <summary>
/// A ready-to-use pool of <see cref="PooledStringBuilder"/> instances, aligned with P89OP's
/// <c>StringBuilderPool</c>: borrowing costs one line, <c>using</c> on the borrowed instance returns it,
/// and <see cref="GetObject(string)"/> starts a builder from an initial string.
/// </summary>
/// <remarks>
/// The pool is built on the ordinary HayateOP engine, configured the way a specialized builder pool
/// wants it: nothing is pre-created, a miss inside the pool size is served by creating a builder on
/// demand, and the background features a builder pool has no use for (auto-scaling, eviction) are
/// switched off. Validation stays on: it is what enforces the maximum capacity below, on every return.<br />
/// Builders are created with <see cref="MinimumStringBuilderCapacity"/> characters and may grow while
/// borrowed; a builder that comes back with a capacity above <see cref="MaximumStringBuilderCapacity"/>
/// is destroyed instead of being parked, so character buffers never linger larger than the configuration
/// allows. Returned builders are cleared before the next borrow.<br />
/// Lowering the maximum capacity clears the pool, matching P89OP: parked builders above the new maximum
/// cannot pass it. The minimum is the creation capacity only — a builder's capacity never shrinks, so
/// there is no minimum to enforce on the way back.
/// </remarks>
/// <example>
/// <code>
/// using var sb = StringBuilderPool.Instance.GetObject();
/// sb.StringBuilder.Append("hello");
/// var text = sb.ToString();   // returned to the pool at the end of the block
/// </code>
/// </example>
public sealed class StringBuilderPool : IHayateObjectPool<PooledStringBuilder>
{
    /// <summary>
    /// The default maximum pool size (16), the same value P89OP's <c>ObjectPool.DefaultPoolMaximumSize</c>
    /// uses for its specialized pools.
    /// </summary>
    public const int DefaultPoolMaximumSize = 16;

    /// <summary>
    /// The default minimum string builder capacity: 4096 characters, the P89OP default.
    /// </summary>
    public const int DefaultMinimumStringBuilderCapacity = 4 * 1024;

    /// <summary>
    /// The default maximum string builder capacity: 524288 characters, the P89OP default.
    /// </summary>
    public const int DefaultMaximumStringBuilderCapacity = 512 * 1024;

    /// <summary>
    /// A shared, thread-safe pool instance for callers that do not want to manage a pool's lifetime.
    /// </summary>
    public static StringBuilderPool Instance { get; } = new();

    private readonly IHayateObjectPool<PooledStringBuilder> _pool;
    private readonly StringBuilderPoolPolicy _policy;
    private int _minimumCapacity = DefaultMinimumStringBuilderCapacity;
    private int _maximumCapacity = DefaultMaximumStringBuilderCapacity;

    /// <summary>
    /// Builds a pool holding at most <see cref="DefaultPoolMaximumSize"/> builders.
    /// </summary>
    public StringBuilderPool()
        : this(DefaultPoolMaximumSize)
    {
    }

    /// <summary>
    /// Builds a pool holding at most <paramref name="maxPoolSize"/> builders.
    /// </summary>
    /// <param name="maxPoolSize">The maximum number of builders the pool keeps and lends out.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxPoolSize"/> is not greater than
    /// zero.</exception>
    public StringBuilderPool(int maxPoolSize)
    {
        if (maxPoolSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPoolSize), maxPoolSize,
                "The pool size must be greater than zero.");
        }

        _policy = new StringBuilderPoolPolicy(this);

        _pool = new HayatePoolBuilder<PooledStringBuilder>()
            .WithPoolName(nameof(PooledStringBuilder))
            .WithMinSize(0)
            .WithMaxSize(maxPoolSize)
            .WithPolicy(_policy)
            // Lazy like every specialized pool: nothing is pre-created, and a miss inside the size is
            // served immediately by creating a builder (the default wait-then-timeout policy would stall
            // every borrow past the first until the background scaler caught up — and the scaler is
            // switched off here).
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand)
            // A small pool gains nothing from sharding, and builder pooling needs no background
            // features: an idle pool holds its builders and runs no timers.
            .WithEnableSharding(false)
            .WithEnableAutoScaling(false)
            .WithEnableEviction(false)
            .WithEnableGenerationOptimization(false)
            .WithEnableLeakDetection(false)
            // Validation is what enforces the maximum capacity on every return; borrow-time validation
            // destroys a builder a borrower managed to break while idle. Idle validation needs the
            // background timer and is off with the rest of them.
            .WithValidateOnBorrow(true)
            .WithValidateOnReturn(true)
            .WithValidateWhileIdle(false)
            .Build();
    }

    /// <summary>
    /// The capacity a builder is created with. Defaults to <see cref="DefaultMinimumStringBuilderCapacity"/>.
    /// </summary>
    /// <remarks>
    /// This is the creation capacity only: a builder's capacity never shrinks, so there is no minimum to
    /// enforce on the way back. Raising this value clears the pool to match P89OP's setter behaviour, so
    /// the parked builders are recreated at the new size.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The assigned value is not greater than zero.</exception>
    public int MinimumStringBuilderCapacity
    {
        get => _minimumCapacity;
        set
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value,
                    "The minimum string builder capacity must be greater than zero.");
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
    /// The maximum capacity a returned builder may have to be accepted back into the pool; larger
    /// builders are destroyed on return. Defaults to <see cref="DefaultMaximumStringBuilderCapacity"/>.
    /// </summary>
    /// <remarks>
    /// Lowering this value clears the pool: parked builders above the new maximum cannot pass it.
    /// </remarks>
    public int MaximumStringBuilderCapacity
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
    /// Acquires a builder from the pool. Synonym of <see cref="Acquire()"/>, named as P89OP names it.
    /// </summary>
    public PooledStringBuilder GetObject() => Acquire();

    /// <summary>
    /// Acquires a builder from the pool, blocking up to the given timeout. Synonym of
    /// <see cref="Acquire(TimeSpan)"/>, named as P89OP names it.
    /// </summary>
    public PooledStringBuilder GetObject(TimeSpan timeout) => Acquire(timeout);

    /// <summary>
    /// Acquires a builder from the pool and appends <paramref name="value"/> to it, as P89OP's
    /// <c>GetObject(string)</c> does.
    /// </summary>
    /// <param name="value">The string appended to the freshly borrowed builder; may be <c>null</c>
    /// (appending nothing).</param>
    /// <returns>A pooled builder whose content starts with <paramref name="value"/>.</returns>
    public PooledStringBuilder GetObject(string? value)
    {
        var pooled = Acquire();
        pooled.StringBuilder.Append(value);
        return pooled;
    }

    /// <inheritdoc />
    public PooledStringBuilder Acquire() => _pool.Acquire();

    /// <inheritdoc />
    public PooledStringBuilder Acquire(TimeSpan timeout) => _pool.Acquire(timeout);

    /// <inheritdoc />
    public Task<PooledStringBuilder> AcquireAsync(CancellationToken cancellationToken = default)
        => _pool.AcquireAsync(cancellationToken);

    /// <inheritdoc />
    public Task<PooledStringBuilder> AcquireAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        => _pool.AcquireAsync(timeout, cancellationToken);

    /// <inheritdoc />
    public void Release(PooledStringBuilder item) => _pool.Release(item);

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
