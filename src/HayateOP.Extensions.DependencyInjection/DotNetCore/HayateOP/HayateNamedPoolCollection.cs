using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetCore.HayateOP;

/// <summary>
/// Holds every named pool of one element type: the lazily built singletons behind the
/// <see cref="HayateNamedPoolRegistration{T}"/> declarations in the container.
/// </summary>
/// <typeparam name="T">The pooled object type.</typeparam>
/// <remarks>
/// A pool is built on the first request for its name and then reused, so declaring a pool costs
/// nothing until something asks for it — and asking twice never builds twice. Names that were never
/// requested stay unbuilt, which is what makes "declare a pool per tenant" affordable.<br />
/// The collection owns the pools it built and disposes them when the container is disposed; a pool
/// that was never requested is never created and therefore never disposed.
/// </remarks>
public sealed class HayateNamedPoolCollection<T> : IDisposable where T : class
{
    private readonly IServiceProvider _services;
    private readonly Dictionary<string, Func<IServiceProvider, IHayateObjectPool<T>>> _factories =
        new(StringComparer.Ordinal);
    private readonly List<string> _names = new();
    private readonly ConcurrentDictionary<string, Lazy<IHayateObjectPool<T>>> _pools =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Collects the declared pools of <typeparamref name="T"/>.
    /// </summary>
    /// <param name="services">The container, passed to a pool's factory delegate when it is built.</param>
    /// <param name="registrations">Every declaration of a named pool of <typeparamref name="T"/>;
    /// when two declarations share a name, the last one wins (the container's own resolution rule for
    /// repeated registrations).</param>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <c>null</c>.</exception>
    public HayateNamedPoolCollection(IServiceProvider services,
        IEnumerable<HayateNamedPoolRegistration<T>>? registrations)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));

        if (registrations == null) return;
        foreach (var registration in registrations)
        {
            if (registration?.Key == null) continue;

            if (!_factories.ContainsKey(registration.Key.RegistryName))
            {
                _names.Add(registration.Key.Name);
            }

            _factories[registration.Key.RegistryName] = registration.Factory;
        }
    }

    /// <summary>
    /// The logical names declared for this element type, in declaration order, whether or not the
    /// pool has been built yet.
    /// </summary>
    public IEnumerable<string> Names => _names.ToArray();

    /// <summary>
    /// Returns the pool declared under <paramref name="name"/>, building it on first use.
    /// </summary>
    /// <param name="name">The logical pool name.</param>
    /// <param name="pool">When this method returns <c>true</c>, the pool; otherwise <c>null</c>.</param>
    /// <returns><c>true</c> when a pool is declared under <paramref name="name"/>.</returns>
    public bool TryGetPool(string name, out IHayateObjectPool<T>? pool)
    {
        pool = null;
        if (string.IsNullOrWhiteSpace(name)) return false;

        var registryName = HayateServiceKey.Create<T>(name!).RegistryName;
        if (!_factories.TryGetValue(registryName, out var factory)) return false;

        // GetOrAdd may run its factory more than once under contention, so the value it stores is a
        // Lazy: only the instance that won the race is ever forced, and a pool is built exactly once.
        var lazy = _pools.GetOrAdd(registryName, _ => new Lazy<IHayateObjectPool<T>>(() => factory(_services)));
        pool = lazy.Value;
        return true;
    }

    /// <summary>
    /// Returns the pool declared under <paramref name="name"/>, building it on first use, and throws
    /// when no pool is declared under that name.
    /// </summary>
    /// <param name="name">The logical pool name.</param>
    /// <returns>The pool.</returns>
    /// <exception cref="InvalidOperationException">No pool of <typeparamref name="T"/> is declared
    /// under <paramref name="name"/>.</exception>
    public IHayateObjectPool<T> GetPool(string name)
    {
        if (TryGetPool(name, out var pool) && pool != null) return pool;

        throw new InvalidOperationException(
            $"No HayateOP pool of type '{typeof(T).Name}' is registered under the name '{name}'. " +
            $"Registered names: {string.Join(", ", Names)}.");
    }

    /// <summary>Disposes every pool this collection built.</summary>
    public void Dispose()
    {
        foreach (var lazy in _pools.Values)
        {
            if (!lazy.IsValueCreated) continue;
            try
            {
                lazy.Value.Dispose();
            }
            catch
            {
                // A pool that fails to dispose must not stop the rest from being released.
            }
        }

        _pools.Clear();
    }
}
