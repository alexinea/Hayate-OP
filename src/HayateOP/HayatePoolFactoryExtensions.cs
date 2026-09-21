using System;

namespace DotNetCore.HayateOP;

/// <summary>
/// Strongly typed helpers over <see cref="IHayatePoolFactory"/>, so a caller that knows the element
/// type at compile time never has to cast the non-generic result back.
/// </summary>
/// <remarks>
/// These are thin adapters: the pool is still built (and registered) by the factory's non-generic
/// method, so <see cref="GetOrCreatePool{T}"/> deduplicates exactly like
/// <see cref="IHayatePoolFactory.GetOrCreate"/> does. The cast is safe by construction — a factory
/// builds <see cref="IHayateObjectPool{T}"/> for the key's element type.<br />
/// Both methods keep the <c>new()</c> constraint on purpose: the factory builds with the library's default
/// policy and exposes no way to supply one, so a type without a public parameterless constructor cannot be
/// pooled through this path. Pool such a type through <see cref="HayatePoolBuilder{T}"/> and its
/// <c>WithPolicy</c> instead (2.9, B1).
/// </remarks>
/// <example>
/// <code>
/// var pool = factory.GetOrCreatePool&lt;MyConnection&gt;("primary", o =&gt; o.MaxPoolSize = 64);
/// </code>
/// </example>
public static class HayatePoolFactoryExtensions
{
    /// <summary>Builds a new pool of <typeparamref name="T"/> under the given logical name.</summary>
    /// <typeparam name="T">The pooled object type.</typeparam>
    /// <param name="factory">The factory to build with.</param>
    /// <param name="name">The logical pool name.</param>
    /// <param name="configure">An optional callback applied to the options before the pool is built.</param>
    /// <returns>The freshly built pool.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidCastException">The factory returned a pool whose element type is not
    /// <typeparamref name="T"/>.</exception>
    public static IHayateObjectPool<T> CreatePool<T>(this IHayatePoolFactory factory, string name,
        Action<HayatePoolOptions>? configure = null)
        where T : class, new()
    {
        if (factory is null) throw new ArgumentNullException(nameof(factory));

        return (IHayateObjectPool<T>)factory.Create(HayateServiceKey.Create<T>(name), configure);
    }

    /// <summary>
    /// Returns the pool of <typeparamref name="T"/> already registered under the given logical name,
    /// or builds and registers a new one.
    /// </summary>
    /// <typeparam name="T">The pooled object type.</typeparam>
    /// <param name="factory">The factory to build with.</param>
    /// <param name="name">The logical pool name.</param>
    /// <param name="configure">An optional callback applied to the options before the pool is built;
    /// ignored when a pool is already registered under the name.</param>
    /// <returns>The existing or newly built pool.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidCastException">The registered or built pool's element type is not
    /// <typeparamref name="T"/>.</exception>
    public static IHayateObjectPool<T> GetOrCreatePool<T>(this IHayatePoolFactory factory, string name,
        Action<HayatePoolOptions>? configure = null)
        where T : class, new()
    {
        if (factory is null) throw new ArgumentNullException(nameof(factory));

        return (IHayateObjectPool<T>)factory.GetOrCreate(HayateServiceKey.Create<T>(name), configure);
    }
}
