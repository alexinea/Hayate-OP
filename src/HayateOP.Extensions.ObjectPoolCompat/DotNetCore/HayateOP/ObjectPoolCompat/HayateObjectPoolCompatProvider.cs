using DotNetCore.HayateOP;
using Microsoft.Extensions.ObjectPool;
using System;
using System.Reflection;

namespace DotNetCore.HayateOP.ObjectPoolCompat;

/// <summary>
/// Construction options for <see cref="HayateObjectPoolCompatProvider"/>.
/// </summary>
public sealed class HayateCompatOptions
{
    /// <summary>The generated pool-name prefix (followed by <c>typeof(T).Name</c>). Defaults to <c>"HayateCompat."</c>.</summary>
    public string PoolNamePrefix { get; set; } = "HayateCompat.";

    /// <summary>
    /// Pre-warm count. Defaults to 0 (MEOP DefaultObjectPool also lazily creates). Pre-warming is now
    /// purely a warm-up optimisation: a pool below capacity creates synchronously on a miss, so a cold
    /// pool no longer pays a borrow delay (see <see cref="AcquireTimeout"/>).
    /// </summary>
    public int MinSize { get; set; } = 0;

    /// <summary>
    /// Capacity upper bound. Defaults to <c>Environment.ProcessorCount * 2</c>, aligning with the
    /// default <c>maximumRetained</c> of MEOP DefaultObjectPool.
    /// </summary>
    public int MaxSize { get; set; } = Environment.ProcessorCount * 2;

    /// <summary>
    /// Borrow timeout for a request that finds no idle object. Defaults to 1s.
    /// It bounds only the one case the pool cannot serve by creating: every capacity slot is already
    /// taken and every object is lent out. A miss below <see cref="MaxSize"/> — including the very first
    /// borrow of a cold pool — creates synchronously and returns immediately, so this value is not part
    /// of the cold-start path.
    /// </summary>
    public TimeSpan AcquireTimeout { get; set; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// HayateOP implementation of <see cref="ObjectPoolProvider"/> — builds a HayateOP-driven
/// <see cref="ObjectPool{T}"/> from an existing <see cref="IPooledObjectPolicy{T}"/> so MEOP
/// callers switch with zero code changes.
/// </summary>
/// <example>
/// <code>
/// // The only line you need to change:
/// // ObjectPoolProvider provider = new DefaultObjectPoolProvider();
/// ObjectPoolProvider provider = new HayateObjectPoolCompatProvider();
/// var pool = provider.Create&lt;MyPolicy&gt;();   // afterwards Get/Return are identical to MEOP
/// </code>
/// </example>
public sealed class HayateObjectPoolCompatProvider : ObjectPoolProvider
{
    private readonly HayateCompatOptions _options;

    /// <summary>Constructs with default options.</summary>
    public HayateObjectPoolCompatProvider()
        : this(new HayateCompatOptions())
    {
    }

    /// <summary>Constructs with custom options.</summary>
    /// <param name="options">The provider options. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public HayateObjectPoolCompatProvider(HayateCompatOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Creates a HayateOP-driven <see cref="ObjectPool{T}"/> for the given policy.
    /// </summary>
    /// <typeparam name="T">The pooled object type.</typeparam>
    /// <param name="policy">The MEOP pooled object policy. Must not be null.</param>
    /// <returns>A HayateOP-backed object pool.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="policy"/> is null.</exception>
    /// <example>
    /// <code>
    /// ObjectPoolProvider provider = new HayateObjectPoolCompatProvider();
    /// var pool = provider.Create&lt;MyPolicy&gt;();   // Get/Return behave like MEOP
    /// </code>
    /// </example>
    public override ObjectPool<T> Create<T>(IPooledObjectPolicy<T> policy)
    {
        if (policy is null) throw new ArgumentNullException(nameof(policy));

        // HayatePoolBuilder<T> requires a new() constraint, while MEOP's IPooledObjectPolicy<T> only
        // has a class constraint; dispatch via MakeGenericMethod (the CLR does not enforce new() at
        // runtime, and object creation is entirely the policy's responsibility).
        var pool = (IHayateObjectPool<T>)BuildPoolMethod.MakeGenericMethod(typeof(T))
            .Invoke(null, new object?[] { policy, _options })!;

        return new HayateObjectPoolAdapter<T>(pool);
    }

    private static readonly MethodInfo BuildPoolMethod =
        typeof(HayateObjectPoolCompatProvider).GetMethod(nameof(BuildPoolCore),
            BindingFlags.NonPublic | BindingFlags.Static)!;

    private static IHayateObjectPool<TP> BuildPoolCore<TP>(
        IPooledObjectPolicy<TP> policy, HayateCompatOptions options) where TP : class, new()
    {
        return new HayatePoolBuilder<TP>()
            .WithPoolName(options.PoolNamePrefix + typeof(TP).Name)
            .WithMinSize(options.MinSize)
            .WithMaxSize(Math.Max(options.MaxSize, options.MinSize))
            .WithPolicy(new HayateCompatPooledObjectPolicy<TP>(policy))
            // MEOP semantics: create (rather than block and throw) when no idle object is available.
            // CreateOnDemand delivers that synchronously while the pool has room to grow — including the
            // first borrow of a cold pool — instead of waiting out AcquireTimeout the way CreateNew does.
            // The timeout now bounds only the at-capacity request.
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand)
            .WithAcquireTimeout(options.AcquireTimeout)
            // autoScaling must stay enabled: HayatePoolOptions.ApplyFeatureSwitches() forces
            // MaxPoolSize = MinPoolSize when EnableAutoScaling=false (see :487-490), and with Min=0
            // the capacity collapses to 0 -> all shards max=0 -> every return is rejected and pooling
            // is completely disabled. Min=0 preserves the lazy-creation semantics; the scaling cycle
            // may reclaim long-idle objects (HayateOP semantics), after which the next Get creates a
            // fresh object synchronously — no cold-start delay.
            .WithEnableAutoScaling(true)
            .WithEnableValidation(false)       // validation/reset is already handled by the Return hook (policy layer)
            .WithEnableEviction(false)         // MEOP has no background eviction
            .WithEnableLeakDetection(false)
            .WithEnableMetrics(false)
            .Build();
    }
}
