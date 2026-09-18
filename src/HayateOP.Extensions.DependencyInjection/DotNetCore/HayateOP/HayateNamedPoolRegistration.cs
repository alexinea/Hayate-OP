using System;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetCore.HayateOP;

/// <summary>
/// A named-pool declaration: the service key a pool is addressed by, plus the delegate that builds it
/// when it is first asked for.
/// </summary>
/// <typeparam name="T">The pooled object type.</typeparam>
/// <remarks>
/// Named pools are deliberately <b>not</b> built on Microsoft.Extensions.DependencyInjection keyed
/// services: those only exist in version 8.0 and later of the abstractions package, so a keyed
/// registration would be unavailable to the net6.0 / net7.0 targets this library ships. A named pool
/// is therefore declared as one instance of this type in the container — the container's own
/// "many registrations of one service type" facility, available on every supported target — and
/// <see cref="HayateNamedPoolCollection{T}"/> collects them. The observable behavior is the same on
/// net48 through net10.0: one lazily built singleton per (type, name).
/// </remarks>
public sealed class HayateNamedPoolRegistration<T> where T : class
{
    /// <summary>
    /// Creates a declaration.
    /// </summary>
    /// <param name="key">The service key the pool is registered and addressed by.</param>
    /// <param name="factory">Builds the pool when it is first requested; called at most once per
    /// container, on the first request.</param>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> or <paramref name="factory"/> is
    /// <c>null</c>.</exception>
    public HayateNamedPoolRegistration(HayateServiceKey key, Func<IServiceProvider, IHayateObjectPool<T>> factory)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    /// <summary>The service key the pool is registered and addressed by.</summary>
    public HayateServiceKey Key { get; }

    /// <summary>Builds the pool when it is first requested.</summary>
    public Func<IServiceProvider, IHayateObjectPool<T>> Factory { get; }
}
