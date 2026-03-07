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
    void Release(T item);
}