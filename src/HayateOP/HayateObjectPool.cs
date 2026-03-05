using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DotNetCore.HayateOP.Internals;
using DotNetCore.HayateOP.Metrics;
using DotNetCore.HayateOP.Policies;
using DotNetCore.HayateOP.Scaling;
using Microsoft.Extensions.Logging;

namespace DotNetCore.HayateOP;

public class HayateObjectPool<T> : IHayateObjectPool<T>, IDisposable
    where T : class
{
    private readonly string _name;

    private readonly Shard[] _shards;
    private readonly IHayateObjectPolicy<T> _policy;
    private readonly HayateOpOptions _options;
    private readonly IHayateOpScalingStrategy _scalingStrategy;

    private readonly ILogger<HayateObjectPool<T>> _logger;
    private readonly IHayateOpMetrics _metrics;

    private readonly Timer _evictionTimer;
    private readonly Timer _scalingTimer;
    private readonly Timer _validateTimer;

    private long _totalCreated;
    private long _totalReturned;
    private long _totalMissed;
    private long _leakDetectedCount;

    private double _waitTimeSum;
    private double _leaseTimeSum;
    private double _maxWaitTime;
    private double _maxLeaseTime;
    private double _minWaitTime = double.MaxValue;
    private double _minLeaseTime = double.MaxValue;
    private long _waitTimeCount;
    private long _leaseTimeCount;

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
        HayateOpOptions options,
        IHayateOpScalingStrategy scalingStrategy = null,
        ILogger<HayateObjectPool<T>> logger = null,
        IHayateOpMetrics metrics = null)
    {
        _name = typeof(T).Name;

        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _options = options ?? new HayateOpOptions();
        _scalingStrategy = scalingStrategy ?? new ThresholdScalingStrategy();

        _logger = logger;
        _metrics = _options.EnableMetrics
            ? metrics ?? EmptyHayateOpMetrics.Instance
            : EmptyHayateOpMetrics.Instance;

        _shards = Enumerable.Range(0, _options.ShardCount).Select(_ => new Shard(_options)).ToArray();

        _evictionTimer = new Timer(EvictionCallback, null, _options.EvictionIntervalMs, _options.EvictionIntervalMs);
        _scalingTimer = new Timer(ScalingCallback, null, _options.ScalingIntervalMs, _options.ScalingIntervalMs);
        _validateTimer = new Timer(ValidateCallback, null, _options.ValidateIntervalMs, _options.ValidateIntervalMs);

        PreWarm();

        _logger?.LogInformation("Object pool initialized. Min:{Min} Max:{Max} Concurrent:{Concurrent}",
            _options.MinPoolSize, _options.MaxPoolSize, _options.MaxConcurrent);
    }

    private void PreWarm()
    {
        try
        {
            int preShard = _options.MinPoolSize / _shards.Length;

            foreach (var shard in _shards)
            {
                for (var i = 0; i < preShard; i++)
                {
                    shard.Add(CreateWrappedObject());
                }
            }

            _logger?.LogInformation("Object pool pre-warmed with {Count} objects", preShard * _shards.Length);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error during pool pre-warming");
        }
    }

    private T CreateWithRetry()
    {
        return CreateWrappedObject().Value;
    }

    private HayateObject<T> CreateWrappedObject()
    {
        for (var retry = 0; retry < _options.CreationRetryCount; retry++)
        {
            try
            {
                var o = _policy.Create();
                Interlocked.Increment(ref _totalCreated);
                return new HayateObject<T>(o);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error creating object. Retry {Retry}/{MaxRetries}", retry + 1, _options.CreationRetryCount);

                if (retry == _options.CreationRetryCount - 1)
                    throw new InvalidOperationException("Failed to create object after retries", ex);

                Thread.Sleep(_options.CreationRetryDelay);
            }
        }

        throw new InvalidOperationException("Failed to create object after retries");
    }

    public T Get()
    {
        return Get(_options.DefaultGetTimeout);
    }

    public T Get(TimeSpan timeout)
    {
        var sw = ValueStopwatch.StartNew();

        while (true)
        {
            foreach (var shard in _shards)
            {
                if (shard.TryTake(out var w, timeout - sw.Elapsed))
                {
                    if (_options.ValidateOnBorrow && !_policy.Validate(w.Value))
                    {
                        _logger?.LogWarning("Object failed validation on borrow. Disposing. Type: {Type}", typeof(T).Name);
                        Destroy(w);
                        continue;
                    }

                    // 激活对象（如果需要）
                    _policy.ActivateObject(w.Value);

                    // 记录借出时间和调用栈（如果启用泄漏检测）
                    w.LastBorrowedAt = DateTime.UtcNow;
                    w.IsBorrowed = true;
                    w.BorrowTrace = _options.EnableLeakDetection ? Environment.StackTrace : null;

                    // 分代更新
                    if (DateTime.UtcNow - w.CreatedAt > TimeSpan.FromMilliseconds(_options.GenerationThresholdMs))
                        w.Generation++;

                    // 记录等待时间统计
                    var waitTime = sw.Elapsed.TotalMilliseconds;
                    UpdateWaitTimeStats(waitTime);

                    _logger?.LogDebug("Object borrowed from pool. Type: {Type} WaitTime: {WaitTime:F2}ms", typeof(T).Name, waitTime);

                    return w.Value;
                }
            }

            // 超时处理
            if (sw.Elapsed >= timeout)
            {
                Interlocked.Increment(ref _totalMissed);
                return _options.RejectPolicy switch
                {
                    HayateOpRejectPolicy.Abort => throw new TimeoutException($"Pool timeout after {timeout.TotalSeconds} seconds"),
                    HayateOpRejectPolicy.Block => throw new TimeoutException("Pool is full, block policy triggered"),
                    HayateOpRejectPolicy.BlockTimeout => throw new TimeoutException($"Pool timeout after {timeout.TotalSeconds} seconds (BlockTimeout policy)"),
                    HayateOpRejectPolicy.CreateNew => CreateWithRetry(), // 创建新对象（不加入池）
                    _ => throw new TimeoutException("Pool timeout")
                };
            }

            // 短暂等待后重试
            Thread.Sleep(1);
        }
    }

    public async Task<T> GetAsync(CancellationToken cancellationToken = default)
    {
        var delay = TimeSpan.FromMilliseconds(1);

        while (!cancellationToken.IsCancellationRequested)
        {
            foreach (var shard in _shards)
            {
                if (shard.TryTake(out var w, TimeSpan.Zero))
                {
                    if (_options.ValidateOnBorrow && !_policy.Validate(w.Value))
                    {
                        Destroy(w);
                        continue;
                    }

                    _policy.ActivateObject(w.Value);

                    w.IsBorrowed = true;
                    w.LastBorrowedAt = DateTime.UtcNow;

                    return w.Value;
                }
            }

            await Task.Delay(delay, cancellationToken);
        }

        throw new TaskCanceledException();
    }

    public void Return(T item)
    {
        if (item is null)
        {
            _logger?.LogWarning("Returned null object to pool. Type: {Type}", typeof(T).Name);
            return;
        }

        // 查找对应的包装对象，验证是否属于池中对象
        var w = _shards.SelectMany(s => s.GetAll()).FirstOrDefault(x => x.Value == item);
        if (w == null)
        {
            _logger?.LogWarning("Returned object does not belong to pool. Disposing. Type: {Type}", typeof(T).Name);
            Destroy(item);
            return;
        }

        // 有效性检查
        if (_options.ValidateOnReturn && !_policy.Validate(item))
        {
            _logger?.LogWarning("Object returned to pool. Disposing. Type: {Type}", typeof(T).Name);
            Destroy(w);
            return;
        }

        try
        {
            // 纯化对象
            _policy.PassivateObject(item);

            // 更新对象状态
            w.IsBorrowed = false;
            w.LastReturnedAt = DateTime.UtcNow;
            w.LeaseTimeMs = (w.LastReturnedAt - w.LastBorrowedAt).TotalMilliseconds;

            // 重置对象
            _policy.Return(item);

            // 记录租赁时间统计
            UpdateLeaseTimeStats(w.LeaseTimeMs);

            // 归还到当前对应分片
            var shard = _shards[Thread.GetCurrentProcessorId() % _shards.Length];
            shard.Add(w);

            Interlocked.Increment(ref _totalReturned);
            _metrics.RecordObjectReturned(_name, item, true);
            _logger?.LogDebug("Object returned to pool. Type: {Type} LeaseTime: {LeaseTime:F2}ms", typeof(T).Name, w.LeaseTimeMs);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error during object return validation. Disposing. Type: {Type}", typeof(T).Name);
            Destroy(w);
        }
    }

    private void Destroy(HayateObject<T> w)
    {
        if (w == null) return;

        try
        {
            _policy.DestroyObject(w.Value);
            if (w.Value is IDisposable d) d.Dispose();
            _logger?.LogDebug("Wrapped object destroyed. Type: {Type}", typeof(T).Name);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error during wrapped object destruction. Type: {Type}", typeof(T).Name);
        }
    }

    private void Destroy(T item)
    {
        if (item == null) return;

        try
        {
            _policy.DestroyObject(item);
            if (item is IDisposable d) d.Dispose();
            _logger?.LogDebug("Object destroyed. Type: {Type}", typeof(T).Name);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error during object destruction. Type: {Type}", typeof(T).Name);
        }
    }

    private void UpdateWaitTimeStats(double waitTimeMs)
    {
        Interlocked.Add(ref _waitTimeSum, waitTimeMs);
        Interlocked.Increment(ref _waitTimeCount);

        if (waitTimeMs > _maxWaitTime)
            Interlocked.Exchange(ref _maxWaitTime, waitTimeMs);

        if (waitTimeMs < _minWaitTime)
            Interlocked.Exchange(ref _minWaitTime, waitTimeMs);
    }

    private void UpdateLeaseTimeStats(double leaseTimeMs)
    {
        Interlocked.Add(ref _leaseTimeSum, leaseTimeMs);
        Interlocked.Increment(ref _leaseTimeCount);

        if (leaseTimeMs > _maxLeaseTime)
            Interlocked.Exchange(ref _maxLeaseTime, leaseTimeMs);

        if (leaseTimeMs < _minLeaseTime)
            Interlocked.Exchange(ref _minLeaseTime, leaseTimeMs);
    }

    #region Eviction

    private void EvictionCallback(object state)
    {
        try
        {
            var now = DateTime.UtcNow;
            foreach (var shard in _shards)
            {
                // 分批检查
                var itemsToCheck = shard.GetAll()
                    .Take(_options.NumTestsPerEvictionRun)
                    .ToArray();

                foreach (var item in itemsToCheck)
                {
                    // 跳过正在使用的对象
                    if (item.IsBorrowed) continue;

                    // 判断是否需要驱逐
                    var isExpired = now - item.CreatedAt > _options.MaxLifeTime;
                    var isIdleTooLong = now - item.LastReturnedAt > _options.MaxIdleTime;
                    var isSoftIdle = now - item.LastReturnedAt > _options.SoftMinEvictableIdleTime;

                    // 软最小空闲逻辑
                    // 只有当空闲数超过最小池大小时才驱逐软空闲对象
                    var shouldEvictSoft = isSoftIdle && shard.Count > _options.MinPoolSize / _shards.Length;

                    if (isExpired || isIdleTooLong || shouldEvictSoft)
                    {
                        _logger?.LogInformation("Evicting object. Type: {Type} Expired: {Expired} IdleTooLong: {IdleTooLong} SoftIdle: {SoftIdle}",
                            typeof(T).Name, isExpired, isIdleTooLong, shouldEvictSoft);

                        shard.Remove(item);
                        Destroy(item);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Eviction callback failed");
        }
    }

    #endregion

    #region Scaling

    private void ScalingCallback(object state)
    {
        try
        {
            var totalPooled = _shards.Sum(s => s.Count);
            var currentCapacity = _shards.Sum(s => s.Capacity);
            var targetCapacity = _scalingStrategy.CalculateNewSize(currentCapacity, totalPooled, _options);

            if (targetCapacity > currentCapacity)
            {
                // 扩容
                var addCount = targetCapacity - currentCapacity;
                for (var i = 0; i < addCount; i++)
                {
                    var shard = _shards[i % _shards.Length];
                    var newW = CreateWrappedObject();
                    shard.Add(newW);
                }

                _logger?.LogInformation("Pool scaled UP. Target capacity: {TargetCapacity} Current capacity: {CurrentCapacity}", targetCapacity, currentCapacity);
                _metrics.RecordPoolScaled(_name, "UP", currentCapacity, targetCapacity);
            }
            else if (targetCapacity < currentCapacity)
            {
                // 缩容
                var removeCount = currentCapacity - targetCapacity;
                for (var i = 0; i < removeCount; i++)
                {
                    var shard = _shards[i % _shards.Length];
                    if (shard.TryTake(out var w, TimeSpan.Zero))
                    {
                        Destroy(w);
                    }
                }

                _logger?.LogInformation("Pool scaled DOWN. Target capacity: {TargetCapacity} Current capacity: {CurrentCapacity}", targetCapacity, currentCapacity);
                _metrics.RecordPoolScaled(_name, "DOWN", currentCapacity, targetCapacity);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Scaling callback failed");
        }
    }

    #endregion;

    #region Validate

    private void ValidateCallback(object state)
    {
        if (!_options.ValidateWhiteIdle) return;

        try
        {
            foreach (var shard in _shards)
            {
                foreach (var w in shard.GetAll().ToArray())
                {
                    if (!w.IsBorrowed && !_policy.Validate(w.Value))
                    {
                        shard.Remove(w);
                        Destroy(w);
                        _logger?.LogWarning("Idle object failed validation and was evicted. Type: {Type}", typeof(T).Name);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Validate callback failed");
        }
    }

    #endregion

    #region Statistics and Snapshot

    public HayateOpStats GetStats()
    {
        return new HayateOpStats
        {
            PooledCount = _shards.Sum(s => s.Count),
            TotalCreated = Interlocked.Read(ref _totalCreated),
            TotalReturned = Interlocked.Read(ref _totalReturned),
            TotalMissed = Interlocked.Read(ref _totalMissed),
            AvailableSlots = _shards.Sum(s => s.Available),
            MinSize = _options.MinPoolSize,
            CurrentSize = _shards.Sum(s => s.Capacity),
            LeakDetectedCount = Interlocked.Read(ref _leakDetectedCount),
            AverageWaitTimeMs = _waitTimeCount > 0 ? _waitTimeSum / _waitTimeCount : 0,
            AverageLeaseTimeMs = _leaseTimeCount > 0 ? _leaseTimeSum / _leaseTimeCount : 0,
            MaxWaitTimeMs = _maxWaitTime,
            MaxLeaseTimeMs = _maxLeaseTime,
            MinWaitTimeMs = _minWaitTime == double.MaxValue ? 0 : _minWaitTime,
            MinLeaseTimeMs = _minLeaseTime == double.MaxValue ? 0 : _minLeaseTime,
            WaitTimeCount = _waitTimeCount,
            LeaseTimeCount = _leaseTimeCount
        };
    }

    public HayateOpSnapshot TakeSnapshot()
    {
        var leakTraces = new List<string>();
        var now = DateTime.UtcNow;

        foreach (var shard in _shards)
        {
            foreach (var w in shard.GetAll())
            {
                // 检查泄露
                if (w.IsBorrowed && now - w.LastBorrowedAt > _options.LeakDetectionThreshold)
                {
                    leakTraces.Add(w.BorrowTrace ?? "No stack trace available");
                    Interlocked.Increment(ref _leakDetectedCount);
                }
            }
        }

        return new HayateOpSnapshot
        {
            Timestamp = now,
            PooledCount = _shards.Sum(s => s.Count),
            BorrowedCount = _shards.Sum(s => s.BorrowedCount),
            TotalCreated = Interlocked.Read(ref _totalCreated),
            TotalMissed = Interlocked.Read(ref _totalMissed),
            LeakCount = Interlocked.Read(ref _leakDetectedCount),
            LeakTraces = leakTraces.AsReadOnly()
        };
    }

    #endregion

    #region ReloadConfig

    public void ReloadConfig(Action<HayateOpOptions> configure)
    {
        if (configure is null) throw new ArgumentNullException(nameof(configure));

        configure(_options);

        _logger?.LogInformation("Pool configuration reloaded. Type: {Type} NewConfig: {@Config}", typeof(T).Name, _options);
    }

    #endregion

    #region Clear and Dispose

    public void Clear()
    {
        foreach (var shard in _shards)
        {
            shard.Clear();
        }

        _logger?.LogInformation("Clearing object pool. Type: {Type}", typeof(T).Name);
    }

    public void Dispose()
    {
        Clear();
        _evictionTimer?.Dispose();
        _scalingTimer?.Dispose();
        _validateTimer?.Dispose();
        _logger?.LogInformation("Object pool disposed. Type: {Type}", typeof(T).Name);
    }

    #endregion

    #region Shard

    private class Shard
    {
        private readonly ConcurrentQueue<HayateObject<T>> _queue = new();
        private readonly SemaphoreSlim _semaphore;
        private readonly int _maxSize;

        public Shard(HayateOpOptions options)
        {
            _maxSize = options.MaxPoolSize / options.ShardCount;
            _semaphore = new(0, _maxSize);
        }

        public int Count => _queue.Count;

        public int Capacity => _maxSize;

        public int Available => _semaphore.CurrentCount;

        public int BorrowedCount => _queue.Count(x => x.IsBorrowed);

        public void Add(HayateObject<T> w)
        {
            if (w is null) throw new ArgumentNullException(nameof(w));

            if (_queue.Count >= _maxSize)
            {
                throw new InvalidOperationException("Shard is full, cannot add more objects");
            }

            _queue.Enqueue(w);
            _semaphore.Release();
        }

        public bool TryTake(out HayateObject<T> w, TimeSpan timeout)
        {
            w = null;

            if (_semaphore.Wait(timeout))
            {
                if (_queue.TryDequeue(out w))
                    return true;
                // 失败时补偿信号量
                _semaphore.Release();
            }

            return false;
        }

        public void Remove(HayateObject<T> w)
        {
            if (w == null) return;

            var ww = _queue.ToList();

            if (ww.Remove(w))
            {
                // 调整信号量计数
                _ = _semaphore.WaitAsync();

                // 重建队列
                _queue.Clear();
                foreach (var l in ww)
                {
                    _queue.Enqueue(l);
                }
            }
        }

        public IEnumerable<HayateObject<T>> GetAll() => _queue.ToArray();

        public void Clear()
        {
            while (_queue.TryDequeue(out var w))
            {
                try
                {
                    if (w.Value is IDisposable d) d.Dispose();
                }
                catch
                {
                    // ignore
                }
            }

            // 充值信号量
            while (_semaphore.CurrentCount > 0)
            {
                _ = _semaphore.WaitAsync(TimeSpan.Zero);
            }
        }
    }

    #endregion
}