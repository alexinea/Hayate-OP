using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using HayateOP.Metrics;
using HayateOP.Policies;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HayateOP;

public class HayateObjectPool<T> : IHayateObjectPool<T>, IDisposable
    where T : class
{
    private readonly ConcurrentBag<T> _pool;
    private readonly string _poolName;
    private readonly IHayateObjectPolicy<T> _policy;
    private readonly SemaphoreSlim _semaphore;
    private readonly int _maxPoolSize;

    private readonly ILogger<HayateObjectPool<T>>? _logger;
    private readonly IHayateOpMetrics _metrics;

    private long _totalCreated;
    private long _totalReturned;
    private long _totalMissed;

    /// <summary>
    /// 构造对象池
    /// </summary>
    /// <param name="policy"></param>
    /// <param name="maxConcurrent"></param>
    /// <param name="maxPoolSize"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public HayateObjectPool(
        IHayateObjectPolicy<T> policy,
        int maxConcurrent,
        int maxPoolSize = 100)
        : this(nameof(HayateObjectPool<T>), policy, null, null, null, maxConcurrent, maxPoolSize)
    {
    }

    /// <summary>
    /// 构造对象池
    /// </summary>
    /// <param name="name"></param>
    /// <param name="policy"></param>
    /// <param name="maxConcurrent"></param>
    /// <param name="maxPoolSize"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public HayateObjectPool(
        string name,
        IHayateObjectPolicy<T> policy,
        int maxConcurrent,
        int maxPoolSize = 100)
        : this(name, policy, null, null, null, maxConcurrent, maxPoolSize)
    {
    }

    /// <summary>
    /// 构造对象池
    /// </summary>
    /// <param name="name"></param>
    /// <param name="policy"></param>
    /// <param name="logger"></param>
    /// <param name="metrics"></param>
    /// <param name="maxConcurrent"></param>
    /// <param name="maxPoolSize"></param>
    /// <param name="options"></param>
    public HayateObjectPool(
        string name,
        IHayateObjectPolicy<T> policy,
        ILogger<HayateObjectPool<T>>? logger,
        IOptions<HayateOpOptions>? options,
        IHayateOpMetrics? metrics,
        int? maxConcurrent,
        int? maxPoolSize)
    {
        _pool = new ConcurrentBag<T>();
        _poolName = name;
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _semaphore = new(maxConcurrent ?? options?.Value.MaxConcurrent ?? 10);
        _maxPoolSize = maxPoolSize ?? options?.Value.MaxPoolSize ?? 20;
        var options1 = options?.Value ?? new HayateOpOptions();
        _logger = logger;
        _metrics = options1.EnableMetrics
            ? metrics ?? EmptyHayateOpMetrics.Instance
            : EmptyHayateOpMetrics.Instance;
    }

    /// <summary>
    /// 从池获取对象
    /// </summary>
    /// <returns></returns>
    public T Get()
    {
        var stop = System.Diagnostics.Stopwatch.StartNew();
        _semaphore.Wait();

        try
        {
            if (_pool.TryTake(out var item))
            {
                _metrics.RecordObjectAcquired(_poolName, item, stop.Elapsed.TotalMilliseconds);
                _logger?.LogTrace("Object retrieved from pool. Type: {Type}", typeof(T).Name);
            }
            else
            {
                Interlocked.Increment(ref _totalMissed);
                Interlocked.Increment(ref _totalCreated);
                _metrics.RecordObjectMiss(_poolName, typeof(T));
                _logger?.LogTrace("Object pool miss. Created new instance. Type: {Type}", typeof(T).Name);
                item = _policy.Create();
            }


            return item;
        }
        catch
        {
            _semaphore.Release();
            throw;
        }
        finally
        {
            stop.Stop();
        }
    }

    /// <summary>
    /// 从池获取对象（异步）
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<T> GetAsync(CancellationToken cancellationToken = default)
    {
        var stop = System.Diagnostics.Stopwatch.StartNew();
        await _semaphore.WaitAsync(cancellationToken);

        try
        {
            if (_pool.TryTake(out var item))
            {
                _metrics.RecordObjectAcquired(_poolName, item, stop.Elapsed.TotalMilliseconds);
                _logger?.LogTrace("Object retrieved from pool (async). Type: {Type}", typeof(T).Name);
            }
            else
            {
                Interlocked.Increment(ref _totalMissed);
                Interlocked.Increment(ref _totalCreated);
                _metrics.RecordObjectMiss(_poolName, typeof(T));
                _logger?.LogTrace("Object pool miss (async). Created new instance. Type: {Type}", typeof(T).Name);
                item = _policy.Create();
            }

            return item;
        }
        catch
        {
            _semaphore.Release();
            throw;
        }
        finally
        {
            stop.Stop();
        }
    }

    /// <summary>
    /// 归还对象到池
    /// </summary>
    /// <param name="item"></param>
    public void Return(T? item)
    {
        if (item is null)
        {
            _logger?.LogWarning("Returned null object to pool. Type: {Type}", typeof(T).Name);
            _semaphore.Release();
            return;
        }

        if (!_policy.Return(item))
        {
            _logger?.LogWarning("Object rejected by policy. Disposing. Type: {Type}", typeof(T).Name);
            DisposeItem(item);
            _semaphore.Release();
            return;
        }

        Interlocked.Increment(ref _totalReturned);
        _metrics.RecordObjectReturned(_poolName, item, true);
        _logger?.LogTrace("Object returned to pool. Type: {Type}", typeof(T).Name);

        if (_pool.Count < _maxPoolSize)
        {
            _pool.Add(item);
        }
        else
        {
            _logger?.LogWarning("Pool full. Disposing returned object. Type: {Type}", typeof(T).Name);
            DisposeItem(item);
        }

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
        _logger?.LogInformation("Clearing object pool. Type: {Type}", typeof(T).Name);
        while (_pool.TryTake(out var item))
        {
            DisposeItem(item);
        }
    }

    private void DisposeItem(T item)
    {
        if (item is IDisposable d)
        {
            try
            {
                d.Dispose();
                _logger?.LogTrace("Object disposed. Type: {Type}", typeof(T).Name);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error disposing object. Type: {Type}", typeof(T).Name);
            }
        }
    }

    public void Dispose()
    {
        Clear();
        _semaphore.Dispose();
        _logger?.LogInformation("Object pool disposed. Type: {Type}", typeof(T).Name);
    }
}