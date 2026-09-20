using System;
using System.Collections.Generic;

namespace DotNetCore.HayateOP;

/// <summary>
/// A catalog of process-wide shared pools, one per element type by default, so an application can
/// pool a type once and hand the same instance to every caller that needs it.
/// </summary>
/// <remarks>
/// This is the "just give me a pool" entry point: where <see cref="HayatePool.Simple{T}"/> builds a
/// new pool every time it is called, <see cref="GetOrCreate{T}()"/> returns <i>the</i> pool for the
/// element type, building it on first use and returning the same instance afterwards. Pools are
/// addressed through the same <see cref="IHayateObjectPoolRegistry"/> the rest of the library uses —
/// the catalog is a thin layer over that skeleton, not a second registry — so shared pools show up in
/// the management enumeration alongside named and DI-created pools when the catalog is given the
/// application's registry.
/// <para>
/// A shared pool is not a weaker pool: it is an ordinary <see cref="IHayateObjectPool{T}"/> with the
/// full configuration surface, and the <c>configure</c> callback on the create overloads is how its
/// capacity, eviction and timeouts are set. Because creation happens once, only the call that creates
/// the pool can configure it; a later call returns the existing instance and ignores its callback.
/// Use <see cref="TryGet{T}(out IHayateObjectPool{T})"/> first when the configuration matters.
/// </para>
/// <para>
/// <b>Ownership</b>: the catalog owns every pool it creates, so <see cref="Remove{T}(string)"/>,
/// <see cref="Clear"/> and <see cref="Dispose"/> dispose the pool — unlike
/// <see cref="IHayateObjectPoolRegistry.Remove"/>, which only unregisters and leaves the lifetime to
/// the registrar. Pools that were already in an injected registry when the catalog was constructed
/// are never touched by <see cref="Clear"/> or <see cref="Dispose"/>: the catalog only releases what
/// it created itself.
/// </para>
/// <para>
/// <b>Lifetime</b>: <see cref="Default"/> lives for the process, which is what makes a shared pool
/// shared. That also means a caller must not dispose the pool it got back — the catalog, not the
/// borrower, decides when the pool goes away. Dispose the catalog itself at shutdown, or remove the
/// individual entries, when the pools must be torn down.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Anywhere in the application, the same instance comes back:
/// var pool = HayatePool.Shared&lt;MyBuffer&gt;();
/// using var lease = pool.AcquireScoped();
/// lease.Value.Write(payload);
///
/// // Set the shared pool up once, at start-up, when the shape is known:
/// var configured = HayatePool.Shared&lt;MyBuffer&gt;(o =&gt; o.MaxPoolSize = 4096);
/// </code>
/// </example>
public sealed class HayateSharedPoolRegistry : IDisposable
{
    /// <summary>
    /// The logical name a shared pool is registered under when the caller does not supply one, so
    /// <c>GetOrCreate&lt;T&gt;()</c> and <c>GetOrCreate&lt;T&gt;("shared")</c> address the same pool.
    /// </summary>
    public const string DefaultSharedName = "shared";

    private static readonly HayateSharedPoolRegistry DefaultInstance = new HayateSharedPoolRegistry();

    // Creation is a cold path (once per type per process) and it must not race: two threads calling
    // GetOrCreate for the same key at the same time have to end up with one pool, not two, or the
    // loser would leak a background-timer pool nobody can reach. A plain lock is the simplest way to
    // guarantee exactly one build, and there is nothing to gain from anything cleverer here.
    private readonly object _gate = new object();

    private readonly IHayateObjectPoolRegistry _registry;

    // The registry keys this catalog created, and therefore the pools it is allowed to dispose. An
    // injected registry can hold pools the catalog never owned (DI-managed ones), so the ownership
    // boundary has to be recorded rather than inferred from the registry contents.
    private readonly HashSet<string> _owned = new HashSet<string>(StringComparer.Ordinal);

    private bool _disposed;

    /// <summary>
    /// Creates a catalog with its own private registry.
    /// </summary>
    public HayateSharedPoolRegistry() : this(new HayateObjectPoolRegistry())
    {
    }

    /// <summary>
    /// Creates a catalog that registers the pools it builds into <paramref name="registry"/>.
    /// </summary>
    /// <param name="registry">Where the shared pools are registered, so management endpoints, metrics
    /// and diagnostics find them next to the application's other pools. The catalog still only ever
    /// disposes the pools it created itself.</param>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/> is <c>null</c>.</exception>
    public HayateSharedPoolRegistry(IHayateObjectPoolRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <summary>
    /// The process-wide catalog behind <see cref="HayatePool.Shared{T}()"/>.
    /// </summary>
    public static HayateSharedPoolRegistry Default => DefaultInstance;

    /// <summary>
    /// The <see cref="IHayateObjectPoolRegistry"/> holding the shared pools, for enumeration and name
    /// based removal. <see cref="IHayateObjectPoolRegistry.GetAll"/> lists the entries with their
    /// metadata, exactly as it does for any other registered pool.
    /// </summary>
    public IHayateObjectPoolRegistry Registry => _registry;

    /// <summary>The number of shared pools currently held by this catalog.</summary>
    public int Count => _registry.Count;

    /// <summary>
    /// Resolves the shared pool for <typeparamref name="T"/> under
    /// <see cref="DefaultSharedName"/>, creating it on first use.
    /// </summary>
    /// <typeparam name="T">The pooled object type; must have a public parameterless constructor,
    /// which is how the shared factory creates objects.</typeparam>
    /// <returns>The shared pool, the same instance on every call.</returns>
    /// <exception cref="ObjectDisposedException">The catalog has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The pool could not be built — see
    /// <see cref="HayatePoolBuilder{T}.Build"/>.</exception>
    /// <remarks>
    /// The pool is built with the library's default configuration (sharding, validation, eviction,
    /// auto-scaling and leak detection on) and has created nothing yet, so the first borrow pays the
    /// object-creation cost once. Use the overload taking a configuration callback, or
    /// <see cref="GetOrCreateNamed{T}(string, Action{HayatePoolOptions})"/> for several pools per type,
    /// when the default shape is not the right one.
    /// </remarks>
    /// <example>
    /// <code>
    /// var pool = HayatePool.Shared&lt;MyBuffer&gt;();
    /// using var lease = pool.AcquireScoped();
    /// lease.Value.Write(payload);
    /// </code>
    /// </example>
    public IHayateObjectPool<T> GetOrCreate<T>() where T : class, new()
        => GetOrCreateNamed<T>(DefaultSharedName, null);

    /// <summary>
    /// Resolves the shared pool for <typeparamref name="T"/> under
    /// <see cref="DefaultSharedName"/>, creating and configuring it on first use.
    /// </summary>
    /// <typeparam name="T">The pooled object type.</typeparam>
    /// <param name="configure">Applied to the pool's options while it is built.</param>
    /// <returns>The shared pool, the same instance on every call.</returns>
    /// <exception cref="ObjectDisposedException">The catalog has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The pool could not be built.</exception>
    /// <remarks>
    /// <paramref name="configure"/> only takes effect on the call that creates the pool. If the pool
    /// already exists the existing instance is returned and the callback is not run — the shared pool
    /// is configured once, by whoever creates it, and the alternatives to that ambiguity are a
    /// silently reconfigured pool or a pool whose configuration depends on call order, both worse.
    /// Check with <see cref="TryGet{T}(out IHayateObjectPool{T})"/> first when a specific shape is
    /// required, or use a distinct <c>name</c> for a separately configured pool.
    /// </remarks>
    /// <example>
    /// <code>
    /// var pool = HayatePool.Shared&lt;MyBuffer&gt;(o =&gt; { o.MaxPoolSize = 4096; o.MinPoolSize = 64; });
    /// </code>
    /// </example>
    public IHayateObjectPool<T> GetOrCreate<T>(Action<HayatePoolOptions>? configure) where T : class, new()
        => GetOrCreateNamed<T>(DefaultSharedName, configure);

    /// <summary>
    /// Resolves a named shared pool for <typeparamref name="T"/>, creating and configuring it on first
    /// use.
    /// </summary>
    /// <typeparam name="T">The pooled object type.</typeparam>
    /// <param name="name">The logical pool name, so one element type can have several independently
    /// configured shared pools; pass <see cref="DefaultSharedName"/> for the default one.</param>
    /// <param name="configure">Applied to the pool's options while it is built; ignored when the pool
    /// already exists.</param>
    /// <returns>The shared pool under <paramref name="name"/>, the same instance on every call.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or whitespace, or contains
    /// <see cref="HayateServiceKey.NameSeparator"/>.</exception>
    /// <exception cref="ObjectDisposedException">The catalog has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The pool could not be built.</exception>
    /// <remarks>
    /// The name is the same logical name used by <see cref="HayateServiceKey"/> and the named DI
    /// registrations, so a shared pool and a named DI pool of one type are addressed the same way; the
    /// catalog's own pools live under the canonical registry name (for example
    /// <c>MyBuffer:shared</c>), which is also the pool name they log and report under.<br />
    /// This is a separate method rather than an overload of <see cref="GetOrCreate{T}(Action{HayatePoolOptions})"/>
    /// so that a <c>null</c> name cannot silently select the wrong overload: passing <c>null</c> to an
    /// overload set containing a delegate-taking member would resolve to that member and quietly build
    /// the default pool, here it is a rejected argument.
    /// </remarks>
    public IHayateObjectPool<T> GetOrCreateNamed<T>(string name, Action<HayatePoolOptions>? configure = null)
        where T : class, new()
    {
        // Validate the name up front (and outside the lock): a name that cannot be addressed is a
        // programming error, and it should fail the same way whether or not the pool exists.
        var key = HayateServiceKey.Create<T>(name);

        lock (_gate)
        {
            ThrowIfDisposed();

            // Resolve through the registry rather than a side table, so a pool registered outside this
            // catalog (a named DI pool under the same canonical name) is honoured instead of shadowed.
            if (_registry.TryGetPool<T>(name, out var existing) && existing is not null)
            {
                return existing;
            }

            var builder = new HayatePoolBuilder<T>().WithPoolName(key.RegistryName);
            if (configure is not null)
            {
                builder.Configure(configure);
            }

            var pool = builder.Build();
            _registry.Register(key.RegistryName, pool);
            _owned.Add(key.RegistryName);
            return pool;
        }
    }

    /// <summary>
    /// Resolves the default shared pool for <typeparamref name="T"/> without creating it.
    /// </summary>
    /// <typeparam name="T">The pooled object type.</typeparam>
    /// <param name="pool">The shared pool when one exists; otherwise <c>null</c>.</param>
    /// <returns><c>true</c> when a shared pool of <typeparamref name="T"/> is already held under
    /// <see cref="DefaultSharedName"/>.</returns>
    /// <remarks>Never creates a pool, so it is safe to call from a hot path or from code that must not
    /// have side effects.</remarks>
    public bool TryGet<T>(out IHayateObjectPool<T>? pool) where T : class
        => TryGet<T>(DefaultSharedName, out pool);

    /// <summary>
    /// Resolves a named shared pool for <typeparamref name="T"/> without creating it.
    /// </summary>
    /// <typeparam name="T">The pooled object type.</typeparam>
    /// <param name="name">The logical pool name.</param>
    /// <param name="pool">The shared pool when one exists; otherwise <c>null</c>.</param>
    /// <returns><c>true</c> when a shared pool of <typeparamref name="T"/> is held under
    /// <paramref name="name"/>.</returns>
    /// <remarks>
    /// Returns <c>false</c> for an unusable name, a name that is not held, and a pool registered under
    /// that name whose element type is not <typeparamref name="T"/> — a pool of the wrong element type
    /// is reported as absent rather than handed back and cast at the call site.
    /// </remarks>
    public bool TryGet<T>(string name, out IHayateObjectPool<T>? pool) where T : class
    {
        pool = null;
        if (_disposed) return false;

        // TryCreate rejects the names TryGetPool would reject anyway (null, blank, containing the
        // separator); running it here keeps the "unusable name is simply absent" contract explicit
        // instead of leaning on the lookup to fail.
        if (!HayateServiceKey.TryCreate<T>(name, out _)) return false;

        return _registry.TryGetPool<T>(name!, out pool);
    }

    /// <summary>
    /// Removes a shared pool from the catalog and disposes it.
    /// </summary>
    /// <typeparam name="T">The pooled object type.</typeparam>
    /// <param name="name">The logical pool name; defaults to
    /// <see cref="DefaultSharedName"/>.</param>
    /// <returns><c>true</c> when an entry was removed.</returns>
    /// <remarks>
    /// Unlike <see cref="IHayateObjectPoolRegistry.Remove"/>, this disposes the pool: the catalog owns
    /// the pools it creates, and dropping one without disposing it would leave its background timers
    /// running with no way to reach them. Disposing a pool that borrowers are still holding is the
    /// caller's responsibility to avoid — removal is a shutdown / test-reconfiguration operation, not
    /// something to do under load.<br />
    /// Only pools this catalog created are disposed. A pool found under the same name that the catalog
    /// did not create — a named DI pool sharing the registry — is left alone, and only the catalog's
    /// own registration would have been removed anyway; the catalog never claims a pool it did not
    /// build.
    /// </remarks>
    /// <example>
    /// <code>
    /// HayatePool.Shared&lt;MyBuffer&gt;();          // create
    /// HayateSharedPoolRegistry.Default.Remove&lt;MyBuffer&gt;();   // dispose + forget
    /// </code>
    /// </example>
    public bool Remove<T>(string name = DefaultSharedName) where T : class
    {
        if (_disposed) return false;
        if (!HayateServiceKey.TryCreate<T>(name, out var key)) return false;

        lock (_gate)
        {
            if (!_registry.TryGet(key!.RegistryName, out var pool) || pool is null)
            {
                return false;
            }

            if (!_registry.Remove(key.RegistryName))
            {
                return false;
            }

            // Dispose only what this catalog created; see the remarks.
            if (_owned.Remove(key.RegistryName))
            {
                pool.Dispose();
            }

            return true;
        }
    }

    /// <summary>
    /// Removes and disposes every shared pool this catalog created.
    /// </summary>
    /// <remarks>
    /// Registrations the catalog did not create are left untouched, so an injected application
    /// registry keeps its DI-managed pools. After this call the catalog is empty and can be used
    /// again. Like <see cref="Remove{T}(string)"/>, this is a shutdown / test-reconfiguration
    /// operation: it disposes pools that borrowers may still hold.
    /// </remarks>
    public void Clear()
    {
        lock (_gate)
        {
            if (_disposed) return;
            ReleaseOwnedPools();
        }
    }

    /// <summary>
    /// Removes and disposes every shared pool this catalog created, and marks the catalog disposed.
    /// </summary>
    /// <remarks>
    /// Safe to call more than once. Afterwards <see cref="GetOrCreate{T}()"/> throws
    /// <see cref="ObjectDisposedException"/>, while the read-only surface (<see cref="TryGet{T}(out IHayateObjectPool{T})"/>,
    /// <see cref="Count"/>, <see cref="Registry"/>) keeps working and reports an empty catalog.
    /// Disposing the catalog does not dispose an injected registry, and does not touch pools the
    /// catalog did not create.
    /// </remarks>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseOwnedPools();
        }
    }

    private void ReleaseOwnedPools()
    {
        if (_owned.Count == 0) return;

        // Snapshot: the loop mutates _owned as it goes, and the collection must not change underneath it.
        var names = new string[_owned.Count];
        _owned.CopyTo(names);
        _owned.Clear();

        foreach (var name in names)
        {
            if (_registry.TryGet(name, out var pool) && pool is not null && _registry.Remove(name))
            {
                pool.Dispose();
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(HayateSharedPoolRegistry));
        }
    }
}
