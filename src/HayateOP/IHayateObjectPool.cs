using System.Threading;
using System.Threading.Tasks;

namespace HayateOP;

/// <summary>
/// 对象池通用接口
/// </summary>
/// <typeparam name="T"></typeparam>
public interface IHayateObjectPool<T> where T : class
{
    T Get();
    Task<T> GetAsync(CancellationToken cancellationToken = default);
    void Return(T item);
    (int PooledCount, long TotalCreated, long TotalReturned, long TotalMissed, int AvailableSlots) GetStats();
    void Clear();
}