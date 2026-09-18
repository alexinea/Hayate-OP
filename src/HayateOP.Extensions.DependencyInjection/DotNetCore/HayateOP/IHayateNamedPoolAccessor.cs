using System.Collections.Generic;

namespace DotNetCore.HayateOP;

/// <summary>
/// Resolves named pools — and the single unnamed pool of a type — from the container.
/// </summary>
/// <remarks>
/// A pool registered with <c>RegisterHayatePool&lt;T&gt;()</c> is resolvable as
/// <see cref="IHayateObjectPool{T}"/> directly; that stops working as soon as a second pool of the
/// same type exists, because there is then no longer one answer to "the pool of <c>T</c>". Named
/// pools are addressed through this accessor instead, which is what a typed client uses and what an
/// application asks when it wants a specific pool by name.<br />
/// Resolution never creates a pool: it returns what was registered, and throws when the name was
/// never registered. Run-time creation is <see cref="IHayatePoolFactory"/>'s job, so a typo in a
/// pool name surfaces as an error rather than silently starting an unconfigured pool.
/// </remarks>
/// <example>
/// <code>
/// var primary = accessor.GetPool&lt;MyConnection&gt;("primary");
/// var replica = accessor.GetPool&lt;MyConnection&gt;("replica");
/// </code>
/// </example>
public interface IHayateNamedPoolAccessor
{
    /// <summary>
    /// Returns the named pool of <typeparamref name="T"/> registered under <paramref name="name"/>.
    /// </summary>
    /// <typeparam name="T">The pooled object type.</typeparam>
    /// <param name="name">The logical pool name.</param>
    /// <returns>The registered pool.</returns>
    /// <exception cref="System.InvalidOperationException">No pool of <typeparamref name="T"/> is
    /// registered under <paramref name="name"/>.</exception>
    IHayateObjectPool<T> GetPool<T>(string name) where T : class;

    /// <summary>
    /// Returns the unnamed pool of <typeparamref name="T"/> — the one registered with
    /// <c>RegisterHayatePool&lt;T&gt;()</c>.
    /// </summary>
    /// <typeparam name="T">The pooled object type.</typeparam>
    /// <returns>The registered pool.</returns>
    /// <exception cref="System.InvalidOperationException">No unnamed pool of
    /// <typeparamref name="T"/> is registered.</exception>
    IHayateObjectPool<T> GetPool<T>() where T : class;

    /// <summary>
    /// Tries to resolve the named pool of <typeparamref name="T"/> registered under
    /// <paramref name="name"/>.
    /// </summary>
    /// <typeparam name="T">The pooled object type.</typeparam>
    /// <param name="name">The logical pool name.</param>
    /// <param name="pool">When this method returns <c>true</c>, the pool; otherwise <c>null</c>.</param>
    /// <returns><c>true</c> when a pool is registered under <paramref name="name"/>.</returns>
    bool TryGetPool<T>(string name, out IHayateObjectPool<T>? pool) where T : class;

    /// <summary>The logical names registered for <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The pooled object type.</typeparam>
    /// <returns>The registered names; empty when <typeparamref name="T"/> has no named pool.</returns>
    IEnumerable<string> GetPoolNames<T>() where T : class;
}
