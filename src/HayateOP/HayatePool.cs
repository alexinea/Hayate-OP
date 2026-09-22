using System;
using DotNetCore.HayateOP.Common;
using DotNetCore.HayateOP.Logging;
using DotNetCore.HayateOP.Metrics;
using DotNetCore.HayateOP.Policies;
using DotNetCore.HayateOP.Scaling;

namespace DotNetCore.HayateOP;

/// <summary>
/// Entry points that need no configuration object: one call produces a ready-to-use pool — a fresh one
/// with <see cref="Simple{T}"/>, or the process-wide shared one with <see cref="Shared{T}()"/>.
/// </summary>
/// <remarks>
/// Everything here is a thin layer over <see cref="HayatePoolBuilder{T}"/> — the resulting pool is an
/// ordinary <see cref="IHayateObjectPool{T}"/>, with the same lifecycle, diagnostics and run-time
/// reconfiguration surface. When a preset runs out of room, drop down to the builder: nothing the presets
/// set is hidden afterwards.<br />
/// The two families differ in who owns the pool: <see cref="Simple{T}"/> hands back a pool the caller
/// owns and disposes, while <see cref="Shared{T}()"/> hands back the one pool
/// <see cref="HayateSharedPoolRegistry.Default"/> keeps for that element type, which the caller must
/// leave alone.<br />
/// <b>These entry points have to choose their reject policy deliberately.</b> The library's default is
/// <c>BlockTimeout</c>, which waits for a return rather than growing the pool: a pool whose objects are all
/// lent out does not create another one on the borrow path, however much room <c>MaxPoolSize</c> still
/// leaves. An entry point whose contract reads as "a pool of N objects" must therefore set
/// <see cref="HayatePoolRejectPolicy.CreateOnDemand"/> explicitly, so that a miss grows the pool instead of
/// waiting out the acquire timeout. A zero <c>MinPoolSize</c> makes the difference sharpest: the wait-based
/// policies only shortcut creation while the pool tracks nothing at all, so after the first borrow the next
/// borrower waits. <see cref="HayatePoolPresets"/> and <c>ParameterizedHayatePool&lt;TKey, TValue&gt;</c> already do
/// this; <see cref="HayatePoolRejectPolicy"/> is where the five policies are compared.
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

    /// <summary>
    /// Returns the process-wide shared pool for <typeparamref name="T"/>, creating it on first use.
    /// </summary>
    /// <typeparam name="T">The pooled object type; objects are created with its public parameterless
    /// constructor.</typeparam>
    /// <returns>The shared pool — the same instance on every call, from anywhere in the
    /// process.</returns>
    /// <exception cref="InvalidOperationException">The pool could not be built.</exception>
    /// <remarks>
    /// Use this when the application needs <i>a</i> pool for a type and does not care who owns it:
    /// every caller gets the same pool, so one type has one pool rather than one per call site.
    /// The pool is an ordinary <see cref="IHayateObjectPool{T}"/> built with the default configuration
    /// (sharding, validation, eviction, auto-scaling and leak detection on) and starts empty, so the
    /// first borrow pays the creation cost once.<br />
    /// The shared pool is owned by <see cref="HayateSharedPoolRegistry.Default"/> — do <b>not</b>
    /// dispose it, and do not wrap it in <c>using</c>; the pool outlives the call. Tear it down through
    /// the catalog when the process shuts down, or in a test that needs a clean slate. Use the
    /// overload taking a configuration callback to set the pool's shape, and
    /// <see cref="HayatePoolBuilder{T}"/> when the pool is not meant to be shared at all.<br />
    /// The shared pool is built with the library's default policy, which creates objects with
    /// <c>new T()</c>. The catalog exposes no way to supply a policy, so the <c>new()</c> constraint stays
    /// here on purpose: a type without a public parameterless constructor is pooled through
    /// <see cref="HayatePoolBuilder{T}"/> and its <c>WithPolicy</c> instead (2.9, B1).
    /// </remarks>
    /// <example>
    /// <code>
    /// var pool = HayatePool.Shared&lt;MyBuffer&gt;();
    /// using var lease = pool.AcquireScoped();
    /// lease.Value.Write(payload);
    /// </code>
    /// </example>
    public static IHayateObjectPool<T> Shared<T>() where T : class, new()
        => HayateSharedPoolRegistry.Default.GetOrCreate<T>();

    /// <summary>
    /// Returns the process-wide shared pool for <typeparamref name="T"/>, creating and configuring it
    /// on first use.
    /// </summary>
    /// <typeparam name="T">The pooled object type.</typeparam>
    /// <param name="configure">Applied to the pool's options while it is built.</param>
    /// <returns>The shared pool — the same instance on every call.</returns>
    /// <exception cref="InvalidOperationException">The pool could not be built.</exception>
    /// <remarks>
    /// <paramref name="configure"/> only takes effect on the call that creates the pool; once the pool
    /// exists, later calls return it and ignore their callback (see
    /// <see cref="HayateSharedPoolRegistry.GetOrCreate{T}(Action{HayatePoolOptions})"/>). Configure the
    /// shared pool once, at start-up, or use
    /// <see cref="HayateSharedPoolRegistry.GetOrCreateNamed{T}(string, Action{HayatePoolOptions})"/> with
    /// a distinct name for a second, differently configured shared pool of the same type.<br />
    /// As with the parameterless overload, the pool is built with the library's default policy and the
    /// <c>new()</c> constraint is kept deliberately — see <see cref="Shared{T}()"/>.
    /// </remarks>
    /// <example>
    /// <code>
    /// var pool = HayatePool.Shared&lt;MyBuffer&gt;(o =&gt; { o.MaxPoolSize = 4096; o.MinPoolSize = 64; });
    /// </code>
    /// </example>
    public static IHayateObjectPool<T> Shared<T>(Action<HayatePoolOptions>? configure) where T : class, new()
        => HayateSharedPoolRegistry.Default.GetOrCreate<T>(configure);
}
