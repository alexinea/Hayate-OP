using System;
using System.Threading;
using System.Threading.Tasks;

namespace DotNetCore.HayateOP;

/// <summary>
/// 对象池通用接口
/// </summary>
/// <typeparam name="T"></typeparam>
public interface IHayateObjectPool<T> where T : class
{
    T Get();
    T Get(TimeSpan timeout);
    Task<T> GetAsync(CancellationToken cancellationToken = default);
    void Return(T item);
    HayateOpStats GetStats();
    HayateOpSnapshot TakeSnapshot();
    void ReloadConfig(Action<HayateOpOptions> configure);
    void Clear();
}