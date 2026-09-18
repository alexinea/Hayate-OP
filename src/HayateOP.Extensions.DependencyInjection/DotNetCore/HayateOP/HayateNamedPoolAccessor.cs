using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetCore.HayateOP;

/// <summary>
/// Default <see cref="IHayateNamedPoolAccessor"/>: named pools come from
/// <see cref="HayateNamedPoolCollection{T}"/>, the unnamed pool from the registry (where it is
/// registered under the bare type name when it is built) and then from the container.
/// </summary>
public sealed class HayateNamedPoolAccessor : IHayateNamedPoolAccessor
{
    private readonly IServiceProvider _services;
    private readonly IHayateObjectPoolRegistry? _registry;

    /// <summary>
    /// Creates an accessor over the given container.
    /// </summary>
    /// <param name="services">The container used to resolve the named-pool collections and, for the
    /// unnamed pool, the pool itself.</param>
    /// <param name="registry">The pool registry; may be <c>null</c>, in which case the unnamed pool is
    /// resolved from the container only.</param>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <c>null</c>.</exception>
    public HayateNamedPoolAccessor(IServiceProvider services, IHayateObjectPoolRegistry? registry = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _registry = registry;
    }

    /// <inheritdoc />
    public IHayateObjectPool<T> GetPool<T>(string name) where T : class
    {
        if (TryGetPool<T>(name, out var pool) && pool != null) return pool;

        throw new InvalidOperationException(
            $"No HayateOP pool of type '{typeof(T).Name}' is registered under the name '{name}'. " +
            $"Registered names: {string.Join(", ", GetPoolNames<T>())}.");
    }

    /// <inheritdoc />
    public IHayateObjectPool<T> GetPool<T>() where T : class
    {
        // The unnamed pool is registered under the bare type name (not a composite key), so it is
        // looked up directly and only then checked for the element type. It is found there once it
        // has been built, and in the container before that. Named pools are never returned here —
        // asking for "the pool of T" must stay unambiguous even when named pools exist.
        var typeName = typeof(T).Name;
        if (_registry != null && _registry.TryGet(typeName, out var untyped) &&
            untyped is IHayateObjectPool<T> registered)
        {
            return registered;
        }

        var fromContainer = _services.GetService<IHayateObjectPool<T>>();
        if (fromContainer != null) return fromContainer;

        throw new InvalidOperationException(
            $"No unnamed HayateOP pool of type '{typeof(T).Name}' is registered. " +
            "Call RegisterHayatePool<T>() to register one, or address a named pool by name.");
    }

    /// <inheritdoc />
    public bool TryGetPool<T>(string name, out IHayateObjectPool<T>? pool) where T : class
    {
        pool = null;
        if (string.IsNullOrWhiteSpace(name)) return false;

        var collection = _services.GetService<HayateNamedPoolCollection<T>>();
        return collection != null && collection.TryGetPool(name!, out pool) && pool != null;
    }

    /// <inheritdoc />
    public IEnumerable<string> GetPoolNames<T>() where T : class
    {
        var collection = _services.GetService<HayateNamedPoolCollection<T>>();
        return collection?.Names ?? Array.Empty<string>();
    }
}
