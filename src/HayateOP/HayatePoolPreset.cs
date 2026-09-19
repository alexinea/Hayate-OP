namespace DotNetCore.HayateOP;

/// <summary>
/// A named, ready-made configuration for <see cref="HayatePoolOptions"/>.
/// </summary>
/// <remarks>
/// The option surface is large because the pool covers very different workloads; a preset names the
/// combination that fits one of them, so the common shapes cost one word instead of forty settings,
/// without hiding any of the knobs.<br />
/// A preset is a starting point rather than a lock. It assigns the options it owns and leaves every
/// other option at the value it already had, and any option it does own can be overruled by a later
/// feature call — <c>WithPreset(ConnectionPool).WithMaxSize(32)</c> keeps the preset's validation and
/// reclaim behaviour and changes only the ceiling.<br />
/// Pool sizing is deliberately not owned by a preset unless the trade-off is the point of the preset,
/// because the right ceiling depends on the machine and on the workload rather than on the feature set.
/// <see cref="HayatePoolPreset.Default"/> is the exception in the other direction: it owns everything,
/// since "the shipped behaviour" means exactly that.<br />
/// Reach a preset through <see cref="HayatePoolOptions.UsePreset"/>, <see cref="HayatePoolPresets.Create"/>
/// or the builder's <c>WithPreset</c> overloads.
/// </remarks>
public enum HayatePoolPreset
{
    /// <summary>
    /// The shipped defaults: every optional feature at the value it has out of the box, and nothing
    /// else touched. Unlike the other presets this one owns the whole option surface — including
    /// sizing, timeouts, the reject policy and the callbacks — so it also serves as an explicit reset
    /// back to the shipped behaviour.
    /// </summary>
    Default = 0,

    /// <summary>
    /// The wrapper-free fast path: no sharding, no background passes and no per-object bookkeeping, so a
    /// pure borrow/return workload runs at the reference <c>DefaultObjectPool</c> cost.
    /// Same field set as <see cref="HayatePoolOptions.UseLeanProfile"/>.
    /// </summary>
    /// <remarks>
    /// Trade-off: the fast path has nowhere to keep per-object state, so the feature switches are forced
    /// off rather than honoured, and the borrow order cannot be set to LIFO. Sizing, timeouts and the
    /// reject policy are left alone.
    /// </remarks>
    Lean = 1,

    /// <summary>
    /// Every optional feature switched on: sharding, auto-scaling, validation, eviction, generation
    /// optimization, leak detection, metrics and allocation tracking.
    /// Same field set as <see cref="HayatePoolOptions.UseFullProfile"/>.
    /// </summary>
    /// <remarks>
    /// The profile to start from when the pool is being brought up or diagnosed and the cost of the
    /// bookkeeping is not yet the question. Numeric thresholds, intervals and the validation
    /// sub-switches keep their shipped values, so nothing validates until asked to.
    /// </remarks>
    Full = 2,

    /// <summary>
    /// Built to keep a busy pool off the creation path: sharding on, scaling up early and in large
    /// steps, objects retained well past a lull, and the optional bookkeeping surfaces switched off.
    /// </summary>
    /// <remarks>
    /// Owns the scaling, eviction-lifetime, validation, leak-detection and diagnostics options; leaves
    /// sizing, the shard count and the borrow order alone. Trade-off: leak forensics is off, which is
    /// the first switch to turn back on when an object goes missing.
    /// </remarks>
    HighThroughput = 3,

    /// <summary>
    /// Built for a steady per-operation latency on a request path: no background pass and no optional
    /// bookkeeping that can take a shard lock or compete for CPU with the borrower, and the first
    /// acquire waits for the pre-warmed floor instead of paying for the creation itself.
    /// </summary>
    /// <remarks>
    /// Owns the scaling, validation, eviction, generation, leak-detection, diagnostics and warmup
    /// options. Trade-off: with eviction off, objects are never retired — pair it with
    /// <c>WithValidateOnBorrow</c> or <c>WithMaxLifeTime</c> where a stale object is a real risk, and
    /// size the floor with <c>WithMinSize</c> if the whole pool has to be hot.
    /// </remarks>
    LowLatency = 4,

    /// <summary>
    /// Built to hold as little as possible: one shard, nothing retained while idle, quick reclamation,
    /// and the diagnostic surfaces closed.
    /// </summary>
    /// <remarks>
    /// The only preset that owns sizing, because "retain nothing" <i>is</i> the trade-off: it sets a zero
    /// floor together with <see cref="HayatePoolRejectPolicy.CreateOnDemand"/>. The two belong together —
    /// with a zero floor and a block policy the pool is empty once the first object has been lent out,
    /// and the block policies only shortcut creation while the pool tracks nothing, so the next borrower
    /// would sit out the whole acquire timeout.<br />
    /// Leak detection stays on: it follows borrowed objects rather than pooled ones, so it is not part of
    /// the retained footprint this preset exists to bound.
    /// </remarks>
    MemoryConstrained = 5,

    /// <summary>
    /// Built for expensive, finite external handles: bounded capacity with no proactive scaling,
    /// validation before a handle is handed out, scheduled recycling, abandoned-handle reclaim, and the
    /// availability surface open so callers can be told the dependency went away.
    /// </summary>
    /// <remarks>
    /// Owns the scaling, validation, eviction-lifetime, leak-detection, abandoned-recovery, circuit
    /// breaker, metrics and warmup options; leaves sizing alone, so the ceiling stays whatever the caller
    /// chose (the shipped default otherwise). Validation only does something for a pooled type that
    /// implements <see cref="IHayateValidatable"/>; for any other type it reports healthy, so the preset
    /// is safe to apply regardless of the pooled type.
    /// </remarks>
    ConnectionPool = 6,

    /// <summary>
    /// Built for bursts: react to occupancy every second, grow in large steps while the batch is being
    /// admitted, and let the capacity go once the batch has drained.
    /// </summary>
    /// <remarks>
    /// Owns the scaling, eviction-lifetime, validation, leak-detection and diagnostics options. The leak
    /// warning is moved out of the way of a healthy run rather than switched off, because a batch worker
    /// may legitimately hold its object for minutes.
    /// </remarks>
    BatchProcessing = 7
}
