using System;
using System.Threading;
using System.Threading.Tasks;
using DotNetCore.HayateOP.Common;

namespace DotNetCore.HayateOP;

// Lean (wrapper-free) fast path.
//
// The general-purpose engine earns its features with per-object state: every pooled value is
// wrapped in a HayateObject<T> that carries timestamps, a generation, a free-list node and a
// lifecycle location, and every transition runs through a shard spin lock. That is the right
// trade for validation, eviction, leak forensics and metrics, and pure overhead for a pool that
// only ever hands objects out and takes them back.
//
// Lean mode drops all of it. The pooled value goes straight into a bounded buffer
// (_leanFirstItem plus _leanSlots) and moves through Interlocked compare-exchange, the same shape
// used by Microsoft.Extensions.ObjectPool.DefaultObjectPool. Borrowing and returning therefore
// allocate nothing, take no lock, keep no timestamps and write no counters, so the steady-state
// path is a handful of instructions.
//
// What is preserved: the MaxPoolSize ceiling (enforced by an atomic reservation in TryGrowLean),
// MinPoolSize pre-warming, the four HayatePoolRejectPolicy behaviours, the object-policy hooks,
// asynchronous acquisition, and the Clear / Dispose lifecycle.
public partial class HayatePoolBasic<T>
{
    #region Lean state

    // Constructor-time constant. The dispatch branch at the top of Acquire / Release folds away
    // completely for pools that never opt in, so the default path pays nothing for this feature.
    private readonly bool _enableLean;

    // Bounded retention buffer. _leanFirstItem is the dedicated single-slot fast lane that the
    // steady-state borrow hits; _leanSlots carries the remaining capacity. The two together retain
    // at most MaxPoolSize idle objects, matching the ceiling of the general-purpose engine.
    // _leanSlots is sized MaxPoolSize - 1 precisely so the fast lane does not add an extra slot.
    private readonly T?[] _leanSlots;
    private T? _leanFirstItem;
    private readonly int _leanCapacity;
    private readonly bool _leanRetentionEnabled;

    // Live object count (idle + borrowed). Written only when an object is created or destroyed —
    // never on the steady-state borrow/return path — so it costs the hot path nothing while still
    // giving the pool a hard ceiling on how many objects it will ever create.
    private long _leanLive;

    #endregion

    #region Lean creation and storage

    /// <summary>
    /// Creates a pooled value without a wrapper and without a registry entry.
    /// Mirrors the retry policy of the wrapping creation path so a flaky factory behaves
    /// identically in both modes.
    /// </summary>
    private T CreateLeanObject()
    {
        for (var retry = 0; retry < _options.CreationRetryCount; retry++)
        {
            try
            {
                return _policy.Create();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating object. Retry {Retry}/{MaxRetries}", retry + 1, _options.CreationRetryCount);

                if (retry == _options.CreationRetryCount - 1)
                    throw new InvalidOperationException("Failed to create object after retries", ex);

                Thread.Sleep(_options.CreationRetryDelay);
            }
        }

        throw new InvalidOperationException("Failed to create object after retries");
    }

    /// <summary>
    /// Reserves one slot against the <see cref="HayatePoolOptions.MaxPoolSize"/> ceiling and creates
    /// the object for it.
    /// </summary>
    /// <param name="item">The newly created object, or <c>null</c> when the ceiling is reached.</param>
    /// <returns><c>true</c> when an object was created; <c>false</c> when the pool is at capacity.</returns>
    /// <remarks>
    /// The reservation is a compare-exchange loop rather than a bare increment, so concurrent
    /// borrowers can never overshoot the ceiling; a creation failure rolls the reservation back.
    /// </remarks>
    private bool TryGrowLean(out T item)
    {
        item = null!;

        while (true)
        {
            var live = Volatile.Read(ref _leanLive);
            if (live >= _leanCapacity) return false;
            if (Interlocked.CompareExchange(ref _leanLive, live + 1, live) != live) continue;
            break;
        }

        try
        {
            item = CreateLeanObject();
        }
        catch
        {
            Interlocked.Decrement(ref _leanLive);
            throw;
        }

        return true;
    }

    /// <summary>
    /// Takes an idle object out of the lean buffer: the single-slot fast lane first (the
    /// steady-state hit), then the bounded slot array. Pure <see cref="Interlocked"/> — no lock,
    /// no allocation, no bookkeeping.
    /// </summary>
    /// <param name="item">The object taken, or <c>null</c> when the buffer holds nothing idle.</param>
    /// <returns><c>true</c> when an object was taken.</returns>
    private bool TryTakeLean(out T item)
    {
        // Claim pattern: read the slot, then compare-exchange it to null. CompareExchange returns
        // the previous value, so a reference match proves this thread was the one that won it.
        var first = _leanFirstItem;
        if (first is not null &&
            ReferenceEquals(first, Interlocked.CompareExchange(ref _leanFirstItem, null, first)))
        {
            item = first;
            return true;
        }

        var slots = _leanSlots;
        for (var i = 0; i < slots.Length; i++)
        {
            var slot = slots[i];
            if (slot is not null &&
                ReferenceEquals(slot, Interlocked.CompareExchange(ref slots[i], null, slot)))
            {
                item = slot;
                return true;
            }
        }

        item = null!;
        return false;
    }

    /// <summary>
    /// Puts an object back into the lean buffer, filling the fast lane first and then the first
    /// empty slot.
    /// </summary>
    /// <param name="item">The object to retain; must not be <c>null</c>.</param>
    /// <returns>
    /// <c>true</c> when the object was retained; <c>false</c> when the buffer already holds
    /// <see cref="HayatePoolOptions.MaxPoolSize"/> idle objects — the caller then drops it, which
    /// is the lean analogue of the reference pool's discard-on-full behaviour.
    /// </returns>
    private bool TryReturnLean(T item)
    {
        if (!_leanRetentionEnabled) return false;

        if (_leanFirstItem is null &&
            Interlocked.CompareExchange(ref _leanFirstItem, item, null) is null)
        {
            return true;
        }

        var slots = _leanSlots;
        for (var i = 0; i < slots.Length; i++)
        {
            if (slots[i] is null &&
                Interlocked.CompareExchange(ref slots[i], item, null) is null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Counts the idle objects currently retained in the lean buffer. Diagnostic path only — the
    /// steady-state borrow/return path never needs the count, which is exactly why the lean buffer
    /// can avoid maintaining one.
    /// </summary>
    private int CountLeanIdle()
    {
        var count = _leanFirstItem is null ? 0 : 1;
        var slots = _leanSlots;

        for (var i = 0; i < slots.Length; i++)
        {
            if (slots[i] is not null) count++;
        }

        return count;
    }

    /// <summary>
    /// Destroys a lean-mode object: runs the policy hook, disposes the value and releases its slot
    /// reservation. Every object reachable from the lean buffer was created by this pool, so the
    /// reservation always exists — except when a caller returns an object the pool never created,
    /// which lean mode cannot detect (there is no registry to check against) and which the
    /// counter guard below keeps from corrupting the live count.
    /// </summary>
    private void DestroyLean(T item)
    {
        if (item is null) return;

        try
        {
            _policy.OnDestroy(item);
            if (item is IDisposable d) d.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during object destruction. Type: {Type}", typeof(T).Name);
        }
        finally
        {
            if (Volatile.Read(ref _leanLive) > 0) Interlocked.Decrement(ref _leanLive);
        }
    }

    #endregion

    #region Lean warm-up and lifecycle

    /// <summary>
    /// Pre-creates <see cref="HayatePoolOptions.MinPoolSize"/> objects into the lean buffer.
    /// Never creates more than <see cref="HayatePoolOptions.MaxPoolSize"/>.
    /// </summary>
    private void PreWarmLean()
    {
        try
        {
            var target = Math.Min(_options.MinPoolSize, _leanCapacity);
            var warmed = 0;

            for (var i = 0; i < target; i++)
            {
                var item = CreateLeanObject();
                Interlocked.Increment(ref _leanLive);

                if (!TryReturnLean(item))
                {
                    // Unreachable while the loop bound respects the capacity; kept as a guard so an
                    // object can never leak out of the live count if that invariant is broken later.
                    DestroyLean(item);
                    break;
                }

                warmed++;
            }

            _logger.LogInformation("Object pool [{PoolName}] pre-warmed with {Count} objects (lean mode)", _name, warmed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during pool [{PoolName}] pre-warming", _name);
        }
    }

    /// <summary>
    /// Destroys every idle object retained by the lean buffer. Borrowed objects are untouched, so
    /// their reservations stay accounted for and they re-enter the buffer normally on return.
    /// </summary>
    private void ClearLean()
    {
        var destroyed = 0;

        var first = Interlocked.Exchange(ref _leanFirstItem, null);
        if (first is not null)
        {
            DestroyLean(first);
            destroyed++;
        }

        var slots = _leanSlots;
        for (var i = 0; i < slots.Length; i++)
        {
            var slot = Interlocked.Exchange(ref slots[i], null);
            if (slot is not null)
            {
                DestroyLean(slot);
                destroyed++;
            }
        }

        _logger.LogInformation("Clearing object pool. Type: {Type} (lean mode) destroyed: {Count}", typeof(T).Name, destroyed);
    }

    #endregion

    #region Lean acquire / release

    /// <summary>
    /// Synchronous lean acquire. An empty buffer is grown up to the ceiling before the reject
    /// policy is consulted, which is what makes an empty lean pool non-blocking instead of waiting
    /// for a timeout to elapse.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="timeout"/> is negative.</exception>
    /// <exception cref="InvalidOperationException">Thrown by the Abort policy when the pool is at capacity and holds nothing idle.</exception>
    /// <exception cref="TimeoutException">Thrown by the BlockTimeout (or CreateNew) policy when acquisition exceeds <paramref name="timeout"/>.</exception>
    private T AcquireLean(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be non-negative");

        WaitForWarmupIfNeeded();

        // Fast path: an idle object is already available, so the timeout cannot come into play and
        // the elapsed-time measurement is not needed. Keeping the stopwatch out of this branch
        // matters: a QueryPerformanceCounter read costs more than the borrow itself and would
        // dominate the entire fast path.
        if (TryTakeLean(out var hit))
        {
            // Consume one wake-up signal (if any), keeping the signal count aligned with the idle
            // objects exactly as the general-purpose path does. The CurrentCount guard keeps the
            // common no-signal case down to a single volatile read.
            if (_blockGate.CurrentCount > 0) _blockGate.Wait(0);

            _policy.OnAcquire(hit);
            return hit;
        }

        return AcquireLeanSlow(timeout);
    }

    /// <summary>
    /// The lean acquire path once the buffer came back empty: grow up to the ceiling first, and
    /// only then fall back to the configured reject policy. The elapsed-time measurement starts
    /// here, because a timeout can only be exceeded after at least one miss.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown by the Abort policy when the pool is at capacity and holds nothing idle.</exception>
    /// <exception cref="TimeoutException">Thrown by the BlockTimeout (or CreateNew) policy when acquisition exceeds <paramref name="timeout"/>.</exception>
    private T AcquireLeanSlow(TimeSpan timeout)
    {
        var sw = ValueStopwatch.StartNew();

        while (true)
        {
            if (TryTakeLean(out var item))
            {
                if (_blockGate.CurrentCount > 0) _blockGate.Wait(0);

                _policy.OnAcquire(item);
                return item;
            }

            if (TryGrowLean(out var created))
            {
                _policy.OnAcquire(created);
                return created;
            }

            switch (_options.RejectPolicy)
            {
                case HayatePoolRejectPolicy.Abort:
                    {
                        throw new InvalidOperationException($"HayatePool [{_name}] has no available object; the request was rejected by the Abort reject policy.");
                    }

                case HayatePoolRejectPolicy.Block:
                    {
                        // No timeout concept under this policy; wait for a return signal and retry.
                        _blockGate.Wait(BlockWaitSliceMs);
                        continue;
                    }

                case HayatePoolRejectPolicy.CreateNew:
                case HayatePoolRejectPolicy.CreateOnDemand:
                case HayatePoolRejectPolicy.BlockTimeout:
                    {
                        // CreateNew cannot deliver on its promise here: the pool is already at
                        // MaxPoolSize, which is a hard ceiling in lean mode exactly as it is in the
                        // general-purpose engine. It therefore degrades to the timeout behaviour.
                        // CreateOnDemand is in the same position — its storage is a fixed buffer sized at
                        // construction, so "create while the pool has room" is never satisfiable here.
                        if (sw.Elapsed >= timeout)
                        {
                            throw new TimeoutException($"HayatePool [{_name}] timed out acquiring an object after {timeout.TotalSeconds}s.");
                        }

                        _blockGate.Wait(BlockWaitSliceMs);
                        continue;
                    }

                default:
                    {
                        throw new ArgumentOutOfRangeException(nameof(_options.RejectPolicy), "Unknown reject policy.");
                    }
            }
        }
    }

    /// <summary>
    /// Asynchronous lean acquire. Shares the storage with the synchronous path, so a pool serves
    /// both call styles from one buffer.
    /// </summary>
    /// <exception cref="TaskCanceledException">Thrown if <paramref name="cancellationToken"/> is cancelled while waiting.</exception>
    private async Task<T> AcquireLeanAsync(CancellationToken cancellationToken)
    {
        if (_waitForWarmup) await _warmupCompletion.Task.ConfigureAwait(false);

        while (!cancellationToken.IsCancellationRequested)
        {
            if (TryTakeLean(out var item))
            {
                if (_blockGate.CurrentCount > 0) _blockGate.Wait(0);

                _policy.OnAcquire(item);
                return item;
            }

            if (TryGrowLean(out var created))
            {
                _policy.OnAcquire(created);
                return created;
            }

            await _blockGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        throw new TaskCanceledException();
    }

    /// <summary>
    /// Returns an object to the lean buffer.
    /// </summary>
    /// <remarks>
    /// Lean mode keeps no registry, so it cannot verify that <paramref name="item"/> came from this
    /// pool — an object from elsewhere is accepted and pooled. That is the same contract the
    /// reference zero-wrapper pool offers, and it is the deliberate price of removing the reverse
    /// lookup from the return path.
    /// </remarks>
    private void ReleaseLean(T item)
    {
        if (item is null)
        {
            _logger.LogWarning("Returned null object to pool. Type: {Type}", typeof(T).Name);
            return;
        }

        try
        {
            _policy.OnPassivate(item);

            if (!_policy.OnRelease(item))
            {
                _logger.LogWarning("Policy rejected object on release. Disposing. Type: {Type}", typeof(T).Name);
                DestroyLean(item);
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during object return validation. Disposing. Type: {Type}", typeof(T).Name);
            DestroyLean(item);
            return;
        }

        if (!TryReturnLean(item))
        {
            // Buffer already at MaxPoolSize idle objects: drop it. An overflow is a normal
            // steady-state outcome (more objects outstanding than the pool retains), so it is not
            // an error and is not logged on the hot path.
            DestroyLean(item);
            return;
        }

        // Wake a waiter only when the pool is at its ceiling: below the ceiling a waiting borrower
        // would have grown the pool instead of parking, so there is nobody to wake. This keeps the
        // uncontended return path down to a volatile read.
        if (Volatile.Read(ref _leanLive) >= _leanCapacity)
        {
            try { _blockGate.Release(); }
            catch (SemaphoreFullException)
            {
                // int.MaxValue counter cap protection; unreachable under normal load.
            }
        }
    }

    #endregion

    #region Lean diagnostics

    /// <summary>
    /// Lean-mode statistics. Instantaneous state is measured from the buffer; the cumulative
    /// counters stay at 0 because maintaining them would require an atomic write on every
    /// borrow and return — precisely the overhead the fast path exists to remove.
    /// </summary>
    private HayatePoolStats GetLeanStats()
    {
        var idle = CountLeanIdle();

        return new HayatePoolStats
        {
            PooledCount = idle,
            AvailableSlots = idle,
            MinSize = _options.MinPoolSize,
            CurrentSize = (int)Interlocked.Read(ref _leanLive),
            TotalCreated = 0,
            TotalReleased = 0,
            TotalMissed = 0,
            TotalAcquired = 0,
            LeakDetectedCount = 0,
            LeakSuspectedCount = 0,
            AllocationTrackingEnabled = false,
            MinWaitTimeMs = 0,
            MinLeaseTimeMs = 0
        };
    }

    /// <summary>
    /// Lean-mode snapshot. There is no per-object wrapper to describe, so the object-detail and
    /// leak-trace collections come back empty; the pooled and borrowed counts are derived from the
    /// buffer and the live reservation counter.
    /// </summary>
    private HayatePoolSnapshot GetLeanSnapshot()
    {
        var idle = CountLeanIdle();
        var live = (int)Interlocked.Read(ref _leanLive);
        var borrowed = live - idle;
        if (borrowed < 0) borrowed = 0;

        return new HayatePoolSnapshot
        {
            Timestamp = DateTimeOffset.UtcNow,
            PooledCount = idle,
            BorrowedCount = borrowed,
            TotalCreated = 0,
            TotalMissed = 0,
            TotalAcquired = 0,
            LeakCount = 0,
            LeakSuspectedCount = 0,
            AllocationTrackingEnabled = false,
            LeakTraces = Array.Empty<string>(),
            ObjectDetails = Array.Empty<HayatePoolObjectDetail>()
        };
    }

    #endregion
}
