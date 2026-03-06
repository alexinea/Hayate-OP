using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DotNetCore.HayateOP.Common;
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
    private readonly HayatePoolOptions _options;
    private readonly IHayateScalingStrategy _scalingStrategy;

    private readonly ILogger<HayateObjectPool<T>> _logger;
    private readonly IHayateMetrics _metrics;

    private readonly Timer _evictionTimer;
    private readonly Timer _scalingTimer;
    private readonly Timer _validateTimer;

    private long _totalCreated;
    private long _totalReturned;
    private long _totalMissed;
    private long _leakDetectedCount;

    private readonly HayatePoolStats _stats = new();
    private readonly object _statsLock = new();

    private readonly ConcurrentDictionary<T, HayateObject<T>> _objectMap = new();

    public HayateObjectPool(
        IHayateObjectPolicy<T> policy,
        HayatePoolOptions options,
        IHayateScalingStrategy scalingStrategy = null,
        ILogger<HayateObjectPool<T>> logger = null,
        IHayateMetrics metrics = null)
    {
        _name = typeof(T).Name;

        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _options = options ?? new HayatePoolOptions();
        _scalingStrategy = scalingStrategy ?? new ThresholdScalingStrategy();

        _logger = logger;
        _metrics = _options.EnableMetrics
            ? metrics ?? EmptyHayateMetrics.Instance
            : EmptyHayateMetrics.Instance;

        if (_options.MinPoolSize > _options.MaxPoolSize)
        {
            throw new ArgumentException("MinPoolSize cannot be greater than MaxPoolSize", nameof(options));
        }

        // 验证配置：确保 ShardCount > 0
        if (_options.ShardCount < 1)
        {
            throw new ArgumentException("ShardCount must be at least 1", nameof(options));
        }

        _shards = Enumerable
            .Range(0, _options.ShardCount)
            .Select(index => new Shard(_options, index))
            .ToArray();

        _evictionTimer = new Timer(EvictionCallback, null, _options.EvictionIntervalMs, _options.EvictionIntervalMs);
        _scalingTimer = new Timer(ScalingCallback, null, _options.ScalingIntervalMs, _options.ScalingIntervalMs);
        _validateTimer = new Timer(ValidateCallback, null, _options.ValidateIntervalMs, _options.ValidateIntervalMs);

        PreWarm();

        _logger?.LogInformation("Object pool initialized. Min:{Min} Max:{Max} Concurrent:{Concurrent}",
            _options.MinPoolSize, _options.MaxPoolSize, _options.MaxConcurrent);
    }

    public T Get()
    {
        return Get(_options.DefaultGetTimeout);
    }

    public T Get(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be non-negative");

        var sw = ValueStopwatch.StartNew();
        var spinWait = new SpinWait();

        while (true)
        {
            foreach (var shard in _shards)
            {
                //if (shard.TryTake(out var w, timeout - sw.Elapsed, _options.UseFairSemaphore, _logger))
                if (shard.TryTake(out var w, _options.UseFairSemaphore, _logger))
                {
                    bool shouldValidate = _options.ValidateOnBorrow;

                    // 分代，老年代跳过部分验证
                    if (w.Generation == 1)
                    {
                        w.ValidationSkipCount++;

                        if (w.ValidationSkipCount < _options.OldGenerationValidationInterval)
                        {
                            shouldValidate = false;
                        }
                        else
                        {
                            w.ValidationSkipCount = 0;
                        }
                    }

                    if (shouldValidate && !_policy.Validate(w.Value))
                    {
                        _logger?.LogWarning("[Shard {Index}] Object failed validation on borrow. Disposing. Type: {Type}", shard.Index, typeof(T).Name);
                        Destroy(w);
                        continue;
                    }

                    // 激活对象
                    _policy.ActivateObject(w.Value);

                    // 记录借出时间和调用栈（如果启用泄漏检测）
                    w.LastBorrowedAt = DateTime.UtcNow;
                    w.IsBorrowed = true;
                    w.BorrowTrace = _options.EnableLeakDetection ? Environment.StackTrace : null;

                    // 分代更新
                    if (DateTime.UtcNow - w.CreatedAt > TimeSpan.FromMilliseconds(_options.GenerationThresholdMs))
                        w.Generation = 1;

                    // 记录等待时间统计
                    var waitTime = (long)sw.Elapsed.TotalMilliseconds;
                    UpdateWaitTimeStats(waitTime);

                    // 记录指标
                    _metrics.RecordObjectAcquired(_name, w.Value, waitTime);

                    _logger?.LogDebug("Object borrowed from pool. Type: {Type} WaitTime: {WaitTime:F2}ms, shard: {ShardIndex}", typeof(T).Name, waitTime, shard.Index);

                    return w.Value;
                }
            }

            // 超时处理
            if (sw.Elapsed >= timeout)
            {
                Interlocked.Increment(ref _totalMissed);
                var newObj = _options.RejectPolicy switch
                {
                    HayatePoolRejectPolicy.Abort => throw new TimeoutException($"Pool timeout after {timeout.TotalSeconds} seconds"),
                    HayatePoolRejectPolicy.Block => throw new TimeoutException("Pool is full, block policy triggered"),
                    HayatePoolRejectPolicy.BlockTimeout => throw new TimeoutException($"Pool timeout after {timeout.TotalSeconds} seconds (BlockTimeout policy)"),
                    HayatePoolRejectPolicy.CreateNew => CreateWrappedObject().Value, // 创建新对象（不加入池）
                    _ => throw new TimeoutException("Pool timeout")
                };

                // 记录指标
                _metrics.RecordObjectMiss(_name, newObj);

                return newObj;
            }

            // 短暂等待后重试
            spinWait.SpinOnce();
        }
    }

    public async Task<T> GetAsync(CancellationToken cancellationToken = default)
    {
        var delay = TimeSpan.FromMilliseconds(1);

        while (!cancellationToken.IsCancellationRequested)
        {
            foreach (var shard in _shards)
            {
                //if (shard.TryTake(out var w, TimeSpan.Zero, _options.UseFairSemaphore))
                if (shard.TryTake(out var w, _options.UseFairSemaphore))
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
        if (!_objectMap.TryGetValue(item, out var w))
        {
            _logger?.LogWarning("Returned object does not belong to pool. Disposing. Type: {Type}", typeof(T).Name);

            Destroy(item);

            _metrics.RecordObjectReturned(_name, item, false);

            return;
        }

        // 有效性检查
        if (_options.ValidateOnReturn && !_policy.Validate(item))
        {
            _logger?.LogWarning("Object returned to pool. Disposing. Type: {Type}", typeof(T).Name);

            Destroy(w);

            _metrics.RecordObjectReturned(_name, item, false);

            return;
        }

        try
        {
            // 纯化对象
            _policy.PassivateObject(item);

            // 更新对象状态
            w.IsBorrowed = false;
            w.LastReturnedAt = DateTime.UtcNow;
            w.LeaseTimeMs = (long)(w.LastReturnedAt - w.LastBorrowedAt).TotalMilliseconds;

            // 重置对象
            _policy.Return(item);

            // 记录租赁时间统计
            UpdateLeaseTimeStats(w.LeaseTimeMs);

            // 归还到当前对应分片
            var shardIndex = Thread.GetCurrentProcessorId() % _shards.Length;
            var shard = _shards[shardIndex];
            shard.Add(w, _logger);

            Interlocked.Increment(ref _totalReturned);
            _metrics.RecordObjectReturned(_name, item, true);
            _logger?.LogDebug("Object returned to pool. Type: {Type} LeaseTime: {LeaseTime:F2}ms, shard: {ShardIndex}", typeof(T).Name, w.LeaseTimeMs, shardIndex);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error during object return validation. Disposing. Type: {Type}", typeof(T).Name);
            Destroy(w);
            _metrics.RecordObjectReturned(_name, item, false);
        }
    }

    #region 辅助方法

    private HayateObject<T> CreateWrappedObject()
    {
        for (var retry = 0; retry < _options.CreationRetryCount; retry++)
        {
            try
            {
                var o = _policy.Create();
                var w = new HayateObject<T>(o);
                _objectMap.TryAdd(o, w);
                Interlocked.Increment(ref _totalCreated);
                return w;
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

    private void PreWarm()
    {
        try
        {
            int perShard = _options.MinPoolSize / _shards.Length;
            int remainder = _options.MinPoolSize % _shards.Length;

            int totalPreWarmed = 0;
            for (var i = 0; i < _shards.Length; i++)
            {
                var shard = _shards[i];
                int count = perShard + (i < remainder ? 1 : 0); // 处理余数

                for (var j = 0; j < count; j++)
                {
                    shard.Add(CreateWrappedObject(), _logger);
                    totalPreWarmed++;
                }

                _logger?.LogInformation("[Shard {Index}] Pre-warmed with {Count} objects", shard.Index, perShard);
            }

            _logger?.LogInformation("Object pool pre-warmed with {Count} objects", perShard * _shards.Length);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error during pool pre-warming");
        }
    }

    private void Destroy(HayateObject<T> w)
    {
        if (w == null) return;

        try
        {
            _policy.DestroyObject(w.Value);
            if (w.Value is IDisposable d) d.Dispose();
            _objectMap.TryRemove(w.Value, out _);
            _logger?.LogDebug("Wrapped object destroyed. Type: {Type}", typeof(T).Name);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error during wrapped object destruction. Type: {Type}", typeof(T).Name);
        }
    }

    private void Destroy(T o)
    {
        if (o == null) return;

        try
        {
            _policy.DestroyObject(o);
            if (o is IDisposable d) d.Dispose();
            _objectMap.TryRemove(o, out _);
            _logger?.LogDebug("Object destroyed. Type: {Type}", typeof(T).Name);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error during object destruction. Type: {Type}", typeof(T).Name);
        }
    }

    private void UpdateWaitTimeStats(long waitTimeMs)
    {
        lock (_statsLock)
        {
            _stats.WaitTimeSum += waitTimeMs;
            _stats.WaitTimeCount++;
            if (waitTimeMs > _stats.MaxWaitTimeMs) _stats.MaxWaitTimeMs = waitTimeMs;
            if (waitTimeMs < _stats.MinWaitTimeMs) _stats.MinWaitTimeMs = waitTimeMs;
        }
    }

    private void UpdateLeaseTimeStats(long leaseTimeMs)
    {
        lock (_statsLock)
        {
            _stats.LeaseTimeSum += leaseTimeMs;
            _stats.LeaseTimeCount++;
            if (leaseTimeMs > _stats.MaxLeaseTimeMs) _stats.MaxLeaseTimeMs = leaseTimeMs;
            if (leaseTimeMs < _stats.MinLeaseTimeMs) _stats.MinLeaseTimeMs = leaseTimeMs;
        }
    }

    #endregion

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

                int evictedCount = 0;
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
                        shard.Remove(item);
                        Destroy(item);
                        evictedCount++;

                        _logger?.LogInformation("[Shard {Index}] Evicting object. Type: {Type} Expired: {Expired} IdleTooLong: {IdleTooLong} SoftIdle: {SoftIdle}",
                            shard.Index, typeof(T).Name, isExpired, isIdleTooLong, shouldEvictSoft);
                    }
                }

                if (evictedCount > 0)
                {
                    _logger.LogInformation("[Shard {Index}] Evicted {Count} objects from shard", shard.Index, evictedCount);
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
                    shard.Add(newW, _logger);
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
                    //if (shard.TryTake(out var w, TimeSpan.Zero, _options.UseFairSemaphore))
                    if (shard.TryTake(out var w, _options.UseFairSemaphore))
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
        if (!_options.ValidateWhileIdle) return;

        try
        {
            int invalidCount = 0;
            foreach (var shard in _shards)
            {
                foreach (var w in shard.GetAll())
                {
                    if (!w.IsBorrowed && !_policy.Validate(w.Value))
                    {
                        shard.Remove(w, _logger);
                        Destroy(w);
                        invalidCount++;
                        _logger?.LogWarning("Idle object failed validation and was evicted. Type: {Type}", typeof(T).Name);
                    }
                }
            }

            if (invalidCount > 0)
            {
                _logger?.LogWarning("Validation task removed {Count} invalid objects from pool", invalidCount);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Validate callback failed");
        }
    }

    #endregion

    #region Statistics and Snapshot

    public HayatePoolStats GetStats()
    {
        lock (_statsLock)
        {
            return new HayatePoolStats
            {
                PooledCount = _shards.Sum(s => s.Count),
                TotalCreated = Interlocked.Read(ref _totalCreated),
                TotalReturned = Interlocked.Read(ref _totalReturned),
                TotalMissed = Interlocked.Read(ref _totalMissed),
                AvailableSlots = _shards.Sum(s => s.Available),
                MinSize = _options.MinPoolSize,
                CurrentSize = _shards.Sum(s => s.Capacity),
                LeakDetectedCount = Interlocked.Read(ref _leakDetectedCount),
                WaitTimeSum = _stats.WaitTimeSum,
                WaitTimeCount = _stats.WaitTimeCount,
                LeaseTimeSum = _stats.LeaseTimeSum,
                LeaseTimeCount = _stats.LeaseTimeCount,
                MaxWaitTimeMs = _stats.MaxWaitTimeMs,
                MaxLeaseTimeMs = _stats.MaxLeaseTimeMs,
                MinWaitTimeMs = _stats.MinWaitTimeMs == double.MaxValue ? 0 : _stats.MinWaitTimeMs,
                MinLeaseTimeMs = _stats.MinLeaseTimeMs == double.MaxValue ? 0 : _stats.MinLeaseTimeMs
            };
        }
    }

    public HayatePoolSnapshot TakeSnapshot()
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

        return new HayatePoolSnapshot
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

    public void ReloadConfig(Action<HayatePoolOptions> configure)
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

        _objectMap.Clear();
        _logger?.LogInformation("Clearing object pool. Type: {Type}", typeof(T).Name);
    }

    public HayatePoolOptions GetOptions()
    {
        return new()
        {
            MinPoolSize = _options.MinPoolSize,
            MaxPoolSize = _options.MaxPoolSize,
            MaxConcurrent = _options.MaxConcurrent,
            EnableMetrics = _options.EnableMetrics,
            ScalingIntervalMs = _options.ScalingIntervalMs,
            ScaleUpThreshold = _options.ScaleUpThreshold,
            ScaleDownThreshold = _options.ScaleDownThreshold,
            ValidateOnBorrow = _options.ValidateOnBorrow,
            ValidateOnReturn = _options.ValidateOnReturn,
            ValidateWhileIdle = _options.ValidateWhileIdle,
            ValidateIntervalMs = _options.ValidateIntervalMs,
            MaxLifeTime = _options.MaxLifeTime,
            MaxIdleTime = _options.MaxIdleTime,
            SoftMinEvictableIdleTime = _options.SoftMinEvictableIdleTime,
            EvictionIntervalMs = _options.EvictionIntervalMs,
            NumTestsPerEvictionRun = _options.NumTestsPerEvictionRun,
            DefaultGetTimeout = _options.DefaultGetTimeout,
            UseFairSemaphore = _options.UseFairSemaphore,
            LeakDetectionThreshold = _options.LeakDetectionThreshold,
            EnableLeakDetection = _options.EnableLeakDetection,
            RejectPolicy = _options.RejectPolicy,
            CreationRetryCount = _options.CreationRetryCount,
            CreationRetryDelay = _options.CreationRetryDelay,
            ShardCount = _options.ShardCount,
            GenerationThresholdMs = _options.GenerationThresholdMs,
            OldGenerationValidationInterval = _options.OldGenerationValidationInterval
        };
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
        public int Index { get; }

        private readonly ConcurrentQueue<HayateObject<T>> _queue = new();
        private readonly int _maxSize;
        //private long _availableCount;
        private SpinWait _spinWait = new();

        // 公平锁
        private long _ticketCounter = 0;
        private long _nextTicket = 0;

        public Shard(HayatePoolOptions options, int index)
        {
            Index = index;
            _maxSize = options.MaxPoolSize / options.ShardCount;
            //_availableCount = 0;
        }

        public int Count => _queue.Count;

        public int Capacity => _maxSize;

        public int Available => _queue.Count;// (int)Interlocked.Read(ref _availableCount);

        public int BorrowedCount => _queue.Count(x => x.IsBorrowed);

        public void Add(HayateObject<T> w, ILogger logger = null)
        {
            if (w is null) throw new ArgumentNullException(nameof(w));

            if (_queue.Count >= _maxSize)
            {
                logger?.LogWarning("[Shard {Index}] capacity exceeded, object will be destroyed (shard size: {Size}, max: {Max})",
                    Index, _queue.Count, _maxSize);

                try
                {
                    if (w.Value is IDisposable d) d.Dispose();
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "Failed to dispose object when [shard {Index}] is full", Index);
                }

                return;
            }

            _queue.Enqueue(w);
            //Interlocked.Increment(ref _availableCount);
            logger?.LogDebug("[Shard {Index}] Object added to shard (current size: {Size})", Index, _queue.Count);
        }

        //public bool TryTake(out HayateObject<T> w, TimeSpan timeout, bool useFair, ILogger logger = null)
        public bool TryTake(out HayateObject<T> w, bool useFair, ILogger logger = null)
        {
            w = null;
            //var sw = ValueStopwatch.StartNew();
            long myTicket = 0;

            if (useFair)
            {
                myTicket = Interlocked.Increment(ref _ticketCounter) - 1;
            }

            try
            {
                //while (sw.Elapsed < timeout)
                //{
                if (useFair && Interlocked.Read(ref _nextTicket) != myTicket)
                {
                    //_spinWait.SpinOnce();
                    //continue;
                    return false;
                }

                //if (Interlocked.Read(ref _availableCount) > 0 && _queue.TryDequeue(out w))
                if (_queue.TryDequeue(out w))
                {
                    //Interlocked.Decrement(ref _availableCount);
                    if (useFair)
                    {
                        Interlocked.Increment(ref _nextTicket);
                    }

                    logger?.LogDebug("[Shard {Index}] Object taken from shard (current size: {Size})", Index, _queue.Count);
                    return true;
                }

                //_spinWait.SpinOnce();
                //}

                //if (useFair && Interlocked.Read(ref _nextTicket) == myTicket)
                //{
                //    Interlocked.Increment(ref _nextTicket);
                //}

                return false;

            }
            finally
            {
                // 公平模式：如果拿到了票但没获取到对象，归还票
                if (useFair && Interlocked.Read(ref _nextTicket) == myTicket)
                {
                    Interlocked.Increment(ref _nextTicket);
                }
            }
        }

        public void Remove(HayateObject<T> w, ILogger logger = null)
        {
            if (w == null) return;

            var ww = _queue.ToList();

            if (ww.Remove(w))
            {
                //Interlocked.Decrement(ref _availableCount);

                // 重建队列
                _queue.Clear();
                foreach (var l in ww)
                {
                    _queue.Enqueue(l);
                }
                logger?.LogDebug("[Shard {Index}] Object removed from shard (current size: {Size})", Index, _queue.Count);
            }
        }

        public IEnumerable<HayateObject<T>> GetAll() => _queue.ToArray();

        public void Clear(ILogger logger = null)
        {
            int clearedCount = 0;
            while (_queue.TryDequeue(out var w))
            {
                try
                {
                    if (w.Value is IDisposable d) d.Dispose();
                    clearedCount++;
                }
                catch
                {
                    // ignore
                }
            }

            //Interlocked.Exchange(ref _availableCount, 0);
            logger?.LogInformation("[Shard {Index}] Shard cleared, {Count} objects destroyed", Index, clearedCount);
        }
    }

    #endregion
}