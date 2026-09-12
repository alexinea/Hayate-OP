using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace DotNetCore.HayateOP;

/// <summary>
/// Default thread-safe <see cref="IHayateObjectPoolRegistry"/> backed by a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>. Pools self-register here as
/// they are created; callers resolve by the same logical name.
/// <para>
/// Internal entries are upgraded to <see cref="HayatePoolMetadata"/> (pool + metadata) and expose the
/// full management surface — GetAll / Remove / Count — while Register / TryGet / Names keep their
/// original semantics.
/// </para>
/// </summary>
public class HayateObjectPoolRegistry : IHayateObjectPoolRegistry
{
    private readonly ConcurrentDictionary<string, HayatePoolMetadata> _pools = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public void Register(string poolName, IHayateObjectPool pool)
    {
        if (string.IsNullOrWhiteSpace(poolName))
            throw new ArgumentNullException(nameof(poolName));
        if (pool == null)
            throw new ArgumentNullException(nameof(pool));

        _pools[poolName] = new HayatePoolMetadata(poolName, pool, DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public bool TryGet(string poolName, out IHayateObjectPool pool)
    {
        pool = null!;   // meaningful only when TryGet returns true
        if (poolName is null) return false;
        if (_pools.TryGetValue(poolName, out var entry))
        {
            pool = entry.Pool;
            return true;
        }
        return false;
    }

    /// <inheritdoc />
    public IEnumerable<string> Names => _pools.Keys;

    /// <inheritdoc />
    public IReadOnlyList<HayatePoolMetadata> GetAll()
    {
        // ToArray naturally produces a point-in-time snapshot; registrations or removals made while enumerating do not affect this result.
        var snapshot = _pools.Values.ToArray();
        return snapshot;
    }

    /// <inheritdoc />
    public bool Remove(string poolName)
    {
        if (string.IsNullOrWhiteSpace(poolName)) return false;
        return _pools.TryRemove(poolName, out _);
    }

    /// <inheritdoc />
    public int Count => _pools.Count;
}
