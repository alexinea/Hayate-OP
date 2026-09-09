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
}