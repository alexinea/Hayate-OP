using System.Collections.Concurrent;
#if NET6_0_OR_GREATER
using System.Buffers;
#endif
using System.Diagnostics;
using System.Runtime.CompilerServices;
using DotNetCore.HayateOP.Common;
using DotNetCore.HayateOP.Logging;
using DotNetCore.HayateOP.Metrics;
using DotNetCore.HayateOP.Policies;
using DotNetCore.HayateOP.Scaling;

namespace DotNetCore.HayateOP;

public partial class HayatePoolBasic<T> : IHayateObjectPool<T>
#if NET6_0_OR_GREATER
    , IHayateAsyncObjectPool<T>, IHayateAsyncReturnPool<T>
#endif
    where T : class
{
    private readonly string _name;

    private readonly Shard[] _shards;
    private readonly IHayateObjectPolicy<T> _policy;
#if NET6_0_OR_GREATER
    // The policy seen as its asynchronous contract (docs/async-policy.md), resolved once at
    // construction so dispatch is a null test on a readonly field and a policy that does not implement
    // the interface pays nothing for the feature. Always null on netstandard2.0 / net48, where the
    // interface is not part of the build.
    private readonly IHayateAsyncObjectPolicy<T>? _asyncPolicy;
#endif

    // A2 (docs/async-policy.md §5): the destroy paths prefer the object's IAsyncDisposable
    // teardown when the policy implements the asynchronous contract. Assigned true only on
    // net6.0 and later — netstandard2.0 / net48 have no IAsyncDisposable, so the flag stays
    // false there and every test on it is a constant the JIT folds away.
    private readonly bool _prefersAsyncDisposal = false;
    private readonly HayatePoolOptions _options;
    private readonly IHayateScalingStrategy _scalingStrategy;

    private readonly IHayateLogger _logger;
    private readonly IHayateMetrics _metrics;

    // O7: the rule the background eviction run applies. Never null — a pool that was not given a
    // policy holds HayateDefaultEvictionPolicy<T>.Instance. The run always asks the policy, so there is
    // exactly one implementation of the rule; _useCustomEvictionPolicy only picks the log line.
    private readonly IHayateEvictionPolicy<T> _evictionPolicy;
    private readonly bool _useCustomEvictionPolicy;

    // The original pool-level single _objectMap (ConcurrentDictionary<T, HayateObject<T>>) has been split by shard,
    // and moved into each Shard's internal registry (see HayateObjectPool.Shard.cs). Registry entries are written on object create/destroy
    // to their owning shard, eliminating the non-converging bucket-array peak caused by multiple shards writing the same table concurrently.
    // The pool level keeps only two derived views:
    /// <summary>Total number of live objects (idle + borrowed), derived by summing each shard's registry.</summary>
    private int TrackedObjectCount => _shards.Sum(s => s.TrackedCount);

    // T-R: the return-path soft ceiling, snapshotted at construction. 0 (the default) means the pool
    // retains up to MaxPoolSize idle objects, exactly as it did before the switch existed; the branch
    // that reads it is then a constant test on a readonly field and folds away.
    private readonly int _softCapacity;

    /// <summary>
    /// The number of idle objects the pool currently holds across every shard. Diagnostic and
    /// soft-capacity use: the per-shard free lists are the only place an idle object lives (a borrowed
    /// object is physically unlinked by <c>TryTake</c>), so this is the retained set the return path
    /// compares against <see cref="HayatePoolOptions.SoftCapacity"/>.
    /// </summary>
    /// <remarks>
    /// Written as an indexed loop rather than <c>_shards.Sum(...)</c> so the soft-capacity check costs
    /// no allocation: the enumerable form would box the array's enumerator on every return.
    /// </remarks>
    private int IdleObjectCount()
    {
        var total = 0;
        var shards = _shards;
        for (var i = 0; i < shards.Length; i++)
        {
            total += shards[i].Count;
        }

        return total;
    }

    /// <summary>
    /// Removes the registry entry from the wrapper's owning shard; if ShardIndex is invalid, falls back to a full-shard scan (theoretically unreachable,
    /// since the entry is written to the target shard and its ShardIndex synced in CreateWrappedObject).
    /// </summary>
    private void UntrackObject(HayateObject<T> w)
    {
        if (w?.Value is null) return;

        var home = (uint)w.ShardIndex < (uint)_shards.Length ? _shards[w.ShardIndex] : null;
        if (home is not null && home.Untrack(w.Value)) return;

        foreach (var shard in _shards)
        {
            if (shard.Untrack(w.Value)) return;
        }
    }

    /// <summary>When only a bare object reference (no wrapper) is available, scans all shards to remove the registry entry.</summary>
    private void UntrackKey(T o)
    {
        if (o is null) return;

        foreach (var shard in _shards)
        {
            if (shard.Untrack(o)) return;
        }
    }

    // Return-event notification gate. Block/BlockTimeout/CreateNew waiters use Wait instead of SpinWait
    // busy-waiting, eliminating the 100% CPU idle spin during the wait.
    // The counter means "number of consumable wake-up signals"; on a successful borrow it consumes one signal via Wait(0),
    // preventing stale signals from accumulating and causing waiters to be spuriously woken one by one into a busy loop.
    //
    // A waiter's wait is a single uninterruptible SemaphoreSlim wait: it is woken by a signal or by its own
    // timeout, and never by a periodic re-check. Complete signalling is therefore a correctness requirement and
    // not an optimisation: every event that lets a parked borrower make progress must call SignalAvailability(),
    // or that borrower sleeps until its timeout (and Block, which has no timeout, never wakes at all).
    // Two kinds of event qualify:
    //   * an object became available - an addition to a shard's idle list (the constructor's pre-warm,
    //     PreWarm(), Release(), ForceScaleUpOneStep(), the auto-scaling callback) or a lean return;
    //   * a slot was freed - the tracked or live count dropped, so a create-on-miss waiter may now grow
    //     (ReclaimAbandoned, DestroyLean).
    // Before 3.0 only Release() published a signal and a fixed 100 ms re-check covered everything else; that
    // implicit dependency is what B6-E2 named and B6-1 replaced with the rule above. Do not add a site that
    // makes an object available, or frees a slot, without calling SignalAvailability().
    private readonly SemaphoreSlim _blockGate = new(0, int.MaxValue);

    // Publishes a wake-up signal on the gate: something just happened that lets a parked borrower make
    // progress. See the invariant on _blockGate for the two kinds of event that qualify - every site of
    // either kind has to call this, because a waiter's wait is a single uninterruptible SemaphoreSlim wait.
    private void SignalAvailability()
    {
        try { _blockGate.Release(); }
        catch (SemaphoreFullException)
        {
            // int.MaxValue counter cap; unreachable under normal load.
        }
    }

    /// <summary>
    /// Milliseconds left before <paramref name="timeout"/> elapses, clamped to what
    /// <see cref="SemaphoreSlim.Wait(int)"/> accepts. Callers check the timeout first, so the result is at
    /// least 1: a wait can never degenerate into a spin.
    /// </summary>
    private static int RemainingWaitMs(TimeSpan timeout, TimeSpan elapsed)
    {
        var remaining = (timeout - elapsed).TotalMilliseconds;
        if (remaining < 1.0) return 1;
        return remaining >= int.MaxValue ? int.MaxValue : (int)Math.Ceiling(remaining);
    }

    // Background tasks.
    // Eviction, auto-scaling and idle validation share a single timer. The timer ticks at the smallest
    // enabled period and each loop fires only when its own deadline has elapsed, so every concern keeps
    // its configured cadence while the pool holds one timer handle instead of three. When all three are
    // disabled no timer is created at all — an idle pool with no background features never wakes up.
    // The schedule is fixed at construction, exactly like the three creation-time captures it replaces.
    private Timer? _backgroundTimer;
    private long _evictionPeriodTicks;
    private long _scalingPeriodTicks;
    private long _validationPeriodTicks;
    private long _abandonedPeriodTicks;
    private long _nextEvictionDue;
    private long _nextScalingDue;
    private long _nextValidationDue;
    private long _nextAbandonedDue;
    // Re-entrancy guard: a tick that overruns its period must not overlap the next one. The three loops
    // mutate shared shard state (claim + destroy), and the previous one-timer-per-concern layout never
    // ran a single concern concurrently with itself.
    private int _backgroundTickBusy;

    // Scale-up/down cooldown control to prevent thrashing
    // Stopwatch timestamp (0 = never scaled, equivalent to the original DateTime.MinValue semantics)
    private long _lastScaleUpTime;
    private long _lastScaleDownTime;

    // Statistics
    private long _totalCreated;
    private long _totalReleased;
    private long _totalMissed;
    private long _totalAcquired;

    // CreateNew shard polling cursor (avoids all newly created objects landing on the first shard)
    private int _createCursor;
    private long _leakDetectedCount;

    // Statistics are lock-free on the hot path — borrow/return paths now use Interlocked atomic accumulation,
    // and Min/Max use a CAS loop (stored as long milliseconds, converted to double when GetStats snapshots).
    // Update semantics are unchanged; GetStats no longer holds a global lock and instead reads each field atomically for an eventually-consistent snapshot.
    private long _waitTimeSum;
    private long _waitTimeCount;
    private long _waitTimeMaxMs;
    private long _waitTimeMinMs = long.MaxValue;
    private long _leaseTimeSum;
    private long _leaseTimeCount;
    private long _leaseTimeMaxMs;
    private long _leaseTimeMinMs = long.MaxValue;

    private readonly bool _enableValidation;
    // Diagnostics master switch (O11). When off, the engine writes no counter, reports nothing to
    // IHayateMetrics and emits no per-operation debug entry — the borrow/return paths then carry no
    // diagnostic work at all. Normalization guarantees EnableMetrics is false whenever this is false,
    // so the metrics gate below implies it: every `if (_enableMetrics)` block may rely on diagnostics
    // being on, and only the counter writes that are not metrics-gated need their own check.
    private readonly bool _enableDiagnostics;
    private readonly bool _enableMetrics;
    private readonly bool _enableGenerationOptimization;
    private readonly bool _enableLeakDetection;
    private readonly bool _enableEviction;
    private readonly bool _enableAutoScaling;

    // A3a (off by default): rotate an object that has outlived MaxLifeTime on the borrow path, instead
    // of handing it out. Captured at construction like every other feature switch.
    private readonly bool _enableLifetimeRotationOnBorrow;

    // Precomputed tick bounds for the two age comparisons on the borrow path (generational promotion and
    // the lifetime rotation). Keeping them as ticks removes the per-borrow double multiply/divide the
    // generational check used to do, and lets both consumers share a single age subtraction. They are
    // fields rather than constants because ReloadConfig can change MaxLifeTime / GenerationThresholdMs
    // at runtime, and a stale bound would make the borrow path disagree with the eviction paths about
    // whether the same object is expired.
    private long _generationThresholdTicks;
    private long _maxLifeTicks;

    // Leak forensics capture mode (decoupled from leak detection). Off by default — the borrow hot path does not capture stacks.
    private readonly HayateLeakTraceCaptureMode _leakTraceCaptureMode;
    private readonly int _leakTraceSampleRate;
    private int _leakTraceCounter;

    // Cold-start guard flag. 0 = unclaimed, 1 = a thread is already cold-starting the first object.
    // The CAS winner creates the first object; the loser falls back to the normal wait path. The flag is reset when creation finishes (including on exception),
    // so the empty pool can cold-start again later.
    private int _coldBootClaimed;

    // Capacity-alarm state machine (0 = Normal, 1 = Warning, 2 = Critical).
    // The callback fires once on a state transition (debounced); it silently resets at the low-water mark and can fire again after re-arming.
    private int _capacityAlarmLevel;
    private readonly bool _capacityAlarmEnabled;

    // Leak-recheck alert counter (when EnableLeakDetection = false, TakeSnapshot counts suspected leaks —
    // "borrowed beyond the threshold and not returned" — alongside LeakDetectedCount, not replacing it).
    private long _leakSuspectedCount;

    // Abandoned recovery (K2, CHOPIN's RemoveAbandonedOnBorrow/OnMaintenance). Both toggles are
    // destructive opt-ins captured at construction like every other feature switch; the timeout stays
    // live on _options so ReloadConfig can tune it.
    private readonly bool _removeAbandonedOnBorrow;
    private readonly bool _removeAbandonedOnMaintenance;
    private readonly bool _logAbandoned;
    private readonly bool _enableAbandonedRecovery;
    // Cumulative count of borrowed objects reclaimed as abandoned (the CHOPIN DestroyedByAbandonedCount analog).
    private long _abandonedRemovedCount;
    // A3a: cumulative count of objects destroyed on the borrow path for having outlived MaxLifeTime
    // (0 unless EnableLifetimeRotationOnBorrow is on).
    private long _lifetimeRotatedCount;
    // How many oldest outstanding borrows one borrow-path pass examines (the maintenance pass scans
    // everything). A small bounded budget keeps the opt-in borrow path's latency predictable while the
    // FIFO borrowed list guarantees the oldest — hence most likely abandoned — borrows are seen first.
    private const int BorrowAbandonedScanBudget = 8;

    // A3a-Q2: how many expired objects one borrow may retire before it stops rotating and hands out what
    // it found. The bound keeps a MaxLifeTime shorter than an object's creation time from turning a
    // single borrow into an endless create/destroy cycle — availability wins over lifetime, so the aged
    // object is handed out rather than the caller being made to wait for a replacement that expires
    // just as fast.
    private const int LifetimeRotationsPerBorrow = 1;

    // Shard-affinity mode (constructor-time snapshot). None is the default and has zero overhead (start index is always 0);
    // Thread maps the start shard stably by thread ID; Custom uses the user delegate (falling back to sequential scan on exception/out-of-range/null).
    private readonly HayateShardAffinityMode _affinityMode;
    private readonly Func<int>? _customShardAffinity;

    // Pre-warm readiness signal. false by default (synchronous pre-warm at construction, zero extra wait on borrow, consistent with 2.4);
    // when true, pre-warm runs in the background and the borrow path blocks on _warmupCompletion until pre-warm completes
    // (including on failure — the signal is always set, so there is no permanent block). During the wait the cold-start path yields.
    private readonly bool _waitForWarmup;
    private readonly TaskCompletionSource<object> _warmupCompletion = null!; // assigned in constructor when WaitForWarmup is enabled

    // Allocation tracking (off by default). Counts the per-thread allocation delta (bytes) and sample count on the synchronous borrow/return paths;
    // diagnostic only, never affects pool behavior decisions. Under net48 / netstandard2.0 the API is unavailable, so the counters stay 0.
    private readonly bool _enableAllocationTracking;
    private long _acquireAllocatedBytes;
    private long _releaseAllocatedBytes;
    private long _acquireAllocationSamples;
    private long _releaseAllocationSamples;

    // G-1 operational metrics. All three are maintained only while metrics are on, so a pool with the
    // switch off carries neither a write nor a branch for them.
    // The peak is not counted on the borrow path: the borrowed count is derived where it is already
    // computed (GetStats / TakeSnapshot), which keeps a per-shard lock and a registry probe off the
    // borrow path — the cost the capacity alarm is documented to pay and that this switch must not
    // inherit. 0 UTC ticks mean "never captured" / "no activity yet".
    private int _peakActiveObjects;
    private long _startedAtUtcTicks;
    private long _lastActivityUtcTicks;

    // Pool-level availability circuit breaker (off by default → constant branch, JIT-eliminable).
    // The enable switch and the failure threshold are snapshotted at construction like every other feature
    // switch, so ReloadConfig cannot turn the breaker on or off on a live pool. While the breaker is open,
    // every further borrow fails before doing any pool work at all; the probe is the only background work
    // the feature adds, and its timer exists only while the pool is unavailable (created on the trip,
    // disposed on the recovery), so an available pool holds no extra handle and pays one predicted branch.
    private readonly bool _enableCircuitBreaker;
    private readonly int _breakerFailureThreshold;
    private readonly Func<bool>? _breakerProbe;
    private readonly Action<HayatePoolAvailabilityEventArgs>? _onAvailable;
    private readonly Action<HayatePoolAvailabilityEventArgs>? _onUnavailable;

    // 0 = available, 1 = tripped. The borrow path reads this with a volatile read when the feature is on;
    // every transition goes through a CAS, so concurrent reports/recoveries collapse into one announcement.
    private int _breakerTripped;

    // Consecutive SetUnavailable reports without an intervening recovery; reset by SetAvailable and by a
    // successful probe. Deliberately NOT reset by a successful borrow: the pool cannot observe whether the
    // borrowed object's use succeeded (the typical usage reports the failure after the borrow), so a
    // borrow-side reset would make the breaker unreachable.
    private int _breakerFailureStreak;

    // Stopwatch timestamp of the trip (0 = never tripped) and the reason of the trip. Stable while the
    // breaker stays open; the timestamp lets a probe that started before a re-trip recognize stale results.
    private long _breakerTrippedAt;
    private string _breakerReason = null!; // set before first use on breaker trip

    // Transient probe timer. Created when the breaker trips with a probe configured, disposed on recovery,
    // null the rest of the time. Deliberately separate from the merged background timer so a breaker-only
    // pool performs no periodic wake-ups until it actually trips — no resident overhead, exactly like the
    // timer-less state of a pool with every background concern off.
    private Timer? _breakerTimer;

    // Process-shutdown auto-dispose (off by default). Held only while subscribed: a pool whose owner is the
    // process itself can still release its objects when nothing else will, and the registration is dropped on
    // disposal so the process-wide hook does not keep the pool reachable.
    private readonly HayatePoolShutdownRegistration? _shutdownRegistration;

    /// <exception cref="ArgumentNullException">Thrown if policy, options, scalingStrategy, metrics, logger, or poolName is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the configured options are invalid.</exception>
    internal HayatePoolBasic(
        IHayateObjectPolicy<T> policy,
        HayatePoolOptions options,
        IHayateScalingStrategy scalingStrategy,
        IHayateMetrics metrics,
        IHayateLogger logger,
        string poolName,
        IHayateShutdownHook? shutdownHook = null,
        IHayateEvictionPolicy<T>? evictionPolicy = null)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
#if NET6_0_OR_GREATER
        _asyncPolicy = policy as IHayateAsyncObjectPolicy<T>;
        _prefersAsyncDisposal = _asyncPolicy is not null;
#endif
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _scalingStrategy = scalingStrategy ?? throw new ArgumentNullException(nameof(scalingStrategy));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _name = poolName ?? throw new ArgumentNullException(nameof(poolName));

        // O7: the eviction rule of the maintenance run. Null (the common case) means the built-in
        // default, and an explicitly installed default instance is recognized as the same thing, so
        // only a genuine custom rule takes the alternate log line.
        _evictionPolicy = evictionPolicy ?? HayateDefaultEvictionPolicy<T>.Instance;
        _useCustomEvictionPolicy = !ReferenceEquals(_evictionPolicy, HayateDefaultEvictionPolicy<T>.Instance);

        if (!_options.IsValid())
        {
            throw new InvalidOperationException($"Invalid pool '{_name}' configuration: " + _options);
        }

        // Compute the initial maximum idle capacity per shard
        var shardCount = _options.ShardCount;
        var perShardMax = _options.MaxPoolSize / shardCount;   // FIX: previously MaxPoolSize was assigned directly, so shard capacity was never evenly divided
        var remainderMax = _options.MaxPoolSize % shardCount;

        // Read-only feature-switch fields (enables JIT dead-code elimination)
        _enableValidation = _options.EnableValidation;
        _enableAutoScaling = _options.EnableAutoScaling;
        _enableGenerationOptimization = _options.EnableGenerationOptimization;
        _enableLeakDetection = _options.EnableLeakDetection;
        _enableEviction = _options.EnableEviction;
        _enableLifetimeRotationOnBorrow = _options.EnableLifetimeRotationOnBorrow;
        _enableDiagnostics = _options.EnableDiagnostics;
        _enableMetrics = _options.EnableMetrics;

        // G-1: the operational metrics' time origin. Captured only while metrics are on, so a pool with
        // the switch off reports no start time, no uptime and no throughput rather than an age measured
        // from a moment it never recorded.
        if (_enableMetrics)
        {
            _startedAtUtcTicks = DateTime.UtcNow.Ticks;
        }

        // Derived tick bounds for the borrow path (see the field declarations). Recomputed here and by
        // ReloadConfig, never on the hot path.
        RefreshDerivedTimeThresholds();

        // T-R: the return-path soft ceiling (0 = disabled). Snapshotted here so the return path reads a
        // readonly field; IsValid has already rejected a ceiling outside [MinPoolSize, MaxPoolSize].
        _softCapacity = _options.SoftCapacity;

        // Abandoned recovery (K2) — feature-switch snapshot (lean normalization has already forced both
        // toggles off in lean mode, so the engine pool is the only host).
        _removeAbandonedOnBorrow = _options.RemoveAbandonedOnBorrow;
        _removeAbandonedOnMaintenance = _options.RemoveAbandonedOnMaintenance;
        _logAbandoned = _options.LogAbandoned;
        _enableAbandonedRecovery = _removeAbandonedOnBorrow || _removeAbandonedOnMaintenance;

        // Capacity alarm is disabled by default (WarnAtRatio = 0 and CriticalAtRatio = 0) —
        // frozen as a read-only flag at construction; when disabled the borrow/return path only adds one predictable branch (JIT-friendly).
        _capacityAlarmEnabled = _options.WarnAtRatio > 0 || _options.CriticalAtRatio > 0;

        // Forensics config is snapshotted at construction (the sample denominator is already clamped to >= 1 by IsValid/ApplyFeatureSwitches; this is a final guard).
        _leakTraceCaptureMode = _options.LeakTraceCaptureMode;
        _leakTraceSampleRate = Math.Max(1, _options.LeakTraceSampleRate);

        // Affinity config is snapshotted at construction (ApplyFeatureSwitches guarantees a delegate exists for Custom mode).
        _affinityMode = _options.ShardAffinityMode;
        _customShardAffinity = _options.CustomShardAffinity;

        // Initialize shards
        _shards = new Shard[shardCount];
        for (var i = 0; i < shardCount; i++)
        {
            int shardMax = perShardMax + (i < remainderMax ? 1 : 0);
            _shards[i] = new Shard(_options, i, shardMax, _logger, _prefersAsyncDisposal);
        }

        // Lean-mode storage. Allocated only when the mode is on; the general-purpose path keeps
        // these fields at their defaults and never reads them, so all it pays for the feature is a
        // single perfectly predicted branch at the top of Acquire / Release.
        // The retention buffer is sized at construction and is immutable by design — see
        // ReloadConfig, which rejects a MaxPoolSize change in lean mode for exactly that reason.
        _enableLean = _options.EnableLean;
        _leanCapacity = _enableLean ? _options.MaxPoolSize : 0;
        _leanRetentionEnabled = _leanCapacity > 0;
        _leanSlots = _leanRetentionEnabled ? new T[_leanCapacity - 1] : Array.Empty<T>();

#if NET6_0_OR_GREATER
        // O-D: the ArrayPool direct-storage backend. Rented only when opted in; the fixed-buffer
        // lean path (and every other mode) keeps its exact current allocation shape.
        _enableArrayPoolStorage = _enableLean && _options.EnableArrayPoolStorage;
        _apSlotLimit = _enableArrayPoolStorage ? _leanCapacity - 1 : 0;
        _apSlots = _enableArrayPoolStorage
            ? (_leanRetentionEnabled
                ? ArrayPool<T>.Shared.Rent(Math.Min(_leanCapacity - 1, DefaultArrayPoolStorageSlots))
                : Array.Empty<T>())
            : Array.Empty<T>();
#else
        // System.Buffers.ArrayPool is not a BCL type on netstandard2.0/net48; the flag is ignored
        // there and lean mode keeps its fixed buffer.
        _enableArrayPoolStorage = false;
        _apSlotLimit = 0;
        _apSlots = Array.Empty<T>();
#endif

        // T-R: express the soft capacity as a slot-scan limit for the lean buffer (see
        // HayateObjectPool.Lean.cs). Zero (the default) leaves -1, "no soft ceiling", so the lean
        // borrow/return paths keep their existing shape.
        _leanReturnSlotLimit = _leanRetentionEnabled && _options.SoftCapacity > 0
            ? _options.SoftCapacity - 1
            : -1;

        _waitForWarmup = _options.WaitForWarmup;
        _enableAllocationTracking = _options.EnableAllocationTracking;

        // Circuit-breaker config is snapshotted at construction (lean normalization has already forced the
        // switch off in lean mode). The enable switch is fixed for the pool's lifetime, exactly like every
        // other feature switch; the trip windows stay live on _options so ReloadConfig can tune them.
        _enableCircuitBreaker = _options.EnableCircuitBreaker;
        _breakerFailureThreshold = _options.CircuitBreaker?.FailureThreshold ?? HayateConstant.DEFAULT_CIRCUIT_BREAKER_FAILURE_THRESHOLD;
        _breakerProbe = _options.CircuitBreaker?.Probe;
        _onAvailable = _options.OnAvailable;
        _onUnavailable = _options.OnUnavailable;
        if (_waitForWarmup)
        {
            var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            _warmupCompletion = completion;
            // Pre-warm moves to the background and the constructor returns immediately; the ready signal is set once done (including on failure).
            _ = Task.Run(() =>
            {
                try
                {
                    PreWarm();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during pool [{PoolName}] background pre-warming", _name);
                }
                finally
                {
                    completion.TrySetResult(null!);
                }
            });
        }
        else
        {
            PreWarm();
        }

        StartBackgroundTasks();

        // Process-shutdown auto-dispose is the last construction step, so a subscription only ever exists for
        // a pool that is fully built and running.
        if (_options.EnableAutoDisposeWithSystem)
        {
            _shutdownRegistration = HayatePoolShutdownRegistration.Register(shutdownHook, Dispose);
        }
    }

    #region Initialized

    private void PreWarm()
    {
        if (_enableLean)
        {
            PreWarmLean();
            return;
        }

        try
        {
            // Compute the maximum capacity per shard
            var perShardMax = _options.MaxPoolSize / _shards.Length;
            var remainderMax = _options.MaxPoolSize % _shards.Length;

            // Compute the pre-warm count per shard (not exceeding shard capacity)
            int perShard = _options.MinPoolSize / _shards.Length;
            int remainder = _options.MinPoolSize % _shards.Length;
            int totalPreWarmed = 0;

            for (var i = 0; i < _shards.Length; i++)
            {
                var shard = _shards[i];
                int shardMax = perShardMax + (i < remainderMax ? 1 : 0);
                int count = perShard + (i < remainder ? 1 : 0); // handle the remainder

                // Ensure the pre-warm count does not exceed shard capacity
                count = Math.Min(count, shardMax);

                for (var j = 0; j < count; j++)
                {
                    // Register into the target shard. If the shard rejects (capacity is clamped, theoretically unreachable), destroy as fallback,
                    // to avoid orphaned entries that are registered but in no free list.
                    var w = CreateWrappedObject(shard);
                    if (!shard.Add(w)) Destroy(w);
                    else SignalAvailability();
                    totalPreWarmed++;
                }

                _logger.LogInformation("[Shard {Index}] Pre-warmed with {Count} objects", shard.Index, perShard);
            }

            _logger.LogInformation("Object pool [{PoolName}] pre-warmed with {Count} objects", _name, totalPreWarmed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during pool [{PoolName}] pre-warming", _name);
        }
    }

    /// <summary>
    /// Creates idle objects until the pool holds at least <paramref name="count"/> of them, and returns how
    /// many this call created. See <see cref="IHayateObjectPool{T}.PreWarm"/> for the contract.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    /// <exception cref="InvalidOperationException">The policy could not create a value after the configured
    /// number of retries.</exception>
    public int PreWarm(int count)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "The pre-warm count must be non-negative.");
        }

        // The two storage models keep different books — the sharded engine counts a free list per shard, the
        // lean buffer finds its free slots by scan — so each warms in its own terms. Both honour the same
        // contract: a floor on idle objects, never past the pool's own ceiling, and a reported failure.
        return _enableLean ? PreWarmLean(count) : PreWarmWrappedObjects(count);
    }

    /// <summary>
    /// Warms the sharded engine: the shortfall between the requested idle count and what the shards hold
    /// right now is created round-robin, skipping every shard already at its current capacity.
    /// </summary>
    /// <remarks>
    /// Capacity is read per shard instead of derived from <see cref="HayatePoolOptions.MaxPoolSize"/>, so a
    /// pool that has already scaled down warms to what it currently allows — the same ceiling the next
    /// borrow would meet. A shard can also reject an object that was just created for it (a concurrent
    /// return took the last slot first); that object is destroyed through the single destroy path and the
    /// shard is skipped, so warming never pushes the live count past the ceiling.
    /// </remarks>
    private int PreWarmWrappedObjects(int count)
    {
        var target = Math.Min(count, _options.MaxPoolSize);

        var idle = 0;
        foreach (var shard in _shards) idle += shard.Count;
        var remaining = target - idle;
        if (remaining <= 0) return 0;

        var warmed = 0;
        var index = 0;
        var skipped = 0;

        while (remaining > 0 && skipped < _shards.Length)
        {
            var shard = _shards[index];

            if (shard.Count >= shard.MaxSize)
            {
                skipped++;
            }
            else
            {
                var w = CreateWrappedObject(shard);
                if (shard.Add(w))
                {
                    SignalAvailability();
                    warmed++;
                    remaining--;
                    skipped = 0;
                }
                else
                {
                    Destroy(w);
                    skipped++;
                }
            }

            if (++index == _shards.Length) index = 0;
        }

        if (warmed > 0)
        {
            _logger.LogInformation("Object pool [{PoolName}] pre-warmed on demand with {Count} objects", _name, warmed);
        }

        return warmed;
    }

    /// <summary>
    /// Starts the single background timer that drives eviction, auto-scaling and idle validation.
    /// Returns without creating a timer when all three concerns are disabled, so a pool that needs no
    /// background work holds no timer handle and performs no periodic wake-ups.
    /// </summary>
    private void StartBackgroundTasks()
    {
        // The concerns keep their own periods; the shared timer ticks at the smallest of them.
        var evictionMs = _enableEviction ? Math.Max(1, _options.EvictionIntervalMs) : 0;
        var scalingMs = _enableAutoScaling ? Math.Max(1, _options.ScalingIntervalMs) : 0;
        var validationMs = _enableValidation ? Math.Max(1, _options.ValidateIntervalMs) : 0;
        var abandonedMs = _removeAbandonedOnMaintenance ? Math.Max(1, _options.RemoveAbandonedIntervalMs) : 0;

        var tickMs = 0;
        if (evictionMs > 0) tickMs = evictionMs;
        if (scalingMs > 0 && (tickMs == 0 || scalingMs < tickMs)) tickMs = scalingMs;
        if (validationMs > 0 && (tickMs == 0 || validationMs < tickMs)) tickMs = validationMs;
        if (abandonedMs > 0 && (tickMs == 0 || abandonedMs < tickMs)) tickMs = abandonedMs;

        // All four disabled: no background work, therefore no timer.
        if (tickMs == 0) return;

        _evictionPeriodTicks = MillisecondsToStopwatchTicks(evictionMs);
        _scalingPeriodTicks = MillisecondsToStopwatchTicks(scalingMs);
        _validationPeriodTicks = MillisecondsToStopwatchTicks(validationMs);
        _abandonedPeriodTicks = MillisecondsToStopwatchTicks(abandonedMs);

        // First run of each concern happens one full period after construction, mirroring the original
        // Timer(..., dueTime: interval, period: interval) layout.
        var now = Stopwatch.GetTimestamp();
        if (_evictionPeriodTicks > 0) _nextEvictionDue = now + _evictionPeriodTicks;
        if (_scalingPeriodTicks > 0) _nextScalingDue = now + _scalingPeriodTicks;
        if (_validationPeriodTicks > 0) _nextValidationDue = now + _validationPeriodTicks;
        if (_abandonedPeriodTicks > 0) _nextAbandonedDue = now + _abandonedPeriodTicks;

        _backgroundTimer = new Timer(BackgroundTick, null, tickMs, tickMs);
    }

    /// <summary>
    /// Converts a millisecond period to a Stopwatch tick count (0 for a disabled concern), with a floor
    /// of one tick so an enabled concern can never end up with a zero period and spin.
    /// </summary>
    private static long MillisecondsToStopwatchTicks(int milliseconds)
    {
        if (milliseconds <= 0) return 0;
        var ticks = (long)(milliseconds / 1000.0 * Stopwatch.Frequency);
        return ticks < 1 ? 1 : ticks;
    }

    /// <summary>
    /// Single dispatch point for the merged background timer. Each concern runs only when its own period
    /// has elapsed; because the timer period equals the smallest enabled period, every concern fires on
    /// schedule.
    /// </summary>
    private void BackgroundTick(object? state)
    {
        // Skip rather than overlap: a slow eviction pass must not run concurrently with the next tick.
        if (Interlocked.CompareExchange(ref _backgroundTickBusy, 1, 0) != 0) return;

        try
        {
            var now = Stopwatch.GetTimestamp();

            if (_evictionPeriodTicks > 0 && now >= _nextEvictionDue)
            {
                _nextEvictionDue = now + _evictionPeriodTicks;
                EvictionCallback(null);
            }

            if (_scalingPeriodTicks > 0 && now >= _nextScalingDue)
            {
                _nextScalingDue = now + _scalingPeriodTicks;
                ScalingCallback(null);
            }

            if (_validationPeriodTicks > 0 && now >= _nextValidationDue)
            {
                _nextValidationDue = now + _validationPeriodTicks;
                ValidateCallback(null);
            }

            if (_abandonedPeriodTicks > 0 && now >= _nextAbandonedDue)
            {
                _nextAbandonedDue = now + _abandonedPeriodTicks;
                AbandonedCallback(null);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Background tick failed");
        }
        finally
        {
            Volatile.Write(ref _backgroundTickBusy, 0);
        }
    }

    /// <summary>
    /// Pre-warm readiness gate. Under the default configuration (<see cref="HayatePoolOptions.WaitForWarmup"/> = false)
    /// <c>_waitForWarmup</c> is the constant false, so the hot path only adds one predictable branch (JIT-eliminable);
    /// when enabled it blocks until pre-warm completes — the signal is set on both success and failure paths, so it never hangs permanently.
    /// </summary>
    private void WaitForWarmupIfNeeded()
    {
        if (!_waitForWarmup) return;
        _warmupCompletion.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Reads the current thread's cumulative allocated bytes. net48 / netstandard2.0 lack
    /// <c>GC.GetAllocatedBytesForCurrentThread()</c>; under those target frameworks it returns 0
    /// (allocation tracking is silently unavailable and does not affect any pool behavior).
    /// </summary>
    private static long GetAllocatedBytesForCurrentThread()
    {
#if NETFRAMEWORK || NETSTANDARD2_0
        return 0;
#else
        return GC.GetAllocatedBytesForCurrentThread();
#endif
    }

    #endregion

    #region Core methods

    /// <summary>Synchronously acquires a pooled object using the default timeout.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if the configured default timeout is negative.</exception>
    /// <exception cref="InvalidOperationException">Thrown by the Abort policy when no object is available.</exception>
    /// <exception cref="TimeoutException">Thrown by the BlockTimeout policy when acquisition exceeds the timeout.</exception>
    /// <example><code>
    /// var obj = pool.Acquire();
    /// try { /* use obj */ }
    /// finally { pool.Release(obj); }
    /// </code></example>
    public T Acquire()
    {
        return Acquire(_options.DefaultAcquireTimeout);
    }

    /// <summary>
    /// Synchronously acquires a pooled object (with timeout). When allocation tracking is enabled, also counts the thread allocation delta on this borrow path.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="timeout"/> is negative.</exception>
    /// <exception cref="InvalidOperationException">Thrown by the Abort policy when no object is available.</exception>
    /// <exception cref="TimeoutException">Thrown by the BlockTimeout policy when acquisition exceeds <paramref name="timeout"/>.</exception>
    /// <example><code>
    /// var obj = pool.Acquire(TimeSpan.FromSeconds(5));
    /// try { /* use obj */ }
    /// finally { pool.Release(obj); }
    /// </code></example>
    public T Acquire(TimeSpan timeout)
    {
        // Lean fast path dispatch (off by default → constant branch, JIT-eliminable). Lean mode
        // forces allocation tracking off during normalization, so the two branches cannot overlap.
        if (_enableLean) return AcquireLean(timeout);

        // Allocation tracking (off by default → constant branch, JIT-eliminable; when on, does not affect pool behavior)
        if (!_enableAllocationTracking) return AcquireCore(timeout);

        var allocatedBefore = GetAllocatedBytesForCurrentThread();
        var acquired = AcquireCore(timeout);
        Interlocked.Add(ref _acquireAllocatedBytes, GetAllocatedBytesForCurrentThread() - allocatedBefore);
        Interlocked.Increment(ref _acquireAllocationSamples);
        return acquired;
    }

    private T AcquireCore(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be non-negative");

        // Pool-level circuit breaker (off by default → constant branch, JIT-eliminable). While the breaker
        // is open the whole pool is out of service: fail before touching any shard, wait gate or policy.
        if (_enableCircuitBreaker) ThrowIfCircuitOpen();

        // Wait for pre-warm readiness if not yet done (off by default → constant branch, zero overhead)
        WaitForWarmupIfNeeded();

        // Abandoned recovery on the borrow path (K2, off by default → constant branch, JIT-eliminable).
        // CHOPIN runs the abandoned scan on every borrow; the FIFO borrowed list plus a bounded budget
        // keeps it deterministic (the oldest — hence most likely abandoned — borrows are examined first)
        // and latency-predictable (at most BorrowAbandonedScanBudget claims per borrow).
        if (_removeAbandonedOnBorrow) ReclaimAbandoned(BorrowAbandonedScanBudget);

        var sw = ValueStopwatch.StartNew();

        // A3a: the rotation budget belongs to this borrow call, not to one pass of the retry loop below,
        // so it is declared out here (see LifetimeRotationsPerBorrow).
        var remainingLifetimeRotations = LifetimeRotationsPerBorrow;

        // The affinity start shard is evaluated only once per Acquire (None is always 0, zero extra overhead).
        var affinityStart = _affinityMode == HayateShardAffinityMode.None ? 0 : SelectStartShardIndex();

        while (true)
        {
            // Ring scan starting from the affinity shard (equivalent to a sequential foreach scan when start = 0).
            for (var offset = 0; offset < _shards.Length; offset++)
            {
                var hop = affinityStart + offset;
                var shard = _shards[hop >= _shards.Length ? hop - _shards.Length : hop];

                if (shard.TryTake(out var w))
                {
                    // On a successful borrow, consume one wake-up signal (if any) to keep the signal count aligned with the pool's idle objects;
                    // preventing waiters from being spuriously woken one by one by stale signals into a busy spin. Wait(0) returns false immediately when no signal.
                    _blockGate.Wait(0);

                    #region Borrow-path clock and object age (A3a: one read, two consumers)

                    // One clock read serves the whole borrow. It used to be taken up to three times on this
                    // path — a conditional LastBorrowedAt write, an unconditional one, and the generational
                    // age — and only the unconditional one was load-bearing (nothing observable read the
                    // field between the two writes, and the policy hook receives the pooled value, not the
                    // wrapper). Age is the only input the two remaining consumers need, and it is derived
                    // from timestamps already on hand, so sharing one computation costs nothing.
                    var now = Stopwatch.GetTimestamp();

                    // Computed once and used twice: the lifetime rotation below and the generational
                    // promotion further down. With both switches off the block is a perfectly predicted
                    // branch, and the age is never derived.
                    var ageTicks = 0L;
                    if (_enableGenerationOptimization || _enableLifetimeRotationOnBorrow)
                    {
                        ageTicks = now - w.CreatedAt;

                        // Lifetime rotation (A3a, off by default): MaxLifeTime also caps what may be handed
                        // out, not merely what may stay idle. Evaluated before the policy's acquire hook,
                        // because an object that is about to be discarded must not be activated.
                        if (_enableLifetimeRotationOnBorrow && ageTicks > _maxLifeTicks)
                        {
                            if (remainingLifetimeRotations > 0)
                            {
                                remainingLifetimeRotations--;
                                RetireExpiredOnBorrow(w);

                                // Replace it through the create-on-miss path — the same MaxPoolSize
                                // reservation the reject policies use — so a lifetime event never turns into
                                // a rejection. A null result means a concurrent borrow claimed the freed
                                // slot first, and the ordinary wait/reject handling below applies.
                                var rotated = TryCreateOnDemand((long)sw.Elapsed.TotalMilliseconds);
                                if (rotated is not null) return rotated;
                                continue;
                            }

                            // Budget spent (A3a-Q2): hand the aged object out rather than fail a borrow that
                            // has something to give.
                            if (_enableDiagnostics)
                            {
                                _logger.LogDebug("Borrow handing out an object past MaxLifeTime: the per-borrow rotation budget is spent. Type: {Type}", typeof(T).Name);
                            }
                        }
                    }

                    #endregion

                    #region Generational validation logic

                    // Only runs when both generational optimization and validation are enabled
                    //bool shouldValidate = _options.EnableValidation && _options.ValidateOnBorrow;
                    bool shouldValidate = _enableValidation && _options.ValidateOnBorrow;

                    // Generational: the old generation skips some validations
                    //if (_options.EnableGenerationOptimization && shouldValidate && w.Generation == 1)
                    if (_enableGenerationOptimization && shouldValidate && w.Generation == 1)
                    {
                        w.ValidationSkipCount++;

                        if (w.ValidationSkipCount < _options.OldGenerationValidationInterval)
                        {
                            shouldValidate = false;
                        }
                        else
                        {
                            w.ValidationSkipCount = 0;
                        }
                    }

                    #endregion

                    #region Record source shard index (Release round-trips by this; avoids Thread.GetCurrentProcessorId() % ShardCount hitting the shard with max = 0)

                    // Key: record the source shard as soon as Acquire hits. The same object returns to its original shard on Release,
                    // preventing Thread.GetCurrentProcessorId() % ShardCount from hitting the shard with max = 0 and silently disposing the object.
                    w.ShardIndex = shard.Index;

                    #endregion

                    #region Object validation (only when validation is enabled; generational optimization may skip some)

                    // Validity check
                    if (shouldValidate && !_policy.Validate(w.Value))
                    {
                        _logger.LogWarning("[Shard {Index}] Object failed validation on borrow. Disposing. Type: {Type}", shard.Index, typeof(T).Name);
                        Destroy(w);
                        continue;
                    }

                    #endregion

                    #region Core object handling

                    // Activate the object
                    _policy.OnAcquire(w.Value);

                    // The borrowed state is no longer set separately — Shard.TryTake already moves Location
                    // to Borrowed on claim, and IsBorrowed is its computed property (single source of truth).

                    // A conditional LastBorrowedAt write used to sit here, guarded by
                    // (eviction || leakDetection || generationOptimization). It was dead: that guard is a
                    // subset of the unconditional write below, nothing observable read the field in
                    // between, and the policy hook above is handed the pooled value rather than the
                    // wrapper. Removing it is what pays for the shared age computation.
                    //if (_options.EnableLeakDetection)
                    if (_enableLeakDetection)
                    {
                        // Forensics and detection are decoupled. Leak scanning (threshold check + LeakCount) depends only on LastBorrowedAt,
                        // so forensics add zero overhead; the call stack is controlled independently by LeakTraceCaptureMode —
                        // Off (default) captures no stack (before 2.0 every borrow captured the full stack, ~37.5us / ~28.7KB);
                        // Sampled captures 1 of every N borrows (the first is always captured); EveryAcquire keeps the old behavior and is an explicit opt-in.
                        // The collection carrier is now HayateLeaseContext (AsyncLocal async flow + wrapper-side snapshot reference),
                        // with a monotonically increasing lease ID; concurrent borrows and returns each hold an independent context instance, so they no longer overwrite each other.
                        if (_leakTraceCaptureMode == HayateLeakTraceCaptureMode.EveryAcquire)
                        {
                            CaptureLeaseContext(w);
                        }
                        else if (_leakTraceCaptureMode == HayateLeakTraceCaptureMode.Sampled &&
                                 (Interlocked.Increment(ref _leakTraceCounter) - 1) % _leakTraceSampleRate == 0)
                        {
                            CaptureLeaseContext(w);
                        }
                    }

                    // The borrow timestamp is recorded unconditionally. The old conditional gating (eviction/leakDetection/generation)
                    // left LastBorrowedAt at 0 when everything was off, structurally breaking the leak recheck (LeakSuspectedCount);
                    // it now reuses the read taken at the top of this block rather than taking a second one.
                    w.LastBorrowedAt = now;

                    // Cumulative borrow count. At the moment of borrow the wrapper is exclusively owned by this thread (TryTake already unlinked and claimed it,
                    // and eviction/validation cannot claim a Borrowed object), so a plain increment suffices — no Interlocked needed.
                    // Which thread took this object (wrapper metadata, see HayateObject<T>.LastGetThreadId).
                    w.LastGetThreadId = Environment.CurrentManagedThreadId;
                    w.LeaseCount++;

                    // Generational promotion, only when generational optimization is enabled. Compares the age
                    // computed at the top of this block against a precomputed tick bound, which removes the
                    // per-borrow double multiply/divide this used to perform.
                    //if (_options.EnableGenerationOptimization &&
                    if (_enableGenerationOptimization && ageTicks > _generationThresholdTicks)
                    {
                        w.Generation = 1;
                    }

                    // TotalAcquired is the borrow-count contract and is normally written on every borrow
                    // whatever the metrics switch says (see docs/metrics-gating.md §2). The diagnostics
                    // master switch is the one configuration that opts out of it, which is what makes the
                    // pool's diagnostic surface completely free rather than merely quiet.
                    if (_enableDiagnostics) Interlocked.Increment(ref _totalAcquired);

                    // Capacity-alarm probe (returns immediately internally when disabled)
                    CheckCapacityAlarm();

                    #endregion

                    #region Metrics statistics (only when metrics are enabled)

                    //if (_options.EnableMetrics)
                    if (_enableMetrics)
                    {
                        // Record wait-time statistics
                        var waitTime = (long)sw.Elapsed.TotalMilliseconds;
                        UpdateWaitTimeStats(waitTime);
                        // G-1: this borrow is the pool's latest activity.
                        TouchActivity();
                        // Covered by the _enableMetrics gate (borrow-path leak-trace point)
                        if (_enableMetrics)
                        {
                            _metrics.RecordObjectAcquired(_name, w.Value, waitTime);
                        }
                        _logger.LogDebug("Object borrowed from pool. Type: {Type} WaitTime: {WaitTime:F2}ms, shard: {ShardIndex}", typeof(T).Name, waitTime, shard.Index);
                    }
                    else if (_enableDiagnostics)
                    {
                        // Metrics off, diagnostics on: keep the trace. Dropping this branch when
                        // diagnostics are off is what removes the per-borrow params array (and the boxed
                        // shard index) from the borrow path.
                        _logger.LogDebug("Object borrowed from pool. Type: {Type} shard: {ShardIndex}", typeof(T).Name, shard.Index);
                    }

                    #endregion


                    return w.Value;
                }
            }

            // Timeout handling
            var elapsed = sw.Elapsed;

            switch (_options.RejectPolicy)
            {
                case HayatePoolRejectPolicy.Abort:
                    {
                        // Throw immediately when no idle object is available; do not wait
                        if (_enableMetrics) Interlocked.Increment(ref _totalMissed);
                        if (_enableAutoScaling) ForceScaleUpOneStep();
                        throw new InvalidOperationException($"HayatePool [{_name}] has no available object; the request was rejected by the Abort reject policy.");
                    }

                case HayatePoolRejectPolicy.Block:
                    {
                        // Cold boot: when the pool is completely empty, create the first object on demand, deterministically eliminating the first-borrow hang.
                        // That "completely empty" precondition is the whole growth contract of this policy: a miss on a pool that already tracks objects
                        // waits instead of growing, even while MaxPoolSize would still allow more. The wait is not a deadlock — the background scaler
                        // may add objects on its own period while we wait — but this policy never grows the pool on the borrow path itself. Pinned by
                        // ColdBootTests (BlockTimeout_NonEmptyPoolDoesNotGrowOnMiss).
                        var coldBoot = TryColdBootAcquire((long)sw.Elapsed.TotalMilliseconds);
                        if (coldBoot is not null) return coldBoot;

                        // Wait indefinitely until an object is obtained. This is a single SemaphoreSlim wait, so its latency
                        // is the signalling site's and not a re-check period; it replaces the original SpinOnce busy-wait
                        // (100% CPU). The Block policy has no timeout, so the timeout argument is not used (consistent with
                        // the original behaviour) — which also means a missed signal would hang rather than be recovered by
                        // a re-check. See the invariant on _blockGate for why every site signals.
                        _blockGate.Wait();
                        continue;
                    }

                case HayatePoolRejectPolicy.BlockTimeout:
                    {
                        // Cold boot: when the pool is completely empty, create the first object on demand, deterministically eliminating the first-borrow timeout.
                        // Same growth contract as the Block case: a miss on a non-empty pool waits (here bounded by the timeout) rather than growing, so a
                        // pool whose objects are all lent out reaches MaxPoolSize only through the background scaler — and a caller that gives up first sees a
                        // TimeoutException even though the pool has room. Pinned by ColdBootTests (BlockTimeout_NonEmptyPoolDoesNotGrowOnMiss).
                        var coldBoot = TryColdBootAcquire((long)elapsed.TotalMilliseconds);
                        if (coldBoot is not null) return coldBoot;

                        // Throw after the wait times out
                        if (elapsed >= timeout)
                        {
                            if (_enableMetrics) Interlocked.Increment(ref _totalMissed);
                            if (_enableAutoScaling) ForceScaleUpOneStep();
                            throw new TimeoutException($"HayatePool [{_name}] timed out acquiring an object after {timeout.TotalSeconds}s.");
                        }

                        // Wait for a signal, and for no longer than the timeout still allows: a signal wakes at once, and
                        // when none arrives the wait itself expires the timeout, so the loop re-checks and throws above.
                        _blockGate.Wait(RemainingWaitMs(timeout, elapsed));
                        continue;
                    }

                case HayatePoolRejectPolicy.CreateNew:
                case HayatePoolRejectPolicy.CreateOnDemand:
                    {
                        // Create-on-demand: while the pool can still grow, a miss is served by creating an
                        // object right away instead of making the caller wait out the timeout for a return
                        // that may never come. At capacity the request falls through to the wait-then-create
                        // timing below, which is what CreateNew does in every case. CreateNew skips both fast
                        // paths on purpose — no cold boot and no create-on-miss — and always waits the timeout
                        // out first (see HayatePoolRejectPolicy.CreateNew).
                        if (_options.RejectPolicy == HayatePoolRejectPolicy.CreateOnDemand)
                        {
                            var onDemand = TryCreateOnDemand((long)sw.Elapsed.TotalMilliseconds);
                            if (onDemand is not null) return onDemand;
                        }

                        // Create a new object after the timeout
                        if (elapsed >= timeout)
                        {
                            if (_enableMetrics) Interlocked.Increment(ref _totalMissed);
                            // Covered by the _enableMetrics gate (timeout-create path leak-trace point)
                            if (_enableMetrics)
                            {
                                _metrics.RecordObjectMiss(_name);
                            }

                            // Fix: create a "registered" in-pool object (registered means Borrowed, not placed in the idle
                            // list — preventing other waiters from claiming it via TryTake and causing a double borrow); on Release
                            // it returns to the pool normally by ShardIndex for reuse. The old implementation returned an unregistered bare object, and Release
                            // failed the reverse lookup and destroyed it as a foreign object — every borrow/return created and destroyed a new object, fully defeating pooling.
                            return CreateBorrowedOnDemand((long)sw.Elapsed.TotalMilliseconds);
                        }

                        // Wait for a signal, and for no longer than the timeout still allows: a signal wakes at once, and
                        // when none arrives the wait itself expires the timeout, so the loop re-checks and creates above.
                        _blockGate.Wait(RemainingWaitMs(timeout, elapsed));
                        continue;
                    }

                default:
                    {
                        throw new ArgumentOutOfRangeException(nameof(_options.RejectPolicy), "Unknown reject policy.");
                    }
            }
        }
    }

    /// <summary>Asynchronously acquires a pooled object, honouring the supplied cancellation token.</summary>
    /// <exception cref="TaskCanceledException">Thrown if <paramref name="cancellationToken"/> is cancelled while waiting.</exception>
    /// <example><code>
    /// var obj = await pool.AcquireAsync(cancellationToken);
    /// try { /* use obj */ }
    /// finally { pool.Release(obj); }
    /// </code></example>
    public async Task<T> AcquireAsync(CancellationToken cancellationToken = default)
    {
        // Lean fast path dispatch (off by default → constant branch, JIT-eliminable for the caller).
        if (_enableLean) return await AcquireLeanAsync(cancellationToken).ConfigureAwait(false);

        // Pool-level circuit breaker (off by default → constant branch, JIT-eliminable); the same
        // fail-fast as the synchronous core — the whole pool is out of service while the breaker is open.
        if (_enableCircuitBreaker) ThrowIfCircuitOpen();

        // Wait for pre-warm readiness if not yet done (off by default → constant branch, zero overhead)
        if (_waitForWarmup) await _warmupCompletion.Task.ConfigureAwait(false);

        // Abandoned recovery on the borrow path (K2, off by default → constant branch, JIT-eliminable);
        // same bounded, oldest-first scan as the synchronous core.
        if (_removeAbandonedOnBorrow) ReclaimAbandoned(BorrowAbandonedScanBudget);

        // The affinity start shard is evaluated only once per AcquireAsync (None is always 0, zero extra overhead).
        var affinityStart = _affinityMode == HayateShardAffinityMode.None ? 0 : SelectStartShardIndex();

        // A3a: the rotation budget belongs to this borrow call, not to one pass of the retry loop below.
        var remainingLifetimeRotations = LifetimeRotationsPerBorrow;

        while (!cancellationToken.IsCancellationRequested)
        {
            // Ring scan starting from the affinity shard (equivalent to a sequential foreach scan when start = 0).
            for (var offset = 0; offset < _shards.Length; offset++)
            {
                var hop = affinityStart + offset;
                var shard = _shards[hop >= _shards.Length ? hop - _shards.Length : hop];

                //if (shard.TryTake(out var w, TimeSpan.Zero))
                if (shard.TryTake(out var w))
                {
                    // On a successful borrow, consume one wake-up signal (if any), consistent with the synchronous path,
                    // preventing stale signals from accumulating and causing asynchronous waiters to spin spuriously.
                    _blockGate.Wait(0);

                    // A3a: the asynchronous borrow honours the same lifetime ceiling as the synchronous one.
                    // The age is derived from the timestamp this path records unconditionally anyway, so the
                    // check adds no clock read; leaving it out would let an object held across await
                    // boundaries outlive MaxLifeTime while the synchronous path rotated it, which is the
                    // kind of silent asymmetry the rotation exists to remove.
                    var now = Stopwatch.GetTimestamp();
                    if (_enableLifetimeRotationOnBorrow && now - w.CreatedAt > _maxLifeTicks)
                    {
                        if (remainingLifetimeRotations > 0)
                        {
                            remainingLifetimeRotations--;
                            RetireExpiredOnBorrow(w);

                            // Replacement through the create-on-miss path, dispatched exactly as the
                            // cold-boot branch below dispatches it: an asynchronous policy is awaited,
                            // every other policy runs the synchronous creation.
#if NET6_0_OR_GREATER
                            if (_asyncPolicy is not null)
                            {
                                var rotatedAsync = await TryCreateOnDemandAsync(0, cancellationToken).ConfigureAwait(false);
                                if (rotatedAsync is not null) return rotatedAsync;
                            }
                            else
#endif
                            {
                                var rotated = TryCreateOnDemand(0);
                                if (rotated is not null) return rotated;
                            }

                            continue;
                        }

                        // Budget spent (A3a-Q2): hand the aged object out rather than fail a borrow that has
                        // something to give.
                        if (_enableDiagnostics)
                        {
                            _logger.LogDebug("Borrow handing out an object past MaxLifeTime: the per-borrow rotation budget is spent. Type: {Type}", typeof(T).Name);
                        }
                    }

                    if (_options.ValidateOnBorrow && !_policy.Validate(w.Value))
                    {
                        Destroy(w);
                        continue;
                    }

                    _policy.OnAcquire(w.Value);

                    // TryTake already sets Location = Borrowed on claim, and IsBorrowed is its computed property.

                    // D3: record the source shard index for Release round-trip
                    w.ShardIndex = shard.Index;

                    // The borrow timestamp is recorded unconditionally (consistent with the synchronous path, keeping leak recheck usable)
                    w.LastBorrowedAt = now;

                    // Cumulative borrow count (TryTake already unlinked and claimed, so the wrapper is exclusively owned by this thread now)
                    // Which thread took this object (wrapper metadata, see HayateObject<T>.LastGetThreadId).
                    w.LastGetThreadId = Environment.CurrentManagedThreadId;
                    w.LeaseCount++;

                    if (_enableDiagnostics) Interlocked.Increment(ref _totalAcquired);

                    // G-1: the asynchronous borrow loop has no metrics block of its own (it records no
                    // wait time), so the activity stamp carries its own gate.
                    TouchActivity();

                    // Capacity-alarm probe (returns immediately internally when disabled)
                    CheckCapacityAlarm();

                    return w.Value;
                }
            }

            // Replaces the original Task.Delay(1ms) polling (idle spin and idle CPU overhead on the async path).
            // Suspend waiting for the return signal or cancellation — the signal is emitted by Release on a successful return (shared with the synchronous Acquire
            // via the shared _blockGate, a single signal source); the persistent counter semantics guarantee no lost wake-ups.
            // Design note: the original sketch introduced a Channel<T> to push objects here, but after an object returns to the pool
            // its ownership stays in the shard list, so a channel holding another reference would create dual ownership; the required semantics are the same as the signal gate
            // signal gate, so we directly reuse SemaphoreSlim (WaitAsync is natively async) with zero additional state.

            // Cold boot: when the pool is completely empty, create the first object on demand. The async path originally waited indefinitely
            // for the return signal, so a Min=0 empty pool's first borrow would hang forever until cancellation; cold boot is the only deterministic exit.
            // The create-on-demand policy goes one step further and also creates while the pool still has room, so an async borrow of a
            // fully lent-out pool does not have to wait for a return either.
#if NET6_0_OR_GREATER
            if (_asyncPolicy is not null)
            {
                // An asynchronous policy is driven through its asynchronous creation hook here, so the
                // creation genuinely awaits instead of blocking this thread (rule 1 of docs/async-policy.md).
                // Every other policy runs the synchronous branch below unchanged (rule 2).
                var createdAsync = _options.RejectPolicy == HayatePoolRejectPolicy.CreateOnDemand
                    ? await TryCreateOnDemandAsync(0, cancellationToken).ConfigureAwait(false)
                    : await TryColdBootAcquireAsync(0, cancellationToken).ConfigureAwait(false);
                if (createdAsync is not null) return createdAsync;
            }
            else
#endif
            {
                var coldBoot = _options.RejectPolicy == HayatePoolRejectPolicy.CreateOnDemand
                    ? TryCreateOnDemand(0)
                    : TryColdBootAcquire(0);
                if (coldBoot is not null) return coldBoot;
            }

            await _blockGate.WaitAsync(cancellationToken);
        }

        throw new TaskCanceledException();
    }

    /// <summary>
    /// Asynchronously acquires a pooled object with a timeout boundary.
    /// Implementation: link a CTS combining the "external cancellation + timeout" two cancellation sources and delegate to the no-timeout overload,
    /// with zero added pool state; the timeout semantics align with the synchronous <see cref="Acquire(TimeSpan)"/> —
    /// a timeout throws <see cref="TimeoutException"/> (including the missed count and a forced scale-up step),
    /// and an external cancellation propagates <see cref="TaskCanceledException"/>; the two can be distinguished by the cancellation source.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="timeout"/> is negative.</exception>
    /// <exception cref="TimeoutException">Thrown when acquisition exceeds <paramref name="timeout"/>.</exception>
    /// <exception cref="TaskCanceledException">Thrown if <paramref name="cancellationToken"/> is cancelled and the cancellation is not the timeout.</exception>
    /// <example><code>
    /// var obj = await pool.AcquireAsync(TimeSpan.FromSeconds(5), cancellationToken);
    /// try { /* use obj */ }
    /// finally { pool.Release(obj); }
    /// </code></example>
    public async Task<T> AcquireAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout == Timeout.InfiniteTimeSpan)
        {
            return await AcquireAsync(cancellationToken).ConfigureAwait(false);
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(timeout);
        try
        {
            return await AcquireAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Only a timeout triggers this (external CT not cancelled) → aligns with the BlockTimeout branch of the synchronous Acquire(timeout)
            if (_enableMetrics) Interlocked.Increment(ref _totalMissed);
            if (_enableAutoScaling) ForceScaleUpOneStep();
            throw new TimeoutException($"HayatePool [{_name}] timed out acquiring an object asynchronously after {timeout.TotalSeconds}s.");
        }
    }

    /// <summary>
    /// Returns an object to the pool. When allocation tracking is enabled, also counts the thread allocation delta on this return path.
    /// </summary>
    /// <example><code>
    /// pool.Release(obj);
    /// </code></example>
    public void Release(T item)
    {
#if NET6_0_OR_GREATER
        if (_asyncPolicy is not null)
        {
            ReleaseAsync(item).GetAwaiter().GetResult();
            return;
        }
#endif

        // Lean fast path dispatch (off by default → constant branch, JIT-eliminable).
        if (_enableLean)
        {
            ReleaseLean(item);
            return;
        }

        // Allocation tracking (off by default → constant branch, JIT-eliminable; when on, does not affect pool behavior)
        if (!_enableAllocationTracking)
        {
            ReleaseCore(item);
            return;
        }

        var allocatedBefore = GetAllocatedBytesForCurrentThread();
        ReleaseCore(item);
        Interlocked.Add(ref _releaseAllocatedBytes, GetAllocatedBytesForCurrentThread() - allocatedBefore);
        Interlocked.Increment(ref _releaseAllocationSamples);
    }

#if NET6_0_OR_GREATER
    ValueTask IHayateAsyncReturnPool<T>.ReleaseAsync(T item) => ReleaseAsync(item);

    internal async ValueTask ReleaseAsync(T item)
    {
        if (_asyncPolicy is null)
        {
            Release(item);
            return;
        }

        if (_enableLean)
        {
            await ReleaseLeanAsync(item).ConfigureAwait(false);
            return;
        }

        if (!_enableAllocationTracking)
        {
            await ReleaseCoreAsync(item).ConfigureAwait(false);
            return;
        }

        var allocatedBefore = GetAllocatedBytesForCurrentThread();
        await ReleaseCoreAsync(item).ConfigureAwait(false);
        Interlocked.Add(ref _releaseAllocatedBytes, GetAllocatedBytesForCurrentThread() - allocatedBefore);
        Interlocked.Increment(ref _releaseAllocationSamples);
    }
#endif

    private void ReleaseCore(T item)
    {
        if (!TryPrepareReturn(item, out var w)) return;

        try
        {
            _policy.OnPassivate(item);
            CompleteReturn(w, item, _policy.OnRelease(item));
        }
        catch (Exception ex)
        {
            HandleReturnFailure(w, item, ex);
        }
    }

#if NET6_0_OR_GREATER
    private async ValueTask ReleaseCoreAsync(T item)
    {
        if (!TryPrepareReturn(item, out var w)) return;

        try
        {
            await _asyncPolicy!.OnPassivateAsync(item).ConfigureAwait(false);
            var accepted = await _asyncPolicy.OnReleaseAsync(item).ConfigureAwait(false);
            CompleteReturn(w, item, accepted);
        }
        catch (Exception ex)
        {
            HandleReturnFailure(w, item, ex);
        }
    }
#endif

    private bool TryPrepareReturn(T item, out HayateObject<T> w)
    {
        w = null!;
        if (item is null)
        {
            _logger.LogWarning("Returned null object to pool. Type: {Type}", typeof(T).Name);
            return false;
        }

        foreach (var shard in _shards)
        {
            if (shard.TryGetTracked(item, out w)) break;
        }

        if (w is null)
        {
            _logger.LogWarning("Returned object does not belong to pool. Disposing. Type: {Type}", typeof(T).Name);
            Destroy(item);

            if (_enableMetrics)
            {
                _metrics.RecordObjectReleased(_name, item, false);
            }

            return false;
        }

        if (_enableValidation && _options.ValidateOnReturn && !_policy.Validate(item))
        {
            _logger.LogWarning("Object returned to pool. Disposing. Type: {Type}", typeof(T).Name);
            Destroy(w);

            if (_enableMetrics)
            {
                _metrics.RecordObjectReleased(_name, item, false);
            }

            return false;
        }

        return true;
    }

    private void CompleteReturn(HayateObject<T> w, T item, bool accepted)
    {
        // G-1: stamped for every branch below — a return that is parked, dropped by the soft ceiling or
        // destroyed by a rejected verdict is equally "the pool was active just now".
        TouchActivity();

        w.LastReleasedAt = Stopwatch.GetTimestamp();
        w.LeaseTimeMs = (long)((w.LastReleasedAt - w.LastBorrowedAt) * 1000.0 / Stopwatch.Frequency);

        if (!accepted)
        {
            _logger.LogWarning(
                "Policy rejected object on release. Disposing. Type: {Type}",
                typeof(T).Name);

            Destroy(w);

            if (_enableMetrics)
            {
                _metrics.RecordObjectReleased(_name, item, false);
            }

            if (_enableAutoScaling && _options.MinPoolSize > 0 &&
                TrackedObjectCount < _options.MinPoolSize)
            {
                ForceScaleUpOneStep();
            }

            return;
        }

        if (_enableMetrics)
        {
            UpdateLeaseTimeStats(w.LeaseTimeMs);
            Interlocked.Increment(ref _totalReleased);
            _metrics.RecordObjectReleased(_name, item, true);
        }

        if (_softCapacity > 0)
        {
            var idle = IdleObjectCount();
            if (idle >= _softCapacity)
            {
                if (_enableDiagnostics)
                {
                    _logger.LogDebug(
                        "Object dropped on return: soft capacity reached. Type: {Type}, idle: {Idle}, soft capacity: {SoftCapacity}",
                        typeof(T).Name, idle, _softCapacity);
                }

                Destroy(w);
                return;
            }
        }

        var shardIndex = (uint)w.ShardIndex < (uint)_shards.Length ? w.ShardIndex : 0;
        var shard = _shards[shardIndex];
        if (!shard.Add(w))
        {
            _logger.LogWarning("Object rejected by shard on release. Removing from pool. Type: {Type}, shard: {ShardIndex}",
                typeof(T).Name, shardIndex);
            Destroy(w);

            if (_enableAutoScaling && _options.MinPoolSize > 0 &&
                TrackedObjectCount < _options.MinPoolSize)
            {
                ForceScaleUpOneStep();
            }

            return;
        }

        if (_enableLeakDetection && _leakTraceCaptureMode != HayateLeakTraceCaptureMode.Off)
        {
            HayateLeaseContext.DetachFromFlow();
        }

        SignalAvailability();

        CheckCapacityAlarm();

        if (_enableDiagnostics)
        {
            _logger.LogDebug("Object returned to pool. Type: {Type} LeaseTime: {LeaseTime:F2}ms, shard: {ShardIndex}", typeof(T).Name, w.LeaseTimeMs, shardIndex);
        }
    }

    private void HandleReturnFailure(HayateObject<T> w, T item, Exception ex)
    {
        _logger.LogError(ex, "Error during object return validation. Disposing. Type: {Type}", typeof(T).Name);
        // G-1: a return that threw is still a return attempt, so it counts as activity.
        TouchActivity();
        Destroy(w);

        if (_enableMetrics)
        {
            _metrics.RecordObjectReleased(_name, item, false);
        }
    }

    /// <summary>
    /// Proactively evicts idle objects by category (Touched/Idle/Expired; see <see cref="HayateEvictReason"/> for semantics).
    /// Implementation notes:
    /// ① Only affects idle objects — borrowed objects are not in the shard list (TryTake already physically removed them),
    ///    so a <see cref="Shard.GetAll"/> snapshot naturally excludes them; even if a list item was just borrowed under a race,
    ///    <see cref="Shard.Remove"/>'s atomic claim will fail and skip it, never destroying an in-use object;
    /// ② Destruction reuses the exact same idempotent CAS as background eviction — when concurrent with background eviction /
    ///    idle validation / return rejection hitting the same object, only one side truly destroys it, with no double Dispose;
    /// ③ The default background-eviction behavior is unchanged; this API is an additive proactive ops entry point (eviction counts go to their respective
    ///    Destroy paths, with no extra accounting).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="reason"/> is not Touched, Idle, or Expired.</exception>
    /// <example><code>
    /// int n = pool.Evict(HayateEvictReason.Idle);
    /// </code></example>
    public int Evict(HayateEvictReason reason)
    {
        if (_enableLean)
        {
            // Lean mode keeps no per-object timestamps, lease counts or generations, so none of the
            // three eviction criteria can be evaluated. Failing loudly is preferable to returning a
            // silently meaningless 0.
            throw new InvalidOperationException(
                $"HayatePool [{_name}] uses the lean fast path, which keeps no per-object state and therefore cannot evict by {reason}. Use the general-purpose mode to enable eviction.");
        }

        if (reason != HayateEvictReason.Touched &&
            reason != HayateEvictReason.Idle &&
            reason != HayateEvictReason.Expired)
        {
            throw new ArgumentOutOfRangeException(nameof(reason), "Unknown eviction reason.");
        }

        var now = Stopwatch.GetTimestamp();
        var evicted = 0;

        foreach (var shard in _shards)
        {
            // GetAll returns an in-lock snapshot (idle list array); iteration is lock-free, and claims go through Remove's CAS
            foreach (var w in shard.GetAll())
            {
                var match = false;
                if (reason == HayateEvictReason.Touched)
                {
                    // "Clear on use": has been borrowed before (prewarm-unused objects with LeaseCount = 0 are kept)
                    match = w.LeaseCount > 0;
                }
                else if (reason == HayateEvictReason.Idle)
                {
                    // Same criterion as background idle-too-long eviction (Stopwatch ticks → seconds)
                    match = (now - w.LastReleasedAt) / (double)Stopwatch.Frequency > _options.MaxIdleTime.TotalSeconds;
                }
                else // Expired
                {
                    // Shared with the borrow-side rotation so the two cannot disagree about whether the same
                    // object is expired. The background run judges through IHayateEvictionPolicy<T>, whose
                    // candidate carries the age as a TimeSpan — equivalent to this within a single tick, and
                    // public, so it keeps its own comparison.
                    match = IsExpired(now, w);
                }

                if (!match) continue;

                // Only the caller that successfully claims may destroy (if the object is currently borrowed, Remove returns false)
                if (shard.Remove(w))
                {
                    Destroy(w);
                    evicted++;
                }
            }

            if (evicted > 0)
            {
                _logger.LogInformation("[Shard {Index}] Manual evict ({Reason}) removed objects so far: {Count}",
                    shard.Index, reason, evicted);
            }
        }

        _logger.LogInformation("Manual evict ({Reason}) on pool [{PoolName}] evicted {Count} objects",
            reason, _name, evicted);
        return evicted;
    }

    #endregion

    #region Availability (circuit breaker)

    /// <summary>
    /// Reports whether the pool is currently able to serve borrow requests.
    /// </summary>
    /// <returns><c>true</c> when the pool is available; <c>false</c> while the pool-level circuit breaker
    /// is open.</returns>
    /// <remarks>
    /// Always <c>true</c> when <see cref="HayatePoolOptions.EnableCircuitBreaker"/> is off, because nothing
    /// can take such a pool out of service. Cheap enough to call on a request path: a single volatile read
    /// when the feature is on, and a constant when it is off.
    /// </remarks>
    public bool CheckAvailable()
    {
        if (!_enableCircuitBreaker) return true;
        return Volatile.Read(ref _breakerTripped) == 0;
    }

    /// <summary>
    /// Reports that the dependency behind the pool failed, and takes the pool out of service once
    /// <see cref="HayateCircuitBreakerOptions.FailureThreshold"/> consecutive failures have been reported.
    /// </summary>
    /// <param name="reason">An optional description of the failure, surfaced through
    /// <see cref="HayatePoolOptions.OnUnavailable"/> and the logs.</param>
    /// <remarks>
    /// The intended caller is the application code that discovers the failure — a connection attempt that
    /// timed out, a remote call that failed — not the pool. Calling this on a pool whose circuit breaker is
    /// disabled does nothing, so instrumentation can call it unconditionally.<br />
    /// Each call counts as one consecutive failure; an explicit <see cref="SetAvailable"/> (or a successful
    /// probe) resets the streak, so sporadic failures that survive a recovery are counted from scratch again.
    /// Once the threshold is reached the pool becomes unavailable, a further failure report changes nothing,
    /// and recovery happens either through the configured probe or through <see cref="SetAvailable"/>.
    /// </remarks>
    public void SetUnavailable(string? reason = null)
    {
        if (!_enableCircuitBreaker) return;

        // Count the report. A report below the threshold changes nothing but the streak; the recovery path
        // resets it, so the streak only ever reaches the threshold through fresh consecutive failures.
        if (Interlocked.Increment(ref _breakerFailureStreak) < _breakerFailureThreshold) return;

        TripBreaker(reason);
    }

    /// <summary>
    /// Reports that the dependency behind the pool is healthy again, and brings the pool back into service.
    /// </summary>
    /// <remarks>
    /// Closes the circuit breaker and raises <see cref="HayatePoolOptions.OnAvailable"/> — but only when the
    /// pool was actually unavailable; calling it on a pool that is already available just clears the pending
    /// failure streak and raises nothing. This is the recovery path when no
    /// <see cref="HayateCircuitBreakerOptions.Probe"/> is configured. Calling it on a pool whose circuit
    /// breaker is disabled does nothing.
    /// </remarks>
    public void SetAvailable()
    {
        if (!_enableCircuitBreaker) return;

        // Clearing the streak is the documented escape hatch for sporadic failures: an application that
        // knows the dependency is fine calls this before the threshold is reached, on an available pool.
        Interlocked.Exchange(ref _breakerFailureStreak, 0);
        RecoverBreaker(fromProbe: false, expectedTrippedAt: -1);
    }

    /// <summary>Borrow-path fail-fast. Throws before any pool work while the breaker is open.</summary>
    private void ThrowIfCircuitOpen()
    {
        if (Volatile.Read(ref _breakerTripped) != 0)
        {
            throw new HayatePoolUnavailableException(_name, Volatile.Read(ref _breakerReason));
        }
    }

    /// <summary>
    /// Opens the breaker: flips the pool to unavailable, announces the trip through the callback and the
    /// log, and starts the probe timer when a probe is configured. Concurrent trips collapse into one.
    /// </summary>
    private void TripBreaker(string? reason)
    {
        // Only the thread that flips 0 → 1 announces the trip. Once open, further failure reports are
        // no-ops: the pool is already out of service, and the announcement must stay one-per-transition.
        if (Interlocked.CompareExchange(ref _breakerTripped, 1, 0) != 0) return;

        Volatile.Write(ref _breakerTrippedAt, Stopwatch.GetTimestamp());
        Volatile.Write(ref _breakerReason, reason!);

        try
        {
            _logger.LogWarning(
                "Pool [{PoolName}] taken out of service after {Threshold} consecutive failure reports: {Reason}",
                _name, _breakerFailureThreshold, reason ?? "(no reason reported)");
            _onUnavailable?.Invoke(new HayatePoolAvailabilityEventArgs(_name, reason!));
        }
        catch (Exception ex)
        {
            // A user callback exception must not affect the failure-report flow
            _logger.LogError(ex, "OnUnavailable callback failed for pool [{PoolName}]", _name);
        }

        // Only a configured probe needs background work. With no probe the pool stays unavailable until
        // the application calls SetAvailable, so no timer is created at all — the breaker adds zero
        // resident overhead on a pool that never trips.
        if (_breakerProbe is null) return;

        StartBreakerProbeTimer();
    }

    /// <summary>
    /// Closes the breaker: flips the pool back to available, stops the probe timer, resets the failure
    /// streak and announces the recovery through the callback and the log. Concurrent recoveries — a
    /// racing probe and SetAvailable, or two racing probes — collapse into one announcement.
    /// </summary>
    /// <param name="fromProbe">Whether the recovery was triggered by the background probe (affects the log wording only).</param>
    /// <param name="expectedTrippedAt">
    /// The trip timestamp the caller observed before acting, or a negative value to skip the staleness
    /// check (SetAvailable). The timestamp is stable while the breaker stays open, so a mismatch means the
    /// caller is a probe that started before a re-trip and must not close the newer breaker.
    /// </param>
    private void RecoverBreaker(bool fromProbe, long expectedTrippedAt)
    {
        if (expectedTrippedAt >= 0 && Volatile.Read(ref _breakerTrippedAt) != expectedTrippedAt) return;

        // Only the thread that flips 1 → 0 announces the recovery; everyone else lost the race and returns.
        if (Interlocked.CompareExchange(ref _breakerTripped, 0, 1) != 1) return;

        StopBreakerProbeTimer();
        Interlocked.Exchange(ref _breakerFailureStreak, 0);

        try
        {
            _logger.LogInformation("Pool [{PoolName}] back in service ({Trigger}).",
                _name, fromProbe ? "availability probe succeeded" : "recovery reported by the application");
            _onAvailable?.Invoke(new HayatePoolAvailabilityEventArgs(_name, null!));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OnAvailable callback failed for pool [{PoolName}]", _name);
        }
    }

    /// <summary>
    /// Starts the transient probe timer: the first probe runs one ResetTimeout after the trip, then one
    /// probe per ProbeInterval. Called only from the trip path, so there is no create/create race; the
    /// timer exists only while the pool is unavailable and is disposed on recovery.
    /// </summary>
    private void StartBreakerProbeTimer()
    {
        var breaker = _options.CircuitBreaker;
        var dueMs = ClampToTimerMilliseconds(breaker?.ResetTimeout)
                    ?? (int)TimeSpan.FromSeconds(HayateConstant.DEFAULT_CIRCUIT_BREAKER_RESET_TIMEOUT_SECONDS).TotalMilliseconds;
        var periodMs = ClampToTimerMilliseconds(breaker?.ProbeInterval)
                       ?? (int)TimeSpan.FromSeconds(HayateConstant.DEFAULT_CIRCUIT_BREAKER_PROBE_INTERVAL_SECONDS).TotalMilliseconds;

        try
        {
            _breakerTimer = new Timer(BreakerProbeTick, null, dueMs, periodMs);
        }
        catch (Exception ex)
        {
            // A failed timer creation must not undo the trip — the pool is still correctly out of
            // service; only the automatic recovery is lost, which the log makes visible.
            _logger.LogError(ex, "Failed to start the availability probe for pool [{PoolName}]; automatic recovery is unavailable until the pool recovers through SetAvailable", _name);
        }
    }

    /// <summary>Stops and releases the transient probe timer; idempotent and race-free by exchange.</summary>
    private void StopBreakerProbeTimer()
    {
        Interlocked.Exchange(ref _breakerTimer, null)?.Dispose();
    }

    /// <summary>
    /// Probe tick. Runs the configured probe and recovers the pool when it reports healthy; a throwing
    /// probe is a failed probe and leaves the pool unavailable for the next interval.
    /// </summary>
    private void BreakerProbeTick(object? state)
    {
        // A tick scheduled before a recovery can still fire after it; such a tick only cleans up — the
        // recovery path has already disposed the timer, so nothing is left behind afterwards.
        var trippedAt = Volatile.Read(ref _breakerTrippedAt);
        if (Volatile.Read(ref _breakerTripped) == 0)
        {
            StopBreakerProbeTimer();
            return;
        }

        var probe = _breakerProbe;
        if (probe is null)
        {
            // Unreachable through the trip path (the timer is only started when a probe is configured);
            // pure defense — stop probing instead of ticking forever on a null delegate.
            StopBreakerProbeTimer();
            return;
        }

        bool healthy;
        try
        {
            healthy = probe();
        }
        catch (Exception ex)
        {
            // A throwing probe is a failed probe: the pool stays unavailable and is probed again after
            // the next interval; the exception is logged, never allowed to escape into the timer.
            _logger.LogWarning("Availability probe for pool [{PoolName}] threw {ExceptionType}: {ExceptionMessage}; the pool stays unavailable",
                _name, ex.GetType().Name, ex.Message);
            return;
        }

        if (healthy)
        {
            RecoverBreaker(fromProbe: true, expectedTrippedAt: trippedAt);
        }
    }

    /// <summary>
    /// Clamps a TimeSpan to the int-millisecond range accepted by Timer (a value beyond ~24.8 days
    /// saturates instead of overflowing); <c>null</c> lets the caller apply its own default.
    /// </summary>
    private static int? ClampToTimerMilliseconds(TimeSpan? value)
    {
        if (value is null) return null;

        var ms = (long)value.Value.TotalMilliseconds;
        if (ms < 1) return 1;
        return ms > int.MaxValue ? int.MaxValue : (int)ms;
    }

    #endregion

    #region Helper methods

    /// <summary>
    /// Invokes the policy's creation hook. A policy that implements
    /// <c>IHayateAsyncObjectPolicy&lt;T&gt;</c> is driven through its asynchronous hook even from a
    /// synchronous caller — a synchronous entry point waits on it instead of calling the synchronous
    /// <see cref="IHayateObjectPolicy{T}.Create"/> (rule 1 of docs/async-policy.md) — while every other
    /// policy keeps calling <c>Create</c> exactly as it did before the interface existed (rule 2).
    /// </summary>
    /// <remarks>
    /// The asynchronous interface is named as plain code, not a <c>cref</c>: this member compiles on
    /// every target, and the type does not exist on netstandard2.0 / net48.
    /// </remarks>
    private T CreateObject()
    {
#if NET6_0_OR_GREATER
        if (_asyncPolicy is not null) return _asyncPolicy.CreateAsync().GetAwaiter().GetResult();
#endif
        return _policy.Create();
    }

    private HayateObject<T> CreateWrappedObject(Shard targetShard)
    {
        // Wrapper recycling: prefer a wrapper parked by a previous destroy (zero allocation on this
        // path) and fall back to a fresh allocation only when the spare stack is empty. The wrapper
        // is taken once for the whole retry loop and fully reset before its new value is observable,
        // so the resulting state is indistinguishable from a brand-new wrapper.
        var w = targetShard.TryTakeSpare();

        for (var retry = 0; retry < _options.CreationRetryCount; retry++)
        {
            try
            {
                var o = CreateObject();
                if (o is null)
                    throw new ArgumentNullException("value"); // keep the constructor's null-value contract

                if (w != null)
                {
                    w.PrepareForRecycle(o, _name, targetShard.Index);
                }
                else
                {
                    // Register into the target shard (ShardIndex is written synchronously as the owner).
                    // The object round-trips back to this shard forever; the registry entry and object lifetime stay in sync,
                    // so the sum of TrackedCount is the pool's true live object count.
                    w = new HayateObject<T>(o) { ShardIndex = targetShard.Index, OwnerPoolName = _name };
                }

                if (targetShard.TryTrack(o, w))
                {
                    if (_enableMetrics) Interlocked.Increment(ref _totalCreated);
                    return w;
                }

                // The same key already exists (a pathological case where the policy creates the same instance twice): destroy the new instance and retry,
                // never add it to the pool — otherwise Release's reverse lookup would hit the old entry and the old/new wrappers would corrupt each other.
                _logger.LogWarning("Duplicate pooled object instance detected. Retrying. Type: {Type}", typeof(T).Name);
                DestroyObject(o);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating object. Retry {Retry}/{MaxRetries}", retry + 1, _options.CreationRetryCount);

                if (retry == _options.CreationRetryCount - 1)
                {
                    // Park the taken wrapper before giving up so a future creation can still reuse it.
                    // Safe even when w.Value references a failed candidate: spare wrappers are only
                    // consumed here, and every consumption resets the wrapper before the value is read.
                    if (w != null) targetShard.ReturnSpare(w);
                    throw new InvalidOperationException("Failed to create object after retries", ex);
                }

                Thread.Sleep(_options.CreationRetryDelay);
            }
        }

        // Park the taken wrapper before giving up so a future creation can still reuse it (same
        // reasoning as the retry-exhausted path above).
        if (w != null) targetShard.ReturnSpare(w);
        throw new InvalidOperationException("Failed to create object after retries");
    }

    private void ForceScaleUpOneStep()
    {
        if (!_enableAutoScaling) return;

        try
        {
            int currentTotal = TrackedObjectCount;
            if (currentTotal >= _options.MaxPoolSize) return;
            if ((Stopwatch.GetTimestamp() - _lastScaleUpTime) / (double)Stopwatch.Frequency < _options.ScaleUpCooldownSeconds) return;

            // Add 5 per timeout to prevent an avalanche
            int add = Math.Min(_options.ScaleUpStep, _options.MaxPoolSize - currentTotal);
            if (add <= 0) return;

            // Update shard capacity so objects are not discarded
            UpdateShardMaxSizes();

            var added = 0;

            for (var i = 0; i < add; i++)
            {
                var shard = _shards[i % _shards.Length];
                // Register into the target shard; under concurrency Add may still be filled first by another thread and rejected, so destroy as fallback to prevent orphaned registry entries.
                var w = CreateWrappedObject(shard);
                if (!shard.Add(w)) Destroy(w);
                else SignalAvailability();
                added++;
            }

            _lastScaleUpTime = Stopwatch.GetTimestamp();
            _logger.LogWarning("FORCE SCALE UP Pool [{PoolName}] (because timeout) → total: {Total}, added: {Count}",
                _name, TrackedObjectCount, added);

            if (_enableMetrics)
            {
                _metrics.RecordPoolScaled(_name, "ForceUp", currentTotal, currentTotal + add);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Force scale up failed");
        }
    }

    /// <summary>
    /// Cold boot. When the pool is completely empty (no idle and no borrowed) and the capacity limit &gt; 0,
    /// synchronously create the first object and borrow it directly (atomic de-duplication; concurrent first borrows create only one),
    /// eliminating the uncertainty of a Min=0 empty pool "waiting for a timeout to replenish" (empirically two failure timings).
    /// This is the only point at which a waiting policy grows the pool instead of waiting: the Block / BlockTimeout
    /// cases below call it, the async wait path calls its asynchronous twin, and the create-on-demand policies
    /// delegate to it first (see <see cref="TryCreateOnDemand"/>). The "completely empty" precondition is therefore
    /// load-bearing — <c>MaxPoolSize</c> is a ceiling for these policies, not a growth target, so a miss on a pool
    /// that already tracks objects never grows the pool. What can still add objects while a caller waits is the
    /// background scaler, on its own period, plus the one-step forced scale-up the timeout paths run just before
    /// giving up. CreateNew deliberately keeps the "create after the full timeout" semantics, while Abort keeps the
    /// direct-reject semantics.
    /// </summary>
    /// <returns>Object borrowed via cold boot; returns <c>null</c> when this call did not claim the boot (pool non-empty / at capacity / another thread is creating), and the caller should continue normal waiting.</returns>
    private T? TryColdBootAcquire(long waitTimeMs)
    {
        if (_options.MaxPoolSize <= 0) return null;
        if (TrackedObjectCount != 0) return null;
        if (Interlocked.CompareExchange(ref _coldBootClaimed, 1, 0) != 0) return null;

        try
        {
            // Double-check: during the CAS, a concurrent return / scale-up may make the pool non-empty — just fall back to the normal wait path then.
            if (TrackedObjectCount != 0) return null;

            var value = CreateBorrowedOnDemand(waitTimeMs);
            _logger.LogInformation("Pool [{PoolName}] cold-boot acquired on demand (pool was empty)", _name);
            return value;
        }
        finally
        {
            // Whether creation succeeds or fails, the flag must be reset, so the pool can cold-boot again after being emptied;
            // when CreateWrappedObject throws, the exception propagates to the caller (consistent with the CreateNew path behavior).
            Interlocked.Exchange(ref _coldBootClaimed, 0);
        }
    }

    /// <summary>
    /// Create-on-miss path of the create-on-demand policy. Serves a miss synchronously whenever the pool
    /// can still grow: an empty pool is delegated to the cold-boot routine (the same synchronous
    /// first-object creation the block policies use, so concurrent first borrows still create only one),
    /// and a non-empty pool with room left creates an object directly.
    /// </summary>
    /// <returns>The borrowed object, or <c>null</c> when the pool already holds <c>MaxPoolSize</c> objects
    /// — the caller then waits for a return exactly as the create-after-timeout policy does.</returns>
    private T? TryCreateOnDemand(long waitTimeMs)
    {
        var coldBoot = TryColdBootAcquire(waitTimeMs);
        if (coldBoot is not null) return coldBoot;

        if (TrackedObjectCount >= _options.MaxPoolSize) return null;

        return CreateBorrowedOnDemand(waitTimeMs);
    }

    /// <summary>
    /// Creates one object and hands it out already borrowed: picks the next shard by the round-robin
    /// cursor, creates and registers the wrapper (a fresh object is registered as borrowed, never placed
    /// in the idle list, so no other waiter can claim it through a take), then runs the shared hand-out
    /// bookkeeping.
    /// Shared by the cold-boot path and both create-on-miss paths so they all allocate identically.
    /// </summary>
    private T CreateBorrowedOnDemand(long waitTimeMs)
    {
        var shard = _shards[(int)(Interlocked.Increment(ref _createCursor) - 1) % _shards.Length];
        var w = CreateWrappedObject(shard);   // TryTrack registration is already done internally (includes the _totalCreated counter)
        FinishBorrowedOnDemand(shard, w, waitTimeMs);
        return w.Value;
    }

    /// <summary>
    /// The hand-out half of <see cref="CreateBorrowedOnDemand"/>, shared with its asynchronous twin:
    /// runs the policy acquire hook and updates the counters, the capacity alarm and the metrics.
    /// </summary>
    private void FinishBorrowedOnDemand(Shard shard, HayateObject<T> w, long waitTimeMs)
    {
        w.Location = HayateObjectLocation.Borrowed;
        // A freshly created object handed out already borrowed must join the borrowed list too (K2),
        // or the abandoned scan would never see create-on-miss / cold-boot borrows.
        if (_enableAbandonedRecovery) shard.MarkBorrowed(w);
        // The borrow timestamp is recorded unconditionally (same as the Acquire main path, keeping leak recheck usable)
        w.LastBorrowedAt = Stopwatch.GetTimestamp();
        // Cumulative borrow count (a freshly created object is borrowed by definition; the wrapper is exclusively owned by this thread now)
        // Which thread took this object (wrapper metadata, see HayateObject<T>.LastGetThreadId).
        w.LastGetThreadId = Environment.CurrentManagedThreadId;
        w.LeaseCount++;
        _policy.OnAcquire(w.Value);
        // Borrow-count contract, gated by the diagnostics master switch alone (see the sync path).
        if (_enableDiagnostics) Interlocked.Increment(ref _totalAcquired);

        // Capacity-alarm probe (returns immediately internally when disabled)
        CheckCapacityAlarm();

        if (_enableMetrics)
        {
            UpdateWaitTimeStats(waitTimeMs);
            // G-1: a create-on-miss / cold-boot hand-out is activity too (shared by the sync and async paths).
            TouchActivity();
            _metrics.RecordObjectAcquired(_name, w.Value, waitTimeMs);
        }
    }

#if NET6_0_OR_GREATER
    // Asynchronous creation twins. They are reached only when the policy implements
    // IHayateAsyncObjectPolicy<T> (every caller dispatches on _asyncPolicy), so a pool whose policy
    // keeps the synchronous contract runs the routines above untouched — that is what makes the
    // asynchronous surface additive instead of a rewrite of the creation path. Each twin mirrors its
    // synchronous counterpart statement for statement: same retry budget, same registration order,
    // same give-up exception. The differences are that the creation hook is awaited and that the retry
    // delay yields the thread instead of sleeping on it.

    /// <summary>Asynchronous twin of <see cref="CreateWrappedObject"/>, awaited by the cold-boot and create-on-miss paths.</summary>
    private async ValueTask<HayateObject<T>> CreateWrappedObjectAsync(Shard targetShard, CancellationToken cancellationToken)
    {
        // Wrapper recycling, taken once for the whole retry loop, exactly as the synchronous twin does.
        var w = targetShard.TryTakeSpare();

        for (var retry = 0; retry < _options.CreationRetryCount; retry++)
        {
            try
            {
                var o = await _asyncPolicy!.CreateAsync(cancellationToken).ConfigureAwait(false);
                if (o is null)
                    throw new ArgumentNullException("value"); // keep the constructor's null-value contract

                if (w != null)
                {
                    w.PrepareForRecycle(o, _name, targetShard.Index);
                }
                else
                {
                    w = new HayateObject<T>(o) { ShardIndex = targetShard.Index, OwnerPoolName = _name };
                }

                if (targetShard.TryTrack(o, w))
                {
                    if (_enableMetrics) Interlocked.Increment(ref _totalCreated);
                    return w;
                }

                // The same key already exists (a pathological case where the policy creates the same instance twice): destroy the new instance and retry.
                _logger.LogWarning("Duplicate pooled object instance detected. Retrying. Type: {Type}", typeof(T).Name);
                await DestroyObjectAsync(o).ConfigureAwait(false);
            }
            // A cancelled token is not a creation failure: it propagates to the caller instead of
            // consuming retries and logging an error.
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Error creating object. Retry {Retry}/{MaxRetries}", retry + 1, _options.CreationRetryCount);

                if (retry == _options.CreationRetryCount - 1)
                {
                    // Park the taken wrapper before giving up so a future creation can still reuse it
                    // (same reasoning as the synchronous twin).
                    if (w != null) targetShard.ReturnSpare(w);
                    throw new InvalidOperationException("Failed to create object after retries", ex);
                }

                await Task.Delay(_options.CreationRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        if (w != null) targetShard.ReturnSpare(w);
        throw new InvalidOperationException("Failed to create object after retries");
    }

    /// <summary>Asynchronous twin of <see cref="TryColdBootAcquire"/>: the atomic claim and the double-checks are the same.</summary>
    private async ValueTask<T?> TryColdBootAcquireAsync(long waitTimeMs, CancellationToken cancellationToken)
    {
        if (_options.MaxPoolSize <= 0) return null;
        if (TrackedObjectCount != 0) return null;
        if (Interlocked.CompareExchange(ref _coldBootClaimed, 1, 0) != 0) return null;

        try
        {
            // Double-check: during the CAS, a concurrent return / scale-up may make the pool non-empty — just fall back to the normal wait path then.
            if (TrackedObjectCount != 0) return null;

            var value = await CreateBorrowedOnDemandAsync(waitTimeMs, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Pool [{PoolName}] cold-boot acquired on demand (pool was empty)", _name);
            return value;
        }
        finally
        {
            // Whether creation succeeds or fails, the flag must be reset, so the pool can cold-boot again after being emptied.
            Interlocked.Exchange(ref _coldBootClaimed, 0);
        }
    }

    /// <summary>Asynchronous twin of <see cref="TryCreateOnDemand"/>: same cold-boot delegation, same ceiling check.</summary>
    private async ValueTask<T?> TryCreateOnDemandAsync(long waitTimeMs, CancellationToken cancellationToken)
    {
        var coldBoot = await TryColdBootAcquireAsync(waitTimeMs, cancellationToken).ConfigureAwait(false);
        if (coldBoot is not null) return coldBoot;

        if (TrackedObjectCount >= _options.MaxPoolSize) return null;

        return await CreateBorrowedOnDemandAsync(waitTimeMs, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Asynchronous twin of <see cref="CreateBorrowedOnDemand"/>: only the creation is awaited, the hand-out bookkeeping is the shared helper.</summary>
    private async ValueTask<T> CreateBorrowedOnDemandAsync(long waitTimeMs, CancellationToken cancellationToken)
    {
        var shard = _shards[(int)(Interlocked.Increment(ref _createCursor) - 1) % _shards.Length];
        var w = await CreateWrappedObjectAsync(shard, cancellationToken).ConfigureAwait(false);
        FinishBorrowedOnDemand(shard, w, waitTimeMs);
        return w.Value;
    }
#endif

    private void UpdateShardMaxSizes()
    {
        var shardCount = _shards.Length;
        var perShardMax = _options.MaxPoolSize / shardCount;
        var remainderMax = _options.MaxPoolSize % shardCount;

        // FIX: previously the loop bound used perShardMax, which would overflow _shards[i] when MaxPoolSize > ShardCount
        for (var i = 0; i < shardCount; i++)
        {
            var newMax = perShardMax + ((i < remainderMax ? 1 : 0));
            _shards[i].UpdateMaxSize(newMax);
        }
    }

    /// <summary>
    /// The destroy-hook dispatch, mirroring <see cref="CreateObject"/> on the way out: a policy that
    /// implements <c>IHayateAsyncObjectPolicy&lt;T&gt;</c> is destroyed through its asynchronous hook even
    /// from a synchronous destroy site — the synchronous site waits on it instead of calling the
    /// synchronous <see cref="IHayateObjectPolicy{T}.OnDestroy"/> (rule 1 of docs/async-policy.md) —
    /// and such a pool also tears its objects down through <c>IAsyncDisposable</c> in preference to
    /// <c>IDisposable</c>. Every other policy runs the two statements the site used to carry inline,
    /// unchanged (rule 2).
    /// </summary>
    /// <remarks>
    /// The hook is not swallowed here: each caller keeps the try/catch that already surrounded its
    /// inline statements, so failure handling is exactly what it was. The asynchronous interface is
    /// named as plain code, not a <c>cref</c>: this member compiles on every target, and the type does
    /// not exist on netstandard2.0 / net48.
    /// </remarks>
    private void DestroyObject(T o)
    {
#if NET6_0_OR_GREATER
        if (_asyncPolicy is not null)
        {
            _asyncPolicy.OnDestroyAsync(o).GetAwaiter().GetResult();
            if (o is IAsyncDisposable ad)
            {
                ad.DisposeAsync().GetAwaiter().GetResult();
                return;
            }
        }
        else
#endif
        {
            _policy.OnDestroy(o);
        }

        if (o is IDisposable d) d.Dispose();
    }

#if NET6_0_OR_GREATER
    /// <summary>
    /// The awaited twin of <see cref="DestroyObject"/>, reached only under an asynchronous policy
    /// (every caller dispatches on <see cref="_asyncPolicy"/>), so the destroy hook is the
    /// asynchronous one and the object's disposal prefers <c>IAsyncDisposable</c>
    /// (docs/async-policy.md §5).
    /// </summary>
    private async ValueTask DestroyObjectAsync(T o)
    {
        await _asyncPolicy!.OnDestroyAsync(o).ConfigureAwait(false);
        await DisposeObjectAsync(o).ConfigureAwait(false);
    }

    /// <summary>
    /// Pure object disposal for the asynchronous surface: <c>IAsyncDisposable</c> where the object
    /// implements it, <c>IDisposable</c> otherwise (docs/async-policy.md §5). Used by the awaited
    /// destroy twin above and by the <c>DisposeAsync</c> drain, which an explicitly asynchronous
    /// caller opted into even when the policy did not.
    /// </summary>
    private async ValueTask DisposeObjectAsync(T o)
    {
        if (o is IAsyncDisposable ad)
        {
            await ad.DisposeAsync().ConfigureAwait(false);
            return;
        }

        if (o is IDisposable d) d.Dispose();
    }
#endif

    private void Destroy(HayateObject<T> w)
    {
        if (w == null) return;

        // Idempotent protection: eviction, idle validation, and return rejection may concurrently hit the same wrapper,
        // and only the first caller to pass the CAS truly destroys it; the rest return immediately, avoiding a double Dispose.
        if (Interlocked.Exchange(ref w.Destroyed, 1) == 1) return;

        // A destroyed wrapper must never stay linked as borrowed (K2): whatever the destroy reason
        // (abandoned reclamation, return rejection, validation failure, overflow), a later abandoned
        // scan must not see it as a live candidate. Runs even on the exception path below.
        if ((uint)w.ShardIndex < (uint)_shards.Length)
        {
            _shards[w.ShardIndex].UnmarkBorrowed(w);
        }

        try
        {
            DestroyObject(w.Value);
            UntrackObject(w);
            w.Location = HayateObjectLocation.Destroyed;
            // On destruction, clear the lease context reference so the stack frames can be GC'd with the context (on the AsyncLocal flow side
            // each caller Detaches on its own Release; this code does not touch other flows' contexts — such is the AsyncLocal semantics.
            w.LeaseContext = null!;
            // Drop the value reference: the wrapper may be parked on the spare stack for reuse, and a
            // parked wrapper must never keep a destroyed pooled object alive. UntrackObject has already
            // removed the registry entry above, so nothing reads w.Value after this point.
            w.Value = null!;
            if (_enableDiagnostics) _logger.LogDebug("Wrapped object destroyed. Type: {Type}", typeof(T).Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during wrapped object destruction. Type: {Type}", typeof(T).Name);
            // Do not park a half-cleaned wrapper: the destroy may have failed before the registry
            // entry was removed (or before the value reference was dropped), and recycling it could
            // leave a stale registry entry pointing at the reused wrapper. It is simply collected.
            return;
        }

        // Wrapper recycling: park the fully destroyed wrapper on its home shard's spare stack
        // (bounded by the shard's max size) so a future CreateWrappedObject can reuse it instead of
        // allocating. The wrapper is only ever consumed through CreateWrappedObject, which resets it
        // completely before the new value becomes observable.
        if ((uint)w.ShardIndex < (uint)_shards.Length)
        {
            _shards[w.ShardIndex].ReturnSpare(w);
        }
    }

    private void Destroy(T o)
    {
        if (o == null) return;

        try
        {
            DestroyObject(o);
            UntrackKey(o);
            if (_enableDiagnostics) _logger.LogDebug("Object destroyed. Type: {Type}", typeof(T).Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during object destruction. Type: {Type}", typeof(T).Name);
        }
    }

    /// <summary>
    /// G-1: records the highest number of concurrently borrowed objects seen so far. Called only from the
    /// two places that already derive the borrowed count (<see cref="GetStats"/> and
    /// <see cref="TakeSnapshot"/>) — see the field declaration for why it is not counted on the borrow
    /// path. A high-water mark: a read can raise it, never lower it.
    /// </summary>
    private void UpdatePeakActiveObjects(int borrowed)
    {
        var observed = Volatile.Read(ref _peakActiveObjects);
        while (borrowed > observed)
        {
            var previous = Interlocked.CompareExchange(ref _peakActiveObjects, borrowed, observed);
            if (previous == observed) return;
            observed = previous;
        }
    }

    /// <summary>
    /// G-1: stamps "something happened on this pool just now" for
    /// <see cref="HayatePoolStats.LastActivityTime"/>. Maintained only while metrics are on, so a pool with
    /// the switch off pays one predictable branch (JIT-inlined and folded away for a constant false).
    /// </summary>
    private void TouchActivity()
    {
        if (!_enableMetrics) return;
        Interlocked.Exchange(ref _lastActivityUtcTicks, DateTime.UtcNow.Ticks);
    }

    private void UpdateWaitTimeStats(long waitTimeMs)
    {
        // Lock-free update (the original held a global _statsLock, a contention point for concurrent borrow/return)
        Interlocked.Add(ref _waitTimeSum, waitTimeMs);
        Interlocked.Increment(ref _waitTimeCount);
        InterlockedMax(ref _waitTimeMaxMs, waitTimeMs);
        InterlockedMin(ref _waitTimeMinMs, waitTimeMs);
    }

    private void UpdateLeaseTimeStats(long leaseTimeMs)
    {
        // Lock-free update (same as above)
        Interlocked.Add(ref _leaseTimeSum, leaseTimeMs);
        Interlocked.Increment(ref _leaseTimeCount);
        InterlockedMax(ref _leaseTimeMaxMs, leaseTimeMs);
        InterlockedMin(ref _leaseTimeMinMs, leaseTimeMs);
    }

    /// <summary>
    /// Computes the starting shard index for this borrow.
    /// None always returns 0 (the caller short-circuits with <c>_affinityMode != None</c> up front, guaranteeing zero overhead on the default path);
    /// Thread maps stably by managed thread ID via a golden-ratio hash (the same thread always prefers the same shard, with no coupling to the shard count via a common divisor);
    /// Custom uses the user delegate; an out-of-range value or a missing delegate falls back to 0 silently, while an exception is caught, logged and also falls back to 0 (borrow-path robustness first — the borrow is never interrupted by a missing policy).
    /// </summary>
    private int SelectStartShardIndex()
    {
        switch (_affinityMode)
        {
            case HayateShardAffinityMode.Thread:
                return (int)((uint)Environment.CurrentManagedThreadId * 2654435761u % (uint)_shards.Length);

            case HayateShardAffinityMode.Custom:
                try
                {
                    var idx = _customShardAffinity?.Invoke();
                    if (idx.HasValue && (uint)idx.Value < (uint)_shards.Length)
                        return idx.Value;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "CustomShardAffinity selector failed; falling back to sequential scan");
                }
                return 0;

            default:
                return 0;
        }
    }

    /// <summary>
    /// Borrow-path lease-context capture. Creates an immutable lease (lease ID + frame array + borrow timestamp),
    /// written both to the wrapper (for snapshot forensics) and to the current async flow (AsyncLocal, which callers can read via
    /// <see cref="HayateLeaseContext.Current"/>; concurrent borrows and returns are isolated and do not overwrite each other).
    /// Frames are captured with fNeedFileInfo: false — source file/line is not resolved, avoiding PDB I/O.
    /// </summary>
    private void CaptureLeaseContext(HayateObject<T> w)
    {
        var frames = new StackTrace(fNeedFileInfo: false).GetFrames() ?? Array.Empty<StackFrame>();
        var ctx = new HayateLeaseContext(frames, Stopwatch.GetTimestamp());
        w.LeaseContext = ctx;
        ctx.AttachToFlow();
    }

    /// <summary>
    /// Formats the lease context into a single-line text (for snapshot LeakTraces output).
    /// Frames are joined by "&lt;-" (call direction: outermost frame first); the frame format is
    /// <c>Type.Method+0xoffset</c>; falls back to placeholder text when there is no context or no frames.
    /// </summary>
    private static string FormatLeaseTrace(HayateLeaseContext? ctx)
    {
        if (ctx is null) return "No stack trace available";
        var frames = ctx.Frames;
        if (frames is null || frames.Length == 0)
            return "No stack trace available";

        var sb = new System.Text.StringBuilder(frames.Length * 48);
        if (ctx.LeaseId > 0)
        {
            sb.Append("[Lease ").Append(ctx.LeaseId).Append("] ");
        }

        for (var i = 0; i < frames.Length; i++)
        {
            var method = frames[i].GetMethod();
            if (method is null) continue;

            if (sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(" <- ");
            sb.Append(method.DeclaringType?.Name).Append('.').Append(method.Name)
              .Append("+0x").Append(frames[i].GetNativeOffset().ToString("X"));
        }

        return sb.Length > 0 ? sb.ToString() : "No stack trace available";
    }

    /// <summary>
    /// Lock-free Max merge (CAS loop). Concurrent calls eventually converge to the true maximum.
    /// </summary>
    private static void InterlockedMax(ref long location, long value)
    {
        var current = Volatile.Read(ref location);
        while (value > current)
        {
            var previous = Interlocked.CompareExchange(ref location, value, current);
            if (previous == current) break;
            current = previous;
        }
    }

    /// <summary>
    /// Lock-free Min merge (CAS loop). Concurrent calls eventually converge to the true minimum.
    /// </summary>
    private static void InterlockedMin(ref long location, long value)
    {
        var current = Volatile.Read(ref location);
        while (value < current)
        {
            var previous = Interlocked.CompareExchange(ref location, value, current);
            if (previous == current) break;
            current = previous;
        }
    }

    /// <summary>
    /// Capacity-alarm check. Utilization = borrowed count / MaxPoolSize (the borrowed count is derived
    /// from "total live count - idle count", the same basis as TakeSnapshot).
    /// A debounced flip: only the thread that successfully flips the state via CAS fires the callback once;
    /// falling back to the low-water mark silently resets (no callback), and re-crossing the line can fire again.
    /// when disabled (WarnAtRatio = 0 and CriticalAtRatio = 0) the caller's read-only flag short-circuits it at zero cost.
    /// </summary>
    private void CheckCapacityAlarm()
    {
        if (!_capacityAlarmEnabled) return;

        var max = _options.MaxPoolSize;
        if (max <= 0) return;

        var borrowed = TrackedObjectCount - _shards.Sum(s => s.Count);
        if (borrowed < 0) borrowed = 0;
        var ratio = (double)borrowed / max;

        var critical = _options.CriticalAtRatio;
        var warn = _options.WarnAtRatio;
        int desired =
            critical > 0 && ratio >= critical ? 2 :
            warn > 0 && ratio >= warn ? 1 :
            0;

        var current = Volatile.Read(ref _capacityAlarmLevel);
        if (current == desired) return;

        // Concurrent flip: the CAS winner triggers the callback; the loser gives up (water-level events allow eventual consistency).
        if (Interlocked.CompareExchange(ref _capacityAlarmLevel, desired, current) != current) return;

        // Falling back to Normal: silently reset, only re-arming the state machine, without disturbing the user.
        if (desired == 0) return;

        try
        {
            var level = desired == 2 ? HayatePoolCapacityAlarmLevel.Critical : HayatePoolCapacityAlarmLevel.Warning;
            var args = new HayatePoolCapacityAlarmEventArgs(_name, level, ratio, borrowed, max);

            if (desired == 2)
            {
                _logger.LogWarning("Pool [{PoolName}] capacity CRITICAL: usage {Ratio:P1} ({Borrowed}/{Max})",
                    _name, ratio, borrowed, max);
                _options.OnCapacityCritical?.Invoke(args);
            }
            else
            {
                _logger.LogWarning("Pool [{PoolName}] capacity WARNING: usage {Ratio:P1} ({Borrowed}/{Max})",
                    _name, ratio, borrowed, max);
                _options.OnCapacityWarning?.Invoke(args);
            }
        }
        catch (Exception ex)
        {
            // A user callback exception must not affect the borrow/return main flow
            _logger.LogError(ex, "Capacity alarm callback failed for pool [{PoolName}]", _name);
        }
    }

    #endregion

    #region Lifetime rotation (A3a)

    /// <summary>
    /// Recomputes the tick bounds the borrow path compares against. Called at construction and by
    /// <see cref="ReloadConfig"/>, which may change <see cref="HayatePoolOptions.MaxLifeTime"/> or
    /// <see cref="HayatePoolOptions.GenerationThresholdMs"/> — without the refresh the borrow path would
    /// keep judging against the values the pool was built with while the eviction paths, which read the
    /// options live, judged against the new ones.
    /// </summary>
    private void RefreshDerivedTimeThresholds()
    {
        var frequency = Stopwatch.Frequency;
        _generationThresholdTicks = (long)(_options.GenerationThresholdMs * 0.001 * frequency);
        _maxLifeTicks = (long)(_options.MaxLifeTime.TotalSeconds * frequency);
    }

    /// <summary>
    /// Whether the pooled object has outlived <see cref="HayatePoolOptions.MaxLifeTime"/>, judged from
    /// the tick count the caller already holds.
    /// </summary>
    /// <remarks>
    /// Single point of truth for the pool's own lifetime judgement, so the borrow-side rotation and the
    /// manual <c>Evict</c> cannot disagree at the boundary. The background eviction run judges through
    /// <see cref="IHayateEvictionPolicy{T}"/> instead — that extension point carries the object's age as
    /// a <see cref="TimeSpan"/> and is public, so it keeps its own (equivalent within a single tick)
    /// comparison rather than being re-based on ticks.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsExpired(long now, HayateObject<T> w) => now - w.CreatedAt > _maxLifeTicks;

    /// <summary>
    /// Retires an object the borrow path found expired, so that hitting the lifetime ceiling never
    /// surfaces as a rejection.
    /// </summary>
    /// <remarks>
    /// Only the retirement lives here; the caller creates the replacement through the create-on-miss path
    /// (and therefore the same <c>MaxPoolSize</c> reservation) its generation already uses — destroying
    /// first frees the slot that makes that possible. At most one rotation is performed per borrow and the
    /// replacement is never re-checked, so a <see cref="HayatePoolOptions.MaxLifeTime"/> shorter than the
    /// time it takes to create an object cannot spin.
    /// </remarks>
    private void RetireExpiredOnBorrow(HayateObject<T> w)
    {
        Destroy(w);
        Interlocked.Increment(ref _lifetimeRotatedCount);
        if (_enableDiagnostics)
        {
            _logger.LogDebug("Object rotated on borrow: outlived MaxLifeTime. Type: {Type}", typeof(T).Name);
        }
    }

    #endregion

    #region Background Tasks

    private void EvictionCallback(object? state)
    {
        //if (!_options.EnableEviction) return;
        if (!_enableEviction) return;
        try
        {
            var now = Stopwatch.GetTimestamp();
            int sampleCapacity = _options.NumTestsPerEvictionRun < 1 ? 1 : _options.NumTestsPerEvictionRun;
            var evictionSample = new HayateObject<T>[sampleCapacity];

            // The shard's share of the minimum pool size: the floor the soft-minimum-idle rule compares
            // the idle count against, computed once per run rather than per candidate.
            var minIdlePerShard = _options.MinPoolSize / _shards.Length;

            foreach (var shard in _shards)
            {
                // Batched check (reuses the sampling buffer to avoid the long-tail allocation of ToArray on the entire idle list each time)
                int evictedCount = 0;
                int sampleCount = shard.SnapshotHead(evictionSample, evictionSample.Length);

                for (int samplerIndex = 0; samplerIndex < sampleCount; samplerIndex++)
                {
                    var w = evictionSample[samplerIndex];
                    // Skip objects currently in use
                    if (w.IsBorrowed) continue;

                    // O7: the policy decides. The pool always goes through it — the default policy is
                    // just another implementation of the rule this scan used to inline, which keeps a
                    // custom rule from being one code path and the built-in rule another.
                    var candidate = new HayateEvictionCandidate<T>(
                        w.Value,
                        TimeSpan.FromSeconds((now - w.CreatedAt) / (double)Stopwatch.Frequency),
                        TimeSpan.FromSeconds((now - w.LastReleasedAt) / (double)Stopwatch.Frequency),
                        w.LeaseCount,
                        shard.Count,
                        minIdlePerShard,
                        _options.MaxLifeTime,
                        _options.MaxIdleTime,
                        _options.SoftMinEvictableIdleTime,
                        shard.Index);

                    if (_evictionPolicy.ShouldEvict(in candidate))
                    {
                        // Only the caller that successfully claims may destroy: if the object is currently borrowed (Remove returns false),
                        // it must never be destroyed, or it would corrupt the business thread using it.
                        if (shard.Remove(w))
                        {
                            Destroy(w);
                            evictedCount++;

                            LogEviction(shard.Index, candidate);
                        }
                    }
                }

                if (evictedCount > 0)
                {
                    _logger.LogInformation("[Shard {Index}] Evicted {Count} objects from shard", shard.Index, evictedCount);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Eviction callback failed");
        }
    }

    /// <summary>
    /// Logs one eviction. A pool running the built-in rule keeps the historical line, whose three flags
    /// are re-derived from the candidate (they are what the default policy evaluated); a custom policy
    /// gets a line naming the rule and the measurements it decided on, so an unexpected eviction can be
    /// traced back to the policy that produced it.
    /// </summary>
    private void LogEviction(int shardIndex, in HayateEvictionCandidate<T> candidate)
    {
        if (_useCustomEvictionPolicy)
        {
            _logger.LogInformation("[Shard {Index}] Eviction policy {Policy} selected an object. Type: {Type} Age: {Age} Idle: {Idle} IdleCount: {IdleCount} LeaseCount: {LeaseCount}",
                shardIndex, _evictionPolicy.GetType().Name, typeof(T).Name,
                candidate.Age, candidate.IdleTime, candidate.IdleCount, candidate.LeaseCount);
            return;
        }

        var isExpired = candidate.Age > candidate.MaxLifeTime;
        var isIdleTooLong = candidate.IdleTime > candidate.MaxIdleTime;
        var isSoftIdle = candidate.IdleTime > candidate.SoftMinEvictableIdleTime
                         && candidate.IdleCount > candidate.MinIdleCount;

        _logger.LogInformation("[Shard {Index}] Evicting object. Type: {Type} Expired: {Expired} IdleTooLong: {IdleTooLong} SoftIdle: {SoftIdle}",
            shardIndex, typeof(T).Name, isExpired, isIdleTooLong, isSoftIdle);
    }

    /// <summary>
    /// Reclaims up to <paramref name="maxCandidates"/> borrowed objects whose borrow age exceeds
    /// <see cref="HayatePoolOptions.RemoveAbandonedTimeout"/> (K2 — CHOPIN's
    /// <c>RemoveAbandonedOnBorrow/OnMaintenance</c>). Oldest borrows are examined first: each shard's
    /// borrowed list is FIFO by borrow time, so the scan walks it from the head and stops at the first
    /// entry not past the timeout — nothing after it can be abandoned either, which makes the bounded
    /// borrow-path budget (8) exact rather than merely approximate. Claims and destroys happen under the
    /// shard claim protocol, so a concurrent return or another reclaim pass can never double-destroy.
    /// </summary>
    /// <returns>The number of objects reclaimed.</returns>
    private int ReclaimAbandoned(int maxCandidates)
    {
        if (maxCandidates <= 0) return 0;

        var timeoutTicks = (long)(_options.RemoveAbandonedTimeout.TotalSeconds * Stopwatch.Frequency);
        var now = Stopwatch.GetTimestamp();
        // Per-call candidate buffer (like the eviction run's sampling buffer): the destroy path calls
        // user policy code, which could recursively borrow and re-enter this method, so a shared
        // instance buffer would be clobbered mid-scan.
        var buffer = new HayateObject<T>[Math.Min(maxCandidates, 64)];
        var reclaimed = 0;

        foreach (var shard in _shards)
        {
            while (reclaimed < maxCandidates)
            {
                var n = shard.SnapshotBorrowed(buffer, buffer.Length);
                if (n == 0) break;

                var found = false;
                for (var i = 0; i < n; i++)
                {
                    var w = buffer[i];
                    // Returned to the pool between the snapshot and this check (a concurrent Release) —
                    // skip; the next window re-walks from the new head.
                    if (!w.IsBorrowed) continue;
                    // FIFO by borrow time: the first entry not past the timeout means nothing after it
                    // is abandoned either, so stop scanning this shard.
                    if (now - w.LastBorrowedAt <= timeoutTicks) break;
                    // Another reclaim pass (borrow path and maintenance can race) may have claimed it
                    // first — only the claim winner destroys.
                    if (!shard.ClaimBorrowed(w)) continue;

                    found = true;
                    reclaimed++;
                    Interlocked.Increment(ref _abandonedRemovedCount);
                    if (_logAbandoned) LogAbandonedObject(w);
                    Destroy(w);
                    if (reclaimed >= maxCandidates) break;
                }

                if (!found) break;
            }
        }

        // Reclaimed objects were borrowed, so the tracked count just dropped: a create-on-miss waiter parked at
        // capacity can grow again. See the invariant on _blockGate (the "slot was freed" half).
        if (reclaimed > 0) SignalAvailability();

        return reclaimed;
    }

    /// <summary>
    /// Forensics companion of reclamation (CHOPIN's <c>LogAbandoned</c>): logs a warning for an
    /// abandoned object with the captured lease trace (when <see cref="HayateLeaseContext"/> holds one —
    /// stack capture is gated by <see cref="HayatePoolOptions.LeakTraceCaptureMode"/>), so the log states
    /// the borrow origin of the reclaimed object or explicitly that no stack was captured.
    /// </summary>
    private void LogAbandonedObject(HayateObject<T> w)
    {
        var trace = w.LeaseContext != null
            ? FormatLeaseTrace(w.LeaseContext)
            : "no lease context captured (LeakTraceCaptureMode is Off)";
        var borrowedMs = (long)((Stopwatch.GetTimestamp() - w.LastBorrowedAt) * 1000.0 / Stopwatch.Frequency);
        _logger.LogWarning(
            "[Shard {Index}] Removing abandoned object (borrowed past RemoveAbandonedTimeout). Type: {Type} BorrowedMs: {BorrowedMs} Trace: {Trace}",
            w.ShardIndex, typeof(T).Name, borrowedMs, trace);
    }

    /// <summary>
    /// Background maintenance pass for abandoned recovery (K2). Scans every shard's borrowed list and
    /// reclaims every object past <see cref="HayatePoolOptions.RemoveAbandonedTimeout"/> — CHOPIN's
    /// <c>RemoveAbandonedOnMaintenance</c>. Runs on the shared background timer at
    /// <see cref="HayatePoolOptions.RemoveAbandonedIntervalMs"/>.
    /// </summary>
    private void AbandonedCallback(object? state)
    {
        //if (!_options.RemoveAbandonedOnMaintenance) return;
        if (!_removeAbandonedOnMaintenance) return;
        try
        {
            ReclaimAbandoned(int.MaxValue);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Abandoned recovery callback failed");
        }
    }

    private void ScalingCallback(object? state)
    {
        if (!_enableAutoScaling) return;
        try
        {
            var totalIdle = _shards.Sum(s => s.Count);
            var currentTotal = TrackedObjectCount;
            if (currentTotal == 0) return;

            var targetSize = _scalingStrategy.CalculateNewSize(currentTotal, totalIdle, _options);
#if NET48 || NETSTANDARD2_0
            // .NET Framework 4.8 / netstandard2.0 does not support Math.Clamp
            // (introduced only in netcoreapp2.0+ / netstandard2.1), so emulate it with a Max/Min combination;
            // higher versions such as net6+ take the native Math.Clamp in the #else branch.
            targetSize = Math.Max(_options.MinPoolSize, Math.Min(_options.MaxPoolSize, targetSize));
#else
            targetSize = Math.Clamp(targetSize, _options.MinPoolSize, _options.MaxPoolSize);
#endif

            // Scale-up cooldown is 3 seconds and scale-down cooldown is 15 seconds, to prevent frequent thrashing
            var canScaleUp = (Stopwatch.GetTimestamp() - _lastScaleUpTime) / (double)Stopwatch.Frequency >= _options.ScaleUpCooldownSeconds;
            var canScaleDown = (Stopwatch.GetTimestamp() - _lastScaleDownTime) / (double)Stopwatch.Frequency >= _options.ScaleDownCooldownSeconds;

            if (targetSize > currentTotal && canScaleUp)
            {
                // Scale up
                UpdateShardMaxSizes();
                var add = targetSize - currentTotal;
                for (var i = 0; i < add; i++)
                {
                    var shard = _shards[i % _shards.Length];
                    // Register into the target shard; if Add is rejected, destroy as fallback to prevent orphaned registry entries.
                    var w = CreateWrappedObject(shard);
                    if (!shard.Add(w)) Destroy(w);
                    else SignalAvailability();
                }

                _lastScaleUpTime = Stopwatch.GetTimestamp();
                _logger.LogInformation("Pool [{PoolName}] scaled UP. {CurrentCapacity} → {TargetCapacity}", _name, currentTotal, targetSize);
                if (_enableMetrics)
                {
                    _metrics.RecordPoolScaled(_name, "UP", currentTotal, targetSize);
                }
            }
            // Removed the scale-down mutual-exclusion gate. The original `totalIdle < currentTotal * 0.4f`
            // required utilization > 0.6 to allow it, while the strategy (ThresholdScalingStrategy) requires utilization
            // < ScaleDownThreshold (default 0.2) to produce a shrink target — the two can never hold at once,
            // making the scale-down branch dead code under the default config (autoScaling only ever grew).
            // Now the scale-down decision fully trusts the strategy: if targetSize < currentTotal && the cooldown has elapsed, it is allowed;
            // thrashing is constrained by the dual defense of the canScaleDown cooldown and the ScaleDownStep.
            else if (targetSize < currentTotal && canScaleDown)
            {
                // Scale down
                var remove = currentTotal - targetSize;
                int removed = 0;

                foreach (var shard in _shards)
                {
                    if (removed >= remove) break;
                    //if (shard.Count <= _options.MinPoolSize / _shards.Length) continue;

                    //while (shard.Count > _options.MinPoolSize / _shards.Length && removed < remove)
                    //{
                    //    if (shard.TryTake(out var w))
                    //    {
                    //        Destroy(w);
                    //        removed++;
                    //    }
                    //    else break;
                    //}

                    while (removed < remove)
                    {
                        if (shard.TryTake(out var w))
                        {
                            Destroy(w);
                            removed++;
                        }
                        else break;
                    }
                }

                UpdateShardMaxSizes();
                _lastScaleDownTime = Stopwatch.GetTimestamp();
                _logger.LogInformation("Pool [{PoolName}] scaled DOWN. {CurrentCapacity} → {TargetCapacity}", _name, currentTotal, targetSize);

                if (_enableMetrics)
                {
                    _metrics.RecordPoolScaled(_name, "DOWN", currentTotal, targetSize);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scaling callback failed");
        }
    }

    private void ValidateCallback(object? state)
    {
        //if (!_options.EnableValidation || !_options.ValidateWhileIdle) return;
        if (!_enableValidation || !_options.ValidateWhileIdle) return;

        try
        {
            int invalidCount = 0;
            foreach (var shard in _shards)
            {
                foreach (var w in shard.GetAll())
                {
                    if (!w.IsBorrowed && !_policy.Validate(w.Value))
                    {
                        // A failed claim means the object was already borrowed or claimed by another thread; it must not be destroyed then
                        if (shard.Remove(w))
                        {
                            Destroy(w);
                            invalidCount++;
                            _logger.LogWarning("Idle object failed validation and was evicted. Type: {Type}", typeof(T).Name);
                        }
                    }
                }
            }

            if (invalidCount > 0)
            {
                _logger.LogWarning("Validation task removed {Count} invalid objects from pool", invalidCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Validate callback failed");
        }
    }

    #endregion

    #region Statistics and Snapshot

    /// <example><code>
    /// var stats = pool.GetStats();
    /// Console.WriteLine(stats.CurrentSize);
    /// </code></example>
    public HayatePoolStats GetStats()
    {
        if (_enableLean) return GetLeanStats();

        // No longer holds a global _statsLock — statistics fields are read atomically one by one (an eventually-consistent snapshot);
        // the shard-count read semantics are the same as the original (whose in-lock read also did not constitute shard-level consistency).
        int totalIdle = _shards.Sum(s => s.Count);
        int totalObjects = TrackedObjectCount; // True total object count (idle + borrowed)

        long waitSum = Volatile.Read(ref _waitTimeSum);
        long waitCount = Volatile.Read(ref _waitTimeCount);
        long waitMax = Volatile.Read(ref _waitTimeMaxMs);
        long waitMin = Volatile.Read(ref _waitTimeMinMs);
        long leaseSum = Volatile.Read(ref _leaseTimeSum);
        long leaseCount = Volatile.Read(ref _leaseTimeCount);
        long leaseMax = Volatile.Read(ref _leaseTimeMaxMs);
        long leaseMin = Volatile.Read(ref _leaseTimeMinMs);

        // G-1: the borrowed count is derived here already (the same "live objects - idle objects" basis
        // TakeSnapshot and the capacity alarm use), so recording the peak costs nothing beyond one branch.
        // Gated by the metrics switch, which is also what makes the operational members meaningful.
        if (_enableMetrics)
        {
            var active = totalObjects - totalIdle;
            UpdatePeakActiveObjects(active < 0 ? 0 : active);
        }

        return new HayatePoolStats
        {
            PooledCount = _shards.Sum(s => s.Count),
            TotalCreated = Interlocked.Read(ref _totalCreated),
            TotalReleased = Interlocked.Read(ref _totalReleased),
            TotalMissed = Interlocked.Read(ref _totalMissed),
            TotalAcquired = Interlocked.Read(ref _totalAcquired),
            AvailableSlots = totalIdle,
            MinSize = _options.MinPoolSize,
            CurrentSize = totalObjects,
            LeakDetectedCount = Interlocked.Read(ref _leakDetectedCount),
            LeakSuspectedCount = Interlocked.Read(ref _leakSuspectedCount),
            AbandonedRemovedCount = Interlocked.Read(ref _abandonedRemovedCount),
            LifetimeRotatedCount = Interlocked.Read(ref _lifetimeRotatedCount),
            AllocationTrackingEnabled = _enableAllocationTracking,
            AcquireAllocatedBytes = Interlocked.Read(ref _acquireAllocatedBytes),
            ReleaseAllocatedBytes = Interlocked.Read(ref _releaseAllocatedBytes),
            AcquireAllocationSamples = Interlocked.Read(ref _acquireAllocationSamples),
            ReleaseAllocationSamples = Interlocked.Read(ref _releaseAllocationSamples),
            WaitTimeSum = waitSum,
            WaitTimeCount = waitCount,
            LeaseTimeSum = leaseSum,
            LeaseTimeCount = leaseCount,
            MaxWaitTimeMs = waitMax,
            MaxLeaseTimeMs = leaseMax,
            MinWaitTimeMs = waitMin == long.MaxValue ? 0 : waitMin,
            MinLeaseTimeMs = leaseMin == long.MaxValue ? 0 : leaseMin,
            MetricsEnabled = _enableMetrics,
            PeakActiveObjects = Volatile.Read(ref _peakActiveObjects),
            StartedAt = _startedAtUtcTicks == 0 ? default : new DateTimeOffset(_startedAtUtcTicks, TimeSpan.Zero),
            LastActivityTime = _lastActivityUtcTicks == 0 ? null : new DateTimeOffset(_lastActivityUtcTicks, TimeSpan.Zero)
        };
    }

    /// <example><code>
    /// var snap = pool.TakeSnapshot();
    /// foreach (var d in snap.ObjectDetails) { /* inspect */ }
    /// </code></example>
    public HayatePoolSnapshot TakeSnapshot()
    {
        if (_enableLean) return GetLeanSnapshot();

        var leakTraces = new List<string>();
        // Per-object lifecycle detail (the snapshot is a diagnostic path, so O(n) aggregation is acceptable)
        var details = new List<HayatePoolObjectDetail>();
        // The snapshot Timestamp keeps wall-clock time (external semantics unchanged); leak detection now uses Stopwatch ticks
        var wallClock = DateTimeOffset.UtcNow;
        var now = Stopwatch.GetTimestamp();

        // The leak forensics scan and the recheck alert share one registry traversal (both branches' conditions stay consistent with 2.4/2.5
        // semantics point by point), while also producing the per-object detail, avoiding three separate O(n) scans.
        foreach (var shard in _shards)
        {
            foreach (var w in shard.TrackedValues)
            {
                details.Add(new HayatePoolObjectDetail
                {
                    ShardIndex = w.ShardIndex,
                    IsBorrowed = w.IsBorrowed,
                    LeaseCount = w.LeaseCount,
                    LastGetThreadId = w.LastGetThreadId,
                    CreatedAtTick = w.CreatedAtTick,
                    LeaseTimeMs = w.LeaseTimeMs,
                    Generation = w.Generation,
                    OwnerPoolName = w.OwnerPoolName
                });

                if (_enableLeakDetection)
                {
                    // The leak scan must traverse the shard registry (all live wrappers).
                    // The original implementation traversed shard.GetAll() (only in-pool idle objects), while TryTake physically removes borrowed objects
                    // from the shard list — borrowed objects were never scanned, so leak detection was structurally never triggered.
                    // Check for leaks (Stopwatch ticks → seconds conversion)
                    if (w.IsBorrowed &&
                        (now - w.LastBorrowedAt) / (double)Stopwatch.Frequency > _options.LeakDetectionThreshold.TotalSeconds)
                    {
                        // LeakTraces remain text-form, formatted by the pool side from the lease context
                        // (with a lease-ID prefix; before 2.4 it was the full Environment.StackTrace output verbatim)
                        leakTraces.Add(FormatLeaseTrace(w.LeaseContext));
                        Interlocked.Increment(ref _leakDetectedCount);
                    }
                }
                else
                {
                    // The recheck-alert path used when leak detection is off. Using the same LeakDetectionThreshold
                    // it counts suspected leaks ("borrowed beyond the threshold and not returned") into LeakSuspectedCount, alongside
                    // LeakDetectedCount — counting only, no forensics (no AcquireStackFrames capture),
                    // and triggering no reclamation; the forensics modes' semantics are unchanged.
                    // Because LastBorrowedAt has been recorded unconditionally on the borrow path since 2.5, the "all features off" config
                    // still supports the recheck (before 2.4 that timestamp was gated by feature switches and could stay 0).
                    if (w.IsBorrowed && w.LastBorrowedAt != 0 &&
                        (now - w.LastBorrowedAt) / (double)Stopwatch.Frequency > _options.LeakDetectionThreshold.TotalSeconds)
                    {
                        Interlocked.Increment(ref _leakSuspectedCount);
                    }
                }
            }
        }

        var pooledCount = _shards.Sum(s => s.Count);

        // The borrowed count is derived from "total live wrapper count - in-pool idle count" (consistent with GetStats'
        // totalObjects - totalIdle basis), rather than relying on Shard.BorrowedCount.
        // Reason: TryTake first physically removes the object from the shard list, then sets Borrowed, so borrowed objects are not in
        // the shard list at all; therefore Shard.BorrowedCount (counting IsBorrowed within the list) is structurally always 0.
        // The shard registry always holds all live wrappers (idle + borrowed) until Destroy removes them;
        // the extremely short transient of an eviction "claimed but not yet destroyed" is counted, but it is bounded by Destroy's fast execution and negligible.
        var borrowedCount = TrackedObjectCount - pooledCount;
        if (borrowedCount < 0) borrowedCount = 0;

        // G-1: the same derived count, so the peak is recorded here too — a snapshot read is as good a
        // sampling point as a GetStats read.
        if (_enableMetrics) UpdatePeakActiveObjects(borrowedCount);

        return new HayatePoolSnapshot
        {
            Timestamp = wallClock,
            PooledCount = pooledCount,
            BorrowedCount = borrowedCount,
            TotalCreated = Interlocked.Read(ref _totalCreated),
            TotalMissed = Interlocked.Read(ref _totalMissed),
            TotalAcquired = Interlocked.Read(ref _totalAcquired),
            LeakCount = Interlocked.Read(ref _leakDetectedCount),
            LeakSuspectedCount = Interlocked.Read(ref _leakSuspectedCount),
            AbandonedRemovedCount = Interlocked.Read(ref _abandonedRemovedCount),
            LifetimeRotatedCount = Interlocked.Read(ref _lifetimeRotatedCount),
            AllocationTrackingEnabled = _enableAllocationTracking,
            AcquireAllocatedBytes = Interlocked.Read(ref _acquireAllocatedBytes),
            ReleaseAllocatedBytes = Interlocked.Read(ref _releaseAllocatedBytes),
            LeakTraces = leakTraces.AsReadOnly(),
            ObjectDetails = details.AsReadOnly()
        };
    }

    #endregion

    #region ReloadConfig

    /// <exception cref="ArgumentNullException">Thrown if <paramref name="configure"/> is null.</exception>
    /// <example><code>
    /// pool.ReloadConfig(o =&gt; o.MaxPoolSize = 64);
    /// </code></example>
    public void ReloadConfig(Action<HayatePoolOptions> configure)
    {
        if (configure is null) throw new ArgumentNullException(nameof(configure));

        lock (_options)
        {
            var previousMaxPoolSize = _options.MaxPoolSize;
            var previousLease = _options.EnableLean;
            var previousSoftCapacity = _options.SoftCapacity;

            configure(_options);
            _options.ApplyFeatureSwitches(); // Force-correct the configuration

            // The borrow path compares against precomputed tick bounds rather than converting the options
            // on every borrow, so a reload that changes MaxLifeTime or GenerationThresholdMs has to
            // recompute them — otherwise the borrow path would keep judging against the values the pool was
            // built with while the eviction paths, which read the options live, judged against the new ones.
            RefreshDerivedTimeThresholds();

            // Lean mode owns a retention buffer and a dispatch decision that are both fixed at
            // construction time. Resizing or leaving the mode would silently desynchronize the pool
            // from its configuration, so both are rejected with the original values restored.
            if (_enableLean)
            {
                if (_options.MaxPoolSize != previousMaxPoolSize)
                {
                    _options.MaxPoolSize = previousMaxPoolSize;
                    throw new InvalidOperationException(
                        $"HayatePool [{_name}] uses the lean fast path, whose retention buffer is sized at construction and cannot be resized. Rebuild the pool to change MaxPoolSize.");
                }

                if (!_options.EnableLean)
                {
                    _options.EnableLean = previousLease;
                    throw new InvalidOperationException(
                        $"HayatePool [{_name}] uses the lean fast path and cannot switch back to the general-purpose engine at runtime. Rebuild the pool instead.");
                }
            }

            // T-R: the soft ceiling is read by the return path from a constructor-time snapshot and, in
            // lean mode, fixes the buffer's slot-scan limit when the pool is built. A reload could
            // therefore change the option without changing the pool's behaviour, so it is rejected with
            // the original value restored rather than accepted and ignored.
            if (_options.SoftCapacity != previousSoftCapacity)
            {
                _options.SoftCapacity = previousSoftCapacity;
                throw new InvalidOperationException(
                    $"HayatePool [{_name}] cannot change SoftCapacity at runtime: the ceiling is applied by the return path from a value fixed when the pool was built (in lean mode it also fixes the buffer's slot limit). Rebuild the pool to change it.");
            }

            // `{Config}`, not `{@Config}`: the `@` destructuring operator belongs to Serilog's template
            // parser, and this message never reaches one — it goes through Microsoft.Extensions.Logging,
            // which copies the hole verbatim into a structured property literally named "@Config". A sink
            // filtering on `Config` then finds nothing, and Serilog's own destructuring never runs, so the
            // options arrive as a scalar instead of the object they are. (Measured: the rendered text is
            // identical either way — MEL substitutes positionally and calls ToString() — so this changes
            // the structured payload only, not what a human reads.)
            _logger.LogInformation("Pool configuration reloaded. Type: {Type} NewConfig: {Config}", typeof(T).Name, _options);
        }
    }

    #endregion

    #region Clear and Dispose

    public void Clear()
    {
        if (_enableLean)
        {
            ClearLean();
            return;
        }

        foreach (var shard in _shards)
        {
            shard.Clear();
            // The registry is cleared with the shard (including entries of borrowed objects, consistent with the original pool-level map.Clear semantics).
            shard.ClearTracked();
        }
        _logger.LogInformation("Clearing object pool. Type: {Type}", typeof(T).Name);
    }

    public HayatePoolOptions GetOptions()
    {
        return _options.CopyTo();
    }

    public void Dispose()
    {
        Clear();

        // Detach the shutdown subscription first: whatever route disposal took -- an explicit call, or the
        // process-exit notification itself -- the process-wide hook must not keep this pool reachable.
        _shutdownRegistration?.Dispose();

        // Release the shared background timer and drop the reference so a disposed pool retains no timer handle.
        _backgroundTimer?.Dispose();
        _backgroundTimer = null;

        // The probe timer only exists while the breaker is open; dispose it here so a disposed pool
        // retains no handle either (same precondition as the timers: no in-flight probe at Dispose time).
        StopBreakerProbeTimer();

        // Release the return-signal gate (same precondition as the timers: no in-flight Acquire waiters at Dispose time)
        _blockGate.Dispose();

#if NET6_0_OR_GREATER
        // O-D: hand the rented slot array back to the shared pool. Clear() already emptied every
        // slot, so the array is clean to reuse.
        if (_enableArrayPoolStorage && _apSlots.Length > 0)
        {
            ArrayPool<T>.Shared.Return((T[])(object)_apSlots);
            _apSlots = Array.Empty<T>();
        }
#endif

        _logger.LogInformation("Object pool disposed. Type: {Type}", typeof(T).Name);
    }

#if NET6_0_OR_GREATER
    /// <summary>
    /// Drains the pool the way pooled I/O objects are meant to be torn down (A2, α form —
    /// docs/async-policy.md §5): every object the pool owns is disposed preferring
    /// <c>IAsyncDisposable</c> over <c>IDisposable</c>, and the policy's asynchronous destroy hook
    /// is awaited when it provides one. The synchronous <see cref="Dispose"/> keeps its current
    /// semantics for every pool — graceful shutdown does not change meaning for callers that do not
    /// take this member.
    /// </summary>
    /// <remarks>
    /// The teardown around the drain mirrors <see cref="Dispose"/> statement for statement; only the
    /// drain itself is asynchronous. Rolling restarts and graceful shutdown are the target scenario:
    /// the await suspends the caller instead of occupying a thread while connections close.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        await ClearAsync().ConfigureAwait(false);

        // Detach the shutdown subscription first (same order and reasoning as Dispose).
        _shutdownRegistration?.Dispose();

        // Release the shared background timer and drop the reference so a disposed pool retains no timer handle.
        _backgroundTimer?.Dispose();
        _backgroundTimer = null;

        // The probe timer only exists while the breaker is open; dispose it here so a disposed pool
        // retains no handle either (same precondition as the timers: no in-flight probe at Dispose time).
        StopBreakerProbeTimer();

        // Release the return-signal gate (same precondition as the timers: no in-flight Acquire waiters at Dispose time)
        _blockGate.Dispose();

        // O-D: hand the rented slot array back to the shared pool. The drain already emptied every
        // slot, so the array is clean to reuse.
        if (_enableArrayPoolStorage && _apSlots.Length > 0)
        {
            ArrayPool<T>.Shared.Return((T[])(object)_apSlots);
            _apSlots = Array.Empty<T>();
        }

        _logger.LogInformation("Object pool disposed. Type: {Type}", typeof(T).Name);
    }

    /// <summary>
    /// The drain of <see cref="DisposeAsync"/> — the awaited twin of <see cref="Clear"/>. The hook
    /// policy per mode mirrors the synchronous clear: the general shards dispose their objects
    /// without a destroy hook (what <c>Shard.Clear</c> has always done), while the lean buffer runs
    /// the destroy hook for every policy (what <c>ClearLean</c> has always done) — except that a
    /// policy providing the asynchronous destroy hook gets it awaited instead
    /// (docs/async-policy.md §5). Object disposal prefers <c>IAsyncDisposable</c> on both modes:
    /// the caller explicitly chose the asynchronous drain.
    /// </summary>
    private async ValueTask ClearAsync()
    {
        if (_enableLean)
        {
            await ClearLeanAsync().ConfigureAwait(false);
            return;
        }

        foreach (var shard in _shards)
        {
            var drained = shard.DrainForClear();
            var clearedCount = 0;
            foreach (var w in drained)
            {
                try
                {
                    if (_asyncPolicy is not null)
                    {
                        await _asyncPolicy.OnDestroyAsync(w.Value).ConfigureAwait(false);
                    }
                    await DisposeObjectAsync(w.Value).ConfigureAwait(false);
                    clearedCount++;
                }
                catch (Exception ex)
                {
                    // Per-object robustness, mirroring the shard's SafeDispose: one object's failed
                    // teardown must not abandon the drain of the rest.
                    _logger.LogError(ex, "Failed to dispose object when [async pool dispose]");
                }
            }
            // The registry is cleared with the shard (including entries of borrowed objects, consistent with the original pool-level map.Clear semantics).
            shard.ClearTracked();
            _logger.LogInformation("[Shard {Index}] Shard cleared asynchronously, {Count} objects destroyed", shard.Index, clearedCount);
        }
        _logger.LogInformation("Clearing object pool. Type: {Type} (asynchronously)", typeof(T).Name);
    }
#endif

    #endregion
}