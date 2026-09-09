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
/// M11+（2.5）：在按名寻址之上补充完整版管理面——<see cref="GetAll"/> 枚举全部注册项
/// （含池元数据：元素类型 / 运行时类型 / 注册时间）、<see cref="Remove"/> 反注册、
/// <see cref="Count"/> 存量计数。键控池（O-A）与共享实例（O-C）等后续设施复用本骨架。
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

    /// <summary>
    /// M11+：枚举全部注册项（池实例 + 元数据）。返回快照，枚举期间的其他注册/移除不影响结果。
    /// </summary>
    IReadOnlyList<HayatePoolMetadata> GetAll();

    /// <summary>
    /// M11+：按名称反注册。<paramref name="poolName"/> 为 null/空白或名称不存在时返回 <c>false</c>。
    /// 注意：仅从注册表摘除条目，<b>不</b> Dispose 池——生命周期归注册方所有。
    /// </summary>
    bool Remove(string poolName);

    /// <summary>M11+：当前注册的池数量。</summary>
    int Count { get; }
}
