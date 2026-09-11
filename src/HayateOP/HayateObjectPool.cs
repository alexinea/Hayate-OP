using System.Collections.Concurrent;
using System.Diagnostics;
using DotNetCore.HayateOP.Common;
using DotNetCore.HayateOP.Logging;
using DotNetCore.HayateOP.Metrics;
using DotNetCore.HayateOP.Policies;
using DotNetCore.HayateOP.Scaling;

namespace DotNetCore.HayateOP;

public partial class HayatePoolBasic<T> : IHayateObjectPool<T>
    where T : class
{
    private readonly string _name;

    private readonly Shard[] _shards;
    private readonly IHayateObjectPolicy<T> _policy;
    private readonly HayatePoolOptions _options;
    private readonly IHayateScalingStrategy _scalingStrategy;

    private readonly IHayateLogger _logger;
    private readonly IHayateMetrics _metrics;

    // The original pool-level single _objectMap (ConcurrentDictionary<T, HayateObject<T>>) has been split by shard,
    // and moved into each Shard's internal registry (see HayateObjectPool.Shard.cs). Registry entries are written on object create/destroy
    // to their owning shard, eliminating the non-converging bucket-array peak caused by multiple shards writing the same table concurrently.
    // The pool level keeps only two derived views:
    /// <summary>Total number of live objects (idle + borrowed), derived by summing each shard's registry.</summary>
    private int TrackedObjectCount => _shards.Sum(s => s.TrackedCount);

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

    // Return-event notification gate. Released once by Release() on a successful return to the pool; Block/BlockTimeout/CreateNew
    // waiters use Wait instead of SpinWait busy-waiting, eliminating the 100% CPU idle spin during the wait.
    // The counter means "number of consumable wake-up signals"; on a successful borrow it consumes one signal via Wait(0),
    // preventing stale signals from accumulating and causing waiters to be spuriously woken one by one into a busy loop.
    private readonly SemaphoreSlim _blockGate = new(0, int.MaxValue);

    // The suspended-wait slice used when no signal arrives. A signal wakes immediately; the slice merely caps the re-check interval when no signal arrives.
    private const int BlockWaitSliceMs = 100;

    // Background tasks
    private Timer _evictionTimer;
    private Timer _scalingTimer;
    private Timer _validationTimer;

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
    private readonly bool _enableMetrics;
    private readonly bool _enableGenerationOptimization;
    private readonly bool _enableLeakDetection;
    private readonly bool _enableEviction;
    private readonly bool _enableAutoScaling;

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

    // Shard-affinity mode (constructor-time snapshot). None is the default and has zero overhead (start index is always 0);
    // Thread maps the start shard stably by thread ID; Custom uses the user delegate (falling back to sequential scan on exception/out-of-range/null).
    private readonly HayateShardAffinityMode _affinityMode;
    private readonly Func<int> _customShardAffinity;

    // Pre-warm readiness signal. false by default (synchronous pre-warm at construction, zero extra wait on borrow, consistent with 2.4);
    // when true, pre-warm runs in the background and the borrow path blocks on _warmupCompletion until pre-warm completes
    // (including on failure — the signal is always set, so there is no permanent block). During the wait the cold-start path yields.
    private readonly bool _waitForWarmup;
    private readonly TaskCompletionSource<object> _warmupCompletion;

    // Allocation tracking (off by default). Counts the per-thread allocation delta (bytes) and sample count on the synchronous borrow/return paths;
    // diagnostic only, never affects pool behavior decisions. Under net48 / netstandard2.0 the API is unavailable, so the counters stay 0.
    private readonly bool _enableAllocationTracking;
    private long _acquireAllocatedBytes;
    private long _releaseAllocatedBytes;
    private long _acquireAllocationSamples;
    private long _releaseAllocationSamples;

    /// <exception cref="ArgumentNullException">Thrown if policy, options, scalingStrategy, metrics, logger, or poolName is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the configured options are invalid.</exception>
    internal HayatePoolBasic(
        IHayateObjectPolicy<T> policy,
        HayatePoolOptions options,
        IHayateScalingStrategy scalingStrategy,
        IHayateMetrics metrics,
        IHayateLogger logger,
        string poolName)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _scalingStrategy = scalingStrategy ?? throw new ArgumentNullException(nameof(scalingStrategy));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _name = poolName ?? throw new ArgumentNullException(nameof(poolName));

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
        _enableMetrics = _options.EnableMetrics;

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
            _shards[i] = new Shard(_options, i, shardMax, _logger);
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

        _waitForWarmup = _options.WaitForWarmup;
        _enableAllocationTracking = _options.EnableAllocationTracking;
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
                    completion.TrySetResult(null);
                }
            });
        }
        else
        {
            PreWarm();
        }

        StartBackgroundTasks();
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

    private void StartBackgroundTasks()
    {
        if (_enableEviction)
        {
            _evictionTimer = new Timer(EvictionCallback, null, _options.EvictionIntervalMs, _options.EvictionIntervalMs);
        }

        if (_enableAutoScaling)
        {
            _scalingTimer = new Timer(ScalingCallback, null, _options.ScalingIntervalMs, _options.ScalingIntervalMs);
        }

        if (_enableValidation)
        {
            _validationTimer = new Timer(ValidateCallback, null, _options.ValidateIntervalMs, _options.ValidateIntervalMs);
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

        // Wait for pre-warm readiness if not yet done (off by default → constant branch, zero overhead)
        WaitForWarmupIfNeeded();

        var sw = ValueStopwatch.StartNew();

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

                    //if (_options.EnableEviction || _options.EnableLeakDetection || _options.EnableGenerationOptimization)
                    if (_enableEviction || _enableLeakDetection || _enableGenerationOptimization)
                    {
                        w.LastBorrowedAt = Stopwatch.GetTimestamp();
                    }

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
                    // a single QPC (Stopwatch.GetTimestamp) has no allocation and no time-zone conversion,
                    // and its cost is far below the DateTime.UtcNow removed by the scale-timestamp change, so it is acceptable.
                    w.LastBorrowedAt = Stopwatch.GetTimestamp();

                    // Cumulative borrow count. At the moment of borrow the wrapper is exclusively owned by this thread (TryTake already unlinked and claimed it,
                    // and eviction/validation cannot claim a Borrowed object), so a plain increment suffices — no Interlocked needed.
                    w.LeaseCount++;

                    // Generational promotion, only when generational optimization is enabled
                    //if (_options.EnableGenerationOptimization &&
                    if (_enableGenerationOptimization &&
                        (Stopwatch.GetTimestamp() - w.CreatedAt) * 1000.0 / Stopwatch.Frequency > _options.GenerationThresholdMs)
                    {
                        w.Generation = 1;
                    }

                    Interlocked.Increment(ref _totalAcquired);

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
                        // Covered by the _enableMetrics gate (borrow-path leak-trace point)
                        if (_enableMetrics)
                        {
                            _metrics.RecordObjectAcquired(_name, w.Value, waitTime);
                        }
                        _logger.LogDebug("Object borrowed from pool. Type: {Type} WaitTime: {WaitTime:F2}ms, shard: {ShardIndex}", typeof(T).Name, waitTime, shard.Index);
                    }
                    else
                    {
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
                        // Cold boot: when the pool is completely empty, create the first object on demand, deterministically eliminating the first-borrow hang
                        var coldBoot = TryColdBootAcquire((long)sw.Elapsed.TotalMilliseconds);
                        if (coldBoot is not null) return coldBoot;

                        // Wait indefinitely until an object is obtained. Suspend waiting for the return signal (slice caps the re-check interval),
                        // waking immediately on signal to retry TryTake; replaces the original SpinOnce busy-wait (100% CPU).
                        // The Block policy has no timeout; the timeout argument is not used (consistent with the original behavior).
                        _blockGate.Wait(BlockWaitSliceMs);
                        continue;
                    }

                case HayatePoolRejectPolicy.BlockTimeout:
                    {
                        // Cold boot: when the pool is completely empty, create the first object on demand, deterministically eliminating the first-borrow timeout
                        var coldBoot = TryColdBootAcquire((long)elapsed.TotalMilliseconds);
                        if (coldBoot is not null) return coldBoot;

                        // Throw after the wait times out
                        if (elapsed >= timeout)
                        {
                            if (_enableMetrics) Interlocked.Increment(ref _totalMissed);
                            if (_enableAutoScaling) ForceScaleUpOneStep();
                            throw new TimeoutException($"HayatePool [{_name}] timed out acquiring an object after {timeout.TotalSeconds}s.");
                        }

                        // Wait for a return signal; if none arrives within the slice, wake and re-check the timeout and the shard.
                        _blockGate.Wait(BlockWaitSliceMs);
                        continue;
                    }

                case HayatePoolRejectPolicy.CreateNew:
                    {
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
                            var shard = _shards[(int)(Interlocked.Increment(ref _createCursor) - 1) % _shards.Length];
                            var w = CreateWrappedObject(shard);   // TryTrack registration is already done internally
                            w.Location = HayateObjectLocation.Borrowed;
                            // The borrow timestamp is recorded unconditionally (same as the Acquire main path, keeping leak recheck usable)
                            w.LastBorrowedAt = Stopwatch.GetTimestamp();
                            // Cumulative borrow count (newly created means already borrowed; the wrapper is exclusively owned by this thread now)
                            w.LeaseCount++;
                            _policy.OnAcquire(w.Value);
                            Interlocked.Increment(ref _totalAcquired);

                            // Capacity-alarm probe (returns immediately internally when disabled)
                            CheckCapacityAlarm();

                            var waitTime = (long)sw.Elapsed.TotalMilliseconds;
                            if (_enableMetrics)
                            {
                                UpdateWaitTimeStats(waitTime);
                                _metrics.RecordObjectAcquired(_name, w.Value, waitTime);
                            }

                            return w.Value;
                        }

                        // Wait for a return signal; if none arrives within the slice, wake and re-check the timeout and the shard.
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

        // Wait for pre-warm readiness if not yet done (off by default → constant branch, zero overhead)
        if (_waitForWarmup) await _warmupCompletion.Task.ConfigureAwait(false);

        // The affinity start shard is evaluated only once per AcquireAsync (None is always 0, zero extra overhead).
        var affinityStart = _affinityMode == HayateShardAffinityMode.None ? 0 : SelectStartShardIndex();

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
                    w.LastBorrowedAt = Stopwatch.GetTimestamp();

                    // Cumulative borrow count (TryTake already unlinked and claimed, so the wrapper is exclusively owned by this thread now)
                    w.LeaseCount++;

                    Interlocked.Increment(ref _totalAcquired);

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
            var coldBoot = TryColdBootAcquire(0);
            if (coldBoot is not null) return coldBoot;

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

    private void ReleaseCore(T item)
    {
        if (item is null)
        {
            _logger.LogWarning("Returned null object to pool. Type: {Type}", typeof(T).Name);
            return;
        }

        // Find the corresponding wrapper object and verify it belongs to the pool.
        // After the registry is split by shard, reverse lookup by T requires probing each shard (read-only and lock-free; the shard count is single-digit,
        // so the cost is negligible). The same object is only registered in its creation shard; a hit in any shard confirms it belongs to this pool.
        HayateObject<T> w = null;
        foreach (var shard in _shards)
        {
            if (shard.TryGetTracked(item, out w)) break;
        }

        if (w is null)
        {
            _logger.LogWarning("Returned object does not belong to pool. Disposing. Type: {Type}", typeof(T).Name);

            Destroy(item);

            // Covered by the _enableMetrics gate (the reject path was originally ungated — a missed allocation point after enabling HayateDiagnostics)
            if (_enableMetrics)
            {
                _metrics.RecordObjectReleased(_name, item, false);
            }

            return;
        }

        #region Return validation (only when validation is enabled)

        //if (_options.EnableValidation && _options.ValidateOnReturn && !_policy.Validate(item))
        if (_enableValidation && _options.ValidateOnReturn && !_policy.Validate(item))
        {
            _logger.LogWarning("Object returned to pool. Disposing. Type: {Type}", typeof(T).Name);

            Destroy(w);

            // Covered by the _enableMetrics gate (validation-reject path leak-trace point)
            if (_enableMetrics)
            {
                _metrics.RecordObjectReleased(_name, item, false);
            }

            return;
        }

        #endregion

        try
        {
            #region Core return handling

            // Passivate the object
            _policy.OnPassivate(item);

            // The returned state is no longer cleared separately — a successful shard.Add below moves Location back
            // to InPool (IsBorrowed becomes false); if Add is rejected, Destroy runs (Location = Destroyed).

            // The return timestamp is recorded unconditionally (the same 2.5 invariant): the old conditional gating
            // (eviction/metrics) left LastReleasedAt at the creation time when everything was off,
            // structurally breaking Evict(Idle)'s idle judgment; a single QPC costs the same as on the borrow side and is acceptable.
            w.LastReleasedAt = Stopwatch.GetTimestamp();
            w.LeaseTimeMs = (long)((w.LastReleasedAt - w.LastBorrowedAt) * 1000.0 / Stopwatch.Frequency);

            // Reset the object; the policy can reject the return by returning false (e.g., the object is corrupted or not reusable)
            if (!_policy.OnRelease(item))
            {
                _logger.LogWarning(
                    "Policy rejected object on release. Disposing. Type: {Type}",
                    typeof(T).Name);

                Destroy(w);

                // Covered by the _enableMetrics gate (policy-reject path leak-trace point)
                if (_enableMetrics)
                {
                    _metrics.RecordObjectReleased(_name, item, false);
                }

                // Maintain the minimum idle watermark: after the policy rejects, the pool may be drained, so proactively replenish.
                // Only triggers when auto-scaling is enabled and MinPoolSize > 0, to avoid meaningless overhead.
                if (_enableAutoScaling && _options.MinPoolSize > 0 &&
                    TrackedObjectCount < _options.MinPoolSize)
                {
                    ForceScaleUpOneStep();
                }

                return;
            }

            #endregion

            #region Metrics statistics (only when metrics are enabled)

            if (_enableMetrics)
            {
                // Record lease-time statistics
                UpdateLeaseTimeStats(w.LeaseTimeMs);
                Interlocked.Increment(ref _totalReleased);
                _metrics.RecordObjectReleased(_name, item, true);
            }

            #endregion

            #region Return to shard

            // Critical fix (D3): round-trip by the ShardIndex recorded at Acquire,
            // never revert to Thread.GetCurrentProcessorId() % _shards.Length —
            // the latter silently disposes objects in 2/3 of shards (those with max = 0) when MaxPoolSize < ShardCount.
            var shardIndex = (uint)w.ShardIndex < (uint)_shards.Length ? w.ShardIndex : 0;
            var shard = _shards[shardIndex];
            if (!shard.Add(w))
            {
                // The shard refused to accept; two possibilities:
                // 1) The shard is full (overflow) — after Add no longer disposes the object itself,
                //    so this path's Destroy(w) performs the full destruction (triggering OnDestroy), avoiding missed hooks;
                // 2) The object was already claimed by eviction / idle validation — the other thread is destroying it.
                // Either way, the object is no longer reusable; destroy it here uniformly and remove it from the global index;
                // Destroy is idempotent and will not double-Dispose.
                _logger.LogWarning("Object rejected by shard on release. Removing from pool. Type: {Type}, shard: {ShardIndex}",
                    typeof(T).Name, shardIndex);

                // Destroy untracks the object from the registry internally — no separate UntrackObject
                // call is needed here (it would be a no-op anyway, since Destroy drops the value
                // reference and UntrackObject short-circuits on a null value).
                Destroy(w);

                // Fix: the shard rejected and destroyed an object, so the pool total may drop below MinPoolSize
                // (especially when the eviction thread first claims an InPool object, then this returned object is overflowed).
                // Consistent with the OnRelease=false path, replenish once only when auto-scaling is enabled and the pool is genuinely below the watermark,
                // to avoid latency-sensitive work hitting a cold start under eviction/return interleaving.
                if (_enableAutoScaling && _options.MinPoolSize > 0 &&
                    TrackedObjectCount < _options.MinPoolSize)
                {
                    ForceScaleUpOneStep();
                }

                return;
            }

            #endregion

            // Lease ends — clear the current async flow's lease context (Current becomes null;
            // AsyncLocal writes incur an execution-context copy cost, paid only when forensics is enabled).
            if (_enableLeakDetection && _leakTraceCaptureMode != HayateLeakTraceCaptureMode.Off)
            {
                HayateLeaseContext.DetachFromFlow();
            }

            // The object returned to the pool successfully; wake one waiting Acquire (Block/BlockTimeout/CreateNew).
            // When there is no waiter, the counter accumulates and is consumed by the borrow path's Wait(0), so it never leaks.
            try { _blockGate.Release(); }
            catch (SemaphoreFullException)
            {
                // int.MaxValue counter cap protection; unreachable under normal load; swallowed to keep the Release path uninterrupted.
            }

            // Capacity-alarm probe — the borrow-water-level drop on return is sensed here (reset/retripped on state transition).
            CheckCapacityAlarm();

            _logger.LogDebug("Object returned to pool. Type: {Type} LeaseTime: {LeaseTime:F2}ms, shard: {ShardIndex}", typeof(T).Name, w.LeaseTimeMs, shardIndex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during object return validation. Disposing. Type: {Type}", typeof(T).Name);
            Destroy(w);
            //if (_options.EnableMetrics)
            if (_enableMetrics)
            {
                _metrics.RecordObjectReleased(_name, item, false);
            }
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
                    // Same criterion as background expired eviction
                    match = (now - w.CreatedAt) / (double)Stopwatch.Frequency > _options.MaxLifeTime.TotalSeconds;
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

    #region Helper methods

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
                var o = _policy.Create();
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
                _policy.OnDestroy(o);
                if (o is IDisposable d) d.Dispose();
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
    /// Called only by the Block / BlockTimeout policies and the async wait path — CreateNew keeps the "create after the full timeout" semantics,
    /// while Abort keeps the direct-reject semantics.
    /// </summary>
    /// <returns>Object borrowed via cold boot; returns <c>null</c> when this call did not claim the boot (pool non-empty / at capacity / another thread is creating), and the caller should continue normal waiting.</returns>
    private T TryColdBootAcquire(long waitTimeMs)
    {
        if (_options.MaxPoolSize <= 0) return null;
        if (TrackedObjectCount != 0) return null;
        if (Interlocked.CompareExchange(ref _coldBootClaimed, 1, 0) != 0) return null;

        try
        {
            // Double-check: during the CAS, a concurrent return / scale-up may make the pool non-empty — just fall back to the normal wait path then.
            if (TrackedObjectCount != 0) return null;

            var shard = _shards[(int)(Interlocked.Increment(ref _createCursor) - 1) % _shards.Length];
            var w = CreateWrappedObject(shard);   // TryTrack registration is already done internally (includes the _totalCreated counter)
            w.Location = HayateObjectLocation.Borrowed;
            // The borrow timestamp is recorded unconditionally (same as the Acquire main path, keeping leak recheck usable)
            w.LastBorrowedAt = Stopwatch.GetTimestamp();
            // Cumulative borrow count (cold boot means borrowed; the wrapper is exclusively owned by this thread now)
            w.LeaseCount++;
            _policy.OnAcquire(w.Value);
            Interlocked.Increment(ref _totalAcquired);

            // Capacity-alarm probe (returns immediately internally when disabled)
            CheckCapacityAlarm();

            if (_enableMetrics)
            {
                UpdateWaitTimeStats(waitTimeMs);
                _metrics.RecordObjectAcquired(_name, w.Value, waitTimeMs);
            }

            _logger.LogInformation("Pool [{PoolName}] cold-boot acquired on demand (pool was empty)", _name);
            return w.Value;
        }
        finally
        {
            // Whether creation succeeds or fails, the flag must be reset, so the pool can cold-boot again after being emptied;
            // when CreateWrappedObject throws, the exception propagates to the caller (consistent with the CreateNew path behavior).
            Interlocked.Exchange(ref _coldBootClaimed, 0);
        }
    }

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

    private void Destroy(HayateObject<T> w)
    {
        if (w == null) return;

        // Idempotent protection: eviction, idle validation, and return rejection may concurrently hit the same wrapper,
        // and only the first caller to pass the CAS truly destroys it; the rest return immediately, avoiding a double Dispose.
        if (Interlocked.Exchange(ref w.Destroyed, 1) == 1) return;

        try
        {
            _policy.OnDestroy(w.Value);
            if (w.Value is IDisposable d) d.Dispose();
            UntrackObject(w);
            w.Location = HayateObjectLocation.Destroyed;
            // On destruction, clear the lease context reference so the stack frames can be GC'd with the context (on the AsyncLocal flow side
            // each caller Detaches on its own Release; this code does not touch other flows' contexts — such is the AsyncLocal semantics.
            w.LeaseContext = null;
            // Drop the value reference: the wrapper may be parked on the spare stack for reuse, and a
            // parked wrapper must never keep a destroyed pooled object alive. UntrackObject has already
            // removed the registry entry above, so nothing reads w.Value after this point.
            w.Value = null;
            _logger.LogDebug("Wrapped object destroyed. Type: {Type}", typeof(T).Name);
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
            _policy.OnDestroy(o);
            if (o is IDisposable d) d.Dispose();
            UntrackKey(o);
            _logger.LogDebug("Object destroyed. Type: {Type}", typeof(T).Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during object destruction. Type: {Type}", typeof(T).Name);
        }
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
    /// Custom uses the user delegate; null/out-of-range/exception all fall back to 0 (borrow-path robustness first — the borrow is never interrupted by a missing policy).
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
    private static string FormatLeaseTrace(HayateLeaseContext ctx)
    {
        var frames = ctx?.Frames;
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

    #region Background Tasks

    private void EvictionCallback(object state)
    {
        //if (!_options.EnableEviction) return;
        if (!_enableEviction) return;
        try
        {
            var now = Stopwatch.GetTimestamp();
            int sampleCapacity = _options.NumTestsPerEvictionRun < 1 ? 1 : _options.NumTestsPerEvictionRun;
            var evictionSample = new HayateObject<T>[sampleCapacity];

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

                    // Decide whether to evict (Stopwatch ticks → seconds conversion)
                    var isExpired = (now - w.CreatedAt) / (double)Stopwatch.Frequency > _options.MaxLifeTime.TotalSeconds;
                    var isIdleTooLong = (now - w.LastReleasedAt) / (double)Stopwatch.Frequency > _options.MaxIdleTime.TotalSeconds;
                    var isSoftIdle = (now - w.LastReleasedAt) / (double)Stopwatch.Frequency > _options.SoftMinEvictableIdleTime.TotalSeconds;

                    // Soft-minimum-idle logic
                    // Only evict soft-idle objects when the idle count exceeds the minimum pool size
                    var shouldEvictSoft = isSoftIdle && shard.Count > _options.MinPoolSize / _shards.Length;

                    if (isExpired || isIdleTooLong || shouldEvictSoft)
                    {
                        // Only the caller that successfully claims may destroy: if the object is currently borrowed (Remove returns false),
                        // it must never be destroyed, or it would corrupt the business thread using it.
                        if (shard.Remove(w))
                        {
                            Destroy(w);
                            evictedCount++;

                            _logger.LogInformation("[Shard {Index}] Evicting object. Type: {Type} Expired: {Expired} IdleTooLong: {IdleTooLong} SoftIdle: {SoftIdle}",
                                shard.Index, typeof(T).Name, isExpired, isIdleTooLong, shouldEvictSoft);
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

    private void ScalingCallback(object state)
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

    private void ValidateCallback(object state)
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
            MinLeaseTimeMs = leaseMin == long.MaxValue ? 0 : leaseMin
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

            configure(_options);
            _options.ApplyFeatureSwitches(); // Force-correct the configuration

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

            _logger.LogInformation("Pool configuration reloaded. Type: {Type} NewConfig: {@Config}", typeof(T).Name, _options);
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
        _evictionTimer?.Dispose();
        _scalingTimer?.Dispose();
        _validationTimer?.Dispose();

        // Release the return-signal gate (same precondition as the timers: no in-flight Acquire waiters at Dispose time)
        _blockGate.Dispose();

        _logger.LogInformation("Object pool disposed. Type: {Type}", typeof(T).Name);
    }

    #endregion
}