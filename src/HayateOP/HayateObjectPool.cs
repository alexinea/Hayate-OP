using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using DotNetCore.HayateOP.Metrics;
using DotNetCore.HayateOP.Policies;
using DotNetCore.HayateOP.Scaling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DotNetCore.HayateOP;

public class HayateObjectPool<T> : IHayateObjectPool<T>, IDisposable
    where T : class
{
    private readonly ConcurrentBag<T> _pool;
    private readonly string _poolName;
    private readonly IHayateObjectPolicy<T> _policy;
    private readonly HayateOpOptions _options;
    private readonly SemaphoreSlim _semaphore;
    private readonly IHayateOpScalingStrategy _scalingStrategy;
    private readonly Timer _scalingTimer;

    private readonly ILogger<HayateObjectPool<T>>? _logger;
    private readonly IHayateOpMetrics _metrics;

    private long _totalCreated;
    private long _totalReturned;
    private long _totalMissed;

    private int _currentPoolSize;

    /// <summary>
    /// 构造对象池
    /// </summary>
    /// <param name="policy"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public HayateObjectPool(IHayateObjectPolicy<T> policy)
        : this(policy, null, null, null, null)
    {
    }

    /// <summary>
    /// 构造对象池
    /// </summary>
    /// <param name="policy"></param>
    /// <param name="scalingStrategy"></param>
    /// <param name="logger"></param>
    /// <param name="metrics"></param>
    /// <param name="options"></param>
    public HayateObjectPool(
        IHayateObjectPolicy<T> policy,
        HayateOpOptions? options,
        IHayateOpScalingStrategy? scalingStrategy,
        ILogger<HayateObjectPool<T>>? logger,
        IHayateOpMetrics? metrics)
    {
        _poolName = typeof(T).Name;
        _pool = new ConcurrentBag<T>();
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _options = options ?? new HayateOpOptions();
        _scalingStrategy = scalingStrategy ?? new ThresholdScalingStrategy();
        _semaphore = new(_options.MaxConcurrent);

        _currentPoolSize = _options.MaxPoolSize;

        _logger = logger;
        _metrics = _options.EnableMetrics
            ? metrics ?? EmptyHayateOpMetrics.Instance
            : EmptyHayateOpMetrics.Instance;

        // 预创建最小容量对象
        for (var i = 0; i < _options.MinPoolSize; i++)
        {
            _pool.Add(_policy.Create());
            Interlocked.Increment(ref _totalCreated);
        }

        // 自动伸缩定时器
        _scalingTimer = new Timer(ScalingCallback!, null, _options.ScalingIntervalMilliseconds, _options.ScalingIntervalMilliseconds);

        _logger?.LogInformation("Object pool initialized. Min:{Min} Max:{Max} Concurrent:{Concurrent}",
            _options.MinPoolSize, _options.MaxPoolSize, _options.MaxConcurrent);
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

        if (_pool.Count < _currentPoolSize)
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
    public (int PooledCount, long TotalCreated, long TotalReturned, long TotalMissed, int AvailableSlots, int MinSize, int MaxSize) GetStats()
    {
        return (
            PooledCount: _pool.Count,
            TotalCreated: Interlocked.Read(ref _totalCreated),
            TotalReturned: Interlocked.Read(ref _totalReturned),
            TotalMissed: Interlocked.Read(ref _totalMissed),
            AvailableSlots: _semaphore.CurrentCount,
            MinSize: _options.MinPoolSize,
            MaxSize: _currentPoolSize
        );
    }

    #region Scaling

    /// <summary>
    /// 扩缩容回调，根据当前使用率自动调整池大小
    /// </summary>
    /// <param name="state"></param>
    private void ScalingCallback(object state)
    {
        try
        {
            var stats = GetStats();
            int newSize = _scalingStrategy.CalculateNewSize(_currentPoolSize, stats.PooledCount, _options);
            
            // 扩容
            if (newSize > _currentPoolSize)
            {
                int oldSize = _currentPoolSize;
                int addCount = newSize - oldSize;

                for (var i = 0; i < addCount; i++)
                {
                    _pool.Add(_policy.Create());
                    Interlocked.Increment(ref _totalCreated);
                }

                _currentPoolSize = newSize;
                _logger?.LogInformation("Pool scaled UP. New size: {Size}", _currentPoolSize);
                _metrics.RecordPoolScaled(_poolName, "UP", oldSize, newSize);
            }
            // 缩容
            else if (newSize < _currentPoolSize)
            {
                int oldSize = _currentPoolSize;
                int removeCount = oldSize - newSize;

                for (var i = 0; i < removeCount; i++)
                {
                    if (_pool.TryTake(out var item))
                    {
                        DisposeItem(item);
                    }
                }

                _currentPoolSize = newSize;
                _logger?.LogInformation("Pool scaled DOWN. New size: {Size}", _currentPoolSize);
                _metrics.RecordPoolScaled(_poolName, "DOWN", oldSize, newSize);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Scaling callback failed");
        }
    }

    #endregion;

    #region Clear and Dispose

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
        _scalingTimer.Dispose();
        _logger?.LogInformation("Object pool disposed. Type: {Type}", typeof(T).Name);
    }

    #endregion
}