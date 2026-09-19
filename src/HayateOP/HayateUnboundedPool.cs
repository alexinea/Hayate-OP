using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace DotNetCore.HayateOP;

/// <summary>
/// The unbounded pool model (N1), aligned with marklauter's <c>UnboundedPool</c>: ArrayPool-style
/// borrowing that never blocks, never waits and never rejects — when the idle queue is empty the
/// next borrow simply creates a new object, whatever the momentary demand is.
/// </summary>
/// <remarks>
/// This is the second pool model next to the bounded engine (<see cref="HayatePoolBuilder{T}"/>).
/// The differences are structural, not tunable:<br />
/// - <b>The lease is ownership.</b> A borrowed object belongs to the borrower; returning it is
/// optional. An object that is never returned is simply collected by the garbage collector — the
/// pool keeps no per-object bookkeeping, so a forgotten return cannot leak or block anyone (this
/// is what makes it safe for transient buffers and short-lived large objects). The flip side: the
/// pool cannot detect double returns, so returning the same object twice would queue it twice —
/// with ownership semantics that is the caller's contract to keep.<br />
/// - <b><see cref="MaxIdle"/> keeps the resident set bounded.</b> A return that arrives when
/// <see cref="MaxIdle"/> objects are already idle destroys the object instead of parking it, so
/// memory does not grow with the peak demand — the pool never holds more than
/// <see cref="MaxIdle"/> objects, while having served arbitrarily many.<br />
/// - <b>No <c>MaxSize</c>, no timeouts, no rejection policies, no background features.</b> There
/// is nothing to wait for and nothing to scale: the only knob is how many objects to keep.
/// <br />
/// The pool is a full <see cref="IHayateObjectPool{T}"/>, so scoped borrows
/// (<c>AcquireScoped()</c>), statistics and the DI surface work unchanged. Objects implementing
/// <see cref="IHayateResettable"/> are reset on return; objects implementing
/// <see cref="IHayateValidatable"/> are validated on return, and a failed validation destroys the
/// object instead of parking it. <see cref="AcquireAsync(System.Threading.CancellationToken)"/> completes synchronously (there is
/// nothing to wait for), and <see cref="Acquire(TimeSpan)"/> ignores the timeout for the same
/// reason. There is no circuit breaker: <see cref="CheckAvailable"/> reflects the manual
/// availability flag only.
/// </remarks>
/// <example>
/// <code>
/// var pool = new HayateUnboundedPool&lt;MessageBuffer&gt;(maxIdle: 64);
///
/// // Borrow under any burst: the pool creates on demand, never blocks.
/// var buffer = pool.Acquire();
/// try { /* use the buffer */ }
/// finally { pool.Release(buffer); }   // optional: not returning is fine, the GC handles it
/// </code>
/// </example>
/// <typeparam name="T">The pooled object type.</typeparam>
public class HayateUnboundedPool<T> : IHayateObjectPool<T> where T : class, new()
{
    /// <summary>
    /// The default <see cref="MaxIdle"/>: 32 retained objects.
    /// </summary>
    public const int DefaultMaxIdle = 32;

    private readonly ConcurrentQueue<T> _idle = new();
    private readonly Func<T> _factory;
    private readonly string _name;
    private readonly bool _resetOnReturn;
    private readonly bool _validateOnReturn;

    // The number of objects currently parked in _idle; kept in lockstep with the queue.
    private int _pooledCount;

    private volatile bool _available = true;

    private long _totalCreated;
    private long _totalMissed;
    private long _totalAcquired;
    private long _totalReleased;
    private long _totalDestroyed;

    /// <summary>
    /// Builds an unbounded pool that keeps at most <see cref="DefaultMaxIdle"/> idle objects and
    /// creates new ones with their parameterless constructor.
    /// </summary>
    public HayateUnboundedPool()
        : this(DefaultMaxIdle, static () => new T())
    {
    }

    /// <summary>
    /// Builds an unbounded pool that keeps at most <paramref name="maxIdle"/> idle objects and
    /// creates new ones with their parameterless constructor.
    /// </summary>
    /// <param name="maxIdle">The maximum number of objects the pool retains; must be greater than
    /// zero.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxIdle"/> is not greater than
    /// zero.</exception>
    public HayateUnboundedPool(int maxIdle)
        : this(maxIdle, static () => new T())
    {
    }

    /// <summary>
    /// Builds an unbounded pool that keeps at most <paramref name="maxIdle"/> idle objects and
    /// creates new ones through <paramref name="factory"/>.
    /// </summary>
    /// <param name="maxIdle">The maximum number of objects the pool retains; must be greater than
    /// zero.</param>
    /// <param name="factory">The factory used when the idle queue is empty.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxIdle"/> is not greater than
    /// zero.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <c>null</c>.</exception>
    public HayateUnboundedPool(int maxIdle, Func<T> factory)
    {
        if (maxIdle <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxIdle), maxIdle,
                "The maximum number of retained objects must be greater than zero.");
        }

        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        MaxIdle = maxIdle;
        _name = typeof(T).Name;
        _resetOnReturn = typeof(IHayateResettable).IsAssignableFrom(typeof(T));
        _validateOnReturn = typeof(IHayateValidatable).IsAssignableFrom(typeof(T));
    }

    /// <summary>
    /// The maximum number of objects the pool retains. A return that arrives while this many
    /// objects are idle destroys the object instead of parking it.
    /// </summary>
    /// <remarks>
    /// This is not a capacity limit on demand — the pool serves any number of concurrent borrows —
    /// only a limit on the resident (retained) set.
    /// </remarks>
    public int MaxIdle { get; }

    /// <summary>
    /// The logical name of the pool (the type name of <typeparamref name="T"/>), used in snapshots.
    /// </summary>
    public string Name => _name;

    /// <summary>
    /// The number of objects currently parked in the pool.
    /// </summary>
    public int PooledCount => _pooledCount;

    /// <inheritdoc />
    public T Acquire()
    {
        if (_idle.TryDequeue(out var item))
        {
            Interlocked.Decrement(ref _pooledCount);
        }
        else
        {
            item = _factory();
            Interlocked.Increment(ref _totalCreated);
            // A miss in a bounded pool means waiting or failing; here it is just the create path,
            // but the counter keeps the statistics comparable.
            Interlocked.Increment(ref _totalMissed);
        }

        Interlocked.Increment(ref _totalAcquired);
        return item;
    }

    /// <summary>
    /// Acquires an object; the timeout is accepted for interface compatibility and ignored, because
    /// an unbounded pool never waits.
    /// </summary>
    /// <inheritdoc />
    public T Acquire(TimeSpan timeout) => Acquire();

    /// <summary>
    /// Acquires an object; the pool never waits, so the returned task is always completed and the
    /// token is never observed.
    /// </summary>
    /// <inheritdoc />
    public Task<T> AcquireAsync(CancellationToken cancellationToken = default) => Task.FromResult(Acquire());

    /// <summary>
    /// Acquires an object; the pool never waits, so the returned task is always completed and
    /// neither the timeout nor the token is ever observed.
    /// </summary>
    /// <inheritdoc />
    public Task<T> AcquireAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        => Task.FromResult(Acquire());

    /// <summary>
    /// Returns an object to the pool. Returning is optional under the ownership model: a borrowed
    /// object that is never returned is left to the garbage collector.
    /// </summary>
    /// <param name="item">The object previously obtained from <see cref="Acquire()"/>.</param>
    /// <remarks>
    /// When <see cref="MaxIdle"/> objects are already parked, the object is destroyed instead of
    /// being parked, keeping the resident set bounded by the peak-idle policy rather than the peak
    /// demand. An object implementing <see cref="IHayateValidatable"/> that fails validation is
    /// destroyed as well; one implementing <see cref="IHayateResettable"/> is reset before parking.
    /// Every return counts toward <see cref="HayatePoolStats.TotalReleased"/>, including the ones
    /// that end in a destroy.
    /// Returning the same object twice is a caller contract violation under the ownership model and
    /// is not detected — it would hand the same object to two borrowers later.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="item"/> is <c>null</c>.</exception>
    public void Release(T item)
    {
        if (item is null)
        {
            throw new ArgumentNullException(nameof(item));
        }

        // The return action itself is counted first: parking and destroying are both outcomes of
        // it, so TotalReleased + parked stays comparable with TotalAcquired.
        Interlocked.Increment(ref _totalReleased);

        if (_validateOnReturn && item is IHayateValidatable validatable && !validatable.IsValid())
        {
            Destroy(item);
            return;
        }

        // Park only while there is room; past MaxIdle the object is destroyed, which is the
        // resident-set bound this model offers instead of a MaxSize.
        while (true)
        {
            var current = _pooledCount;
            if (current >= MaxIdle)
            {
                Destroy(item);
                return;
            }

            if (Interlocked.CompareExchange(ref _pooledCount, current + 1, current) == current)
            {
                break;
            }
        }

        if (_resetOnReturn && item is IHayateResettable resettable)
        {
            resettable.Reset();
        }

        _idle.Enqueue(item);
    }

    /// <inheritdoc />
    public HayatePoolStats GetStats()
    {
        return new HayatePoolStats
        {
            PooledCount = _pooledCount,
            CurrentSize = _pooledCount,
            MinSize = 0,
            AvailableSlots = Math.Max(0, MaxIdle - _pooledCount),
            TotalCreated = Interlocked.Read(ref _totalCreated),
            TotalMissed = Interlocked.Read(ref _totalMissed),
            TotalAcquired = Interlocked.Read(ref _totalAcquired),
            TotalReleased = Interlocked.Read(ref _totalReleased),
            TotalDestroyed = Interlocked.Read(ref _totalDestroyed),
        };
    }

    /// <inheritdoc />
    public HayatePoolSnapshot TakeSnapshot()
    {
        var stats = GetStats();
        return new HayatePoolSnapshot
        {
            Timestamp = DateTimeOffset.UtcNow,
            PooledCount = stats.PooledCount,
            BorrowedCount = 0, // ownership model: the pool does not track borrowed objects
            TotalCreated = stats.TotalCreated,
            TotalMissed = stats.TotalMissed,
            TotalAcquired = stats.TotalAcquired,
            ObjectDetails = [], // no wrappers to enumerate
        };
    }

    /// <summary>
    /// The unbounded pool has no <see cref="HayatePoolOptions"/>-driven configuration; the call is
    /// accepted and ignored so generic interface consumers keep working.
    /// </summary>
    /// <inheritdoc />
    public void ReloadConfig(Action<HayatePoolOptions> configure)
    {
    }

    /// <summary>
    /// Destroys every parked object and empties the pool; the next borrow creates fresh objects.
    /// </summary>
    /// <inheritdoc />
    public void Clear()
    {
        while (_idle.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _pooledCount);
            Interlocked.Increment(ref _totalDestroyed);
        }
    }

    /// <summary>
    /// A default-constructed options object; the unbounded pool has no engine options, and this
    /// value exists for interface compatibility only.
    /// </summary>
    /// <inheritdoc />
    public HayatePoolOptions GetOptions() => new HayatePoolOptions();

    /// <summary>
    /// Reports the manual availability flag. There is no circuit breaker: the flag is set through
    /// <see cref="SetUnavailable"/> / <see cref="SetAvailable"/> only, and borrows are unaffected
    /// either way — an unbounded pool has no dependency to fail.
    /// </summary>
    /// <inheritdoc />
    public bool CheckAvailable() => _available;

    /// <summary>
    /// Clears the availability flag (see <see cref="CheckAvailable"/>); borrows are unaffected.
    /// </summary>
    /// <inheritdoc />
    public void SetUnavailable(string? reason = null) => _available = false;

    /// <summary>
    /// Sets the availability flag (see <see cref="CheckAvailable"/>); borrows are unaffected.
    /// </summary>
    /// <inheritdoc />
    public void SetAvailable() => _available = true;

    /// <summary>
    /// Destroys every parked object and returns the number destroyed. Borrowed objects belong to
    /// their borrowers and are never touched; the reason is accepted for interface compatibility
    /// and not interpreted (there is no background aging in this model).
    /// </summary>
    /// <inheritdoc />
    public int Evict(HayateEvictReason reason)
    {
        var destroyed = 0;
        while (_idle.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _pooledCount);
            Interlocked.Increment(ref _totalDestroyed);
            destroyed++;
        }

        return destroyed;
    }

    /// <summary>
    /// Creates objects until the retained set holds at least <paramref name="count"/> idle objects, and
    /// returns how many this call created.
    /// </summary>
    /// <param name="count">The number of idle objects to retain; must not be negative.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    /// <remarks>
    /// This model's ceiling is <see cref="MaxIdle"/> — a limit on the resident set, not on concurrent demand
    /// — so a request above it warms up to <c>MaxIdle</c>. Objects currently lent out are not idle and do not
    /// count towards <paramref name="count"/>. Warming is the only way to make the first borrows cheap in
    /// this model: it creates on a miss, and it has no background warm-up of its own.<br />
    /// The objects go straight into the idle queue; they have never been used, so there is nothing to reset
    /// or validate on the way in.
    /// </remarks>
    /// <example>
    /// <code>
    /// new HayateUnboundedPool&lt;Buffer&gt;(64).PreWarm(64);
    /// </code>
    /// </example>
    /// <inheritdoc />
    public int PreWarm(int count)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "The pre-warm count must be non-negative.");
        }

        var target = Math.Min(count, MaxIdle);
        var created = 0;

        while (true)
        {
            // The same claim-then-park order as Release, so the resident count and the queue stay in
            // lockstep under concurrent borrows, returns and warm-ups.
            var current = Volatile.Read(ref _pooledCount);
            if (current >= target) break;
            if (Interlocked.CompareExchange(ref _pooledCount, current + 1, current) != current) continue;

            T item;
            try
            {
                item = _factory();
            }
            catch
            {
                Interlocked.Decrement(ref _pooledCount);
                throw;
            }

            Interlocked.Increment(ref _totalCreated);
            _idle.Enqueue(item);
            created++;
        }

        return created;
    }

    /// <summary>
    /// Destroys every parked object; the pool cannot be reused afterwards.
    /// </summary>
    /// <inheritdoc />
    public void Dispose()
    {
        Clear();
    }

    private void Destroy(T item)
    {
        // Dropping the reference is the whole destroy in this model: the GC reclaims the object,
        // which is the point of pooling garbage-collected types without a close protocol.
        Interlocked.Increment(ref _totalDestroyed);
    }
}
