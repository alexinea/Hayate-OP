using System;
using System.Threading;
using System.Threading.Tasks;

namespace DotNetCore.HayateOP;

public interface IHayateObjectPool : IDisposable
{
    HayatePoolStats GetStats();
    HayatePoolSnapshot TakeSnapshot();
    void ReloadConfig(Action<HayatePoolOptions> configure);
    void Clear();
    HayatePoolOptions GetOptions();
}

/// <summary>
/// 对象池通用接口
/// </summary>
/// <typeparam name="T"></typeparam>
public interface IHayateObjectPool<T> : IHayateObjectPool where T : class
{
    T Acquire();
    T Acquire(TimeSpan timeout);
    Task<T> AcquireAsync(CancellationToken cancellationToken = default);
    /// <summary>
    /// 异步获取池化对象，带超时边界（与同步 <see cref="Acquire(TimeSpan)"/> 语义对齐）：
    /// 超时抛 <see cref="TimeoutException"/>；外部取消传播 <see cref="TaskCanceledException"/>。
    /// </summary>
    /// <param name="timeout">等待上限。</param>
    /// <param name="cancellationToken">外部取消令牌，可随时中断等待。</param>
    Task<T> AcquireAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
    void Release(T item);

    /// <summary>
    /// M15（2.5）：按分类依据主动驱逐空闲对象，返回实际驱逐数量。
    /// 仅作用于<b>空闲</b>对象（借出中的对象一律不碰）；驱逐复用与后台驱逐相同的
    /// 分片原子认领 + 销毁幂等 CAS，可与其他驱逐/借还路径并发调用。
    /// 默认后台驱逐（<c>EvictionCallback</c>）行为不变——本 API 是叠加的主动运维出口。
    /// </summary>
    /// <param name="reason">驱逐依据（Touched=用过即清 / Idle=超 MaxIdleTime / Expired=超 MaxLifeTime）。</param>
    int Evict(HayateEvictReason reason);
}