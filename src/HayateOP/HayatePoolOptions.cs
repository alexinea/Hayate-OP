using System;
using DotNetCore.HayateOP.Common;

namespace DotNetCore.HayateOP;

/// <summary>
/// Runtime options for the Hayate object pool.
/// </summary>
/// <remarks>
/// These options balance throughput, latency, and resource usage. Time-based options prefer
/// <see cref="TimeSpan"/>; options whose name ends with <c>Ms</c> are expressed in milliseconds.
/// </remarks>
public class HayatePoolOptions
{
    /// <summary>
    /// Minimum number of objects prewarmed in the pool.<br />
    /// Default value: <see cref="HayateConstant.DEFAULT_MIN_POOL_SIZE"/> (5).
    /// </summary>
    /// <remarks>
    /// Purpose: pre-create objects at startup to reduce cold-start jitter.<br />
    /// Special case: setting it to 0 disables prewarming.<br />
    /// Boundary: should be &gt;= 0 and not greater than <see cref="MaxPoolSize"/>.<br />
    /// Recommended range: 0~64.
    /// </remarks>
    public int MinPoolSize { get; set; } = HayateConstant.DEFAULT_MIN_POOL_SIZE;

    /// <summary>
    /// Maximum number of objects the pool is allowed to maintain.<br />
    /// Default value: <see cref="HayateConstant.DEFAULT_MAX_POOL_SIZE"/> (50).
    /// </summary>
    /// <remarks>
    /// Purpose: caps memory and downstream resource usage.<br />
    /// Special case: too small a cap amplifies waiting and timeouts.<br />
    /// Boundary: should be &gt;= 1 and not less than <see cref="MinPoolSize"/>.<br />
    /// Recommended range: 32~2048.
    /// </remarks>
    public int MaxPoolSize { get; set; } = HayateConstant.DEFAULT_MAX_POOL_SIZE;

    #region Timeouts

    /// <summary>
    /// Default borrow timeout.<br />
    /// Default value: <c>TimeSpan.FromSeconds(5)</c>.
    /// </summary>
    /// <remarks>
    /// Purpose: serves as the wait upper bound for the parameterless <c>Acquire</c>.<br />
    /// Special case: under the blocking policy, a timeout triggers the reject-policy branch.<br />
    /// Boundary: should be greater than <see cref="TimeSpan.Zero"/>.<br />
    /// Recommended range: 1~30 seconds.
    /// </remarks>
    public TimeSpan DefaultAcquireTimeout { get; set; } = TimeSpan.FromSeconds(HayateConstant.DEFAULT_ACQUIRE_TIMEOUT_SECONDS);

    #endregion

    #region Reject policy

    /// <summary>
    /// Reject policy applied when a borrow fails.<br />
    /// Default value: <see cref="HayatePoolRejectPolicy.BlockTimeout"/>.
    /// </summary>
    /// <remarks>
    /// Purpose: defines the action taken after the wait times out.<br />
    /// Special case: the current implementation has explicit branches for <c>Abort</c>, <c>CreateNew</c>
    /// and <c>CreateOnDemand</c>; other values throw <see cref="InvalidOperationException"/>.<br />
    /// Boundary: must be a valid enum value.<br />
    /// Recommended range: use <c>BlockTimeout</c> for general scenarios; <c>CreateNew</c> is an option for
    /// graceful-degradation fallbacks, and <c>CreateOnDemand</c> for callers that expect the reference
    /// <c>DefaultObjectPool</c> behaviour of "create on a miss instead of blocking".
    /// </remarks>
    public HayatePoolRejectPolicy RejectPolicy { get; set; } = HayatePoolRejectPolicy.BlockTimeout;

    #endregion

    #region Creation retry

    /*
     * Creation retry
     */

    /// <summary>
    /// Maximum number of retries after a creation failure.<br />
    /// Default value: <see cref="HayateConstant.DEFAULT_CREATION_RETRY_COUNT"/> (3).
    /// </summary>
    /// <remarks>
    /// Purpose: improves creation success rate under transient failures.<br />
    /// Special case: setting it to 0 fails immediately without retrying.<br />
    /// Boundary: should be &gt;= 0.<br />
    /// Recommended range: 1~5.
    /// </remarks>
    public int CreationRetryCount { get; set; } = HayateConstant.DEFAULT_CREATION_RETRY_COUNT;

    /// <summary>
    /// Delay between creation retries.<br />
    /// Default value: <c>TimeSpan.FromMilliseconds(100)</c> (currently uses
    /// <see cref="HayateConstant.DEFAULT_CREATION_RETRY_DELAY_MILLISECONDS"/>).
    /// </summary>
    /// <remarks>
    /// Purpose: controls the backoff between consecutive retries.<br />
    /// Special case: the current default is relatively large (30s), which differs from the common
    /// expectation for a "creation retry delay".<br />
    /// Boundary: should be &gt;= <see cref="TimeSpan.Zero"/>.<br />
    /// Recommended range: 50~1000ms.
    /// </remarks>
    public TimeSpan CreationRetryDelay { get; set; } = TimeSpan.FromMilliseconds(HayateConstant.DEFAULT_CREATION_RETRY_DELAY_MILLISECONDS);

    #endregion

    #region Execution mode

    /// <summary>
    /// Whether to use the lean (wrapper-free) fast path.<br />
    /// Default value: <c>false</c> (the general-purpose sharded engine).
    /// </summary>
    /// <remarks>
    /// Purpose: for pure pooling workloads — objects that need no validation, eviction, leak
    /// forensics, metrics or auto-scaling — the pool can store the pooled value directly in a
    /// bounded array and borrow/return it through <c>Interlocked</c> operations. That removes the
    /// per-object wrapper allocation, the per-shard free-list lock, the registry lookup on return
    /// and every diagnostic call from the borrow/return path.<br />
    /// Special case: lean is a <i>mode</i>, not a knob — enabling it forces
    /// <see cref="EnableSharding"/>, <see cref="EnableAutoScaling"/>, <see cref="EnableValidation"/>,
    /// <see cref="EnableEviction"/>, <see cref="EnableGenerationOptimization"/>,
    /// <see cref="EnableLeakDetection"/>, <see cref="EnableMetrics"/> and
    /// <see cref="EnableAllocationTracking"/> off and clears the capacity-alarm thresholds, because
    /// each of them needs per-object bookkeeping or a background timer that the fast path does not
    /// maintain. The normalized result is produced by <see cref="ApplyFeatureSwitches"/> (also
    /// called from <see cref="IsValid"/>) and is visible through the pool's <c>GetOptions</c>.
    /// Ordering is therefore irrelevant: <c>WithLean()</c> followed by <c>WithEnableMetrics(true)</c>
    /// still ends up in lean mode.<br />
    /// Consequences to be aware of: the lean path keeps no cumulative counters
    /// (<see cref="HayatePoolStats.TotalCreated"/> and friends report 0), rejects
    /// <see cref="HayateEvictReason"/>, cannot resize its retention buffer through
    /// <c>ReloadConfig</c>, and trusts the caller of <c>Release</c> to hand back an object that
    /// really came from this pool.<br />
    /// Boundary: boolean switch.<br />
    /// Recommended range: enable for high-frequency pooling of small, stateless objects; keep
    /// disabled whenever validation, eviction, leak detection or metrics are required.
    /// </remarks>
    /// <example>
    /// <code>
    /// var options = new HayatePoolOptions { EnableLean = true, MinPoolSize = 16, MaxPoolSize = 64 };
    /// </code>
    /// </example>
    public bool EnableLean { get; set; } = false;

    #endregion

    #region Sharding strategy

    /// <summary>
    /// Enables sharding. When disabled, the pool is forced into a single shard and all
    /// shard-related configuration is ignored.
    /// </summary>
    public bool EnableSharding { get; set; } = true;

    /// <summary>
    /// Number of shards. Default value: <see cref="HayateConstant.DEFAULT_SHARD_COUNT"/> (4).
    /// </summary>
    /// <remarks>
    /// Purpose: reduce concurrent contention through multiple shards.<br />
    /// Special case: too many shards raise management cost and amplify prewarm skew.<br />
    /// Boundary: should be &gt;= 1.<br />
    /// Recommended range: 2~16.
    /// </remarks>
    public int ShardCount { get; set; } = HayateConstant.DEFAULT_SHARD_COUNT;

    /// <summary>
    /// Shard affinity mode for the borrow path. Defaults to
    /// <see cref="HayateShardAffinityMode.None"/> (sequential scan, preserving the original
    /// semantics).
    /// </summary>
    /// <remarks>
    /// None: starts scanning from shard 0 (identical to 2.4 and earlier, with zero extra
    /// overhead).<br />
    /// Thread: maps the starting shard stably by managed thread ID (the same thread always prefers
    /// the same shard first, improving cache/handle locality; when the starting shard is busy it
    /// continues scanning the remaining shards ring-wise, without losing availability).<br />
    /// Custom: uses the starting shard index returned by the <see cref="CustomShardAffinity"/>
    /// delegate; when the delegate returns null or an out-of-range value, it falls back to None.
    /// Note: this setting has no effect when sharding is closed (single shard).
    /// </remarks>
    public HayateShardAffinityMode ShardAffinityMode { get; set; } = HayateShardAffinityMode.None;

    /// <summary>
    /// Starting-shard-index delegate for <see cref="HayateShardAffinityMode.Custom"/> mode.
    /// The return value should fall in [0, ShardCount); an out-of-range or null return makes this
    /// borrow fall back to a sequential scan. Only invoked when <see cref="ShardAffinityMode"/> is
    /// Custom (at most once per borrow).
    /// </summary>
    public Func<int> CustomShardAffinity { get; set; }

    #endregion

    #region Scaling strategy

    /// <summary>
    /// Enables automatic scaling. When disabled, the pool size is fixed to MinPoolSize and all
    /// scaling-related configuration is ignored.
    /// </summary>
    public bool EnableAutoScaling { get; set; } = true;

    /// <summary>
    /// Scaling check interval (milliseconds).<br />
    /// Default value: <see cref="HayateConstant.DEFAULT_SCALING_INTERVAL_MILLISECONDS"/> (5000ms).
    /// </summary>
    /// <remarks>
    /// Purpose: controls how often scaling decisions are made.<br />
    /// Special case: too small causes frequent adjustments; too large makes the pool slow to
    /// respond.<br />
    /// Boundary: should be &gt; 0.<br />
    /// Recommended range: 1000~10000ms.
    /// </remarks>
    public int ScalingIntervalMs { get; set; } = HayateConstant.DEFAULT_SCALING_INTERVAL_MILLISECONDS;

    /// <summary>
    /// Scale-up trigger threshold (utilization).<br />
    /// Default value: <see cref="HayateConstant.DEFAULT_SCALE_UP_THRESHOLD"/> (0.8).
    /// </summary>
    /// <remarks>
    /// Purpose: scale up when utilization reaches the threshold.<br />
    /// Special case: too close to <see cref="ScaleDownThreshold"/> causes flapping.<br />
    /// Boundary: should be between 0 and 1, and greater than the scale-down threshold.<br />
    /// Recommended range: 0.70~0.90.
    /// </remarks>
    public double ScaleUpThreshold { get; set; } = HayateConstant.DEFAULT_SCALE_UP_THRESHOLD;

    /// <summary>
    /// Scale-down trigger threshold (utilization).<br />
    /// Default value: <see cref="HayateConstant.DEFAULT_SCALE_DOWN_THRESHOLD"/> (0.2).
    /// </summary>
    /// <remarks>
    /// Purpose: scale down when utilization stays below the threshold for a long time.<br />
    /// Special case: too high causes objects to be destroyed and recreated too often.<br />
    /// Boundary: should be between 0 and 1, and less than the scale-up threshold.<br />
    /// Recommended range: 0.10~0.40.
    /// </remarks>
    public double ScaleDownThreshold { get; set; } = HayateConstant.DEFAULT_SCALE_DOWN_THRESHOLD;
    public int ScaleUpCooldownSeconds { get; set; } = HayateConstant.DEFAULT_SCALE_UP_COOLDOWN_SECONDS;

    public int ScaleDownCooldownSeconds { get; set; } = HayateConstant.DEFAULT_SCALE_DOWN_COOLDOWN_SECONDS;

    public int ScaleUpStep { get; set; } = HayateConstant.DEFAULT_SCALE_UP_STEP;

    /// <summary>
    /// Scale-down step (number of objects removed per scale-down).<br />
    /// Default value: <see cref="HayateConstant.DEFAULT_SCALE_DOWN_STEP"/> (5).
    /// </summary>
    /// <remarks>
    /// Purpose: decoupled from <see cref="ScaleUpStep"/> so callers can tune "aggressive scale-up,
    /// conservative scale-down".<br />
    /// Boundary: must be &gt;= 1; if it exceeds the current available object count, the result is
    /// clamped to <see cref="MinPoolSize"/>.
    /// </remarks>
    public int ScaleDownStep { get; set; } = HayateConstant.DEFAULT_SCALE_DOWN_STEP;

    #endregion

    #region Validation

    /// <summary>
    /// Enables object validation. When disabled, all borrow/return/idle validation logic is
    /// ignored.
    /// </summary>
    public bool EnableValidation { get; set; } = true;

    /// <summary>
    /// Whether to validate object validity before borrowing.<br />
    /// Default value: <c>false</c>.
    /// </summary>
    /// <remarks>
    /// Purpose: filter out invalid objects before <c>Acquire</c>.<br />
    /// Special case: enabling it adds latency to the borrow path.<br />
    /// Boundary: boolean switch.<br />
    /// Recommended range: enable when objects are prone to becoming invalid; disable for pure
    /// in-memory lightweight objects.
    /// </remarks>
    public bool ValidateOnBorrow { get; set; }

    /// <summary>
    /// Whether to validate object validity before returning.<br />
    /// Default value: <c>false</c>.
    /// </summary>
    /// <remarks>
    /// Purpose: filter out abnormal objects at return time.<br />
    /// Special case: the current main flow does not use this switch yet; treat it as a reserved
    /// configuration.<br />
    /// Boundary: boolean switch.<br />
    /// Recommended range: keep the default until the implementation is wired in, then enable per
    /// business needs.
    /// </remarks>
    public bool ValidateOnReturn { get; set; }

    /// <summary>
    /// Whether to periodically validate idle objects (property name kept from the current
    /// implementation).<br />
    /// Default value: <c>false</c>.
    /// </summary>
    /// <remarks>
    /// Purpose: periodically clean up invalid objects in the idle queue.<br />
    /// Special case: the current main flow does not use this switch yet; treat it as a reserved
    /// configuration.<br />
    /// Boundary: boolean switch.<br />
    /// Recommended range: keep the default.
    /// </remarks>
    public bool ValidateWhileIdle { get; set; }

    /// <summary>
    /// Periodic validation interval (milliseconds).<br />
    /// Default value: <see cref="HayateConstant.DEFAULT_VALIDATE_INTERVAL_MILLISECONDS"/> (30000ms).
    /// </summary>
    /// <remarks>
    /// Purpose: controls the frequency of the background validation task.<br />
    /// Special case: even when the relevant switches are off, too small an interval increases timer
    /// wake-up frequency.<br />
    /// Boundary: should be &gt; 0.<br />
    /// Recommended range: 10000~60000ms.
    /// </remarks>
    public int ValidateIntervalMs { get; set; } = HayateConstant.DEFAULT_VALIDATE_INTERVAL_MILLISECONDS;


    #endregion

    #region Eviction

    /// <summary>
    /// Enables idle-object eviction. When disabled, no eviction logic runs and all eviction-related
    /// configuration is ignored.
    /// </summary>
    public bool EnableEviction { get; set; } = true;

    /// <summary>
    /// Maximum object lifetime.<br />
    /// Default value: <c>TimeSpan.FromMinutes(10)</c>.
    /// </summary>
    /// <remarks>
    /// Purpose: cap object lifetime to reduce stale-state risk.<br />
    /// Special case: external-connection objects can use a shorter value.<br />
    /// Boundary: should be greater than <see cref="TimeSpan.Zero"/>.<br />
    /// Recommended range: 5~60 minutes.
    /// </remarks>
    public TimeSpan MaxLifeTime { get; set; } = TimeSpan.FromMinutes(HayateConstant.DEFAULT_MAX_LIFE_TIME_MINUTES);

    /// <summary>
    /// Maximum object idle time.<br />
    /// Default value: <c>TimeSpan.FromMinutes(5)</c>.
    /// </summary>
    /// <remarks>
    /// Purpose: reclaim resources by evicting long-unused objects.<br />
    /// Special case: low-frequency workloads can relax this to reduce recreation.<br />
    /// Boundary: should be &gt;= <see cref="TimeSpan.Zero"/>.<br />
    /// Recommended range: 1~30 minutes.
    /// </remarks>
    public TimeSpan MaxIdleTime { get; set; } = TimeSpan.FromMinutes(HayateConstant.DEFAULT_MAX_IDLE_TIME_MINUTES);

    /// <summary>
    /// Soft minimum evictable idle time.<br />
    /// Default value: <c>TimeSpan.FromMinutes(2)</c>.
    /// </summary>
    /// <remarks>
    /// Purpose: keep objects resident for a minimum idle duration to reduce flapping.<br />
    /// Special case: may still be evicted under resource pressure.<br />
    /// Boundary: should be &gt;= <see cref="TimeSpan.Zero"/> and not greater than
    /// <see cref="MaxIdleTime"/>.<br />
    /// Recommended range: 0.5~10 minutes.
    /// </remarks>
    public TimeSpan SoftMinEvictableIdleTime { get; set; } = TimeSpan.FromMinutes(HayateConstant.DEFAULT_MIN_EVICTION_IDLE_TIME_MINUTES);

    /// <summary>
    /// Eviction scan interval (milliseconds).<br />
    /// Default value: <see cref="HayateConstant.DEFAULT_EVICTION_INTERVAL_MILLISECONDS"/> (30000ms).
    /// </summary>
    /// <remarks>
    /// Purpose: controls how often the eviction task runs.<br />
    /// Special case: scanning too frequently raises CPU and lock contention.<br />
    /// Boundary: should be &gt; 0.<br />
    /// Recommended range: 10000~60000ms.
    /// </remarks>
    public int EvictionIntervalMs { get; set; } = HayateConstant.DEFAULT_EVICTION_INTERVAL_MILLISECONDS;

    /// <summary>
    /// Number of samples scanned per eviction run.<br />
    /// Default value: <see cref="HayateConstant.DEFAULT_EVICTION_RUNS_PER_EVICTION"/> (10).
    /// </summary>
    /// <remarks>
    /// Purpose: controls the cost and cleanup strength of a single eviction.<br />
    /// Special case: too small delays cleanup; too large affects peak latency.<br />
    /// Boundary: should be &gt;= 1.<br />
    /// Recommended range: 5~128.
    /// </remarks>
    public int NumTestsPerEvictionRun { get; set; } = HayateConstant.DEFAULT_EVICTION_RUNS_PER_EVICTION;

    #endregion

    #region Generation

    /// <summary>
    /// Enables generational optimization. When disabled, all objects are treated as the young
    /// generation and validation is never skipped.
    /// </summary>
    public bool EnableGenerationOptimization { get; set; } = true;

    /// <summary>
    /// Threshold (milliseconds) for an object to be promoted to an older generation.<br />
    /// Default value: <see cref="HayateConstant.DEFAULT_GEN_THRESHOLD_MILLISECONDS"/> (30000ms).
    /// </summary>
    /// <remarks>
    /// Purpose: mark long-lived objects so the policy layer can manage generations.<br />
    /// Special case: too small a threshold promotes objects prematurely.<br />
    /// Boundary: should be &gt;= 0.<br />
    /// Recommended range: 5000~120000ms.
    /// </remarks>
    public int GenerationThresholdMs { get; set; } = HayateConstant.DEFAULT_GEN_THRESHOLD_MILLISECONDS;

    /// <summary>
    /// Interval (in regular-validation passes) between full validations of the old generation.
    /// </summary>
    /// <value>
    /// Default value: 3;
    /// Recommended range: 1 ~ 10;
    /// Boundary: minimum 1, no hard maximum (recommended not to exceed 20);
    /// </value>
    /// <remarks>
    /// Core purpose: controls the validation frequency of the old generation. A value of N means
    /// that for every N regular validation passes, the old generation is fully validated once. This
    /// balances the completeness of old-generation validation against performance cost (a full
    /// old-generation validation is relatively expensive, so validating too often reduces overall
    /// throughput).
    ///
    /// Special cases:
    /// 1. When the value is 1: every regular validation pass triggers a full old-generation
    ///    validation. Use this for scenarios with very high consistency requirements and low
    ///    performance sensitivity (e.g. financial core-data checks).
    /// 2. When the value is &lt;= 0: the framework automatically corrects it to the default of 3;
    ///    the old-generation validation cannot be disabled this way (to disable it entirely, set
    ///    OldGenerationValidationEnabled = false separately).
    /// 3. When the value &gt; 10: the validation frequency is too low, which may let
    ///    old-generation anomalies accumulate for too long and increase troubleshooting difficulty.
    ///    Only use this temporarily in pure performance-first, high fault-tolerance scenarios.
    ///
    /// Recommended range:
    /// - General business scenarios (balance performance and validation completeness): 3 ~ 5;
    /// - High-performance, low-consistency scenarios: 6 ~ 10;
    /// - High-consistency, low-performance scenarios: 1 ~ 2;
    /// </remarks>
    public int OldGenerationValidationInterval { get; set; } = HayateConstant.DEFAULT_OLD_GEN_VALIDATION_INTERVAL;

    #endregion

    #region Leak detection

    /// <summary>
    /// Enables object leak detection. When disabled, call stacks are not recorded and no leak scan
    /// runs.
    /// </summary>
    public bool EnableLeakDetection { get; set; } = true;

    /// <summary>
    /// Leak detection threshold.<br />
    /// Default value: <c>TimeSpan.FromMinutes(30)</c> (constructed from
    /// <see cref="HayateConstant.DEFAULT_LEAK_DETECTION_THRESHOLD_SECONDS"/>).
    /// </summary>
    /// <remarks>
    /// Purpose: defines how long a borrowed object can stay out before it is treated as a suspected
    /// leak.<br />
    /// Special case: the constant is named "SECONDS" but the default expression is built in
    /// minutes.<br />
    /// Boundary: should be greater than <see cref="TimeSpan.Zero"/>.<br />
    /// Recommended range: 10 seconds ~ 10 minutes (adjust to actual durations).
    /// </remarks>
    public TimeSpan LeakDetectionThreshold { get; set; } = TimeSpan.FromMinutes(HayateConstant.DEFAULT_LEAK_DETECTION_THRESHOLD_SECONDS);

    /// <summary>
    /// Leak trace capture mode.<br />
    /// Default value: <see cref="HayateLeakTraceCaptureMode.Off"/>.<br />
    /// </summary>
    /// <remarks>
    /// Purpose: controls whether the borrow hot path captures a call stack (for evidence),
    /// decoupled from leak detection itself (threshold judgement + LeakCount).<br />
    /// Behavior change: prior to 2.0, a full stack trace was captured on every borrow by default
    /// (tens of microseconds of CPU / 10~40KB allocated per borrow); starting with 2.1 the default
    /// is <c>Off</c> and LeakTraces holds a placeholder entry. To capture stacks, explicitly choose
    /// <see cref="HayateLeakTraceCaptureMode.Sampled"/> or
    /// <see cref="HayateLeakTraceCaptureMode.EveryAcquire"/>.<br />
    /// Boundary: only takes effect when <see cref="EnableLeakDetection"/> is <c>true</c>.
    /// </remarks>
    public HayateLeakTraceCaptureMode LeakTraceCaptureMode { get; set; } = HayateLeakTraceCaptureMode.Off;

    /// <summary>
    /// Leak-trace sample denominator (1/N).<br />
    /// Default value: <c>1024</c> (constructed from
    /// <see cref="HayateConstant.DEFAULT_LEAK_TRACE_SAMPLE_RATE"/>).
    /// </summary>
    /// <remarks>
    /// Purpose: only effective in <see cref="HayateLeakTraceCaptureMode.Sampled"/> mode; captures a
    /// stack once every N borrows (the first borrow always captures); N=1 is equivalent to
    /// capturing every time.<br />
    /// Boundary: when &lt;= 0, the build/runtime auto-corrects it to the default 1024 (see
    /// <see cref="ApplyFeatureSwitches"/>).
    /// </remarks>
    public int LeakTraceSampleRate { get; set; } = HayateConstant.DEFAULT_LEAK_TRACE_SAMPLE_RATE;

    #endregion

    #region Capacity alarm

    /// <summary>
    /// Capacity warning threshold (utilization, borrowed count / MaxPoolSize).<br />
    /// Default value: <c>0</c> (0 means capacity warnings are disabled, with zero hot-path
    /// overhead).
    /// </summary>
    /// <remarks>
    /// Purpose: when the borrow water level crosses this ratio, fires the
    /// <see cref="OnCapacityWarning"/> callback once.<br />
    /// Special case: edge-debounced — crossing the line fires only once; after falling back below
    /// the threshold and resetting silently, it can fire again.<br />
    /// Boundary: 0 (disabled) or (0, 1]; values &gt; 1 are clamped to 1 by
    /// <see cref="ApplyFeatureSwitches"/>.
    /// </remarks>
    public double WarnAtRatio { get; set; }

    /// <summary>
    /// Capacity critical threshold (utilization, borrowed count / MaxPoolSize).<br />
    /// Default value: <c>0</c> (0 means critical alarms are disabled).
    /// </summary>
    /// <remarks>
    /// Purpose: when the borrow water level crosses this ratio, fires the
    /// <see cref="OnCapacityCritical"/> callback once.<br />
    /// Special case: when enabled together with <see cref="WarnAtRatio"/>, it must not be smaller
    /// than it (normalized by auto-raising to WarnAtRatio); crossing directly from Normal to
    /// Critical fires only the Critical callback, without a supplementary Warning.<br />
    /// Boundary: 0 (disabled) or (0, 1]; values &gt; 1 are clamped to 1 by
    /// <see cref="ApplyFeatureSwitches"/>.
    /// </remarks>
    public double CriticalAtRatio { get; set; }

    /// <summary>
    /// Capacity warning callback (fires once on a state flip when utilization &gt;=
    /// <see cref="WarnAtRatio"/>). Default <c>null</c>.
    /// </summary>
    /// <remarks>
    /// The callback runs inline on the borrow/return path and should stay lightweight (return
    /// within milliseconds); exceptions thrown inside it are caught and logged by the pool and do
    /// not affect the borrow/return main flow.
    /// </remarks>
    public Action<HayatePoolCapacityAlarmEventArgs> OnCapacityWarning { get; set; }

    /// <summary>
    /// Capacity critical callback (fires once on a state flip when utilization &gt;=
    /// <see cref="CriticalAtRatio"/>). Default <c>null</c>.
    /// </summary>
    /// <remarks>
    /// The callback runs inline on the borrow/return path and should stay lightweight (return
    /// within milliseconds); exceptions thrown inside it are caught and logged by the pool and do
    /// not affect the borrow/return main flow.
    /// </remarks>
    public Action<HayatePoolCapacityAlarmEventArgs> OnCapacityCritical { get; set; }

    #endregion

    #region Metrics

    /// <summary>
    /// Whether to enable metrics collection.<br />
    /// Default value: <c>false</c>.
    /// </summary>
    /// <remarks>
    /// Purpose: emit pool runtime metrics for observability.<br />
    /// Special case: enabling it on high-frequency paths adds a small overhead.<br />
    /// Boundary: boolean switch.<br />
    /// Recommended range: enable in test/production; disable under extreme-performance benchmarks.
    /// </remarks>
    public bool EnableMetrics { get; set; } = false;

    /// <summary>
    /// Whether to enable allocation tracking.<br />
    /// Default value: <c>false</c>.
    /// </summary>
    /// <remarks>
    /// Purpose: count the per-thread allocation delta (in bytes) on the borrow/return path, to
    /// locate hidden allocations on the hot path; corroborates the benchmark's <c>B/Op</c> metric.<br />
    /// Special case: each borrow/return makes one extra allocation-query API call (near zero
    /// allocation) and still has a small overhead; the <c>net48</c> / <c>netstandard2.0</c> target
    /// frameworks lack this API, so tracking is silently unavailable there (count stays 0).<br />
    /// Boundary: boolean switch.<br />
    /// Recommended range: enable during diagnosis/tuning; disable on production hot paths.
    /// </remarks>
    public bool EnableAllocationTracking { get; set; } = false;

    #endregion

    #region Warmup readiness

    /// <summary>
    /// Whether to run warmup in the background and have borrow operations wait for warmup to
    /// finish.<br />
    /// Default value: <c>false</c> (consistent with 2.4 and earlier — synchronous warmup at
    /// construction, with no extra wait on borrow).
    /// </summary>
    /// <remarks>
    /// Purpose: when <c>true</c>, construction returns immediately and the <see cref="MinPoolSize"/>
    /// warmup completes on a background thread; before warmup finishes, all <c>Acquire</c> /
    /// <c>AcquireAsync</c> block on the readiness signal, so callers always get a ready pool
    /// (avoiding large creation cost landing on the first business request during cold start).<br />
    /// Special case: mutually exclusive with cold-pool bootstrap — during the wait, the "synchronous
    /// create on empty pool" bootstrap is not triggered; bootstrap yields to the readiness signal;
    /// if warmup fails, the signal is still set (no permanent block) and the failure reason is
    /// logged.<br />
    /// Boundary: boolean switch.<br />
    /// Recommended range: enable for services that can tolerate a small startup delay yet want zero
    /// creation cost on the first request.
    /// </remarks>
    public bool WaitForWarmup { get; set; } = false;

    #endregion

    /// <summary>
    /// Creates a new <see cref="HayatePoolOptions"/> and copies all current settings into it.
    /// </summary>
    /// <returns>A new options instance with the same settings as this one.</returns>
    /// <example>
    /// <code>
    /// var copy = options.CopyTo();
    /// </code>
    /// </example>
    public HayatePoolOptions CopyTo()
    {
        var options = new HayatePoolOptions();
        return CopyTo(options);
    }

    /// <summary>
    /// Copies all current settings into the supplied <paramref name="options"/> instance.
    /// </summary>
    /// <param name="options">The target options instance to copy values into.</param>
    /// <returns>The same <paramref name="options"/> instance, updated with the current settings.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <c>null</c>.</exception>
    /// <example>
    /// <code>
    /// var target = new HayatePoolOptions();
    /// options.CopyTo(target);
    /// </code>
    /// </example>
    public HayatePoolOptions CopyTo(HayatePoolOptions options)
    {
        // Null guard to keep the method robust
        if (options == null)
        {
            throw new ArgumentNullException(nameof(options), "Target options instance cannot be null");
        }

        // Basic pool size
        options.MinPoolSize = this.MinPoolSize;
        options.MaxPoolSize = this.MaxPoolSize;

        // Timeout
        options.DefaultAcquireTimeout = this.DefaultAcquireTimeout;

        // Reject policy
        options.RejectPolicy = this.RejectPolicy;

        // Creation retry
        options.CreationRetryCount = this.CreationRetryCount;
        options.CreationRetryDelay = this.CreationRetryDelay;

        // Execution mode
        options.EnableLean = this.EnableLean;

        // Sharding
        options.EnableSharding = this.EnableSharding;
        options.ShardCount = this.ShardCount;
        options.ShardAffinityMode = this.ShardAffinityMode;
        options.CustomShardAffinity = this.CustomShardAffinity;

        // Scaling
        options.EnableAutoScaling = this.EnableAutoScaling;
        options.ScalingIntervalMs = this.ScalingIntervalMs;
        options.ScaleUpThreshold = this.ScaleUpThreshold;
        options.ScaleDownThreshold = this.ScaleDownThreshold;
        options.ScaleUpCooldownSeconds = this.ScaleUpCooldownSeconds;
        options.ScaleDownCooldownSeconds = this.ScaleDownCooldownSeconds;
        options.ScaleUpStep = this.ScaleUpStep;
        options.ScaleDownStep = this.ScaleDownStep;

        // Validation
        options.EnableValidation = this.EnableValidation;
        options.ValidateOnBorrow = this.ValidateOnBorrow;
        options.ValidateOnReturn = this.ValidateOnReturn;
        options.ValidateWhileIdle = this.ValidateWhileIdle;
        options.ValidateIntervalMs = this.ValidateIntervalMs;

        // Eviction
        options.EnableEviction = this.EnableEviction;
        options.MaxLifeTime = this.MaxLifeTime;
        options.MaxIdleTime = this.MaxIdleTime;
        options.SoftMinEvictableIdleTime = this.SoftMinEvictableIdleTime;
        options.EvictionIntervalMs = this.EvictionIntervalMs;
        options.NumTestsPerEvictionRun = this.NumTestsPerEvictionRun;

        // Generation
        options.EnableGenerationOptimization = this.EnableGenerationOptimization;
        options.GenerationThresholdMs = this.GenerationThresholdMs;
        options.OldGenerationValidationInterval = this.OldGenerationValidationInterval;

        // Leak detection
        options.EnableLeakDetection = this.EnableLeakDetection;
        options.LeakDetectionThreshold = this.LeakDetectionThreshold;
        options.LeakTraceCaptureMode = this.LeakTraceCaptureMode;
        options.LeakTraceSampleRate = this.LeakTraceSampleRate;

        // Capacity alarm
        options.WarnAtRatio = this.WarnAtRatio;
        options.CriticalAtRatio = this.CriticalAtRatio;
        options.OnCapacityWarning = this.OnCapacityWarning;
        options.OnCapacityCritical = this.OnCapacityCritical;

        // Metrics
        options.EnableMetrics = this.EnableMetrics;

        // Allocation tracking
        options.EnableAllocationTracking = this.EnableAllocationTracking;

        // Warmup readiness
        options.WaitForWarmup = this.WaitForWarmup;

        return options;
    }

    //public HayatePoolOptions AutoCopy(HayatePoolOptions options)
    //{
    //    options ??= new();

    //    var properties = typeof(HayatePoolOptions).GetProperties();
    //    foreach (var prop in properties)
    //    {
    //        if (!prop.CanRead || !prop.CanWrite) continue;
    //        prop.SetValue(options, prop.GetValue(this));
    //    }

    //    return options;
    //}

    /// <summary>
    /// Normalizes dependent options so the configuration is internally consistent and passes
    /// <see cref="IsValid"/>.
    /// </summary>
    /// <remarks>
    /// Disabling a feature (sharding, auto-scaling, validation) forces its related settings into a
    /// safe state; sample rates and alarm thresholds are clamped and ordering constraints are
    /// enforced. Call this before building or validating the pool.
    /// </remarks>
    /// <example>
    /// <code>
    /// options.ApplyFeatureSwitches();
    /// if (options.IsValid()) { /* build the pool */ }
    /// </code>
    /// </example>
    public void ApplyFeatureSwitches()
    {
        // Lean fast-path normalization. Declared first so the sharding branch below collapses the
        // pool to a single shard exactly as it would for an explicitly sharding-free pool.
        // Lean stores the pooled value directly in a bounded array and moves it through Interlocked
        // operations, so it structurally has nowhere to keep per-object timestamps, generations,
        // leak forensics, metrics samples, capacity-alarm levels or shard free lists. Rather than
        // rejecting such a combination (which would make builder call order significant), the mode
        // simply wins and the conflicting features are switched off — the normalized configuration
        // is then visible through GetOptions.
        if (EnableLean)
        {
            EnableSharding = false;
            EnableAutoScaling = false;
            EnableValidation = false;
            ValidateOnBorrow = false;
            ValidateOnReturn = false;
            ValidateWhileIdle = false;
            EnableEviction = false;
            EnableGenerationOptimization = false;
            EnableLeakDetection = false;
            EnableMetrics = false;
            EnableAllocationTracking = false;
            WarnAtRatio = 0;
            CriticalAtRatio = 0;
            ShardAffinityMode = HayateShardAffinityMode.None;
        }

        // Sharding disabled: elastic single shard
        if (!EnableSharding)
        {
            ShardCount = 1;
        }

        // Auto-scaling disabled (behavior changed in 2.1):
        // Old semantics forced MaxPoolSize = MinPoolSize, collapsing capacity to 0 when Min=0,
        // and silently rejecting all returns because each shard's max was 0 (same root cause as the
        // historical "minimal pool Min had to be raised to 250" oddity).
        // New semantics: only auto-scaling is disabled (scale-up callbacks and timeout-driven forced
        // scale-up are both gated by EnableAutoScaling and cannot exceed Max), MaxPoolSize keeps the
        // user's explicit value as a hard cap; it is only raised to preserve ordering when Max < Min,
        // avoiding an IsValid validation failure.
        if (!EnableAutoScaling && MaxPoolSize < MinPoolSize)
        {
            MaxPoolSize = MinPoolSize;
        }

        // Validation disabled: force all validation toggles off
        if (!EnableValidation)
        {
            ValidateOnBorrow = false;
            ValidateOnReturn = false;
            ValidateWhileIdle = false;
        }

        // Leak-trace sample-rate guard: values <= 0 are auto-corrected to the default (only used in
        // Sampled mode, must be >= 1)
        if (LeakTraceSampleRate < 1)
        {
            LeakTraceSampleRate = HayateConstant.DEFAULT_LEAK_TRACE_SAMPLE_RATE;
        }

        // Affinity normalization: when Custom mode is selected without a delegate, fall back to None
        // (borrow-path robustness takes priority; we do not reject at the IsValid layer, so a missing
        // strategy cannot fail the entire pool build). Unknown enum values also fall back to None.
        if (ShardAffinityMode == HayateShardAffinityMode.Custom && CustomShardAffinity is null)
        {
            ShardAffinityMode = HayateShardAffinityMode.None;
        }
        else if (ShardAffinityMode != HayateShardAffinityMode.None &&
                 ShardAffinityMode != HayateShardAffinityMode.Thread &&
                 ShardAffinityMode != HayateShardAffinityMode.Custom)
        {
            ShardAffinityMode = HayateShardAffinityMode.None;
        }

        // Capacity alarm threshold normalization: negative values are treated as disabled (0), and
        // values greater than 1 are clamped to 1; when both thresholds are enabled, Critical must
        // not be below Warn (raise it to Warn to keep the state machine monotonic).
        if (WarnAtRatio < 0) WarnAtRatio = 0;
        else if (WarnAtRatio > 1) WarnAtRatio = 1;

        if (CriticalAtRatio < 0) CriticalAtRatio = 0;
        else if (CriticalAtRatio > 1) CriticalAtRatio = 1;

        if (WarnAtRatio > 0 && CriticalAtRatio > 0 && CriticalAtRatio < WarnAtRatio)
        {
            CriticalAtRatio = WarnAtRatio;
        }
    }

    /// <summary>
    /// Validates that the current option values form a legal, buildable configuration.
    /// </summary>
    /// <returns><c>true</c> if the configuration is valid; otherwise <c>false</c>.</returns>
    /// <remarks>
    /// Internally calls <see cref="ApplyFeatureSwitches"/> first, then checks pool-size bounds,
    /// scaling thresholds, time spans and feature-specific constraints.
    /// </remarks>
    /// <example>
    /// <code>
    /// if (!options.IsValid())
    ///     throw new InvalidOperationException("Invalid pool options");
    /// </code>
    /// </example>
    public bool IsValid()
    {
        ApplyFeatureSwitches();

        // Basic configuration checks
        if (MinPoolSize < 0 || MaxPoolSize < MinPoolSize) return false;
        if (DefaultAcquireTimeout <= TimeSpan.Zero) return false;
        if (ShardCount < 1 || ShardCount > 32) return false;
        if (CreationRetryCount < 0) return false;
        if (DefaultAcquireTimeout <= TimeSpan.Zero) return false;

        // Checks when auto-scaling is enabled
        if (EnableAutoScaling)
        {
            if (ScaleUpThreshold <= ScaleDownThreshold) return false;
            if (ScaleUpThreshold < 0 || ScaleUpThreshold > 1) return false;
            if (ScaleDownThreshold < 0 || ScaleDownThreshold > 1) return false;
            if (ScaleUpStep < 1) return false;
            if (ScaleDownStep < 1) return false;
        }

        // Time-based validation
        if (EnableEviction)
        {
            if (MaxLifeTime <= TimeSpan.Zero) return false;
            if (MaxIdleTime <= TimeSpan.Zero) return false;
            if (SoftMinEvictableIdleTime <= TimeSpan.Zero) return false;
        }

        if (EnableSharding)
        {
            if (GenerationThresholdMs < 1000) return false;
            if (OldGenerationValidationInterval < 1) return false;
        }

        if (EnableLeakDetection)
        {
            if (LeakDetectionThreshold <= TimeSpan.Zero) return false;
        }

        return true;
    }
}
