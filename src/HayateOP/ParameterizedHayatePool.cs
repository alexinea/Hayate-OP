using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DotNetCore.HayateOP.Common;
using DotNetCore.HayateOP.Logging;
using DotNetCore.HayateOP.Metrics;
using DotNetCore.HayateOP.Policies;
using DotNetCore.HayateOP.Scaling;

namespace DotNetCore.HayateOP;

/// <summary>
/// A pool of pools: one independent <see cref="IHayateObjectPool{TValue}"/> per key, addressed by
/// <typeparamref name="TKey"/>.
/// </summary>
/// <typeparam name="TKey">The key that selects a sub-pool — a tenant, a connection string, an endpoint.</typeparam>
/// <typeparam name="TValue">The pooled object type.</typeparam>
/// <remarks>
/// The point of keying a pool is isolation. Objects are only ever reused by the key that created them, so a
/// connection for tenant A never ends up handed to tenant B, and each sub-pool keeps its own capacity, its
/// own eviction clock and its own circuit breaker: one slow key cannot exhaust or poison the others.<br />
/// Every sub-pool is an ordinary HayateOP pool, so sharding, validation, eviction, auto-scaling, leak
/// detection, statistics and snapshots all work per key, and the address of a sub-pool is its key — see
/// <see cref="GetPool"/>.<br />
/// Sub-pools are created on first use and live until the keyed pool is disposed. Unbounded key spaces — a
/// key per request id, say — grow unbounded too; <see cref="TryRemove"/> retires a key, and
/// <see cref="KeysInPoolCount"/> is the number to watch.
/// </remarks>
/// <example>
/// <code>
/// var pools = new ParameterizedHayatePool&lt;string, Connection&gt;(
///     connectionString =&gt; new Connection(connectionString),
///     maxSizePerKey: 8);
///
/// var connection = pools.GetObject("tenant-a");
/// try { connection.Query("..."); }
/// finally { pools.ReturnObject("tenant-a", connection); }
/// </code>
/// </example>
public sealed class ParameterizedHayatePool<TKey, TValue> : IDisposable
    where TKey : notnull
    where TValue : class
{
    private readonly ConcurrentDictionary<TKey, IHayateObjectPool<TValue>> _subPools = new();
    private readonly Func<TKey, IHayateObjectPool<TValue>> _subPoolFactory;
    private readonly IHayateObjectPoolRegistry _registry;
    private readonly string _name;

    // Creation and retirement both mutate the dictionary and the registry together, so they serialize on
    // this. Borrow and return never take it: they read the dictionary, which is already concurrent.
    private readonly object _gate = new();
    private bool _disposed;

    /// <summary>
    /// Builds a keyed pool whose sub-pools come from <paramref name="subPoolFactory"/>.
    /// </summary>
    /// <param name="subPoolFactory">Creates the sub-pool for a key. Called at most once per key, on first
    /// use, and must not call back into this keyed pool.</param>
    /// <param name="registry">Where to register each sub-pool so management endpoints and metrics can find
    /// it. Defaults to a registry private to this instance — pass the application's registry (the one the
    /// dependency-injection package registers) to publish the sub-pools alongside the rest.</param>
    /// <param name="name">An optional name used as the prefix of every sub-pool name; defaults to the
    /// pooled type name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="subPoolFactory"/> is <c>null</c>.</exception>
    /// <remarks>
    /// Use this overload when a sub-pool needs more than a factory — a custom policy, its own settings —
    /// because it hands you the whole pool to build.
    /// </remarks>
    /// <example>
    /// <code>
    /// var pools = new ParameterizedHayatePool&lt;string, Connection&gt;(key =&gt;
    ///     new HayatePoolBuilder&lt;Connection&gt;()
    ///         .WithMaxSize(8)
    ///         .WithPolicy(new ConnectionPolicy(key))
    ///         .Build());
    /// </code>
    /// </example>
    public ParameterizedHayatePool(Func<TKey, IHayateObjectPool<TValue>> subPoolFactory,
        IHayateObjectPoolRegistry? registry = null, string? name = null)
    {
        _subPoolFactory = subPoolFactory ?? throw new ArgumentNullException(nameof(subPoolFactory));
        _registry = registry ?? new HayateObjectPoolRegistry();
        _name = string.IsNullOrWhiteSpace(name) ? typeof(TValue).Name : name!;
    }

    /// <summary>
    /// Builds a keyed pool that creates its objects with <paramref name="create"/>, giving every key a
    /// sub-pool of at most <paramref name="maxSizePerKey"/> objects.
    /// </summary>
    /// <param name="create">Creates an object for a key; must not return <c>null</c>.</param>
    /// <param name="maxSizePerKey">The capacity of each sub-pool — how many objects one key may have out
    /// at once and keep idle.</param>
    /// <param name="onGet">An optional callback invoked every time an object is borrowed, with the key it
    /// was borrowed for.</param>
    /// <param name="configure">An optional callback that adjusts the options of the sub-pool being created
    /// for a key — an eviction interval, a breaker, a scaling strategy. Applied to a copy of the defaults,
    /// so it cannot affect other keys.</param>
    /// <param name="registry">Where to register each sub-pool; see the other constructor.</param>
    /// <param name="name">An optional name prefix for the sub-pool names.</param>
    /// <param name="metrics">An optional <see cref="IHayateMetrics"/> shared by every sub-pool this keyed
    /// pool creates, so a keyed pool can publish counters the way any other pool does. Defaults to the
    /// empty sink — no counters are recorded, which is what this constructor did before it took one.</param>
    /// <param name="logger">An optional <see cref="IHayateLogger"/> shared by every sub-pool this keyed pool
    /// creates. Defaults to the built-in no-op logger, which is what this constructor used before it took
    /// one. Pass a logger built per key when the keys need to be told apart downstream — the instance given
    /// here is shared, so every sub-pool writes to the same place.</param>
    /// <exception cref="ArgumentNullException"><paramref name="create"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxSizePerKey"/> is not greater than
    /// zero.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="create"/> returned <c>null</c> — raised
    /// on the first creation instead of letting a null object into a sub-pool.</exception>
    /// <remarks>
    /// Nothing is pre-created: a key costs nothing until it is first used, and the minimum idle count is
    /// zero so a sub-pool holds no object until one is borrowed. Every other option keeps its documented
    /// default, so each sub-pool is a full-featured pool — use the other constructor, or the
    /// <see cref="HayatePoolBuilder{T}"/>, when a key needs its own policy.<br />
    /// Two defaults do not survive keying. Sharding divides capacity across shards, so a sub-pool smaller
    /// than the default shard count is given a matching shard count instead of being split into shards that
    /// can hold nothing. And a sub-pool creates on demand rather than waiting for a return: created empty,
    /// with nothing held in reserve, it would otherwise hand out one object and make every later borrow wait
    /// for the background scaler. At the per-key size it waits like any other pool.<br />
    /// Every sub-pool shares the <see cref="IHayateMetrics"/> and <see cref="IHayateLogger"/> given here,
    /// or the empty sink and the built-in no-op logger when none is given — the two this constructor
    /// hard-coded before it accepted them, so an unchanged caller sees exactly what it saw then. Sharing is
    /// the point for metrics, which aggregate; a caller who needs the keys told apart in a log should pass a
    /// logger that routes by key itself, or use the other constructor, which hands over the whole sub-pool
    /// and can give each key its own.
    /// </remarks>
    /// <example>
    /// <code>
    /// var pools = new ParameterizedHayatePool&lt;string, Connection&gt;(
    ///     cs =&gt; new Connection(cs),
    ///     maxSizePerKey: 8,
    ///     onGet: (cs, connection) =&gt; connection.Open(cs));
    /// </code>
    /// </example>
    public ParameterizedHayatePool(Func<TKey, TValue> create, int maxSizePerKey,
        Action<TKey, TValue>? onGet = null, Action<TKey, HayatePoolOptions>? configure = null,
        IHayateObjectPoolRegistry? registry = null, string? name = null,
        IHayateMetrics? metrics = null, IHayateLogger? logger = null)
        : this(key => BuildSubPool(key, create!, maxSizePerKey, onGet, configure,
                  string.IsNullOrWhiteSpace(name) ? typeof(TValue).Name : name!, metrics, logger),
              registry, name)
    {
        // Guarded here as well as in BuildSubPool so the exception is raised by the constructor call the
        // caller wrote, not deferred to the first borrow.
        if (create is null) throw new ArgumentNullException(nameof(create));
        if (maxSizePerKey <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSizePerKey), maxSizePerKey,
                "The per-key pool size must be greater than zero.");
        }
    }

    private static IHayateObjectPool<TValue> BuildSubPool(TKey key, Func<TKey, TValue> create,
        int maxSizePerKey, Action<TKey, TValue>? onGet, Action<TKey, HayatePoolOptions>? configure,
        string namePrefix, IHayateMetrics? metrics, IHayateLogger? logger)
    {
        if (create is null) throw new ArgumentNullException(nameof(create));
        if (maxSizePerKey <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSizePerKey), maxSizePerKey,
                "The per-key pool size must be greater than zero.");
        }

        var options = new HayatePoolOptions
        {
            // Lazy per key: a key that is never used never allocates, and a key that is used keeps at most
            // `maxSizePerKey`, with no idle objects held in reserve.
            MinPoolSize = 0,
            MaxPoolSize = maxSizePerKey,

            // Sharding divides capacity across shards, and a sub-pool smaller than the default shard count
            // would be split into shards that can hold nothing at all — so a key asking for two objects would
            // get one. Following the size keeps `maxSizePerKey` honest; a sub-pool of a handful of objects
            // gains nothing from sharding anyway. `configure` can still set `ShardCount` explicitly.
            ShardCount = Math.Min(HayateConstant.DEFAULT_SHARD_COUNT, maxSizePerKey),

            // The one default that would make `maxSizePerKey` a promise the pool does not keep. The default
            // policy waits for a return and times out; since a sub-pool is created empty and holds nothing in
            // reserve, every borrow past the first — up to the whole per-key size — would wait for an object
            // that only the background scaler can add, seconds later. Creating on demand is what "this key may
            // have N objects" has to mean: a miss inside the size is served immediately, and a miss at the size
            // waits for a return.
            RejectPolicy = HayatePoolRejectPolicy.CreateOnDemand
        };
        configure?.Invoke(key, options);
        options.ApplyFeatureSwitches();

        return new HayatePoolBasic<TValue>(
            new DelegateHayateObjectPolicy<TValue>(() => create(key),
                onGet is null ? null : new Action<TValue>(value => onGet(key, value))),
            options,
            new ThresholdScalingStrategy(),
            metrics ?? EmptyHayateMetrics.Instance,
            logger ?? new DefaultHayateLogger(),
            SubPoolNameOf(namePrefix, key));
    }

    /// <summary>The name every sub-pool name is derived from.</summary>
    public string Name => _name;

    /// <summary>
    /// How many keys currently have a sub-pool.
    /// </summary>
    /// <remarks>
    /// This is the number to watch in a keyed pool: it only grows, one entry per distinct key ever used,
    /// until a key is retired with <see cref="TryRemove"/> or the pool is disposed.
    /// </remarks>
    public int KeysInPoolCount => _subPools.Count;

    /// <summary>A snapshot of the keys that currently have a sub-pool.</summary>
    public IReadOnlyCollection<TKey> Keys => _subPools.Keys.ToArray();

    /// <summary>
    /// Borrows an object from the sub-pool of <paramref name="key"/>, creating that sub-pool on first use.
    /// </summary>
    /// <param name="key">The key identifying which sub-pool to borrow from.</param>
    /// <returns>An object belonging to <paramref name="key"/>; return it with
    /// <see cref="ReturnObject"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <c>null</c>.</exception>
    /// <exception cref="ObjectDisposedException">The keyed pool has been disposed.</exception>
    /// <example>
    /// <code>
    /// var connection = pools.GetObject("tenant-a");
    /// try { connection.Query("..."); }
    /// finally { pools.ReturnObject("tenant-a", connection); }
    /// </code>
    /// </example>
    public TValue GetObject(TKey key)
    {
        if (key is null) throw new ArgumentNullException(nameof(key));
        return GetOrCreate(key).Acquire();
    }

    /// <summary>
    /// Asynchronously borrows an object from the sub-pool of <paramref name="key"/>, creating that sub-pool
    /// on first use.
    /// </summary>
    /// <param name="key">The key identifying which sub-pool to borrow from.</param>
    /// <param name="cancellationToken">A token that can cancel the wait.</param>
    /// <returns>A task yielding an object belonging to <paramref name="key"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <c>null</c>.</exception>
    /// <exception cref="ObjectDisposedException">The keyed pool has been disposed.</exception>
    /// <remarks>
    /// Waits like the sub-pool's own asynchronous borrow: cancellation interrupts the wait, and a wait that
    /// ends in failure borrows nothing, so there is never an object to return.
    /// </remarks>
    /// <example>
    /// <code>
    /// var connection = await pools.GetObjectAsync("tenant-a", cancellationToken);
    /// try { await connection.QueryAsync("..."); }
    /// finally { pools.ReturnObject("tenant-a", connection); }
    /// </code>
    /// </example>
    public Task<TValue> GetObjectAsync(TKey key, CancellationToken cancellationToken = default)
    {
        if (key is null) throw new ArgumentNullException(nameof(key));
        return GetOrCreate(key).AcquireAsync(cancellationToken);
    }

    /// <summary>
    /// Returns an object to the sub-pool it was borrowed from.
    /// </summary>
    /// <param name="key">The key the object was borrowed for.</param>
    /// <param name="value">The object to return.</param>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> or <paramref name="value"/> is
    /// <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="key"/> has no sub-pool — the key was
    /// never used here, or it was retired with <see cref="TryRemove"/>.</exception>
    /// <remarks>
    /// The key is required because a keyed pool cannot tell which sub-pool owns an object: the same object
    /// could exist under two keys, and searching every sub-pool would make returning cost more than
    /// borrowing.
    /// </remarks>
    /// <example>
    /// <code>
    /// pools.ReturnObject("tenant-a", connection);
    /// </code>
    /// </example>
    public void ReturnObject(TKey key, TValue value)
    {
        if (key is null) throw new ArgumentNullException(nameof(key));
        if (value is null) throw new ArgumentNullException(nameof(value));

        if (!_subPools.TryGetValue(key, out var pool))
        {
            throw new InvalidOperationException(
                $"No sub-pool exists for the key '{DescribeKey(key)}' in keyed pool '{_name}'; " +
                "an object can only be returned to the key it was borrowed for.");
        }

        pool.Release(value);
    }

    /// <summary>
    /// Returns the sub-pool of <paramref name="key"/>, creating it on first use.
    /// </summary>
    /// <param name="key">The key identifying the sub-pool.</param>
    /// <returns>The sub-pool serving <paramref name="key"/>.</returns>
    /// <remarks>
    /// The escape hatch to everything a sub-pool offers: statistics, a snapshot, eviction, run-time
    /// reconfiguration. The sub-pool is the object registered in the registry, so this is also how a
    /// resolved pool gets back to its key's own diagnostics.
    /// </remarks>
    /// <example>
    /// <code>
    /// var stats = pools.GetPool("tenant-a").GetStats();
    /// </code>
    /// </example>
    public IHayateObjectPool<TValue> GetPool(TKey key)
    {
        if (key is null) throw new ArgumentNullException(nameof(key));
        return GetOrCreate(key);
    }

    /// <summary>
    /// Looks up the sub-pool of <paramref name="key"/> without creating one.
    /// </summary>
    /// <param name="key">The key identifying the sub-pool.</param>
    /// <param name="pool">When this method returns <c>true</c>, the sub-pool serving
    /// <paramref name="key"/>; otherwise <c>null</c>.</param>
    /// <returns><c>true</c> if the key already has a sub-pool; otherwise <c>false</c>.</returns>
    /// <example>
    /// <code>
    /// if (pools.TryGetPool("tenant-a", out var pool)) Console.WriteLine(pool.GetStats().PooledCount);
    /// </code>
    /// </example>
    public bool TryGetPool(TKey key, out IHayateObjectPool<TValue> pool)
    {
        pool = null!;   // meaningful only when this method returns true
        if (key is null) return false;
        return _subPools.TryGetValue(key, out pool!);
    }

    /// <summary>
    /// Retires the sub-pool of <paramref name="key"/>, disposing it and deregistering the key.
    /// </summary>
    /// <param name="key">The key to retire.</param>
    /// <returns><c>true</c> if a sub-pool was retired; <c>false</c> if the key had none.</returns>
    /// <remarks>
    /// Disposing a sub-pool destroys the objects it holds, so objects borrowed from it and not yet returned
    /// must not be returned afterwards — <see cref="ReturnObject"/> rejects them once the key is gone. This
    /// is the way to keep a keyed pool bounded when the key space is open-ended.<br />
    /// A key re-used after this gets a fresh sub-pool, so this is a way to drop a sub-pool that has grown
    /// stale, not a way to ban a key.
    /// </remarks>
    /// <example>
    /// <code>
    /// pools.TryRemove(goneTenant);
    /// </code>
    /// </example>
    public bool TryRemove(TKey key)
    {
        if (key is null) throw new ArgumentNullException(nameof(key));

        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_subPools.TryRemove(key, out var pool)) return false;

            _registry.Remove(SubPoolName(key));
            pool.Dispose();
            return true;
        }
    }

    /// <summary>
    /// Destroys every object held by every sub-pool, keeping the keys and their capacity.
    /// </summary>
    /// <remarks>
    /// Objects currently borrowed are left alone and stay the caller's responsibility — the same ownership
    /// rule as a single pool's <see cref="IHayateObjectPool.Clear"/>.
    /// </remarks>
    public void Clear()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            foreach (var pool in _subPools.Values) pool.Clear();
        }
    }

    /// <summary>
    /// Disposes every sub-pool and deregisters every key.
    /// </summary>
    /// <remarks>
    /// Idempotent, and it retires the keys as well, so a disposed keyed pool holds nothing and registers
    /// nothing. Borrowing afterwards throws <see cref="ObjectDisposedException"/> rather than silently
    /// creating a sub-pool that nothing will ever dispose.
    /// </remarks>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var pair in _subPools)
            {
                _registry.Remove(SubPoolName(pair.Key));
                pair.Value.Dispose();
            }

            _subPools.Clear();
        }
    }

    private IHayateObjectPool<TValue> GetOrCreate(TKey key)
    {
        if (_subPools.TryGetValue(key, out var existing)) return existing;

        lock (_gate)
        {
            ThrowIfDisposed();
            if (_subPools.TryGetValue(key, out existing)) return existing;

            var created = _subPoolFactory(key);
            if (created is null)
            {
                throw new InvalidOperationException(
                    "The ParameterizedHayatePool sub-pool factory returned null.");
            }

            _subPools[key] = created;
            _registry.Register(SubPoolName(key), created);
            return created;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(_name);
    }

    private string SubPoolName(TKey key) => SubPoolNameOf(_name, key);

    private static string SubPoolNameOf(string prefix, TKey key) =>
        prefix + "[" + DescribeKey(key) + "]";

    private static string DescribeKey(TKey key)
    {
        // A key's own ToString is the readable address; keys are compared by equality, never by this text,
        // so a null or empty rendering only costs a readable name.
        var text = key is string s ? s : Convert.ToString(key, CultureInfo.InvariantCulture);
        return string.IsNullOrEmpty(text) ? "(null)" : text!;
    }
}
