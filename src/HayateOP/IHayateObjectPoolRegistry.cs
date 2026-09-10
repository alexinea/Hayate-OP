using System;
using System.Collections.Generic;

namespace DotNetCore.HayateOP;

/// <summary>
/// A registry that maps a logical pool name (e.g. <c>typeof(T).Name</c>) to its
/// <see cref="IHayateObjectPool"/> instance.
/// <para>
/// It decouples management endpoints / diagnostics from reflection-based lookup
/// (<c>Type.GetType(poolName)</c> cannot resolve a plain type-name like "MyObj").
/// Registrations are populated as pools are created; lookups are by the same
/// name used to register the pool.
/// </para>
/// <para>
/// Builds on name-based addressing with a full management surface -- <see cref="GetAll"/> enumerates every
/// registration (including pool metadata: element type / runtime type / registration time), <see cref="Remove"/>
/// deregisters, and <see cref="Count"/> reports the current count. Keyed pools and shared instances reuse this skeleton.
/// </para>
/// </summary>
public interface IHayateObjectPoolRegistry
{
    /// <summary>Registers (or replaces) a pool under the given name.</summary>
    /// <param name="poolName">The logical name to register the pool under.</param>
    /// <param name="pool">The pool instance to register.</param>
    /// <exception cref="ArgumentNullException"><paramref name="poolName"/> or <paramref name="pool"/> is <c>null</c>.</exception>
    void Register(string poolName, IHayateObjectPool pool);

    /// <summary>Tries to resolve a previously-registered pool by name.</summary>
    /// <param name="poolName">The logical name to look up.</param>
    /// <param name="pool">When this method returns <c>true</c>, receives the resolved pool; otherwise <c>null</c>.</param>
    /// <returns><c>true</c> if a pool was found; otherwise <c>false</c>.</returns>
    bool TryGet(string poolName, out IHayateObjectPool pool);

    /// <summary>All currently registered pool names.</summary>
    IEnumerable<string> Names { get; }

    /// <summary>
    /// Enumerates every registration (pool instance + metadata). Returns a snapshot; registrations
    /// or removals made while enumerating do not affect the result.
    /// </summary>
    /// <example>
    /// <code>
    /// foreach (var meta in registry.GetAll())
    ///     Console.WriteLine($"{meta.PoolName}: {meta.ElementType}");
    /// </code>
    /// </example>
    IReadOnlyList<HayatePoolMetadata> GetAll();

    /// <summary>
    /// Deregisters a pool by name. Only removes the registry entry; it does NOT dispose the pool --
    /// pool lifetime is owned by the registrar.
    /// </summary>
    /// <param name="poolName">The name to remove.</param>
    /// <returns><c>true</c> if an entry was removed; <c>false</c> if <paramref name="poolName"/> is
    /// <c>null</c>/whitespace or the name is not registered.</returns>
    bool Remove(string poolName);

    /// <summary>The number of currently registered pools.</summary>
    int Count { get; }
}
