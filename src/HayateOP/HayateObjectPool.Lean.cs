using System;
#if NET6_0_OR_GREATER
using System.Buffers;
#endif
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

    // T-R: the soft-capacity ceiling expressed as a slot-scan limit; -1 (the default) means no soft
    // ceiling and the whole buffer is available. When one is set, the fast lane carries one object and
    // the slot scan is capped at SoftCapacity - 1 slots, so the retained total never exceeds
    // SoftCapacity. The lean path keeps no idle counter, so expressing the ceiling structurally — as
    // "do not look past this slot" — is what keeps it off the return hot path.
    private readonly int _leanReturnSlotLimit = -1;

    // Live object count (idle + borrowed). Written only when an object is created or destroyed —
    // never on the steady-state borrow/return path — so it costs the hot path nothing while still
    // giving the pool a hard ceiling on how many objects it will ever create.
    private long _leanLive;

    // O-D: ArrayPool direct-storage backend. When enabled, _apSlots replaces _leanSlots as the
    // slot array: it is rented from ArrayPool<T>.Shared (net6+), grows on demand (×2) up to the
    // MaxPoolSize - 1 slot ceiling, and is returned to the shared pool on Dispose, so a large pool
    // only holds the array its demand actually reached. The fast lane (_leanFirstItem) is shared
    // with the fixed-buffer mode. Volatile: borrowers and returners observe the current array
    // through the volatile read, while a grow replaces the reference with a CAS. On netstandard2.0
    // / net48 the flag stays false (ArrayPool<T> is not a BCL type there) and lean keeps its
    // fixed buffer.
    private readonly bool _enableArrayPoolStorage;
    private volatile T?[] _apSlots;

    // The logical slot ceiling of the ArrayPool backend: MaxPoolSize - 1 (the fast lane holds one).
    // The rented array can be physically larger (ArrayPool returns bucket-aligned sizes), so the
    // capacity decisions must compare against this limit, never against _apSlots.Length.
    private readonly int _apSlotLimit;

    #endregion

    #region Lean creation and storage

    // Initial rented array size (slots beyond the fast lane): 31 slots + the fast lane = 32
    // objects retained without growing, matching the default MEOP start. Grows ×2 on demand.
    private const int DefaultArrayPoolStorageSlots = 31;

    /// <summary>The slot array the lean buffer currently stores into (fast lane excluded).</summary>
    private T?[] LeanSlotArray => _enableArrayPoolStorage ? _apSlots : _leanSlots;

    /// <summary>
    /// How many slots of <see cref="LeanSlotArray"/> the buffer scans: the storage backend's logical
    /// ceiling, further capped by the soft capacity (T-R) when one is set.
    /// </summary>
    /// <remarks>
    /// Nothing is ever stored past this limit, so the borrow path uses the same limit as the return
    /// path — a scan of a longer range could only look at slots it knows are empty.
    /// </remarks>
    private int LeanSlotScanLimit
    {
        get
        {
            var slots = LeanSlotArray;
            var limit = _enableArrayPoolStorage ? Math.Min(slots.Length, _apSlotLimit) : slots.Length;
            if (_leanReturnSlotLimit >= 0 && limit > _leanReturnSlotLimit) limit = _leanReturnSlotLimit;
            return limit;
        }
    }

    /// <summary>
    /// The largest number of idle objects the lean buffer retains: its ceiling, capped by the soft
    /// capacity (T-R) when one is set (the fast lane plus the slots the scan may reach).
    /// </summary>
    private int LeanIdleCapacity => _leanReturnSlotLimit >= 0 ? _leanReturnSlotLimit + 1 : _leanCapacity;

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
                return CreateObject();
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

#if NET6_0_OR_GREATER
    /// <summary>
    /// Asynchronous twin of <see cref="CreateLeanObject"/>: drives an
    /// <see cref="Policies.IHayateAsyncObjectPolicy{T}"/> through its asynchronous hook instead of the
    /// synchronous one (rule 1 of docs/async-policy.md). Reachable only when the pool holds such a
    /// policy, so the synchronous retry path above is unchanged for every other policy (rule 2).
    /// </summary>
    private async ValueTask<T> CreateLeanObjectAsync(CancellationToken cancellationToken)
    {
        for (var retry = 0; retry < _options.CreationRetryCount; retry++)
        {
            try
            {
                return await _asyncPolicy!.CreateAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // A cancelled token is not a creation failure: it propagates to the caller instead of
                // consuming retries and logging an error.
                _logger.LogError(ex, "Error creating object. Retry {Retry}/{MaxRetries}", retry + 1, _options.CreationRetryCount);

                if (retry == _options.CreationRetryCount - 1)
                    throw new InvalidOperationException("Failed to create object after retries", ex);

                await Task.Delay(_options.CreationRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException("Failed to create object after retries");
    }

    /// <summary>
    /// Asynchronous twin of <see cref="TryGrowLean(out T)"/>: same atomic reservation against the
    /// <see cref="HayatePoolOptions.MaxPoolSize"/> ceiling, asynchronous creation.
    /// </summary>
    /// <returns>
    /// A tuple rather than the <c>out</c> pair of the synchronous twin: a policy is allowed to return
    /// <c>null</c>, so a null sentinel could not distinguish "at capacity" from "created nothing" —
    /// while the synchronous signature has the <c>bool</c> return to carry that meaning.
    /// </returns>
    private async ValueTask<(bool Grown, T? Item)> TryGrowLeanAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var live = Volatile.Read(ref _leanLive);
            if (live >= _leanCapacity) return (false, null);
            if (Interlocked.CompareExchange(ref _leanLive, live + 1, live) != live) continue;
            break;
        }

        try
        {
            return (true, await CreateLeanObjectAsync(cancellationToken).ConfigureAwait(false));
        }
        catch
        {
            Interlocked.Decrement(ref _leanLive);
            throw;
        }
    }
#endif

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

        var slots = LeanSlotArray;
        var scanLimit = LeanSlotScanLimit;
        for (var i = 0; i < scanLimit; i++)
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

        var slots = LeanSlotArray;
        var scanLimit = LeanSlotScanLimit;
        for (var i = 0; i < scanLimit; i++)
        {
            if (slots[i] is null &&
                Interlocked.CompareExchange(ref slots[i], item, null) is null)
            {
                return true;
            }
        }

        // A soft ceiling (T-R) caps what the buffer may retain, so there is nothing to grow towards:
        // the growth path below exists only to reach the *hard* ceiling from a physically smaller
        // rented array, and running it would park this object past the soft limit.
        if (_leanReturnSlotLimit >= 0) return false;

        // Full within the logical limit: in ArrayPool mode a physically smaller array can still be
        // grown toward the ceiling; otherwise full is final and the caller drops the object.
#if NET6_0_OR_GREATER
        if (_enableArrayPoolStorage && slots.Length < _apSlotLimit)
        {
            return TryGrowLeanSlotsAndReturn(item);
        }
#endif
        return false;
    }

#if NET6_0_OR_GREATER
    /// <summary>
    /// Grows the rented slot array (O-D) by doubling up to the <c>MaxPoolSize - 1</c> slot ceiling,
    /// moves the existing content across, returns the old array to the shared pool, and places
    /// <paramref name="item"/> into the first fresh slot.
    /// </summary>
    /// <returns><c>true</c> when the object was retained in the grown array; <c>false</c> when the
    /// ceiling is already reached — the caller then drops the object, as in fixed-buffer mode.</returns>
    /// <remarks>
    /// The new array is filled before it is published, so a scanner can never observe a null slot
    /// where this return is about to land; on a lost publish race the item is taken back, the grown
    /// array is returned, and the loop retries against the current array.
    /// </remarks>
    private bool TryGrowLeanSlotsAndReturn(T item)
    {
        while (true)
        {
            var current = _apSlots;
            var slotCeiling = _apSlotLimit;
            if (current.Length >= slotCeiling)
            {
                return false;
            }

            var doubled = current.Length * 2;
            if (doubled < 0 || doubled < current.Length)
            {
                doubled = int.MaxValue - 1;
            }

            var grown = ArrayPool<T>.Shared.Rent(Math.Min(doubled, slotCeiling));
            Array.Copy(current, grown, current.Length);

            grown[current.Length] = item;
            if (Interlocked.CompareExchange(ref _apSlots, grown, current) == current)
            {
                ArrayPool<T>.Shared.Return((T[])(object)current);
                return true;
            }

            grown[current.Length] = null!;
            ArrayPool<T>.Shared.Return((T[])(object)grown);
        }
    }
#endif

    /// <summary>
    /// Counts the idle objects currently retained in the lean buffer. Diagnostic path only — the
    /// steady-state borrow/return path never needs the count, which is exactly why the lean buffer
    /// can avoid maintaining one.
    /// </summary>
    private int CountLeanIdle()
    {
        var count = _leanFirstItem is null ? 0 : 1;
        var slots = LeanSlotArray;
        var scanLimit = LeanSlotScanLimit;

        for (var i = 0; i < scanLimit; i++)
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
            DestroyObject(item);
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

#if NET6_0_OR_GREATER
    /// <summary>
    /// The awaited twin of <see cref="DestroyLean"/>, used by <see cref="ClearLeanAsync"/>: the
    /// destroy hook runs for every policy exactly as the lean clear always has — the asynchronous
    /// form when the policy provides one, the synchronous form otherwise (docs/async-policy.md §5)
    /// — and the object's disposal prefers <c>IAsyncDisposable</c>, because the caller explicitly
    /// chose the asynchronous drain.
    /// </summary>
    private async ValueTask DestroyLeanAsync(T item)
    {
        if (item is null) return;

        try
        {
            if (_asyncPolicy is not null)
            {
                await _asyncPolicy.OnDestroyAsync(item).ConfigureAwait(false);
            }
            else
            {
                _policy.OnDestroy(item);
            }
            await DisposeObjectAsync(item).ConfigureAwait(false);
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
#endif

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
            var target = Math.Min(_options.MinPoolSize, LeanIdleCapacity);
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
    /// Warms the lean buffer on demand: the shortfall between the requested idle count and what the buffer
    /// holds right now is created through the reservation path and parked in the buffer.
    /// </summary>
    /// <remarks>
    /// The reservation path enforces the pool ceiling on the live count (idle + borrowed), so warming never
    /// pushes a lean pool past its maximum. A created object can still lose the race for the last slot to a
    /// concurrent return; it is destroyed through the single lean destroy path, which also releases its
    /// reservation, and the warm-up stops there rather than retrying against a moving ceiling.
    /// </remarks>
    private int PreWarmLean(int count)
    {
        if (!_leanRetentionEnabled) return 0;

        var target = Math.Min(count, LeanIdleCapacity);
        var remaining = target - CountLeanIdle();
        if (remaining <= 0) return 0;

        var warmed = 0;
        for (var i = 0; i < remaining; i++)
        {
            if (!TryGrowLean(out var item)) break;
            if (!TryReturnLean(item))
            {
                DestroyLean(item);
                break;
            }

            warmed++;
        }

        if (warmed > 0)
        {
            _logger.LogInformation("Object pool [{PoolName}] pre-warmed on demand with {Count} objects (lean mode)", _name, warmed);
        }

        return warmed;
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

        var slots = LeanSlotArray;
        var scanLimit = _enableArrayPoolStorage ? Math.Min(slots.Length, _apSlotLimit) : slots.Length;
        for (var i = 0; i < scanLimit; i++)
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

#if NET6_0_OR_GREATER
    /// <summary>
    /// The awaited twin of <see cref="ClearLean"/>, used as the lean drain of
    /// <c>DisposeAsync</c>: the same slot sweep and the same live-count bookkeeping, statement for
    /// statement, with each destroy awaited through <see cref="DestroyLeanAsync"/> instead of run
    /// synchronously.
    /// </summary>
    private async ValueTask ClearLeanAsync()
    {
        var destroyed = 0;

        var first = Interlocked.Exchange(ref _leanFirstItem, null);
        if (first is not null)
        {
            await DestroyLeanAsync(first).ConfigureAwait(false);
            destroyed++;
        }

        var slots = LeanSlotArray;
        var scanLimit = _enableArrayPoolStorage ? Math.Min(slots.Length, _apSlotLimit) : slots.Length;
        for (var i = 0; i < scanLimit; i++)
        {
            var slot = Interlocked.Exchange(ref slots[i], null);
            if (slot is not null)
            {
                await DestroyLeanAsync(slot).ConfigureAwait(false);
                destroyed++;
            }
        }

        _logger.LogInformation("Clearing object pool. Type: {Type} (lean mode) destroyed: {Count}", typeof(T).Name, destroyed);
    }
#endif

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

    // The suspended-wait slice used by the lean acquire path when no signal arrives. A signal wakes
    // immediately; the slice merely caps the re-check interval when none does.
    //
    // The general-purpose engine no longer has a slice (B6-1): its synchronous borrow paths wait once,
    // on the signal, and every site that frees an object or a slot publishes one. Lean cannot follow
    // yet. TryGrowLean() only succeeds below the ceiling, so a borrower parks exactly when the live
    // count has reached it, and the drop that would let it grow again can come from a path that
    // publishes nothing: DestroyLean(), reached when the policy rejects a return or the buffer
    // overflows, only decrements the live count. The slice is what covers that gap, so it is
    // load-bearing here and cannot be removed until those paths signal too.
    private const int BlockWaitSliceMs = 100;

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

#if NET6_0_OR_GREATER
            if (_asyncPolicy is not null)
            {
                var (grown, createdAsync) = await TryGrowLeanAsync(cancellationToken).ConfigureAwait(false);
                if (grown)
                {
                    _policy.OnAcquire(createdAsync!);
                    return createdAsync!;
                }
            }
            else
#endif
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
#if NET6_0_OR_GREATER
        if (_asyncPolicy is not null)
        {
            ReleaseLeanAsync(item).GetAwaiter().GetResult();
            return;
        }
#endif

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

        CompleteLeanReturn(item);
    }

#if NET6_0_OR_GREATER
    private async ValueTask ReleaseLeanAsync(T item)
    {
        if (item is null)
        {
            _logger.LogWarning("Returned null object to pool. Type: {Type}", typeof(T).Name);
            return;
        }

        try
        {
            await _asyncPolicy!.OnPassivateAsync(item).ConfigureAwait(false);

            if (!await _asyncPolicy.OnReleaseAsync(item).ConfigureAwait(false))
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

        CompleteLeanReturn(item);
    }
#endif

    private void CompleteLeanReturn(T item)
    {
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
    /// borrow and return — precisely the overhead the fast path exists to remove. The G-1
    /// operational members are listed explicitly at their zero values for the same reason: the
    /// lean profile closes the master diagnostic switch, which closes metrics with it, so nothing
    /// here is ever maintained and the stats object must say so rather than leave a blank.
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
            MinLeaseTimeMs = 0,
            MetricsEnabled = false,
            PeakActiveObjects = 0,
            StartedAt = default,
            LastActivityTime = null
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
