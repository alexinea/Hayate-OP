using System;
using System.Collections.Generic;

namespace DotNetCore.HayateOP;

/// <summary>
/// Typed lookups over <see cref="IHayateObjectPoolRegistry"/>, kept as extension methods so the
/// interface itself stays at its original size and existing implementations keep compiling.
/// </summary>
/// <remarks>
/// The registry stores the non-generic <see cref="IHayateObjectPool"/> face, which is what the
/// management endpoints and diagnostics need; a caller that knows the element type would otherwise
/// have to cast and null-check by hand, and a cast that silently succeeds for the wrong element type
/// is worse than one that fails loudly. Every lookup here therefore verifies the element type and
/// reports <c>false</c> / throws when it does not match, instead of handing back an unusable pool.<br />
/// The string overloads take the <b>logical</b> name — the same name passed to
/// <see cref="HayateServiceKey.Create{T}"/> when the pool was registered — and derive the canonical
/// registry key from it; <see cref="IHayateObjectPoolRegistry.TryGet"/> keeps taking the canonical
/// key, so the two are never confused.
/// </remarks>
/// <example>
/// <code>
/// if (registry.TryGetPool&lt;MyConnection&gt;("primary", out var pool))
///     using (var lease = pool.Acquire()) { /* ... */ }
/// </code>
/// </example>
public static class HayatePoolRegistryExtensions
{
    /// <summary>Resolves a registered pool by logical name and element type.</summary>
    /// <typeparam name="T">The pooled object type.</typeparam>
    /// <param name="registry">The registry to query.</param>
    /// <param name="name">The logical pool name.</param>
    /// <param name="pool">When this method returns <c>true</c>, the resolved pool; otherwise
    /// <c>null</c>.</param>
    /// <returns><c>true</c> when a pool of <typeparamref name="T"/> is registered under
    /// <paramref name="name"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/> is <c>null</c>.</exception>
    public static bool TryGetPool<T>(this IHayateObjectPoolRegistry registry, string name,
        out IHayateObjectPool<T>? pool)
        where T : class
    {
        if (registry is null) throw new ArgumentNullException(nameof(registry));

        pool = null;
        if (string.IsNullOrWhiteSpace(name)) return false;

        return TryGetRegistered<T>(registry, HayateServiceKey.Create<T>(name!).RegistryName, out pool);
    }

    /// <summary>Resolves a registered pool by service key and element type.</summary>
    /// <typeparam name="T">The pooled object type.</typeparam>
    /// <param name="registry">The registry to query.</param>
    /// <param name="key">The service key identifying the pool.</param>
    /// <param name="pool">When this method returns <c>true</c>, the resolved pool; otherwise
    /// <c>null</c>.</param>
    /// <returns><c>true</c> when a pool of <typeparamref name="T"/> is registered under the
    /// key.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/> or <paramref name="key"/>
    /// is <c>null</c>.</exception>
    public static bool TryGetPool<T>(this IHayateObjectPoolRegistry registry, HayateServiceKey key,
        out IHayateObjectPool<T>? pool)
        where T : class
    {
        if (registry is null) throw new ArgumentNullException(nameof(registry));
        if (key is null) throw new ArgumentNullException(nameof(key));

        return TryGetRegistered<T>(registry, key.RegistryName, out pool);
    }

    /// <summary>Resolves a registered pool by canonical registry name and element type.</summary>
    /// <typeparam name="T">The pooled object type.</typeparam>
    /// <param name="registry">The registry to query.</param>
    /// <param name="name">The logical pool name.</param>
    /// <returns>The resolved pool, or <c>null</c> when no pool of <typeparamref name="T"/> is
    /// registered under that name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/> is <c>null</c>.</exception>
    public static IHayateObjectPool<T>? GetPool<T>(this IHayateObjectPoolRegistry registry, string name)
        where T : class
    {
        registry.TryGetPool<T>(name, out var pool);
        return pool;
    }

    /// <summary>
    /// Resolves a registered pool by logical name and element type, throwing when it is absent or of
    /// another element type.
    /// </summary>
    /// <typeparam name="T">The pooled object type.</typeparam>
    /// <param name="registry">The registry to query.</param>
    /// <param name="name">The logical pool name.</param>
    /// <returns>The resolved pool.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">No pool of <typeparamref name="T"/> is registered
    /// under <paramref name="name"/>.</exception>
    public static IHayateObjectPool<T> GetRequiredPool<T>(this IHayateObjectPoolRegistry registry, string name)
        where T : class
    {
        var pool = registry.GetPool<T>(name);
        if (pool is null)
        {
            throw new InvalidOperationException(
                $"No HayateOP pool of type '{typeof(T).Name}' is registered under the name '{name}'. " +
                $"Registered pools: {string.Join(", ", registry.Names)}.");
        }

        return pool;
    }

    private static bool TryGetRegistered<T>(IHayateObjectPoolRegistry registry, string registryName,
        out IHayateObjectPool<T>? pool)
        where T : class
    {
        pool = null;
        if (!registry.TryGet(registryName, out var untyped) || untyped is null) return false;
        if (untyped is not IHayateObjectPool<T> typed) return false;

        pool = typed;
        return true;
    }
}
