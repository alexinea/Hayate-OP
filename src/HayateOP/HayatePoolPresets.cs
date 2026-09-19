using System;

namespace DotNetCore.HayateOP;

/// <summary>
/// The named configuration catalogue behind <see cref="HayatePoolPreset"/>.
/// </summary>
/// <remarks>
/// Every preset is defined once, here, and all three entry points route through <see cref="Apply"/> —
/// <see cref="Create"/>, <see cref="HayatePoolOptions.UsePreset"/> and the builder's <c>WithPreset</c>
/// overloads — so they cannot drift apart.<br />
/// Each preset assigns the complete group of options that forms its identity, shipped defaults included,
/// and assigns nothing outside that group. That is what makes a preset composable: an option it does not
/// own keeps whatever the caller set, and an option it does own is still overrulable by a later feature
/// call.
/// </remarks>
/// <example>
/// <code>
/// var options = HayatePoolPresets.Create(HayatePoolPreset.ConnectionPool);
///
/// var pool = new HayatePoolBuilder&lt;MyConnection&gt;()
///     .WithPreset(HayatePoolPreset.ConnectionPool)
///     .WithMaxSize(32)
///     .Build();
/// </code>
/// </example>
public static class HayatePoolPresets
{
    /// <summary>
    /// Builds a fresh options instance carrying the named preset.
    /// </summary>
    /// <param name="preset">The preset to apply.</param>
    /// <returns>A new <see cref="HayatePoolOptions"/> with the preset applied.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="preset"/> is not a defined preset.</exception>
    /// <remarks>
    /// The instance is created on every call, so callers may mutate the result freely.
    /// </remarks>
    public static HayatePoolOptions Create(HayatePoolPreset preset)
    {
        var options = new HayatePoolOptions();
        Apply(preset, options);
        return options;
    }

    /// <summary>
    /// Applies the named preset to an existing options instance.
    /// </summary>
    /// <param name="preset">The preset to apply.</param>
    /// <param name="options">The options instance to write into.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="preset"/> is not a defined preset.</exception>
    /// <remarks>
    /// An unrecognised preset is rejected rather than ignored: a configuration that silently keeps
    /// whatever it had would present itself as a preset that had been applied.
    /// </remarks>
    public static void Apply(HayatePoolPreset preset, HayatePoolOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));

        switch (preset)
        {
            case HayatePoolPreset.Default:
                ApplyDefault(options);
                return;

            case HayatePoolPreset.Lean:
                options.UseLeanProfile();
                return;

            case HayatePoolPreset.Full:
                options.UseFullProfile();
                return;

            case HayatePoolPreset.HighThroughput:
                ApplyHighThroughput(options);
                return;

            case HayatePoolPreset.LowLatency:
                ApplyLowLatency(options);
                return;

            case HayatePoolPreset.MemoryConstrained:
                ApplyMemoryConstrained(options);
                return;

            case HayatePoolPreset.ConnectionPool:
                ApplyConnectionPool(options);
                return;

            case HayatePoolPreset.BatchProcessing:
                ApplyBatchProcessing(options);
                return;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(preset),
                    preset,
                    "Unknown HayatePoolPreset value; the catalogue is defined by HayatePoolPresets.");
        }
    }

    /// <summary>
    /// The shipped defaults. Restored from a pristine instance rather than from a written-out list, so
    /// the preset cannot fall behind a default that changes elsewhere.
    /// </summary>
    private static void ApplyDefault(HayatePoolOptions options)
    {
        new HayatePoolOptions().CopyTo(options);
    }

    private static void ApplyHighThroughput(HayatePoolOptions options)
    {
        options.EnableLean = false;
        options.EnableSharding = true;

        // Meet a rising queue of borrowers with capacity instead of with waiting: react quickly, scale up
        // below the shipped threshold and in steps of eight. Scale down slowly and in small steps, so a
        // brief lull does not throw away objects that the next burst would have to recreate.
        options.EnableAutoScaling = true;
        options.ScalingIntervalMs = 2000;
        options.ScaleUpThreshold = 0.7;
        options.ScaleUpStep = 8;
        options.ScaleUpCooldownSeconds = 1;
        options.ScaleDownThreshold = 0.2;
        options.ScaleDownStep = 5;
        options.ScaleDownCooldownSeconds = 30;

        // Retain objects well past a lull — creating one is the expensive part of a throughput workload.
        options.EnableEviction = true;
        options.MaxLifeTime = TimeSpan.FromMinutes(30);
        options.MaxIdleTime = TimeSpan.FromMinutes(15);
        options.SoftMinEvictableIdleTime = TimeSpan.FromMinutes(5);

        options.EnableGenerationOptimization = true;

        options.EnableValidation = true;
        options.ValidateOnBorrow = false;
        options.ValidateOnReturn = false;
        options.ValidateWhileIdle = false;

        // The optional bookkeeping surfaces are the throughput cost a busy pool can drop without losing
        // an object. Leak forensics is the one to re-enable when a handle goes missing.
        options.EnableLeakDetection = false;
        options.EnableDiagnostics = false;
        options.EnableMetrics = false;
        options.EnableAllocationTracking = false;
    }

    private static void ApplyLowLatency(HayatePoolOptions options)
    {
        options.EnableLean = false;
        options.EnableSharding = true;

        // Every pass below wakes on a timer, takes shard locks and competes with the borrower for CPU —
        // the cost lands on the very request the preset exists to keep short.
        options.EnableAutoScaling = false;
        options.EnableValidation = false;
        options.ValidateOnBorrow = false;
        options.ValidateOnReturn = false;
        options.ValidateWhileIdle = false;
        options.EnableEviction = false;
        options.EnableGenerationOptimization = false;

        options.EnableLeakDetection = false;
        options.EnableDiagnostics = false;
        options.EnableMetrics = false;
        options.EnableAllocationTracking = false;

        // Block the first acquire until the pre-warmed floor exists, so the first request after start-up
        // does not pay for creating it.
        options.WaitForWarmup = true;
    }

    private static void ApplyMemoryConstrained(HayatePoolOptions options)
    {
        options.EnableLean = false;

        // One shard: the per-shard free list, counters and scan state are the pool's own overhead, and
        // this preset trades concurrency for the smallest possible footprint.
        options.EnableSharding = false;

        // Hold nothing while idle, and pair the zero floor with the policy that keeps a zero floor
        // usable: the block policies only shortcut creation while the pool tracks nothing at all, so
        // once the first object has been lent out the next borrower would wait out the full acquire
        // timeout instead of being served by a fresh object.
        options.MinPoolSize = 0;
        options.RejectPolicy = HayatePoolRejectPolicy.CreateOnDemand;

        options.EnableAutoScaling = false;

        options.EnableValidation = false;
        options.ValidateOnBorrow = false;
        options.ValidateOnReturn = false;
        options.ValidateWhileIdle = false;

        // Reclaim quickly rather than retain for the next burst, and look at more of the idle list per
        // pass so a small pool is swept promptly.
        options.EnableEviction = true;
        options.MaxLifeTime = TimeSpan.FromMinutes(10);
        options.MaxIdleTime = TimeSpan.FromMinutes(1);
        options.SoftMinEvictableIdleTime = TimeSpan.FromSeconds(30);
        options.EvictionIntervalMs = 10000;
        options.NumTestsPerEvictionRun = 20;

        options.EnableGenerationOptimization = false;

        // Leak tracking follows borrowed objects, not pooled ones, so it is not part of the retained
        // footprint this preset bounds.
        options.EnableLeakDetection = true;

        options.EnableDiagnostics = false;
        options.EnableMetrics = false;
        options.EnableAllocationTracking = false;
    }

    private static void ApplyConnectionPool(HayatePoolOptions options)
    {
        options.EnableLean = false;
        options.EnableSharding = true;

        // A fixed ceiling rather than a moving one: the resource on the other side is finite, so the pool
        // grows on demand up to the caller's maximum and never scales proactively.
        options.EnableAutoScaling = false;

        // Validate before a handle is handed out — a connection can die while it sits idle — and sweep
        // the idle ones so a dead handle is retired instead of being given to a caller.
        options.EnableValidation = true;
        options.ValidateOnBorrow = true;
        options.ValidateOnReturn = false;
        options.ValidateWhileIdle = true;
        options.ValidateIntervalMs = 30000;

        // Recycle on a schedule, so a handle outlives neither the server's own timeout nor its usefulness.
        options.EnableEviction = true;
        options.MaxLifeTime = TimeSpan.FromMinutes(30);
        options.MaxIdleTime = TimeSpan.FromMinutes(5);
        options.SoftMinEvictableIdleTime = TimeSpan.FromMinutes(2);
        options.EvictionIntervalMs = 30000;
        options.NumTestsPerEvictionRun = 10;

        options.EnableGenerationOptimization = true;

        // A handle that was never returned is the classic leak here, so both the warning and the reclaim
        // path stay on. The reclaim timeout is shorter than the shipped five minutes: a leaked handle is
        // one the far side is still holding open.
        options.EnableLeakDetection = true;
        options.LeakDetectionThreshold = TimeSpan.FromSeconds(30);
        options.RemoveAbandonedOnBorrow = true;
        options.RemoveAbandonedOnMaintenance = true;
        options.RemoveAbandonedTimeout = TimeSpan.FromSeconds(60);
        options.LogAbandoned = true;
        options.RemoveAbandonedIntervalMs = 30000;

        // Let the pool report the dependency going away, so callers can be told instead of timing out.
        options.EnableCircuitBreaker = true;

        options.EnableDiagnostics = true;
        options.EnableMetrics = true;
        options.EnableAllocationTracking = false;

        // Acquire blocks until the floor exists, so the first caller does not pay for the handshake.
        options.WaitForWarmup = true;
    }

    private static void ApplyBatchProcessing(HayatePoolOptions options)
    {
        options.EnableLean = false;
        options.EnableSharding = true;

        // Follow the burst: sample every second, scale up at 60% occupancy in steps of ten, and scale
        // back once the batch has drained. A burst admitted inside the shipped five-second window would
        // otherwise be served almost entirely by fresh creation.
        options.EnableAutoScaling = true;
        options.ScalingIntervalMs = 1000;
        options.ScaleUpThreshold = 0.6;
        options.ScaleUpStep = 10;
        options.ScaleUpCooldownSeconds = 1;
        options.ScaleDownThreshold = 0.2;
        options.ScaleDownStep = 10;
        options.ScaleDownCooldownSeconds = 15;

        // Let the burst capacity go once the batch is finished instead of holding it for the next one.
        options.EnableEviction = true;
        options.MaxLifeTime = TimeSpan.FromMinutes(15);
        options.MaxIdleTime = TimeSpan.FromMinutes(2);
        options.SoftMinEvictableIdleTime = TimeSpan.FromMinutes(1);
        options.EvictionIntervalMs = 10000;
        options.NumTestsPerEvictionRun = 10;

        options.EnableValidation = true;
        options.ValidateOnBorrow = false;
        options.ValidateOnReturn = false;
        options.ValidateWhileIdle = false;

        options.EnableGenerationOptimization = true;

        // A batch worker may legitimately hold its object for minutes, so the leak warning is moved out
        // of the way of a healthy run rather than switched off.
        options.EnableLeakDetection = true;
        options.LeakDetectionThreshold = TimeSpan.FromMinutes(5);

        options.EnableDiagnostics = true;
        options.EnableMetrics = false;
        options.EnableAllocationTracking = false;
    }
}
