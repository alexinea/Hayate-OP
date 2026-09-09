namespace DotNetCore.HayateOP;

/// <summary>
/// M15（2.5）：<see cref="IHayateObjectPool{T}.Evict(HayateEvictReason)"/> 的分类驱逐依据。
/// </summary>
public enum HayateEvictReason
{
    /// <summary>
    /// 曾被借出使用过的空闲对象（LeaseCount &gt; 0）——「用过即清」，适合对象内聚状态
    /// 难以可靠重置的场景。
    /// </summary>
    Touched = 0,

    /// <summary>
    /// 空闲时长超过 <see cref="HayatePoolOptions.MaxIdleTime"/> 的对象（与后台驱逐的
    /// idle-too-long 判定同口径）。
    /// </summary>
    Idle = 1,

    /// <summary>
    /// 存活时长超过 <see cref="HayatePoolOptions.MaxLifeTime"/> 的对象（与后台驱逐的
    /// expired 判定同口径）。
    /// </summary>
    Expired = 2
}
