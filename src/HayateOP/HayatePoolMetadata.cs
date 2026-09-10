using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace DotNetCore.HayateOP;

/// <summary>
/// A metadata entry for a single pool in the registry (2.5).
/// <para>
/// Adds management-plane information on top of the existing "logical pool name -> pool instance" mapping: element type, pool runtime type,
/// and registration (build) time. <see cref="RegisteredAt"/> is taken at the registration instant - in a DI flow the pool is registered immediately after being built,
/// which can be treated as the pool's build time; in a direct Builder scenario, self-registration is equivalent.
/// </para>
/// </summary>
public sealed class HayatePoolMetadata
{
    /// <summary>The registered pool name (the logical name, used as the registry key).</summary>
    public string PoolName { get; }

    /// <summary>The type of the pooled element (the T of <see cref="IHayateObjectPool{T}"/>); null for non-generic implementations.</summary>
    public Type ElementType { get; }

    /// <summary>The runtime type of the pool instance.</summary>
    public Type PoolType { get; }

    /// <summary>The registration (approximately the build) time.</summary>
    public DateTimeOffset RegisteredAt { get; }

    /// <summary>The pool instance (non-generic surface). Note: decoupled from the registry lifecycle; the pool may have already been disposed externally.</summary>
    public IHayateObjectPool Pool { get; }

    internal HayatePoolMetadata(string poolName, IHayateObjectPool pool, DateTimeOffset registeredAt)
    {
        PoolName = poolName;
        Pool = pool;
        PoolType = pool?.GetType();
        ElementType = ResolveElementType(pool);
        RegisteredAt = registeredAt;
    }

    /// <summary>
    /// Reflects the element type T of <see cref="IHayateObjectPool{T}"/> from the pool instance.
    /// Returns null when it cannot be resolved (a non-generic custom implementation) — the metadata
    /// is still usable, it just lacks type information.
    /// </summary>
    private static Type ResolveElementType(IHayateObjectPool pool)
    {
        if (pool is null) return null;

        var t = pool.GetType();
        foreach (var i in t.GetInterfaces())
        {
            if (i.GetTypeInfo().IsGenericType &&
                i.GetGenericTypeDefinition() == typeof(IHayateObjectPool<>))
            {
                return i.GetGenericArguments()[0];
            }
        }

        return null;
    }
}
