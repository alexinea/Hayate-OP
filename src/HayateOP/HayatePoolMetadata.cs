using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace DotNetCore.HayateOP;

/// <summary>
/// 注册表中单个池的元数据条目（M11+，2.5）。
/// <para>
/// 在「逻辑池名 → 池实例」的既有映射之上补充管理面信息：元素类型、池运行时类型、
/// 注册（构建）时间。<see cref="RegisteredAt"/> 取注册瞬间——DI 流程中池构建后立即注册，
/// 可视为池的构建时间；直接 Builder 场景若自行注册则同义。
/// </para>
/// </summary>
public sealed class HayatePoolMetadata
{
    /// <summary>池注册名（逻辑名，注册表的键）。</summary>
    public string PoolName { get; }

    /// <summary>池化元素的类型（<see cref="IHayateObjectPool{T}"/> 的 T）；非泛型实现为 null。</summary>
    public Type ElementType { get; }

    /// <summary>池实例的运行时类型。</summary>
    public Type PoolType { get; }

    /// <summary>注册（≈构建）时间。</summary>
    public DateTimeOffset RegisteredAt { get; }

    /// <summary>池实例（非泛型面）。注意：与注册表生命周期解耦，池可能已被外部 Dispose。</summary>
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
    /// 从池实例反射解析 <see cref="IHayateObjectPool{T}"/> 的元素类型 T。
    /// 解析不到（非泛型自定义实现）时返回 null——元数据仍可用，只是缺类型信息。
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
