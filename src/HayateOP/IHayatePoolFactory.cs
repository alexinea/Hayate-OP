using System;

namespace DotNetCore.HayateOP;

/// <summary>
/// Creates pools at run time from a <see cref="HayateServiceKey"/> — the named-pool counterpart of
/// the one-shot <see cref="HayatePoolBuilder{T}"/>.
/// </summary>
/// <remarks>
/// Where a builder is written against a known <c>T</c> at compile time, a factory is used when the
/// element type or the set of logical names is only known while the process is running: a
/// multi-tenant host that opens one pool per tenant, or a plugin host that pools whatever a plugin
/// declares.<br />
/// <see cref="GetOrCreate"/> is the usual entry point: it returns an already-registered pool for the
/// key and only builds (and registers) one when the key is new, so repeated calls with the same key
/// are cheap and never leak a second pool. <see cref="Create"/> deliberately always builds a fresh
/// pool and re-registers it under the key — use it when a pool has to be replaced, and dispose the
/// instance it replaces yourself (the registry does not own pool lifetime).
/// </remarks>
/// <example>
/// <code>
/// var factory = new HayatePoolFactory(registry);
/// var pool = (IHayateObjectPool&lt;MyConnection&gt;)factory.GetOrCreate(HayateServiceKey.Create&lt;MyConnection&gt;("tenant-42"));
/// </code>
/// </example>
public interface IHayatePoolFactory
{
    /// <summary>
    /// Builds a new pool for the key and registers it under <see cref="HayateServiceKey.RegistryName"/>,
    /// replacing any pool already registered there.
    /// </summary>
    /// <param name="key">The service key identifying the pool.</param>
    /// <param name="configure">An optional callback applied to the options before the pool is built;
    /// the pool is built with the documented defaults when it is <c>null</c>.</param>
    /// <returns>The freshly built pool (the non-generic face; the element type is on the key).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <c>null</c>.</exception>
    IHayateObjectPool Create(HayateServiceKey key, Action<HayatePoolOptions>? configure = null);

    /// <summary>
    /// Returns the pool already registered for the key, or builds and registers a new one.
    /// </summary>
    /// <param name="key">The service key identifying the pool.</param>
    /// <param name="configure">An optional callback applied to the options before the pool is built;
    /// ignored when a pool is already registered for the key.</param>
    /// <returns>The existing or newly built pool (the non-generic face; the element type is on the
    /// key).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <c>null</c>.</exception>
    IHayateObjectPool GetOrCreate(HayateServiceKey key, Action<HayatePoolOptions>? configure = null);
}
