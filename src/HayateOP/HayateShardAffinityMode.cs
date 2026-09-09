namespace DotNetCore.HayateOP;

/// <summary>
/// M18（2.5）：借出路径的分片亲和模式。
/// 决定 Acquire / AcquireAsync 扫描分片时的起始位置——命中起始分片后仍按环形
/// 继续扫描其余分片，因此任何模式都不损失可用性，只影响命中优先级与局部性。
/// </summary>
public enum HayateShardAffinityMode
{
    /// <summary>
    /// 顺序扫描（默认）：从 0 号分片开始按索引顺序扫描，与 2.4 及之前行为完全一致，零额外开销。
    /// </summary>
    None = 0,

    /// <summary>
    /// 线程亲和：按托管线程 ID 稳定映射起始分片（同一线程始终优先命中同一分片，
    /// 利于对象在 CPU 缓存 / NUMA 节点 / 线程本地资源上的复用）。
    /// </summary>
    Thread = 1,

    /// <summary>
    /// 自定义委托：由 <see cref="HayatePoolOptions.CustomShardAffinity"/> 决定起始分片。
    /// 委托返回 null 或越界时本次借出回落顺序扫描（不抛异常，保证借出路径健壮性）。
    /// </summary>
    Custom = 2
}
