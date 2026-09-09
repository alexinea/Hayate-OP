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
/// M11+（2.5）：内部条目升级为 <see cref="HayatePoolMetadata"/>（池 + 元数据），
/// 对外暴露 GetAll / Remove / Count 完整版管理面；Register / TryGet / Names 原语义不变。
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
        pool = null;
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
        // ToArray 天然是某一瞬间的快照，枚举期间的其他注册/移除不影响本次结果。
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
