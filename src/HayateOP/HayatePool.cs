using System;
using DotNetCore.HayateOP.Common;
using DotNetCore.HayateOP.Logging;
using DotNetCore.HayateOP.Metrics;
using DotNetCore.HayateOP.Policies;
using DotNetCore.HayateOP.Scaling;

namespace DotNetCore.HayateOP;

/// <summary>
/// Entry points that need no configuration object: one call builds a ready-to-use pool.
/// </summary>
/// <remarks>
/// Everything here is a thin layer over <see cref="HayatePoolBuilder{T}"/> — the resulting pool is an
/// ordinary <see cref="IHayateObjectPool{T}"/>, with the same lifecycle, diagnostics and run-time
/// reconfiguration surface. When a preset runs out of room, drop down to the builder: nothing the presets
/// set is hidden afterwards.
/// </remarks>
/// <example>
/// <code>
/// var pool = HayatePool.Simple&lt;MyResource&gt;(32, () =&gt; new MyResource());
/// </code>
/// </example>
public static class HayatePool
{
    /// <summary>
    /// Builds a pool holding at most <paramref name="poolSize"/> objects, created by
    /// <paramref name="create"/>.
    /// </summary>
    /// <param name="poolSize">The maximum number of objects the pool keeps and lends out.</param>
    /// <param name="create">The factory invoked when the pool needs a new object; must not return
    /// <c>null</c>.</param>
    /// <param name="onGet">An optional callback invoked every time an object is borrowed.</param>
    /// <param name="poolName">An optional logical name used in logs and diagnostics; defaults to the type
    /// name.</param>
    /// <returns>A ready-to-use pool.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="poolSize"/> is not greater than
    /// zero.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="create"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="create"/> returned <c>null</c> — raised
    /// on the first creation instead of letting a null object into the pool.</exception>
    /// <remarks>
    /// Everything except the two numbers below keeps its documented default, so the result is a
    /// full-featured pool rather than a stripped-down one: sharding, validation, eviction, auto-scaling and
    /// leak detection are on, and the standard <c>BlockTimeout</c> acquire semantics apply. Nothing is
    /// pre-created, so the first borrow pays the creation cost exactly once.<br />
    /// Use the <see cref="HayatePoolBuilder{T}"/> when the minimum idle count, the intervals or any single
    /// feature switch need tuning; see <see cref="HayatePoolOptions.UseLeanProfile"/> for the
    /// allocation-free pooling mode.
    /// </remarks>
    /// <example>
    /// <code>
    /// using var pool = HayatePool.Simple&lt;MyResource&gt;(32, () =&gt; new MyResource());
    /// using var connections = HayatePool.Simple&lt;Connection&gt;(8, () =&gt; new Connection(cs), c =&gt; c.Open());
    /// </code>
    /// </example>
    public static IHayateObjectPool<T> Simple<T>(int poolSize, Func<T> create, Action<T>? onGet = null,
        string? poolName = null) where T : class
    {
        if (poolSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(poolSize), poolSize,
                "The pool size must be greater than zero.");
        }

        if (create is null) throw new ArgumentNullException(nameof(create));

        var options = new HayatePoolOptions
        {
            // Lazy creation: an idle pool holds nothing until it is used, and `poolSize` is the ceiling
            // rather than something to fill eagerly.
            MinPoolSize = 0,
            MaxPoolSize = poolSize
        };
        options.ApplyFeatureSwitches();

        return new HayatePoolBasic<T>(
            new DelegateHayateObjectPolicy<T>(create, onGet),
            options,
            new ThresholdScalingStrategy(),
            EmptyHayateMetrics.Instance,
            new DefaultHayateLogger(),
            string.IsNullOrWhiteSpace(poolName) ? typeof(T).Name : poolName!);
    }
}
