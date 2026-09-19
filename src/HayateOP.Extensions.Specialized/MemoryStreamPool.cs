using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
/// <see cref="GetObject(int)"/> lets the borrower declare the capacity it needs up front. Declared sizes
/// above the minimum are served by lazily created <b>capacity tiers</b> — one internal engine pool per
/// bucket on the exponential ladder anchored at the minimum — so a mixed-size workload stops paying the
/// grow ladder on every borrow and stops churning streams through grow-past-the-maximum-and-destroy:
/// a tier accepts its declared size back even when that is above <see cref="MaximumMemoryStreamCapacity"/>,
/// because the buffer exists by declaration rather than by accidental growth. Each tier holds up to the
/// pool's stream budget itself, so every declared size retains at most <c>maxPoolSize</c> streams.<br />
/// Tightening the window clears the pool, matching P89OP: raising the minimum or lowering the maximum
/// destroys every parked stream, because none of them can pass the new window; raising the minimum also
/// retires the existing tiers so everything is recreated at the new size.
/// </remarks>
/// <example>
/// <code>
/// using var stream = MemoryStreamPool.Instance.GetObject();
/// stream.Write(payload, 0, payload.Length);   // returned to the pool at the end of the block
///
/// using var big = MemoryStreamPool.Instance.GetObject(200_000);   // pre-sized, no grow ladder
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
    private readonly ConcurrentDictionary<int, IHayateObjectPool<PooledMemoryStream>> _capacityTiers = new();
    private readonly int _maximumPoolSize;
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
    /// <param name="maxPoolSize">The maximum number of streams each tier keeps and lends out.</param>
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
        _maximumPoolSize = maxPoolSize;

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

        // Items bind their real engine from now on; nothing could have been borrowed before the
        // constructor returned.
        _policy.AttachEngine(_pool);
    }

    /// <summary>
    /// The minimum capacity a stream is created with; it is also the smallest capacity a returned stream
    /// may have to be accepted back into the pool. Defaults to <see cref="DefaultMinimumMemoryStreamCapacity"/>.
    /// </summary>
    /// <remarks>
    /// Raising this value clears the pool and retires the existing capacity tiers: parked streams created
    /// under the old minimum cannot pass the new one, and the tier ladder is anchored at the minimum.
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
                foreach (var tier in _capacityTiers.Values)
                {
                    tier.Clear();
                }

                _capacityTiers.Clear();
            }
        }
    }

    /// <summary>
    /// The maximum capacity a returned stream may have to be accepted back into the pool; larger streams
    /// are destroyed on return. Defaults to <see cref="DefaultMaximumMemoryStreamCapacity"/>.
    /// </summary>
    /// <remarks>
    /// Lowering this value clears the pool: parked streams above the new maximum cannot pass it.
    /// Streams borrowed through <see cref="GetObject(int)"/> remain covered by their tier's declared
    /// size, which parks them even above this maximum — declared capacity is what keeps a mixed-size
    /// workload from churning.
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
                Clear();
            }
        }
    }

    /// <summary>
    /// Acquires a stream from the pool. Synonym of <see cref="Acquire()"/>, named as P89OP names it.
    /// </summary>
    public PooledMemoryStream GetObject() => Acquire();

    /// <summary>
    /// Acquires a stream able to hold at least <paramref name="minCapacity"/> bytes without growing,
    /// reserving the buffer up front so the borrow skips the grow ladder entirely.
    /// </summary>
    /// <param name="minCapacity">The number of bytes the borrowed stream's buffer must hold without
    /// growth; must be greater than zero.</param>
    /// <returns>A pooled stream whose capacity is at least <paramref name="minCapacity"/>.</returns>
    /// <remarks>
    /// Requests within <see cref="MinimumMemoryStreamCapacity"/> are served by the base tier exactly
    /// like <see cref="GetObject()"/>. Anything larger routes to a lazily created capacity tier — the
    /// smallest bucket of the exponential ladder anchored at the minimum that covers the request — so
    /// repeated borrows of the same size reuse a parked stream instead of growing one from scratch
    /// every time, and a tier stream is parked on return even when its capacity exceeds
    /// <see cref="MaximumMemoryStreamCapacity"/>. Only growth beyond the declared size (and the global
    /// maximum) destroys a stream, because nothing declared that envelope.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="minCapacity"/> is not greater than
    /// zero.</exception>
    public PooledMemoryStream GetObject(int minCapacity) => Acquire(minCapacity);

    /// <summary>
    /// Acquires a stream from the pool, blocking up to the given timeout. Synonym of
    /// <see cref="Acquire(TimeSpan)"/>, named as P89OP names it.
    /// </summary>
    public PooledMemoryStream GetObject(TimeSpan timeout) => Acquire(timeout);

    /// <inheritdoc />
    public PooledMemoryStream Acquire() => _pool.Acquire();

    /// <summary>
    /// Acquires a stream able to hold at least <paramref name="minCapacity"/> bytes without growing,
    /// routing through <see cref="GetObject(int)"/>'s capacity tiers.
    /// </summary>
    /// <param name="minCapacity">The number of bytes the borrowed stream's buffer must hold without
    /// growth; must be greater than zero.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="minCapacity"/> is not greater than
    /// zero.</exception>
    public PooledMemoryStream Acquire(int minCapacity)
    {
        if (minCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minCapacity), minCapacity,
                "The requested capacity must be greater than zero.");
        }

        // The base tier covers everything up to the creation capacity — its streams are never created
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
    public PooledMemoryStream Acquire(TimeSpan timeout) => _pool.Acquire(timeout);

    /// <inheritdoc />
    public Task<PooledMemoryStream> AcquireAsync(CancellationToken cancellationToken = default)
        => _pool.AcquireAsync(cancellationToken);

    /// <inheritdoc />
    public Task<PooledMemoryStream> AcquireAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        => _pool.AcquireAsync(timeout, cancellationToken);

    /// <summary>
    /// Returns a stream to the pool it came from. A stream borrowed through a capacity tier returns to
    /// that tier, so the declared size keeps its streams; standalone or already-destroyed instances
    /// fall back to the base pool, as before the tiers existed.
    /// </summary>
    /// <inheritdoc />
    public void Release(PooledMemoryStream item)
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
    /// Destroys every stream currently held by the pool, including the ones parked in its capacity
    /// tiers. The tier engines stay registered; the next declared borrow recreates streams at the same
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

    /// <summary>
    /// Warms the base pool — the one that serves every request within the default capacity.
    /// </summary>
    /// <remarks>
    /// The capacity tiers are not warmed: a tier exists because a caller asked for that specific capacity, and
    /// a warm-up targets the size the pool is normally used at. The count is a floor on idle streams, and the
    /// base pool's own maximum still applies.
    /// </remarks>
    /// <inheritdoc />
    public int PreWarm(int count) => _pool.PreWarm(count);

    /// <inheritdoc />
    public void Dispose()
    {
        _pool.Dispose();
        foreach (var tier in _capacityTiers.Values)
        {
            tier.Dispose();
        }
    }

    private IEnumerable<IHayateObjectPool> TierEngines()
    {
        foreach (var tier in _capacityTiers.Values)
        {
            yield return tier;
        }
    }

    private IHayateObjectPool<PooledMemoryStream> CreateTierPool(int bucket)
    {
        var policy = new MemoryStreamPoolPolicy(this, bucket);
        var pool = new HayatePoolBuilder<PooledMemoryStream>()
            .WithPoolName($"{nameof(PooledMemoryStream)}#{bucket}")
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
        // from it, and created streams must bind this engine so returns route home.
        policy.AttachEngine(pool);
        return pool;
    }
}
