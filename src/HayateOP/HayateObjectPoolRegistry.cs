using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace DotNetCore.HayateOP;

/// <summary>
/// Default thread-safe <see cref="IHayateObjectPoolRegistry"/> backed by a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>. Pools self-register here as
/// they are created; callers resolve by the same logical name.
/// </summary>
public class HayateObjectPoolRegistry : IHayateObjectPoolRegistry
{
    private readonly ConcurrentDictionary<string, IHayateObjectPool> _pools = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public void Register(string poolName, IHayateObjectPool pool)
    {
        if (string.IsNullOrWhiteSpace(poolName))
            throw new ArgumentNullException(nameof(poolName));
        if (pool == null)
            throw new ArgumentNullException(nameof(pool));

        _pools[poolName] = pool;
    }

    /// <inheritdoc />
    public bool TryGet(string poolName, out IHayateObjectPool pool)
        => _pools.TryGetValue(poolName ?? string.Empty, out pool!);

    /// <inheritdoc />
    public IEnumerable<string> Names => _pools.Keys;
}
