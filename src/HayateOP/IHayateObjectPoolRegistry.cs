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
/// </summary>
public interface IHayateObjectPoolRegistry
{
    /// <summary>Registers (or replaces) a pool under the given name.</summary>
    void Register(string poolName, IHayateObjectPool pool);

    /// <summary>Tries to resolve a previously-registered pool by name.</summary>
    bool TryGet(string poolName, out IHayateObjectPool pool);

    /// <summary>All currently registered pool names.</summary>
    IEnumerable<string> Names { get; }
}
