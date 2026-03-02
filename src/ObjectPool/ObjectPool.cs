using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace DotNetCore.Extensions.ObjectPool;

public class ObjectPool<T> : IDisposable
{
    private readonly ConcurrentBag<T> _pool;
    private readonly Func<T> _factory;
    private readonly SemaphoreSlim _semaphore;
    private readonly int _maxSize;

    private long _totalCreated;
    private long _totalReturned;
    private long _totalMissed;

    /// <summary>
    /// 构造对象池
    /// </summary>
    /// <param name="factory"></param>
    /// <param name="maxConcurrent"></param>
    /// <param name="maxSize"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public ObjectPool(Func<T> factory, int maxConcurrent, int maxSize = 100)
    {
        _pool = new ConcurrentBag<T>();
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _semaphore = new(maxConcurrent);
        _maxSize = maxSize;
    }

    /// <summary>
    /// 从池获取对象
    /// </summary>
    /// <returns></returns>
    public T Get()
    {
        _semaphore.Wait();

        try
        {
            if (_pool.TryTake(out T item))
                return item;

            Interlocked.Increment(ref _totalMissed);
            Interlocked.Increment(ref _totalCreated);
            return _factory();
        }
        catch
        {
            _semaphore.Release();
            throw;
        }
    }

    /// <summary>
    /// 从池获取对象（异步）
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<T> GetAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);

        try
        {
            if (_pool.TryTake(out T item))
                return item;

            Interlocked.Increment(ref _totalMissed);
            Interlocked.Increment(ref _totalCreated);
            return _factory();
        }
        catch
        {
            _semaphore.Release();
            throw;
        }
    }

    /// <summary>
    /// 归还对象到池
    /// </summary>
    /// <param name="item"></param>
    public void Return(T item)
    {
        if (item is null)
        {
            _semaphore.Release();
            return;
        }

        if (item is IResettable resettable)
        {
            resettable.Reset();
        }

        Interlocked.Increment(ref _totalReturned);

        if (_pool.Count < _maxSize)
            _pool.Add(item);
        else if (item is IDisposable disposable)
            disposable.Dispose();

        _semaphore.Release();
    }

    /// <summary>
    /// 获取统计信息
    /// </summary>
    /// <returns></returns>
    public (int PooledCount, long TotalCreated, long TotalReturned, long TotalMissed, int AvailableConcurrentSlots) GetStats()
    {
        return (
            PooledCount: _pool.Count,
            TotalCreated: Interlocked.Read(ref _totalCreated),
            TotalReturned: Interlocked.Read(ref _totalReturned),
            TotalMissed: Interlocked.Read(ref _totalMissed),
            AvailableConcurrentSlots: _semaphore.CurrentCount
        );
    }

    /// <summary>
    /// 清空池并释放所有对象
    /// </summary>
    public void Clear()
    {
        while (_pool.TryTake(out T item))
        {
            if (item is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    public void Dispose()
    {
        Clear();
        _semaphore.Dispose();
    }
}