using System;
using System.Collections.Concurrent;
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
/// <see cref="GetObject(int)"/> lets the borrower declare the capacity it needs up front. Declared sizes
/// above the minimum are served by lazily created <b>capacity tiers</b> — one internal engine pool per
/// bucket on the exponential ladder anchored at the minimum — so a mixed-size workload stops paying the
/// grow-ladder on every borrow and stops churning builders through grow-past-the-maximum-and-destroy:
/// a tier accepts its declared size back even when that is above <see cref="MaximumStringBuilderCapacity"/>,
/// because the buffer exists by declaration rather than by accidental growth. Each tier holds up to the
/// pool's builder budget itself, so every declared size retains at most <c>maxPoolSize</c> builders.<br />
/// Lowering the maximum capacity clears the pool, matching P89OP: parked builders above the new maximum
/// cannot pass it. Raising the minimum clears the pool and retires the existing tiers, so everything is
/// recreated at the new size. The minimum is the creation capacity only — a builder's capacity never
/// shrinks, so there is no minimum to enforce on the way back.
/// </remarks>
/// <example>
/// <code>
/// using var sb = StringBuilderPool.Instance.GetObject();
/// sb.StringBuilder.Append("hello");
/// var text = sb.ToString();   // returned to the pool at the end of the block
///
/// using var big = StringBuilderPool.Instance.GetObject(200_000);   // pre-sized, no grow ladder
/// </code>
/// </example>
public sealed partial class StringBuilderPool : IHayateObjectPool<PooledStringBuilder>
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
    private readonly ConcurrentDictionary<int, IHayateObjectPool<PooledStringBuilder>> _capacityTiers = new();
    private readonly int _maximumPoolSize;
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
    /// <param name="maxPoolSize">The maximum number of builders each tier keeps and lends out.</param>
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
        _maximumPoolSize = maxPoolSize;

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

        // Items bind their real engine from now on; nothing could have been borrowed before the
        // constructor returned.
        _policy.AttachEngine(_pool);
    }

    /// <summary>
    /// The capacity a builder is created with. Defaults to <see cref="DefaultMinimumStringBuilderCapacity"/>.
    /// </summary>
    /// <remarks>
    /// This is the creation capacity only: a builder's capacity never shrinks, so there is no minimum to
    /// enforce on the way back. Raising this value clears the pool and retires the existing capacity
    /// tiers, matching P89OP's setter behaviour, so the parked builders are recreated at the new size.
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
                // P89OP parity: raising the creation capacity invalidates every parked builder, and the
                // tier ladder is anchored at the minimum, so the tiers retire with it — their builders
                // are destroyed and the ladder re-derives from the new minimum.
                _pool.Clear();
                foreach (var tier in _capacityTiers.Values)
                {
                    tier.Clear();
                }

                _capacityTiers.Clear();
            }
        }
    }

    /// <summary>
    /// The maximum capacity a returned builder may have to be accepted back into the pool; larger
    /// builders are destroyed on return. Defaults to <see cref="DefaultMaximumStringBuilderCapacity"/>.
    /// </summary>
    /// <remarks>
    /// Lowering this value clears the pool: parked builders above the new maximum cannot pass it.
    /// Builders borrowed through <see cref="GetObject(int)"/> remain covered by their tier's declared
    /// size, which parks them even above this maximum — declared capacity is what keeps a mixed-size
    /// workload from churning.
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
                Clear();
            }
        }
    }

    /// <summary>
    /// Acquires a builder from the pool. Synonym of <see cref="Acquire()"/>, named as P89OP names it.
    /// </summary>
    public PooledStringBuilder GetObject() => Acquire();

    /// <summary>
    /// Acquires a builder able to hold at least <paramref name="minCapacity"/> characters without
    /// growing, reserving the capacity up front so the borrow skips the grow ladder entirely.
    /// </summary>
    /// <param name="minCapacity">The number of characters the borrowed builder must hold without
    /// growth; must be greater than zero.</param>
    /// <returns>A pooled builder whose capacity is at least <paramref name="minCapacity"/>.</returns>
    /// <remarks>
    /// Requests within <see cref="MinimumStringBuilderCapacity"/> are served by the base tier exactly
    /// like <see cref="GetObject()"/>. Anything larger routes to a lazily created capacity tier — the
    /// smallest bucket of the exponential ladder anchored at the minimum that covers the request — so
    /// repeated borrows of the same size reuse a parked builder instead of growing one from scratch
    /// every time, and a tier builder is parked on return even when its capacity exceeds
    /// <see cref="MaximumStringBuilderCapacity"/>. Only growth beyond the declared size (and the global
    /// maximum) destroys a builder, because nothing declared that envelope.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="minCapacity"/> is not greater than
    /// zero.</exception>
    public PooledStringBuilder GetObject(int minCapacity) => Acquire(minCapacity);

    /// <summary>
    /// Acquires a builder from the pool, blocking up to the given timeout. Synonym of
    /// <see cref="Acquire(TimeSpan)"/>, named as P89OP names it.
    /// </summary>
    public PooledStringBuilder GetObject(TimeSpan timeout) => Acquire(timeout);

    /// <summary>
    /// Acquires a builder from the pool and fills it with <paramref name="value"/>, as P89OP's
    /// <c>GetObject(string)</c> does.
    /// </summary>
    /// <param name="value">The string the borrowed builder starts with; may be <c>null</c>
    /// (an empty builder).</param>
    /// <returns>A pooled builder whose content is exactly <paramref name="value"/>.</returns>
    /// <remarks>
    /// The builder is cleared before the seed is appended, so the content is exactly the seed however
    /// previous borrows ended — an append-only fill would silently double-append the day a return path
    /// failed to clear. A seed longer than <see cref="MinimumStringBuilderCapacity"/> borrows through a
    /// capacity tier, so the builder already fits the seed instead of growing to it.
    /// </remarks>
    public PooledStringBuilder GetObject(string? value)
    {
        var pooled = value is { Length: > 0 } ? Acquire(value.Length) : Acquire();
        try
        {
            pooled.StringBuilder.Clear();
            pooled.StringBuilder.Append(value);
            return pooled;
        }
        catch
        {
            // The fill failed; the borrowed builder goes back rather than leaking out of the pool.
            pooled.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public PooledStringBuilder Acquire() => _pool.Acquire();

    /// <summary>
    /// Acquires a builder able to hold at least <paramref name="minCapacity"/> characters without
    /// growing, routing through <see cref="GetObject(int)"/>'s capacity tiers.
    /// </summary>
    /// <param name="minCapacity">The number of characters the borrowed builder must hold without
    /// growth; must be greater than zero.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="minCapacity"/> is not greater than
    /// zero.</exception>
    public PooledStringBuilder Acquire(int minCapacity)
    {
        if (minCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minCapacity), minCapacity,
                "The requested capacity must be greater than zero.");
        }

        // The base tier covers everything up to the creation capacity — its builders are never created
        // below the live minimum — so only larger requests need a declared tier.
        if (minCapacity <= _minimumCapacity)
        {
            return _pool.Acquire();
        }

        var bucket = PoolCapacityTiers.Bucket(_minimumCapacity, minCapacity);
        var tier = _capacityTiers.GetOrAdd(bucket, static (capacity, self) => self.CreateTierPool(capacity), this);
        return tier.Acquire();
    }

    /// <inheritdoc />
    public PooledStringBuilder Acquire(TimeSpan timeout) => _pool.Acquire(timeout);

    /// <inheritdoc />
    public Task<PooledStringBuilder> AcquireAsync(CancellationToken cancellationToken = default)
        => _pool.AcquireAsync(cancellationToken);

    /// <inheritdoc />
    public Task<PooledStringBuilder> AcquireAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        => _pool.AcquireAsync(timeout, cancellationToken);

    /// <summary>
    /// Returns a builder to the pool it came from. A builder borrowed through a capacity tier returns to
    /// that tier, so the declared size keeps its builders; standalone or already-destroyed instances
    /// fall back to the base pool, as before the tiers existed.
    /// </summary>
    /// <inheritdoc />
    public void Release(PooledStringBuilder item)
    {
        var home = item.HomePool;
        if (home is not null)
        {
            home.Release(item);
            return;
        }

        _pool.Release(item);
    }

    /// <inheritdoc />
    public HayatePoolStats GetStats()
        => SpecializedPoolAggregation.AggregateStats(_pool, TierEngines());

    /// <inheritdoc />
    public HayatePoolSnapshot TakeSnapshot()
        => SpecializedPoolAggregation.AggregateSnapshot(_pool, TierEngines());

    /// <inheritdoc />
    public void ReloadConfig(Action<HayatePoolOptions> configure)
    {
        _pool.ReloadConfig(configure);
        foreach (var tier in _capacityTiers.Values)
        {
            tier.ReloadConfig(configure);
        }
    }

    /// <summary>
    /// Destroys every builder currently held by the pool, including the ones parked in its capacity
    /// tiers. The tier engines stay registered; the next declared borrow recreates builders at the same
    /// declared sizes.
    /// </summary>
    /// <inheritdoc />
    public void Clear()
    {
        _pool.Clear();
        foreach (var tier in _capacityTiers.Values)
        {
            tier.Clear();
        }
    }

    /// <inheritdoc />
    public HayatePoolOptions GetOptions() => _pool.GetOptions();

    /// <inheritdoc />
    public bool CheckAvailable()
    {
        if (!_pool.CheckAvailable())
        {
            return false;
        }

        foreach (var tier in _capacityTiers.Values)
        {
            if (!tier.CheckAvailable())
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public void SetUnavailable(string? reason = null)
    {
        _pool.SetUnavailable(reason);
        foreach (var tier in _capacityTiers.Values)
        {
            tier.SetUnavailable(reason);
        }
    }

    /// <inheritdoc />
    public void SetAvailable()
    {
        _pool.SetAvailable();
        foreach (var tier in _capacityTiers.Values)
        {
            tier.SetAvailable();
        }
    }

    /// <inheritdoc />
    public int Evict(HayateEvictReason reason)
    {
        var evicted = _pool.Evict(reason);
        foreach (var tier in _capacityTiers.Values)
        {
            evicted += tier.Evict(reason);
        }

        return evicted;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _pool.Dispose();
        foreach (var tier in _capacityTiers.Values)
        {
            tier.Dispose();
        }
    }

    private System.Collections.Generic.IEnumerable<IHayateObjectPool> TierEngines()
    {
        foreach (var tier in _capacityTiers.Values)
        {
            yield return tier;
        }
    }

    private IHayateObjectPool<PooledStringBuilder> CreateTierPool(int bucket)
    {
        var policy = new StringBuilderPoolPolicy(this, bucket);
        var pool = new HayatePoolBuilder<PooledStringBuilder>()
            .WithPoolName($"{nameof(PooledStringBuilder)}#{bucket}")
            .WithMinSize(0)
            .WithMaxSize(_maximumPoolSize)
            .WithPolicy(policy)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand)
            .WithEnableSharding(false)
            .WithEnableAutoScaling(false)
            .WithEnableEviction(false)
            .WithEnableGenerationOptimization(false)
            .WithEnableLeakDetection(false)
            .WithValidateOnBorrow(true)
            .WithValidateOnReturn(true)
            .WithValidateWhileIdle(false)
            .Build();

        // Attach before the tier becomes reachable: once it is in the dictionary, any thread may borrow
        // from it, and created builders must bind this engine so returns route home.
        policy.AttachEngine(pool);
        return pool;
    }
}
