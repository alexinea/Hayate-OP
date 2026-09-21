using System;
using DotNetCore.HayateOP.Common;
using DotNetCore.HayateOP.Logging;
using DotNetCore.HayateOP.Metrics;
using DotNetCore.HayateOP.Policies;
using DotNetCore.HayateOP.Scaling;

namespace DotNetCore.HayateOP;

public class HayatePoolBuilder<T> where T : class
{
    private readonly HayatePoolOptions _options = new();

    // Null means "the library default, not resolved yet" — see Policy.
    private IHayateObjectPolicy<T>? _policy;
    private IHayateScalingStrategy _scalingStrategy;
    private IHayateMetrics _metrics;
    private IHayateLogger _logger;
    private string _poolName;

    // The eviction rule of the maintenance run. Null means "HayateDefaultEvictionPolicy<T>.Instance",
    // i.e. exactly the rule the pool applied before the extension point existed.
    private IHayateEvictionPolicy<T>? _evictionPolicy;

    // Pool-level logger factory and the "explicit logger" flag. Defaults to no factory plus the
    // built-in singleton logger, identical to the behavior before 2.4; an explicit WithLogger
    // takes precedence over the factory (see ResolveLogger).
    private IHayateLoggerFactory? _loggerFactory;
    private bool _loggerExplicitlySet;

    // The shutdown signal used by auto-dispose. Null means "use the process-wide hook"; it is supplied to
    // the pool once at construction and never re-read.
    private IHayateShutdownHook? _shutdownHook;

    public HayatePoolBuilder()
    {
        _scalingStrategy = new ThresholdScalingStrategy();
        _metrics = EmptyHayateMetrics.Instance;
        _logger = new DefaultHayateLogger();
        _poolName = typeof(T).Name;
    }

    /// <summary>
    /// The policy the pool is built with: the one <see cref="WithPolicy"/> set, or the library default.
    /// </summary>
    /// <remarks>
    /// The default is resolved here rather than in the constructor, so that a type without a public
    /// parameterless constructor can still be pooled once a policy is supplied (2.9, B1): the requirement
    /// moved from the generic constraint — which blocked the builder at compile time — to this point, where
    /// it is reported with an actionable message. Nothing is resolved for a builder whose policy was set
    /// explicitly, which is the whole point of the relaxation.
    /// </remarks>
    private IHayateObjectPolicy<T> Policy => _policy ??= HayateDefaultConstruction.Policy<T>();

    #region Feature toggles

    /// <summary>
    /// Enables or disables the lean (wrapper-free) fast path.
    /// </summary>
    /// <param name="enable">Whether to use the lean fast path; defaults to <c>true</c>.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <remarks>
    /// Lean mode is the right choice for high-frequency pooling of small, stateless objects: the
    /// pooled value is stored directly in a bounded array and moved through <c>Interlocked</c>
    /// operations, so the borrow/return path allocates nothing and takes no lock.
    /// Because lean is a mode rather than a knob, it takes precedence over the feature toggles:
    /// sharding, auto-scaling, validation, eviction, generation optimization, leak detection,
    /// metrics, allocation tracking and the capacity alarm are all switched off during
    /// configuration normalization, whatever order the calls are made in. The normalized result is
    /// visible through <see cref="IHayateObjectPool.GetOptions"/>.
    /// </remarks>
    /// <example>
    /// <code>
    /// var pool = new HayatePoolBuilder&lt;MyResource&gt;()
    ///     .WithLean()
    ///     .WithMinSize(16)
    ///     .WithMaxSize(64)
    ///     .Build();
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithLean(bool enable = true)
    {
        _options.EnableLean = enable;
        return this;
    }

    /// <summary>
    /// Switches the lean buffer to the ArrayPool direct-storage backend (O-D): the slot array is
    /// rented from <c>ArrayPool{T}</c> and grows on demand up to
    /// <see cref="HayatePoolOptions.MaxPoolSize"/>, instead of occupying <c>MaxPoolSize</c> slots
    /// for the pool's whole lifetime. Implies <see cref="WithLean"/>; the borrow/return semantics
    /// are identical to lean mode (same ceiling, same wrapper-free fast path). Available on net6.0
    /// and above; on older targets the flag is ignored and lean keeps its fixed buffer.
    /// </summary>
    /// <param name="enable">Whether to use the ArrayPool storage backend; defaults to
    /// <c>true</c>.</param>
    /// <returns>The same builder instance for chaining.</returns>
    public HayatePoolBuilder<T> WithArrayPoolStorage(bool enable = true)
    {
        _options.EnableLean = true;
        _options.EnableArrayPoolStorage = enable;
        return this;
    }

    /// <summary>
    /// Applies the lean profile: the wrapper-free fast path with every bookkeeping feature switched off,
    /// so a pure pooling workload runs at the reference <c>DefaultObjectPool</c> cost.
    /// </summary>
    /// <returns>The same builder instance for chaining.</returns>
    /// <remarks>
    /// The one-call equivalent of <see cref="WithLean"/> plus the explicit suppression of sharding,
    /// auto-scaling, validation, eviction, generation optimization, leak detection, metrics, allocation
    /// tracking and the capacity alarm. Pool sizing, timeouts and the reject policy are not part of the
    /// profile, so they can be configured before or after it, and the profile deliberately loses to a
    /// later <see cref="WithFullProfile"/> call. See
    /// <see cref="HayatePoolOptions.UseLeanProfile"/> for the exact field set.
    /// </remarks>
    /// <example>
    /// <code>
    /// var pool = new HayatePoolBuilder&lt;MyResource&gt;()
    ///     .WithLeanProfile()
    ///     .WithMinSize(16)
    ///     .WithMaxSize(64)
    ///     .Build();
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithLeanProfile()
    {
        _options.UseLeanProfile();
        return this;
    }

    /// <summary>
    /// Applies the full profile: every optional feature switch turned on (sharding, auto-scaling,
    /// validation, eviction, generation optimization, leak detection, metrics and allocation tracking).
    /// </summary>
    /// <returns>The same builder instance for chaining.</returns>
    /// <remarks>
    /// The exact opposite of <see cref="WithLeanProfile"/>: it clears the lean mode, so applying it after
    /// the lean profile leaves a full-featured pool. Numeric thresholds, intervals and the validation
    /// sub-switches keep their documented defaults. A later feature call still overrides the profile —
    /// for example <c>WithFullProfile().WithEnableAllocationTracking(false)</c> leaves the diagnostic
    /// switch off. See <see cref="HayatePoolOptions.UseFullProfile"/> for the exact field set.
    /// </remarks>
    /// <example>
    /// <code>
    /// var pool = new HayatePoolBuilder&lt;MyResource&gt;()
    ///     .WithFullProfile()
    ///     .WithMaxSize(256)
    ///     .Build();
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithFullProfile()
    {
        _options.UseFullProfile();
        return this;
    }

    /// <summary>
    /// Applies a named configuration preset.
    /// </summary>
    /// <param name="preset">The preset to apply.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="preset"/> is not a defined preset.</exception>
    /// <remarks>
    /// The wide-surface counterpart of <see cref="WithLeanProfile"/> and <see cref="WithFullProfile"/>: a
    /// preset assigns the options it owns and leaves every other option at the value it already had, so a
    /// later feature call still overrules it — <c>WithPreset(ConnectionPool).WithMaxSize(32)</c> keeps the
    /// preset's validation and reclaim behaviour and changes only the ceiling. See
    /// <see cref="HayatePoolPreset"/> for the field set each preset owns.
    /// </remarks>
    /// <example>
    /// <code>
    /// var pool = new HayatePoolBuilder&lt;MyResource&gt;()
    ///     .WithPreset(HayatePoolPreset.HighThroughput)
    ///     .WithMaxSize(256)
    ///     .Build();
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithPreset(HayatePoolPreset preset)
    {
        _options.UsePreset(preset);
        return this;
    }

    /// <summary>
    /// Applies a caller-supplied preset: every option is taken from <paramref name="preset"/>.
    /// </summary>
    /// <param name="preset">The options to take the configuration from.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="preset"/> is <c>null</c>.</exception>
    /// <remarks>
    /// The extension point of the preset catalogue: a team can keep its own named configuration beside
    /// <see cref="HayatePoolPreset"/> and apply it the same way. Unlike the enumerated presets this
    /// overload replaces the whole option surface — that is what "my preset" means — and a later feature
    /// call still overrules it. The preset is copied, so mutating it afterwards does not reconfigure the
    /// pool, and the builder-side registrations (<see cref="WithPolicy"/>, <see cref="WithMetrics"/>,
    /// <see cref="WithLogger"/>, the pool name) are not part of the options and stay untouched.
    /// </remarks>
    /// <example>
    /// <code>
    /// var options = new HayatePoolOptions { MaxPoolSize = 128 }.UseFullProfile();
    /// var pool = new HayatePoolBuilder&lt;MyResource&gt;().WithPreset(options).Build();
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithPreset(HayatePoolOptions preset)
    {
        (preset ?? throw new ArgumentNullException(nameof(preset))).CopyTo(_options);
        return this;
    }

    /// <summary>
    /// Enables or disables sharding.
    /// </summary>
    /// <param name="enable">Whether to enable sharding; defaults to <c>true</c>.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;().WithEnableSharding(false);
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithEnableSharding(bool enable = true)
    {
        _options.EnableSharding = enable;
        return this;
    }

    /// <summary>
    /// Enables or disables automatic scaling.
    /// </summary>
    /// <param name="enable">Whether to enable automatic scaling; defaults to <c>true</c>.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;().WithEnableAutoScaling(false);
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithEnableAutoScaling(bool enable = true)
    {
        _options.EnableAutoScaling = enable;
        return this;
    }

    /// <summary>
    /// Enables or disables object validation.
    /// </summary>
    /// <param name="enable">Whether to enable validation; defaults to <c>true</c>.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;().WithEnableValidation(false);
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithEnableValidation(bool enable = true)
    {
        _options.EnableValidation = enable;
        if (!enable)
        {
            _options.ValidateOnBorrow = false;
            _options.ValidateOnReturn = false;
        }

        return this;
    }

    /// <summary>
    /// Enables or disables idle-object eviction.
    /// </summary>
    /// <param name="enable">Whether to enable eviction; defaults to <c>true</c>.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;().WithEnableEviction(false);
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithEnableEviction(bool enable = true)
    {
        _options.EnableEviction = enable;
        return this;
    }

    /// <summary>
    /// Enables or disables generational optimization.
    /// </summary>
    /// <param name="enable">Whether to enable generational optimization; defaults to <c>true</c>.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;().WithEnableGenerationOptimization(false);
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithEnableGenerationOptimization(bool enable = true)
    {
        _options.EnableGenerationOptimization = enable;
        return this;
    }

    /// <summary>
    /// Enables or disables leak detection.
    /// </summary>
    /// <param name="enable">Whether to enable leak detection; defaults to <c>true</c>.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;().WithEnableLeakDetection(false);
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithEnableLeakDetection(bool enable = true)
    {
        _options.EnableLeakDetection = enable;
        return this;
    }

    /// <summary>
    /// Enables or disables the pool's whole diagnostic surface — the cumulative counters, the timing
    /// statistics, the <c>IHayateMetrics</c> sink and the per-operation debug trace. On by default.
    /// </summary>
    /// <param name="enable">Whether to keep diagnostics on; defaults to <c>true</c>.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <remarks>
    /// It is a master gate rather than a peer of <see cref="WithEnableMetrics"/>: switching it off also
    /// normalizes metrics and allocation tracking off, and it is the only way to stop
    /// <c>TotalAcquired</c>, which the metrics switch deliberately leaves running. Registering a custom
    /// <c>IHayateMetrics</c> with diagnostics off fails the build, so a sink can never be silently
    /// discarded. Sizing, timeouts and the reject policy are untouched; the lean fast path forces the
    /// switch off.
    /// </remarks>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;().WithEnableDiagnostics(false);
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithEnableDiagnostics(bool enable = true)
    {
        _options.EnableDiagnostics = enable;
        return this;
    }

    /// <summary>
    /// Enables or disables metrics collection.
    /// </summary>
    /// <param name="enable">Whether to enable metrics; defaults to <c>true</c>.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;().WithEnableMetrics(true);
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithEnableMetrics(bool enable = true)
    {
        _options.EnableMetrics = enable;
        return this;
    }

    /// <summary>
    /// Sets whether to wait for warmup to complete. When <c>true</c>, warmup runs in the background
    /// and borrow operations block until warmup finishes.
    /// </summary>
    /// <param name="wait">Whether to wait for warmup; defaults to <c>false</c> (synchronous warmup
    /// inside the constructor, with no extra wait on borrow).</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;().WithWaitForWarmup(true);
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithWaitForWarmup(bool wait = true)
    {
        _options.WaitForWarmup = wait;
        return this;
    }

    /// <summary>
    /// Sets whether to enable allocation tracking. When enabled, <c>GetStats()</c> /
    /// <c>TakeSnapshot()</c> expose the per-path allocation byte delta and sample count for the
    /// borrow and return paths.
    /// </summary>
    /// <param name="enable">Whether to enable allocation tracking; defaults to <c>true</c>.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;().WithEnableAllocationTracking(true);
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithEnableAllocationTracking(bool enable = true)
    {
        _options.EnableAllocationTracking = enable;
        return this;
    }

    /// <summary>
    /// Sets the capacity alarm thresholds. Utilization is measured as borrowed count / MaxPoolSize.
    /// </summary>
    /// <param name="warnAtRatio">Warning threshold (0~1; 0 disables the warning tier).</param>
    /// <param name="criticalAtRatio">Critical threshold (0~1; 0 disables the critical tier; when
    /// both tiers are enabled it must not be smaller than the warning threshold, and is auto-corrected
    /// if out of range).</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="warnAtRatio"/> or
    /// <paramref name="criticalAtRatio"/> is outside the range [0, 1], or <paramref name="criticalAtRatio"/>
    /// is smaller than <paramref name="warnAtRatio"/> while both are enabled.</exception>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;()
    ///     .WithCapacityAlarm(0.8, 0.95);
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithCapacityAlarm(double warnAtRatio, double criticalAtRatio = 0)
    {
        if (warnAtRatio < 0 || warnAtRatio > 1)
            throw new ArgumentOutOfRangeException(nameof(warnAtRatio), "WarnAtRatio must be between 0 and 1");
        if (criticalAtRatio < 0 || criticalAtRatio > 1)
            throw new ArgumentOutOfRangeException(nameof(criticalAtRatio), "CriticalAtRatio must be between 0 and 1");
        if (warnAtRatio > 0 && criticalAtRatio > 0 && criticalAtRatio < warnAtRatio)
            throw new ArgumentOutOfRangeException(nameof(criticalAtRatio), "CriticalAtRatio must be >= WarnAtRatio when both are enabled");

        _options.WarnAtRatio = warnAtRatio;
        _options.CriticalAtRatio = criticalAtRatio;
        return this;
    }

    /// <summary>
    /// Sets the capacity warning callback (fires once on a state flip when utilization &gt;=
    /// WarnAtRatio).
    /// </summary>
    /// <param name="handler">The callback invoked when the warning threshold is crossed.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="handler"/> is <c>null</c>.</exception>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;()
    ///     .WithOnCapacityWarning(args =&gt; Log.Warn("pool near capacity"));
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithOnCapacityWarning(Action<HayatePoolCapacityAlarmEventArgs> handler)
    {
        _options.OnCapacityWarning = handler ?? throw new ArgumentNullException(nameof(handler));
        return this;
    }

    /// <summary>
    /// Sets the capacity critical callback (fires once on a state flip when utilization &gt;=
    /// CriticalAtRatio).
    /// </summary>
    /// <param name="handler">The callback invoked when the critical threshold is crossed.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="handler"/> is <c>null</c>.</exception>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;()
    ///     .WithOnCapacityCritical(args =&gt; Log.Error("pool at capacity"));
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithOnCapacityCritical(Action<HayatePoolCapacityAlarmEventArgs> handler)
    {
        _options.OnCapacityCritical = handler ?? throw new ArgumentNullException(nameof(handler));
        return this;
    }

    #endregion

    #region Circuit breaker

    /// <summary>
    /// Enables or disables the pool-level availability circuit breaker.
    /// </summary>
    /// <param name="enable">Whether to enable the circuit breaker; defaults to <c>true</c>.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <remarks>
    /// When enabled, the application reports dependency failures with <c>SetUnavailable</c>; after
    /// <see cref="HayateCircuitBreakerOptions.FailureThreshold"/> consecutive reports the pool marks itself
    /// unavailable and every further <c>Acquire</c> / <c>AcquireAsync</c> fails immediately with a
    /// <see cref="HayatePoolUnavailableException"/> instead of handing out objects that are likely to be
    /// broken. Recovery happens through the configured probe or through an explicit <c>SetAvailable</c>.
    /// The switch is fixed at build time: it cannot be turned on or off through <c>ReloadConfig</c>.
    /// </remarks>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;().WithEnableCircuitBreaker();
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithEnableCircuitBreaker(bool enable = true)
    {
        _options.EnableCircuitBreaker = enable;
        return this;
    }

    /// <summary>
    /// Configures the circuit breaker in one call: threshold, trip windows and the recovery probe.
    /// </summary>
    /// <param name="failureThreshold">Consecutive failure reports that trip the breaker (values below 1 are normalized to the default by configuration normalization).</param>
    /// <param name="resetTimeout">How long the pool stays unavailable before the first probe runs.</param>
    /// <param name="probeInterval">Interval between probes once <paramref name="resetTimeout"/> has elapsed.</param>
    /// <param name="probe">The background probe deciding whether the dependency is healthy again; <c>null</c> keeps recovery manual through <c>SetAvailable</c>.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <remarks>
    /// Assigns a fresh <see cref="HayateCircuitBreakerOptions"/> built from the arguments, replacing the
    /// default instance; it also enables the feature, so <c>WithEnableCircuitBreaker</c> is not required
    /// before it. The overload taking an options instance preserves the given object instead.
    /// </remarks>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;()
    ///     .WithCircuitBreaker(3, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5), () => database.Ping());
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithCircuitBreaker(
        int failureThreshold,
        TimeSpan resetTimeout,
        TimeSpan probeInterval,
        Func<bool>? probe = null)
    {
        _options.CircuitBreaker = new HayateCircuitBreakerOptions
        {
            FailureThreshold = failureThreshold,
            ResetTimeout = resetTimeout,
            ProbeInterval = probeInterval,
            Probe = probe!
        };
        _options.EnableCircuitBreaker = true;
        return this;
    }

    /// <summary>
    /// Assigns the circuit-breaker settings and enables the feature.
    /// </summary>
    /// <param name="options">The settings to assign; <c>null</c> restores the default settings (threshold 3, 30 s reset timeout, 5 s probe interval, no probe).</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;()
    ///     .WithCircuitBreaker(new HayateCircuitBreakerOptions { FailureThreshold = 1 });
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithCircuitBreaker(HayateCircuitBreakerOptions options)
    {
        _options.CircuitBreaker = options ?? new HayateCircuitBreakerOptions();
        _options.EnableCircuitBreaker = true;
        return this;
    }

    /// <summary>
    /// Sets the callback invoked when the pool becomes available again (the breaker closes).
    /// </summary>
    /// <param name="handler">The callback invoked once per recovery transition.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="handler"/> is <c>null</c>.</exception>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;()
    ///     .WithOnAvailable(args => Log.Information("pool recovered"));
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithOnAvailable(Action<HayatePoolAvailabilityEventArgs> handler)
    {
        _options.OnAvailable = handler ?? throw new ArgumentNullException(nameof(handler));
        return this;
    }

    /// <summary>
    /// Sets the callback invoked when the pool becomes unavailable (the breaker trips).
    /// </summary>
    /// <param name="handler">The callback invoked once per trip transition.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="handler"/> is <c>null</c>.</exception>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;()
    ///     .WithOnUnavailable(args => Log.Error("pool out of service: {Reason}", args.Reason));
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithOnUnavailable(Action<HayatePoolAvailabilityEventArgs> handler)
    {
        _options.OnUnavailable = handler ?? throw new ArgumentNullException(nameof(handler));
        return this;
    }

    #endregion

    #region Basic configuration

    /// <summary>
    /// Sets the pool name.
    /// </summary>
    /// <param name="name">The name used for logging and metrics.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <c>null</c>.</exception>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;().WithPoolName("orders");
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithPoolName(string name)
    {
        _poolName = name ?? throw new ArgumentNullException(nameof(name));
        return this;
    }

    /// <summary>
    /// Sets the minimum pool size.
    /// </summary>
    /// <param name="minSize">The minimum number of objects to keep in the pool.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="minSize"/> is negative.</exception>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;().WithMinSize(10);
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithMinSize(int minSize)
    {
        if (minSize < 0) throw new ArgumentOutOfRangeException(nameof(minSize), "MinSize cannot be negative");
        _options.MinPoolSize = minSize;
        return this;
    }

    /// <summary>
    /// Sets the maximum pool size.
    /// </summary>
    /// <param name="maxSize">The maximum number of objects the pool may hold.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxSize"/> is negative.</exception>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;().WithMaxSize(256);
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithMaxSize(int maxSize)
    {
        if (maxSize < 0) throw new ArgumentOutOfRangeException(nameof(maxSize), "MaxSize cannot be negative");
        _options.MaxPoolSize = maxSize;
        return this;
    }

    /// <summary>
    /// Sets the soft capacity: how many idle objects the pool keeps before it stops retaining returns.
    /// Zero — the default — disables the ceiling.
    /// </summary>
    /// <param name="softCapacity">The idle-object ceiling applied on the return path; <c>0</c> disables
    /// it.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="softCapacity"/> is negative.</exception>
    /// <remarks>
    /// A returned object is destroyed instead of stored once the pool already holds this many idle
    /// objects, so the retained set stays bounded without lowering the number of objects the pool will
    /// lend out. The value must lie between <see cref="HayatePoolOptions.MinPoolSize"/> and
    /// <see cref="HayatePoolOptions.MaxPoolSize"/> when it is not zero; a ceiling outside that range is
    /// rejected at build time because it could never take effect.
    /// </remarks>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;()
    ///     .WithMaxSize(64)
    ///     .WithSoftCapacity(8);
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithSoftCapacity(int softCapacity)
    {
        if (softCapacity < 0) throw new ArgumentOutOfRangeException(nameof(softCapacity), "SoftCapacity cannot be negative");
        _options.SoftCapacity = softCapacity;
        return this;
    }

    /// <summary>
    /// Sets the number of shards.
    /// </summary>
    /// <param name="shardCount">The number of shards (1~32).</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="shardCount"/> is outside the
    /// range [1, 32].</exception>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;().WithShardCount(8);
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithShardCount(int shardCount)
    {
        if (shardCount < 1 || shardCount > 32)
            throw new ArgumentOutOfRangeException(nameof(shardCount), "ShardCount must be between 1 and 32");
        _options.ShardCount = shardCount;
        return this;
    }

    /// <summary>
    /// Sets the shard affinity mode (None = default sequential scan / Thread = thread affinity /
    /// Custom = custom delegate). When <see cref="HayateShardAffinityMode.Custom"/> is passed, you
    /// must subsequently call <see cref="WithCustomShardAffinity(Func{int})"/>, otherwise it falls
    /// back to None at build time.
    /// </summary>
    /// <param name="mode">The shard affinity mode to use.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is not one of the
    /// supported affinity modes.</exception>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;()
    ///     .WithShardAffinity(HayateShardAffinityMode.Thread);
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithShardAffinity(HayateShardAffinityMode mode)
    {
        if (mode != HayateShardAffinityMode.None &&
            mode != HayateShardAffinityMode.Thread &&
            mode != HayateShardAffinityMode.Custom)
        {
            throw new ArgumentOutOfRangeException(nameof(mode), "Unknown shard affinity mode");
        }
        _options.ShardAffinityMode = mode;
        return this;
    }

    /// <summary>
    /// Sets the custom starting-shard delegate and switches into Custom mode. The return value should
    /// fall in [0, ShardCount); a value outside that range falls back to a sequential scan for that
    /// borrow, silently (no log). If the delegate throws, the borrow falls back as well, but the
    /// exception is logged. The delegate cannot return null -- to express "no affinity for this
    /// borrow", return an out-of-range value such as -1.
    /// </summary>
    /// <param name="shardSelector">The delegate that returns the preferred starting shard index.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="shardSelector"/> is <c>null</c>.</exception>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;()
    ///     .WithCustomShardAffinity(() =&gt; Thread.CurrentThread.ManagedThreadId % 8);
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithCustomShardAffinity(Func<int> shardSelector)
    {
        _options.CustomShardAffinity = shardSelector ?? throw new ArgumentNullException(nameof(shardSelector));
        _options.ShardAffinityMode = HayateShardAffinityMode.Custom;
        return this;
    }

    /// <summary>
    /// Sets the end of the idle list a borrow is served from: <see cref="HayateBorrowStrategy.Fifo"/>
    /// (the default) hands out the oldest returned object, <see cref="HayateBorrowStrategy.Lifo"/> the
    /// most recently returned one.
    /// </summary>
    /// <param name="strategy">The borrow order to use.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="strategy"/> is not one of the
    /// supported borrow strategies.</exception>
    /// <remarks>
    /// Both strategies use the same idle list and the same eviction behaviour, so this switches the
    /// order of borrows only. It is not available on the lean fast path, which keeps no ordered idle
    /// list — asking for Lifo there fails the build rather than being ignored.
    /// </remarks>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;()
    ///     .WithBorrowStrategy(HayateBorrowStrategy.Lifo);
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithBorrowStrategy(HayateBorrowStrategy strategy)
    {
        if (strategy != HayateBorrowStrategy.Fifo && strategy != HayateBorrowStrategy.Lifo)
        {
            throw new ArgumentOutOfRangeException(nameof(strategy), "Unknown borrow strategy");
        }
        _options.BorrowStrategy = strategy;
        return this;
    }

    #endregion

    #region Timeout

    /// <summary>
    /// Sets the default acquire timeout.
    /// </summary>
    /// <param name="timeout">The maximum time to wait for a borrow.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is not greater than
    /// <see cref="TimeSpan.Zero"/>.</exception>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;()
    ///     .WithAcquireTimeout(TimeSpan.FromSeconds(3));
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithAcquireTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be greater than zero");
        _options.DefaultAcquireTimeout = timeout;
        return this;
    }

    #endregion

    #region Scaling

    /// <summary>
    /// Sets the scaling check interval.
    /// </summary>
    /// <param name="intervalMs">The scaling decision interval in milliseconds (minimum 100).</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="intervalMs"/> is below 100ms.</exception>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;().WithScalingInterval(2000);
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithScalingInterval(int intervalMs)
    {
        if (intervalMs < 100)
            throw new ArgumentOutOfRangeException(nameof(intervalMs), "ScalingInterval must be at least 100ms");
        _options.ScalingIntervalMs = intervalMs;
        return this;
    }

    /// <summary>
    /// Sets the scale-up threshold (utilization).
    /// </summary>
    /// <param name="threshold">The utilization ratio (0~1) above which the pool scales up.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="threshold"/> is outside the
    /// range [0, 1].</exception>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;().WithScaleUpThreshold(0.85);
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithScaleUpThreshold(double threshold)
    {
        if (threshold < 0 || threshold > 1)
            throw new ArgumentOutOfRangeException(nameof(threshold), "ScaleUpThreshold must be between 0 and 1");
        _options.ScaleUpThreshold = threshold;
        return this;
    }

    /// <summary>
    /// Sets the scale-down threshold (utilization).
    /// </summary>
    /// <param name="threshold">The utilization ratio (0~1) below which the pool scales down.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="threshold"/> is outside the
    /// range [0, 1].</exception>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;().WithScaleDownThreshold(0.2);
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithScaleDownThreshold(double threshold)
    {
        if (threshold < 0 || threshold > 1)
            throw new ArgumentOutOfRangeException(nameof(threshold), "ScaleDownThreshold must be between 0 and 1");
        _options.ScaleDownThreshold = threshold;
        return this;
    }

    /// <summary>
    /// Sets the scale-up cooldown (seconds).
    /// </summary>
    /// <param name="seconds">The cooldown in seconds before another scale-up is allowed.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="seconds"/> is negative.</exception>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;().WithScaleUpCooldownSeconds(30);
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithScaleUpCooldownSeconds(int seconds)
    {
        if (seconds < 0)
            throw new ArgumentOutOfRangeException(nameof(seconds), "ScaleUpCooldownSeconds cannot be negative");
        _options.ScaleUpCooldownSeconds = seconds;
        return this;
    }

    /// <summary>
    /// Sets the scale-down cooldown (seconds).
    /// </summary>
    /// <param name="seconds">The cooldown in seconds before another scale-down is allowed.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="seconds"/> is negative.</exception>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;().WithScaleDownCooldownSeconds(60);
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithScaleDownCooldownSeconds(int seconds)
    {
        if (seconds < 0)
            throw new ArgumentOutOfRangeException(nameof(seconds), "ScaleDownCooldownSeconds cannot be negative");
        _options.ScaleDownCooldownSeconds = seconds;
        return this;
    }

    /// <summary>
    /// Sets the scale-up step.
    /// </summary>
    /// <param name="step">The number of objects added per scale-up (minimum 1).</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="step"/> is below 1.</exception>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;().WithScaleUpStep(4);
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithScaleUpStep(int step)
    {
        if (step < 1)
            throw new ArgumentOutOfRangeException(nameof(step), "ScaleUpStep must be at least 1");
        _options.ScaleUpStep = step;
        return this;
    }

    /// <summary>
    /// Sets the scale-down step.
    /// </summary>
    /// <param name="step">The number of objects removed per scale-down (minimum 1).</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="step"/> is below 1.</exception>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;().WithScaleDownStep(2);
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithScaleDownStep(int step)
    {
        if (step < 1)
            throw new ArgumentOutOfRangeException(nameof(step), "ScaleDownStep must be at least 1");
        _options.ScaleDownStep = step;
        return this;
    }

    #endregion

    #region Validation

    /// <summary>
    /// Sets whether to validate objects on borrow.
    /// </summary>
    /// <param name="enable">Whether to validate before borrowing; defaults to <c>true</c>.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;().WithValidateOnBorrow(true);
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithValidateOnBorrow(bool enable = true)
    {
        if (_options.EnableValidation)
            _options.ValidateOnBorrow = enable;
        return this;
    }

    /// <summary>
    /// Sets whether to validate objects on return.
    /// </summary>
    /// <param name="enable">Whether to validate before returning; defaults to <c>true</c>.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;().WithValidateOnReturn(true);
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithValidateOnReturn(bool enable = true)
    {
        if (_options.EnableValidation)
            _options.ValidateOnReturn = enable;
        return this;
    }

    /// <summary>
    /// Sets whether to validate objects while idle.
    /// </summary>
    /// <param name="enable">Whether to validate idle objects; defaults to <c>true</c>.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;().WithValidateWhileIdle(true);
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithValidateWhileIdle(bool enable = true)
    {
        if (_options.EnableValidation)
            _options.ValidateWhileIdle = enable;
        return this;
    }

    /// <summary>
    /// Sets the idle validation interval (milliseconds).
    /// </summary>
    /// <param name="intervalMs">The validation interval in milliseconds (minimum 1000).</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="intervalMs"/> is below 1000ms.</exception>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;().WithValidateInterval(30000);
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithValidateInterval(int intervalMs)
    {
        if (intervalMs < 1000)
            throw new ArgumentOutOfRangeException(nameof(intervalMs), "ValidateInterval must be at least 1000ms");

        if (_options.EnableValidation)
            _options.ValidateIntervalMs = intervalMs;

        return this;
    }

    /// <summary>
    /// Sets the old-generation validation interval (number of passes).
    /// </summary>
    /// <param name="interval">The number of regular validation passes between full old-generation
    /// validations (minimum 1).</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="interval"/> is below 1.</exception>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;().WithOldGenerationValidationInterval(5);
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithOldGenerationValidationInterval(int interval)
    {
        if (interval < 1)
            throw new ArgumentOutOfRangeException(nameof(interval), "OldGenerationValidationInterval must be at least 1");

        if (_options.EnableValidation)
            _options.OldGenerationValidationInterval = interval;

        return this;
    }

    /// <summary>
    /// Sets the generation-promotion threshold (milliseconds).
    /// </summary>
    /// <param name="thresholdMs">The age in milliseconds before an object is promoted to an older
    /// generation (minimum 1000).</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="thresholdMs"/> is below 1000ms.</exception>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;().WithGenerationThreshold(30000);
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithGenerationThreshold(int thresholdMs)
    {
        if (thresholdMs < 1000)
            throw new ArgumentOutOfRangeException(nameof(thresholdMs), "GenerationThreshold must be at least 1000ms");

        if (_options.EnableValidation)
            _options.GenerationThresholdMs = thresholdMs;

        return this;
    }

    #endregion

    #region Eviction

    /// <summary>
    /// Sets the maximum object lifetime.
    /// </summary>
    /// <param name="lifetime">The maximum allowed object lifetime.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lifetime"/> is not greater than
    /// <see cref="TimeSpan.Zero"/>.</exception>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;()
    ///     .WithMaxLifeTime(TimeSpan.FromMinutes(15));
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithMaxLifeTime(TimeSpan lifetime)
    {
        if (lifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lifetime), "MaxLifeTime must be greater than zero");
        _options.MaxLifeTime = lifetime;
        return this;
    }

    /// <summary>
    /// Enables or disables rotating an object that has outlived
    /// <see cref="HayatePoolOptions.MaxLifeTime"/> on the borrow path.
    /// </summary>
    /// <param name="enable">Whether to rotate expired objects when they are borrowed; defaults to
    /// <c>true</c>.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <remarks>
    /// Off by default. With it on, <see cref="HayatePoolOptions.MaxLifeTime"/> also caps what the borrow
    /// path may hand out, instead of limiting idle objects only. Cannot be combined with
    /// <see cref="HayatePoolOptions.EnableLean"/> (the lean fast path keeps no timestamps) — that
    /// combination fails validation when the pool is built.
    /// </remarks>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyConnection&gt;()
    ///     .WithMaxLifeTime(TimeSpan.FromMinutes(5))
    ///     .WithEnableLifetimeRotationOnBorrow();
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithEnableLifetimeRotationOnBorrow(bool enable = true)
    {
        _options.EnableLifetimeRotationOnBorrow = enable;
        return this;
    }

    /// <summary>
    /// Sets the maximum object idle time.
    /// </summary>
    /// <param name="idleTime">The maximum time an object may stay idle before eviction.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="idleTime"/> is not greater than
    /// <see cref="TimeSpan.Zero"/>.</exception>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;()
    ///     .WithMaxIdleTime(TimeSpan.FromMinutes(5));
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithMaxIdleTime(TimeSpan idleTime)
    {
        if (idleTime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(idleTime), "MaxIdleTime must be greater than zero");
        _options.MaxIdleTime = idleTime;
        return this;
    }

    /// <summary>
    /// Sets the soft minimum evictable idle time.
    /// </summary>
    /// <param name="idleTime">The minimum idle duration before an object becomes evictable.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="idleTime"/> is not greater than
    /// <see cref="TimeSpan.Zero"/>.</exception>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;()
    ///     .WithSoftMinEvictableIdleTime(TimeSpan.FromMinutes(2));
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithSoftMinEvictableIdleTime(TimeSpan idleTime)
    {
        if (idleTime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(idleTime), "SoftMinEvictableIdleTime must be greater than zero");
        _options.SoftMinEvictableIdleTime = idleTime;
        return this;
    }

    /// <summary>
    /// Sets the eviction check interval (milliseconds).
    /// </summary>
    /// <param name="intervalMs">The eviction scan interval in milliseconds (minimum 1000).</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="intervalMs"/> is below 1000ms.</exception>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;().WithEvictionInterval(30000);
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithEvictionInterval(int intervalMs)
    {
        if (intervalMs < 1000)
            throw new ArgumentOutOfRangeException(nameof(intervalMs), "EvictionInterval must be at least 1000ms");
        _options.EvictionIntervalMs = intervalMs;
        return this;
    }

    /// <summary>
    /// Sets the number of objects tested per eviction run.
    /// </summary>
    /// <param name="count">The number of samples scanned per eviction run (minimum 1).</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is below 1.</exception>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;().WithNumTestsPerEvictionRun(16);
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithNumTestsPerEvictionRun(int count)
    {
        if (count < 1)
            throw new ArgumentOutOfRangeException(nameof(count), "NumTestsPerEvictionRun must be at least 1");
        _options.NumTestsPerEvictionRun = count;
        return this;
    }

    /// <summary>
    /// Installs the rule the background eviction run applies to idle objects (the pluggable form of
    /// CHOPIN's <c>EvictionPolicyClassName</c>).
    /// </summary>
    /// <param name="policy">The rule to apply. Must not be <c>null</c>.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="policy"/> is <c>null</c>.</exception>
    /// <remarks>
    /// Without this call — and when <see cref="HayateDefaultEvictionPolicy{T}.Instance"/> is passed,
    /// which is the same thing — the pool evaluates
    /// <see cref="HayateDefaultEvictionPolicy{T}"/>: evict on expired lifetime, on exceeded idle time,
    /// or on soft-min idleness above the shard's share of
    /// <see cref="HayatePoolOptions.MinPoolSize"/>. A custom policy replaces that rule for the run
    /// entirely, so a policy that returns <c>false</c> always keeps every idle object (abandoned
    /// recovery and an explicit <see cref="IHayateObjectPool{T}.Evict(HayateEvictReason)"/> still
    /// work — neither goes through the policy).<br />
    /// The policy is code, not configuration, so it is installed here rather than through
    /// <see cref="HayatePoolOptions"/>; a pool built by the dependency-injection or configuration
    /// registrations keeps the default. It is not consulted in lean mode, which runs without idle
    /// eviction, and it is ignored while <see cref="HayatePoolOptions.EnableEviction"/> is off.
    /// </remarks>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;()
    ///     .WithEvictionPolicy(new RetireHeavilyUsedObjects());
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithEvictionPolicy(IHayateEvictionPolicy<T> policy)
    {
        _evictionPolicy = policy ?? throw new ArgumentNullException(nameof(policy));
        return this;
    }

    #endregion

    #region Creation

    /// <summary>
    /// Sets the object creation retry count.
    /// </summary>
    /// <param name="count">The maximum number of retries after a creation failure (minimum 0).</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;().WithCreationRetryCount(3);
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithCreationRetryCount(int count)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count), "CreationRetryCount cannot be negative");
        _options.CreationRetryCount = count;
        return this;
    }

    /// <summary>
    /// Sets the object creation retry delay.
    /// </summary>
    /// <param name="delay">The backoff between creation retries.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="delay"/> is negative.</exception>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;()
    ///     .WithCreationRetryDelay(TimeSpan.FromMilliseconds(200));
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithCreationRetryDelay(TimeSpan delay)
    {
        if (delay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(delay), "CreationRetryDelay cannot be negative");
        _options.CreationRetryDelay = delay;
        return this;
    }

    #endregion

    #region Leak detection

    /// <summary>
    /// Sets the leak detection threshold.
    /// </summary>
    /// <param name="threshold">The maximum time a borrowed object may stay out before it is treated
    /// as a suspected leak.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="threshold"/> is not greater than
    /// <see cref="TimeSpan.Zero"/>.</exception>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;()
    ///     .WithLeakDetectionThreshold(TimeSpan.FromMinutes(5));
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithLeakDetectionThreshold(TimeSpan threshold)
    {
        if (threshold <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(threshold), "LeakDetectionThreshold must be greater than zero");
        _options.LeakDetectionThreshold = threshold;
        return this;
    }

    /// <summary>
    /// Sets whether to enable leak detection.
    /// </summary>
    /// <param name="enable">Whether to enable leak detection; defaults to <c>true</c>.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;().WithLeakDetection(true);
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithLeakDetection(bool enable = true)
    {
        _options.EnableLeakDetection = enable;
        return this;
    }

    /// <summary>
    /// Sets the leak trace capture mode and sample rate.<br />
    /// Defaults to <see cref="HayateLeakTraceCaptureMode.Off"/>: the borrow hot path does not capture
    /// a call stack and leak scanning is unaffected. To restore the pre-2.0 behavior of capturing a
    /// full stack on every borrow, pass <see cref="HayateLeakTraceCaptureMode.EveryAcquire"/>.
    /// </summary>
    /// <param name="mode">The capture mode (Off / Sampled / EveryAcquire).</param>
    /// <param name="sampleRate">The sample denominator (1/N), only used in Sampled mode; values
    /// &lt;= 0 are auto-corrected to the default of 1024.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;()
    ///     .WithLeakTraceCapture(HayateLeakTraceCaptureMode.Sampled, 512);
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithLeakTraceCapture(HayateLeakTraceCaptureMode mode, int sampleRate = HayateConstant.DEFAULT_LEAK_TRACE_SAMPLE_RATE)
    {
        _options.LeakTraceCaptureMode = mode;
        _options.LeakTraceSampleRate = sampleRate;
        return this;
    }

    #endregion

    #region Abandoned recovery

    /// <summary>
    /// Reclaims abandoned objects on the borrow path: every borrow first examines the oldest
    /// outstanding borrows (bounded per borrow) and destroys the ones borrowed past
    /// <see cref="HayatePoolOptions.RemoveAbandonedTimeout"/> — CHOPIN's
    /// <c>RemoveAbandonedOnBorrow</c>.
    /// </summary>
    /// <param name="enable">Whether to enable borrow-path reclamation; defaults to <c>true</c>.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <remarks>
    /// Reclamation destroys a borrowed object and disposes its value while a caller may still hold the
    /// reference, so it is an opt-in (default off): the forensics-only leak surface is the default.
    /// Long-lived leases are never reclaimed unless recovery is explicitly enabled.
    /// </remarks>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;()
    ///     .WithRemoveAbandonedOnBorrow()
    ///     .WithRemoveAbandonedTimeout(TimeSpan.FromMinutes(2));
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithRemoveAbandonedOnBorrow(bool enable = true)
    {
        _options.RemoveAbandonedOnBorrow = enable;
        return this;
    }

    /// <summary>
    /// Reclaims abandoned objects on the background maintenance pass: the shared background timer
    /// scans all outstanding borrows every <see cref="HayatePoolOptions.RemoveAbandonedIntervalMs"/>
    /// milliseconds and destroys the ones borrowed past
    /// <see cref="HayatePoolOptions.RemoveAbandonedTimeout"/> — CHOPIN's
    /// <c>RemoveAbandonedOnMaintenance</c>.
    /// </summary>
    /// <param name="enable">Whether to enable maintenance reclamation; defaults to <c>true</c>.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <remarks>
    /// Same opt-in contract as <see cref="WithRemoveAbandonedOnBorrow"/>: reclamation is destructive
    /// and off by default, so a long-lived lease is never reclaimed unless explicitly enabled.
    /// </remarks>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;()
    ///     .WithRemoveAbandonedOnMaintenance()
    ///     .WithRemoveAbandonedInterval(5000);
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithRemoveAbandonedOnMaintenance(bool enable = true)
    {
        _options.RemoveAbandonedOnMaintenance = enable;
        return this;
    }

    /// <summary>
    /// Sets the abandoned-object judgment timeout.
    /// </summary>
    /// <param name="timeout">How long a borrowed object may stay out before it is treated as abandoned
    /// and eligible for reclamation.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is not greater than
    /// <see cref="TimeSpan.Zero"/>.</exception>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;()
    ///     .WithRemoveAbandonedTimeout(TimeSpan.FromMinutes(5));
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithRemoveAbandonedTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "RemoveAbandonedTimeout must be greater than zero");
        _options.RemoveAbandonedTimeout = timeout;
        return this;
    }

    /// <summary>
    /// Logs a warning (with the captured lease trace, if any) when an abandoned object is reclaimed —
    /// CHOPIN's <c>LogAbandoned</c>.
    /// </summary>
    /// <param name="enable">Whether to log reclamation warnings; defaults to <c>true</c>.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;()
    ///     .WithRemoveAbandonedOnBorrow()
    ///     .WithLogAbandoned();
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithLogAbandoned(bool enable = true)
    {
        _options.LogAbandoned = enable;
        return this;
    }

    /// <summary>
    /// Sets the background maintenance cadence for abandoned recovery.
    /// </summary>
    /// <param name="intervalMs">How often the maintenance pass scans for abandoned objects, in
    /// milliseconds; values &lt;= 0 are floored to 1 ms.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;()
    ///     .WithRemoveAbandonedOnMaintenance()
    ///     .WithRemoveAbandonedInterval(5000);
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithRemoveAbandonedInterval(int intervalMs)
    {
        _options.RemoveAbandonedIntervalMs = intervalMs;
        return this;
    }

    #endregion

    #region Reject policy

    /// <summary>
    /// Sets the reject policy.
    /// </summary>
    /// <param name="policy">The policy to apply when a borrow fails.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;()
    ///     .WithRejectPolicy(HayatePoolRejectPolicy.BlockTimeout);
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithRejectPolicy(HayatePoolRejectPolicy policy)
    {
        _options.RejectPolicy = policy;
        return this;
    }

    #endregion

    #region Dependency injection

    /// <summary>
    /// Sets the object pool policy.
    /// </summary>
    /// <param name="policy">The policy used to create, validate and reset pooled objects.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="policy"/> is <c>null</c>.</exception>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;()
    ///     .WithPolicy(new MyResourcePolicy());
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithPolicy(IHayateObjectPolicy<T> policy)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        return this;
    }

    /// <summary>
    /// Sets the scaling strategy.
    /// </summary>
    /// <param name="strategy">The strategy that decides when and how much to scale.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="strategy"/> is <c>null</c>.</exception>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;()
    ///     .WithScalingStrategy(new ThresholdScalingStrategy());
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithScalingStrategy(IHayateScalingStrategy strategy)
    {
        _scalingStrategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
        return this;
    }

    /// <summary>
    /// Sets the metrics collector.
    /// </summary>
    /// <param name="metrics">The metrics sink used to record pool runtime metrics.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="metrics"/> is <c>null</c>.</exception>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;()
    ///     .WithMetrics(new MyMetricsCollector());
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithMetrics(IHayateMetrics metrics)
    {
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        return this;
    }

    /// <summary>
    /// Sets the logger (explicit instance, takes precedence over
    /// <see cref="WithLoggerFactory"/>).
    /// </summary>
    /// <param name="logger">The logger instance to use.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/> is <c>null</c>.</exception>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;()
    ///     .WithLogger(new MyLogger());
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithLogger(IHayateLogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _loggerExplicitlySet = true;
        return this;
    }

    /// <summary>
    /// Disposes the pool automatically when the process is shutting down.
    /// </summary>
    /// <param name="enable">Whether to subscribe to process shutdown; defaults to <c>true</c>.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <remarks>
    /// A pool whose owner is the process never gets its <c>Dispose</c> call, because nothing user-facing runs
    /// at that point. Enabling this releases pooled objects -- and anything they hold, such as file handles or
    /// connections -- on the way out. The subscription is taken at construction and removed on disposal, so
    /// nothing accumulates and an explicitly disposed pool stays unreachable from the process-wide hook.<br />
    /// Nothing else changes: disposal is idempotent, so a later explicit <c>Dispose</c> is still correct, and
    /// neither the borrow nor the return path takes any part in it.<br />
    /// In a hosted application prefer disposing from the host's own shutdown step, or point this at the host's
    /// signal with <see cref="WithShutdownHook"/>.
    /// </remarks>
    /// <example>
    /// <code>
    /// var pool = new HayatePoolBuilder&lt;MyResource&gt;()
    ///     .WithAutoDisposeWithSystem()
    ///     .Build();
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithAutoDisposeWithSystem(bool enable = true)
    {
        _options.EnableAutoDisposeWithSystem = enable;
        return this;
    }

    /// <summary>
    /// Supplies the shutdown signal used by auto-dispose, in place of the process-wide hook.
    /// </summary>
    /// <param name="hook">The hook to subscribe to.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="hook"/> is <c>null</c>.</exception>
    /// <remarks>
    /// Two reasons to supply one: hosts that own a richer shutdown signal (an application lifetime, a
    /// container) can have the pool release itself at exactly that point instead of at raw process exit, and
    /// tests can raise the signal without terminating anything. Supplying a hook does not enable the feature
    /// on its own -- combine it with <see cref="WithAutoDisposeWithSystem"/>.
    /// </remarks>
    /// <example>
    /// <code>
    /// var pool = new HayatePoolBuilder&lt;MyResource&gt;()
    ///     .WithShutdownHook(new MyHostLifetimeHook(lifetime))
    ///     .WithAutoDisposeWithSystem()
    ///     .Build();
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithShutdownHook(IHayateShutdownHook hook)
    {
        _shutdownHook = hook ?? throw new ArgumentNullException(nameof(hook));
        return this;
    }

    /// <summary>
    /// Sets the pool-level logger factory. At build time, <see cref="IHayateLoggerFactory.CreateLogger"/>
    /// is called once per pool name to create an independent logger for each pool.
    /// </summary>
    /// <param name="loggerFactory">The logger factory used to create per-pool loggers.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="loggerFactory"/> is <c>null</c>.</exception>
    /// <remarks>
    /// Non-breaking: when this method is not called, the built-in singleton logger is used; when both
    /// this and <see cref="WithLogger"/> are set, the explicit <see cref="WithLogger"/> instance wins.
    /// </remarks>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;()
    ///     .WithLoggerFactory(new MyLoggerFactory());
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> WithLoggerFactory(IHayateLoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        return this;
    }

    /// <summary>
    /// Resolves the logger used for this build. An explicit <see cref="WithLogger"/> wins; otherwise
    /// the factory is queried by pool name; when no factory is set, the built-in singleton logger is
    /// used (behavior before 2.4).
    /// </summary>
    /// <returns>The logger to use for this pool.</returns>
    private IHayateLogger ResolveLogger()
    {
        if (_loggerExplicitlySet) return _logger;
        return _loggerFactory?.CreateLogger(_poolName) ?? _logger;
    }

    #endregion

    #region Full configuration

    /// <summary>
    /// Applies full configuration by invoking a callback against the underlying options.
    /// </summary>
    /// <param name="configure">The callback that mutates the options instance.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <example>
    /// <code>
    /// var builder = new HayatePoolBuilder&lt;MyResource&gt;()
    ///     .Configure(o =&gt; { o.MinPoolSize = 8; o.MaxPoolSize = 128; });
    /// </code>
    /// </example>
    public HayatePoolBuilder<T> Configure(Action<HayatePoolOptions> configure)
    {
        configure(_options);
        return this;
    }

    #endregion

    /// <summary>
    /// Builds the configured object pool.
    /// </summary>
    /// <returns>A ready-to-use <see cref="IHayateObjectPool{T}"/>.</returns>
    /// <exception cref="InvalidOperationException">The configuration is invalid, or a custom
    /// <c>IHayateMetrics</c> was registered via <see cref="WithMetrics"/> while metrics collection is
    /// disabled.</exception>
    /// <example>
    /// <code>
    /// var pool = new HayatePoolBuilder&lt;MyResource&gt;()
    ///     .WithMaxSize(128)
    ///     .Build();
    /// </code>
    /// </example>
    public IHayateObjectPool<T> Build()
    {
        if (!_options.IsValid())
        {
            throw new InvalidOperationException("HayatePool configuration is invalid");
        }

        if (!_options.EnableMetrics)
        {
            // Behavior change in 2.2: when a custom metrics collector is explicitly registered but
            // EnableMetrics is off, fail fast instead of silently substituting EmptyHayateMetrics
            // (which would let the user wrongly believe their custom metrics are active).
            // O11: the diagnostics master switch closes metrics too, so the same guard covers the
            // "diagnostics off" configuration — a sink registered against a pool that cannot record
            // anything is a mistake either way, and the message names both switches.
            if (!ReferenceEquals(_metrics, EmptyHayateMetrics.Instance))
            {
                throw new InvalidOperationException(
                    "HayatePool: a custom IHayateMetrics was registered via WithMetrics(), but metrics collection is disabled (EnableMetrics = false). " +
                    "Call WithEnableMetrics(true) to activate it, or remove the WithMetrics() registration. " +
                    "Note that EnableDiagnostics = false also disables metrics — call WithEnableDiagnostics(true) to re-open the diagnostic surface.");
            }

            _metrics = EmptyHayateMetrics.Instance;
        }

        // Same fail-fast rule for the eviction policy: a custom rule installed on a pool that never
        // runs an eviction scan (EnableEviction = false, or the lean profile, which forces it off)
        // would silently never be consulted. Passing the default policy is not a custom rule — it is
        // what a pool without a policy holds anyway — so that combination stays valid.
        if (!_options.EnableEviction && _evictionPolicy is not null &&
            !ReferenceEquals(_evictionPolicy, HayateDefaultEvictionPolicy<T>.Instance))
        {
            throw new InvalidOperationException(
                "HayatePool: a custom IHayateEvictionPolicy was registered via WithEvictionPolicy(), but idle eviction is disabled (EnableEviction = false), so the policy would never be consulted. " +
                "Call WithEnableEviction(true) to activate it, or remove the WithEvictionPolicy() registration. " +
                "Note that the lean profile (UseLeanProfile/WithLean) disables eviction by construction.");
        }

        // Same fail-fast rule for the borrow order: the lean fast path stores the pooled value directly
        // in a bounded buffer and holds no ordered idle list, so an explicit LIFO request cannot be
        // honoured there. The default (FIFO) is what a pool that never asked for anything holds, so
        // that combination stays valid — the guard fires only on a request it cannot satisfy, which is
        // what keeps the switch from being an option that is accepted and then ignored.
        if (_options.EnableLean && _options.BorrowStrategy != HayateBorrowStrategy.Fifo)
        {
            throw new InvalidOperationException(
                "HayatePool: BorrowStrategy was set to Lifo via WithBorrowStrategy(), but the pool runs the lean fast path (UseLeanProfile/WithLean), which keeps no ordered idle list and cannot honour a borrow order. " +
                "Remove the WithBorrowStrategy() call, or leave the lean fast path.");
        }

        // Resolve the logger by pool name (explicit WithLogger takes precedence -> factory ->
        // built-in singleton).
        var logger = ResolveLogger();

        var pool = new HayatePoolBasic<T>(
            Policy,
            _options,
            _scalingStrategy,
            _metrics,
            logger,
            _poolName,
            _shutdownHook,
            _evictionPolicy);

        logger.LogInformation("HayatePool [{PoolName}] initialized successfully", _poolName);

        return pool;
    }

    /// <summary>
    /// Fault-tolerant build entry point.<br />
    /// When <paramref name="throwOnError"/> is <c>true</c> (the default), behavior is identical to
    /// <see cref="Build()"/>; when <c>false</c>, a build failure (invalid configuration, metrics
    /// registration conflict, etc.) does not throw but degrades to a usable empty pool with
    /// Min=0/Max=0 and all feature toggles off, while logging an error — subsequent Acquire calls
    /// follow the reject-policy semantics (e.g. BlockTimeout throwing on timeout), and the pool
    /// itself can be safely disposed and observed.
    /// </summary>
    /// <param name="throwOnError">Whether to throw on a build failure; <c>false</c> degrades to an
    /// empty pool.</param>
    /// <returns>A ready-to-use <see cref="IHayateObjectPool{T}"/>.</returns>
    /// <exception cref="InvalidOperationException">When <paramref name="throwOnError"/> is
    /// <c>true</c> and the configuration is invalid.</exception>
    /// <remarks>
    /// Typical scenario: configuration comes from external input (appsettings / remote push) and, on
    /// build failure, the business needs a "runnable degraded pool + alert log" rather than a process
    /// crash. Note: creation failures during warmup are already captured inside the pool (PreWarm logs
    /// and continues), so the empty-pool degradation mainly covers build-time configuration validation
    /// failures.
    /// </remarks>
    /// <example>
    /// <code>
    /// var pool = new HayatePoolBuilder&lt;MyResource&gt;()
    ///     .Configure(o =&gt; LoadFromConfig(o))
    ///     .BuildOrThrow(throwOnError: false);
    /// </code>
    /// </example>
    public IHayateObjectPool<T> BuildOrThrow(bool throwOnError = true)
    {
        if (throwOnError)
        {
            return Build();
        }

        try
        {
            return Build();
        }
        catch (Exception ex)
        {
            // The failure log also goes through the pool-level logger (factory takes precedence), so
            // the degraded alert lands on that pool's log channel.
            ResolveLogger().LogError(ex,
                "HayatePool [{PoolName}] build failed (BuildOrThrow(false)); degrading to empty pool",
                _poolName);

            return BuildDegradedEmptyPool();
        }
    }

    /// <summary>
    /// Builds the degraded empty pool. Min=0 / Max=0 / all feature toggles off, keeping only the
    /// reject-policy-related semantic configuration (timeout / reject policy / creation retry) so
    /// that Acquire behavior stays predictable.
    /// </summary>
    /// <returns>A degraded <see cref="IHayateObjectPool{T}"/> that rejects via the configured policy.</returns>
    private IHayateObjectPool<T> BuildDegradedEmptyPool()
    {
        var degraded = new HayatePoolOptions
        {
            MinPoolSize = 0,
            MaxPoolSize = 0,
            EnableSharding = false,
            EnableAutoScaling = false,
            EnableEviction = false,
            EnableValidation = false,
            EnableGenerationOptimization = false,
            EnableLeakDetection = false,
            EnableMetrics = false,
            // Keep reject semantics; when the original timeout is invalid (<= 0) fall back to the
            // default so the degraded pool still passes IsValid.
            DefaultAcquireTimeout = _options.DefaultAcquireTimeout > TimeSpan.Zero
                ? _options.DefaultAcquireTimeout
                : TimeSpan.FromSeconds(HayateConstant.DEFAULT_ACQUIRE_TIMEOUT_SECONDS),
            RejectPolicy = _options.RejectPolicy,
            CreationRetryCount = _options.CreationRetryCount,
            CreationRetryDelay = _options.CreationRetryDelay
        };

        // The degraded pool also resolves its logger by pool name (keeping log routing consistent).
        var logger = ResolveLogger();

        var pool = new HayatePoolBasic<T>(
            Policy,
            degraded,
            new ThresholdScalingStrategy(),
            EmptyHayateMetrics.Instance,
            logger,
            _poolName,
            _shutdownHook);

        logger.LogWarning(
            "HayatePool [{PoolName}] degraded to empty pool: Min=0/Max=0, all features off; " +
            "Acquire follows reject policy ({Policy}) until the pool is rebuilt with valid options",
            _poolName, degraded.RejectPolicy);

        return pool;
    }
}
