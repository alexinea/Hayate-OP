using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DotNetCore.HayateOP.Common;
using DotNetCore.HayateOP.Logging;
using DotNetCore.HayateOP.Metrics;
using DotNetCore.HayateOP.Policies;
using DotNetCore.HayateOP.Scaling;

namespace DotNetCore.HayateOP;

public partial class HayatePoolBasic<T> : IHayateObjectPool<T>
    where T : class
{
    private readonly string _name;

    private readonly Shard[] _shards;
    private readonly IHayateObjectPolicy<T> _policy;
    private readonly HayatePoolOptions _options;
    private readonly IHayateScalingStrategy _scalingStrategy;

    private readonly IHayateLogger _logger;
    private readonly IHayateMetrics _metrics;

    private readonly ConcurrentDictionary<T, HayateObject<T>> _objectMap = new();

    // 后台任务
    private Timer _evictionTimer;
    private Timer _scalingTimer;
    private Timer _validationTimer;

    // 扩缩容冷却控制，防止抖动
    private DateTime _lastScaleUpTime = DateTime.MinValue;
    private DateTime _lastScaleDownTime = DateTime.MinValue;

    // 统计数据
    private long _totalCreated;
    private long _totalReleased;
    private long _totalMissed;
    private long _leakDetectedCount;
    private readonly HayatePoolStats _stats = new();
    private readonly object _statsLock = new();

    internal HayatePoolBasic(
        IHayateObjectPolicy<T> policy,
        HayatePoolOptions options,
        IHayateScalingStrategy scalingStrategy,
        IHayateMetrics metrics,
        IHayateLogger logger,
        string poolName)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _scalingStrategy = scalingStrategy ?? throw new ArgumentNullException(nameof(scalingStrategy));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _name = poolName ?? throw new ArgumentNullException(nameof(poolName));

        if (!_options.IsValid())
        {
            throw new InvalidOperationException($"Invalid pool '{_name}' configuration: " + _options);
        }

        // 初始化分片
        _shards = Enumerable
            .Range(0, _options.ShardCount)
            .Select(index => new Shard(_options, index, _logger))
            .ToArray();
    }

    #region Initialized

    internal void PreWarm()
    {
        try
        {
            // 计算每个分片的最大容量
            var perShardMax = _options.MaxPoolSize / _shards.Length;
            var remainderMax = _options.MaxPoolSize % _shards.Length;

            // 计算每个分片的预热数量（不超过分片容量）
            int perShard = _options.MinPoolSize / _shards.Length;
            int remainder = _options.MinPoolSize % _shards.Length;
            int totalPreWarmed = 0;

            for (var i = 0; i < _shards.Length; i++)
            {
                var shard = _shards[i];
                int shardMax = perShardMax + (i < remainderMax ? 1 : 0);
                int count = perShard + (i < remainder ? 1 : 0); // 处理余数

                // 确保预热数量不超过分片容量
                count = Math.Min(count, shardMax);

                for (var j = 0; j < count; j++)
                {
                    shard.Add(CreateWrappedObject());
                    totalPreWarmed++;
                }

                _logger.LogInformation("[Shard {Index}] Pre-warmed with {Count} objects", shard.Index, perShard);
            }

            _logger.LogInformation("Object pool [{PoolName}] pre-warmed with {Count} objects", _name, totalPreWarmed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during pool [{PoolName}] pre-warming", _name);
        }
    }

    internal void StartBackgroundTasks()
    {
        if (_options.EnableEviction)
        {
            _evictionTimer = new Timer(EvictionCallback, null, _options.EvictionIntervalMs, _options.EvictionIntervalMs);
        }

        if (_options.EnableAutoScaling)
        {
            _scalingTimer = new Timer(ScalingCallback, null, _options.ScalingIntervalMs, _options.ScalingIntervalMs);
        }

        if (_options.EnableValidation)
        {
            _validationTimer = new Timer(ValidateCallback, null, _options.ValidateIntervalMs, _options.ValidateIntervalMs);
        }
    }

    #endregion

    #region Core methods

    public T Acquire()
    {
        return Acquire(_options.DefaultAcquireTimeout);
    }

    public T Acquire(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be non-negative");

        var sw = ValueStopwatch.StartNew();
        var spinWait = new SpinWait();

        while (true)
        {
            foreach (var shard in _shards)
            {
                if (shard.TryTake(out var w, _options.UseFairMode))
                {
                    #region 分代验证逻辑

                    // 仅开启分代 + 验证时执行
                    bool shouldValidate = _options.EnableValidation && _options.ValidateOnBorrow;

                    // 分代，老年代跳过部分验证
                    if (_options.EnableGenerationOptimization && shouldValidate && w.Generation == 1)
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

                    #endregion

                    #region 对象验证，仅仅开启验证时执行，并且分代优化可能会跳过部分验证

                    // 有效性检查
                    if (shouldValidate && !_policy.Validate(w.Value))
                    {
                        _logger.LogWarning("[Shard {Index}] Object failed validation on borrow. Disposing. Type: {Type}", shard.Index, typeof(T).Name);
                        Destroy(w);
                        continue;
                    }

                    #endregion

                    #region 核心对象处理

                    // 激活对象
                    _policy.OnAcquire(w.Value);

                    // 记录借出时间和调用栈（如果启用泄漏检测）
                    w.IsBorrowed = true;

                    if (_options.EnableEviction || _options.EnableLeakDetection || _options.EnableGenerationOptimization)
                    {
                        w.LastBorrowedAt = DateTime.UtcNow;
                    }

                    if (_options.EnableLeakDetection)
                    {
                        w.AcquireTrace = Environment.StackTrace;
                    }

                    // 分代升级，仅开启分代优化时执行
                    if (_options.EnableGenerationOptimization &&
                        DateTime.UtcNow - w.CreatedAt > TimeSpan.FromMilliseconds(_options.GenerationThresholdMs))
                    {
                        w.Generation = 1;
                    }

                    #endregion

                    #region 指标统计，仅开启指标时执行

                    if (_options.EnableMetrics)
                    {
                        // 记录等待时间统计
                        var waitTime = (long)sw.Elapsed.TotalMilliseconds;
                        UpdateWaitTimeStats(waitTime);
                        _metrics.RecordObjectAcquired(_name, w.Value, waitTime);
                        _logger.LogDebug("Object borrowed from pool. Type: {Type} WaitTime: {WaitTime:F2}ms, shard: {ShardIndex}", typeof(T).Name, waitTime, shard.Index);
                    }
                    else
                    {
                        _logger.LogDebug("Object borrowed from pool. Type: {Type} shard: {ShardIndex}", typeof(T).Name, shard.Index);
                    }

                    #endregion


                    return w.Value;
                }
            }

            // 超时处理
            if (sw.Elapsed >= timeout)
            {
                if (_options.EnableMetrics)
                {
                    Interlocked.Increment(ref _totalMissed);
                }

                // 仅开启扩缩容时，触发强制扩容
                if (_options.EnableAutoScaling)
                {
                    ForceScaleUpOneStep();
                }

                var newObj = _options.RejectPolicy switch
                {
                    HayatePoolRejectPolicy.Abort => throw new TimeoutException($"Pool timeout after {timeout.TotalSeconds} seconds"),
                    HayatePoolRejectPolicy.Block => throw new TimeoutException("Pool is full, block policy triggered"),
                    HayatePoolRejectPolicy.BlockTimeout => throw new TimeoutException($"Pool timeout after {timeout.TotalSeconds} seconds (BlockTimeout policy)"),
                    HayatePoolRejectPolicy.CreateNew => CreateWrappedObject().Value, // 创建新对象（不加入池）
                    _ => throw new TimeoutException($"HayatePool [{_name}] acquire timeout")
                };

                if (_options.EnableMetrics)
                {
                    // 记录指标
                    _metrics.RecordObjectMiss(_name, newObj);
                }

                return newObj;
            }

            // 短暂等待后重试
            spinWait.SpinOnce();
        }
    }

    public async Task<T> AcquireAsync(CancellationToken cancellationToken = default)
    {
        var delay = TimeSpan.FromMilliseconds(1);

        while (!cancellationToken.IsCancellationRequested)
        {
            foreach (var shard in _shards)
            {
                //if (shard.TryTake(out var w, TimeSpan.Zero, _options.UseFairMode))
                if (shard.TryTake(out var w, _options.UseFairMode))
                {
                    if (_options.ValidateOnBorrow && !_policy.Validate(w.Value))
                    {
                        Destroy(w);
                        continue;
                    }

                    _policy.OnAcquire(w.Value);

                    w.IsBorrowed = true;

                    if (_options.EnableEviction || _options.EnableLeakDetection)
                    {
                        w.LastBorrowedAt = DateTime.UtcNow;
                    }

                    return w.Value;
                }
            }

            await Task.Delay(delay, cancellationToken);
        }

        throw new TaskCanceledException();
    }

    public void Release(T item)
    {
        if (item is null)
        {
            _logger.LogWarning("Returned null object to pool. Type: {Type}", typeof(T).Name);
            return;
        }

        // 查找对应的包装对象，验证是否属于池中对象
        if (!_objectMap.TryGetValue(item, out var w))
        {
            _logger.LogWarning("Returned object does not belong to pool. Disposing. Type: {Type}", typeof(T).Name);

            Destroy(item);

            _metrics.RecordObjectReleased(_name, item, false);

            return;
        }

        #region 归还验证，仅开启验证时执行

        if (_options.EnableValidation && _options.ValidateOnReturn && !_policy.Validate(item))
        {
            _logger.LogWarning("Object returned to pool. Disposing. Type: {Type}", typeof(T).Name);

            Destroy(w);

            _metrics.RecordObjectReleased(_name, item, false);

            return;
        }

        #endregion

        try
        {
            #region 核心归还处理

            // 纯化对象
            _policy.OnPassivate(item);

            // 更新对象状态
            w.IsBorrowed = false;

            if (_options.EnableEviction || _options.EnableMetrics)
            {
                w.LastReleasedAt = DateTime.UtcNow;
                w.LeaseTimeMs = (long)(w.LastReleasedAt - w.LastBorrowedAt).TotalMilliseconds;
            }

            // 重置对象
            _policy.OnRelease(item);

            #endregion

            #region 指标统计（仅开启指标时执行）

            if (_options.EnableMetrics)
            {
                // 记录租赁时间统计
                UpdateLeaseTimeStats(w.LeaseTimeMs);
                Interlocked.Increment(ref _totalReleased);
                _metrics.RecordObjectReleased(_name, item, true);
            }

            #endregion

            #region 归还到分片

            // 归还到当前对应分片
            var shardIndex = _options.EnableSharding
                ? Thread.GetCurrentProcessorId() % _shards.Length
                : 0;

            var shard = _shards[shardIndex];
            shard.Add(w);

            #endregion

            _logger.LogDebug("Object returned to pool. Type: {Type} LeaseTime: {LeaseTime:F2}ms, shard: {ShardIndex}", typeof(T).Name, w.LeaseTimeMs, shardIndex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during object return validation. Disposing. Type: {Type}", typeof(T).Name);
            Destroy(w);
            if (_options.EnableMetrics)
            {
                _metrics.RecordObjectReleased(_name, item, false);
            }
        }
    }

    #endregion

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
                if (_options.EnableMetrics) Interlocked.Increment(ref _totalCreated);
                return w;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating object. Retry {Retry}/{MaxRetries}", retry + 1, _options.CreationRetryCount);

                if (retry == _options.CreationRetryCount - 1)
                    throw new InvalidOperationException("Failed to create object after retries", ex);

                Thread.Sleep(_options.CreationRetryDelay);
            }
        }

        throw new InvalidOperationException("Failed to create object after retries");
    }

    private void ForceScaleUpOneStep()
    {
        try
        {
            int currentTotal = _shards.Sum(s => s.Capacity);
            if (currentTotal >= _options.MaxPoolSize) return;
            if ((DateTime.UtcNow - _lastScaleUpTime).TotalSeconds < _options.ScaleUpCooldownSeconds) return;

            // 每次超时 +5 个，防止雪崩
            int add = Math.Min(_options.ScaleUpStep, _options.MaxPoolSize - currentTotal);
            for (var i = 0; i < add; i++)
            {
                var shard = _shards[i % _shards.Length];
                shard.Add(CreateWrappedObject());
            }

            _lastScaleUpTime = DateTime.UtcNow;
            _logger.LogWarning("FORCE SCALE UP (because timeout) → total: {Total}", _shards.Sum(s => s.Capacity));

            if (_options.EnableMetrics)
            {
                _metrics.RecordPoolScaled(_name, "ForceUp", currentTotal, currentTotal + add);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Force scale up failed");
        }
    }

    private void Destroy(HayateObject<T> w)
    {
        if (w == null) return;

        try
        {
            _policy.OnDestroy(w.Value);
            if (w.Value is IDisposable d) d.Dispose();
            _objectMap.TryRemove(w.Value, out _);
            _logger.LogDebug("Wrapped object destroyed. Type: {Type}", typeof(T).Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during wrapped object destruction. Type: {Type}", typeof(T).Name);
        }
    }

    private void Destroy(T o)
    {
        if (o == null) return;

        try
        {
            _policy.OnDestroy(o);
            if (o is IDisposable d) d.Dispose();
            _objectMap.TryRemove(o, out _);
            _logger.LogDebug("Object destroyed. Type: {Type}", typeof(T).Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during object destruction. Type: {Type}", typeof(T).Name);
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

    #region Background Tasks

    private void EvictionCallback(object state)
    {
        if (!_options.EnableEviction) return;
        try
        {
            var now = DateTime.UtcNow;
            foreach (var shard in _shards)
            {
                // 分批检查
                var itemsToCheck = shard.GetAll().Take(_options.NumTestsPerEvictionRun).ToArray();
                int evictedCount = 0;

                foreach (var item in itemsToCheck)
                {
                    // 跳过正在使用的对象
                    if (item.IsBorrowed) continue;

                    // 判断是否需要驱逐
                    var isExpired = now - item.CreatedAt > _options.MaxLifeTime;
                    var isIdleTooLong = now - item.LastReleasedAt > _options.MaxIdleTime;
                    var isSoftIdle = now - item.LastReleasedAt > _options.SoftMinEvictableIdleTime;

                    // 软最小空闲逻辑
                    // 只有当空闲数超过最小池大小时才驱逐软空闲对象
                    var shouldEvictSoft = isSoftIdle && shard.Count > _options.MinPoolSize / _shards.Length;

                    if (isExpired || isIdleTooLong || shouldEvictSoft)
                    {
                        shard.Remove(item);
                        Destroy(item);
                        evictedCount++;

                        _logger.LogInformation("[Shard {Index}] Evicting object. Type: {Type} Expired: {Expired} IdleTooLong: {IdleTooLong} SoftIdle: {SoftIdle}",
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
            _logger.LogError(ex, "Eviction callback failed");
        }
    }

    private void ScalingCallback(object state)
    {
        if (_options.EnableAutoScaling) return;
        try
        {
            var totalIdle = _shards.Sum(s => s.Count);
            var currentCapacity = _shards.Sum(s => s.Capacity);
            var targetCapacity = _scalingStrategy.CalculateNewSize(currentCapacity, totalIdle, _options);

            // 扩容冷却时间 3 秒，缩容冷却时间 15 秒，防止频繁抖动
            var canScaleUp = (DateTime.UtcNow - _lastScaleUpTime).TotalSeconds >= 3;
            var canScaleDown = (DateTime.UtcNow - _lastScaleDownTime).TotalSeconds >= 15;

            if (targetCapacity > currentCapacity && canScaleUp)
            {
                // 扩容
                var add = targetCapacity - currentCapacity;
                for (var i = 0; i < add; i++)
                {
                    var shard = _shards[i % _shards.Length];
                    shard.Add(CreateWrappedObject());
                }

                _lastScaleUpTime = DateTime.UtcNow;
                _logger.LogInformation("Pool [{PoolName}] scaled UP. {CurrentCapacity} → {TargetCapacity}", _name, currentCapacity, targetCapacity);
                if (_options.EnableMetrics)
                {
                    _metrics.RecordPoolScaled(_name, "UP", currentCapacity, targetCapacity);
                }
            }
            else if (targetCapacity < currentCapacity && canScaleDown && totalIdle < currentCapacity * 0.4f)
            {
                // 缩容
                var remove = currentCapacity - targetCapacity;
                int removed = 0;

                foreach (var shard in _shards)
                {
                    if (removed >= remove) break;
                    if (shard.Count <= _options.MinPoolSize / _shards.Length) continue;

                    while (shard.Count > _options.MinPoolSize / _shards.Length && removed < remove)
                    {
                        if (shard.TryTake(out var w, _options.UseFairMode))
                        {
                            Destroy(w);
                            removed++;
                        }
                        else break;
                    }
                }

                _lastScaleDownTime = DateTime.UtcNow;
                _logger.LogInformation("Pool [{PoolName}] scaled DOWN. {CurrentCapacity} → {TargetCapacity}", _name, currentCapacity, targetCapacity);

                if (_options.EnableMetrics)
                {
                    _metrics.RecordPoolScaled(_name, "DOWN", currentCapacity, targetCapacity);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scaling callback failed");
        }
    }

    private void ValidateCallback(object state)
    {
        if (!_options.EnableValidation || !_options.ValidateWhileIdle) return;

        try
        {
            int invalidCount = 0;
            foreach (var shard in _shards)
            {
                foreach (var w in shard.GetAll())
                {
                    if (!w.IsBorrowed && !_policy.Validate(w.Value))
                    {
                        shard.Remove(w);
                        Destroy(w);
                        invalidCount++;
                        _logger.LogWarning("Idle object failed validation and was evicted. Type: {Type}", typeof(T).Name);
                    }
                }
            }

            if (invalidCount > 0)
            {
                _logger.LogWarning("Validation task removed {Count} invalid objects from pool", invalidCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Validate callback failed");
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
                TotalReleased = Interlocked.Read(ref _totalReleased),
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

        // 仅开启泄漏检测时执行扫描
        if (_options.EnableLeakDetection)
        {
            foreach (var shard in _shards)
            {
                foreach (var w in shard.GetAll())
                {
                    // 检查泄露
                    if (w.IsBorrowed && now - w.LastBorrowedAt > _options.LeakDetectionThreshold)
                    {
                        leakTraces.Add(w.AcquireTrace ?? "No stack trace available");
                        Interlocked.Increment(ref _leakDetectedCount);
                    }
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

        lock (_options)
        {
            configure(_options);
            _options.ApplyFeatureSwitches(); // 强制修正配置
            _logger.LogInformation("Pool configuration reloaded. Type: {Type} NewConfig: {@Config}", typeof(T).Name, _options);
        }
    }

    #endregion

    #region Clear and Dispose

    public void Clear()
    {
        foreach (var shard in _shards) shard.Clear();
        _objectMap.Clear();
        _logger.LogInformation("Clearing object pool. Type: {Type}", typeof(T).Name);
    }

    public HayatePoolOptions GetOptions()
    {
        return _options.CopyTo();
    }

    public void Dispose()
    {
        Clear();
        _evictionTimer?.Dispose();
        _scalingTimer?.Dispose();
        _validationTimer?.Dispose();
        _logger.LogInformation("Object pool disposed. Type: {Type}", typeof(T).Name);
    }

    #endregion

    #region Shard

    internal class Shard
    {
        public int Index { get; }

        private readonly ConcurrentQueue<HayateObject<T>> _queue = new();

        private readonly int _maxSize;

        // private readonly SpinWait _spinWait = new();
        private readonly IHayateLogger _logger;

        // 公平锁
        private long _ticketCounter = 0;
        private long _nextTicket = 0;

        public Shard(HayatePoolOptions options, int index, IHayateLogger logger)
        {
            Index = index;
            _maxSize = options.MaxPoolSize / options.ShardCount;
            _logger = logger;
        }

        public int Count => _queue.Count;

        public int Capacity => _maxSize;

        public int Available => _queue.Count;

        public int BorrowedCount => _queue.Count(x => x.IsBorrowed);

        public void Add(HayateObject<T> w)
        {
            if (w is null) throw new ArgumentNullException(nameof(w));

            if (_queue.Count >= _maxSize)
            {
                _logger.LogWarning("[Shard {Index}] capacity exceeded, object will be destroyed (shard size: {Size}, max: {Max})", Index, _queue.Count, _maxSize);

                try
                {
                    if (w.Value is IDisposable d) d.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to dispose object when [shard {Index}] is full", Index);
                }

                return;
            }

            _queue.Enqueue(w);
            //Interlocked.Increment(ref _availableCount);
            _logger.LogDebug("[Shard {Index}] Object added to shard (current size: {Size})", Index, _queue.Count);
        }

        //public bool TryTake(out HayateObject<T> w, TimeSpan timeout, bool useFair, ILogger logger = null)
        public bool TryTake(out HayateObject<T> w, bool useFairMode)
        {
            w = null;
            //var sw = ValueStopwatch.StartNew();
            long myTicket = 0;

            if (useFairMode)
            {
                myTicket = Interlocked.Increment(ref _ticketCounter) - 1;
            }

            try
            {
                //while (sw.Elapsed < timeout)
                //{
                if (useFairMode && Interlocked.Read(ref _nextTicket) != myTicket)
                {
                    //_spinWait.SpinOnce();
                    //continue;
                    return false;
                }

                //if (Interlocked.Read(ref _availableCount) > 0 && _queue.TryDequeue(out w))
                if (_queue.TryDequeue(out w))
                {
                    //Interlocked.Decrement(ref _availableCount);
                    if (useFairMode)
                    {
                        Interlocked.Increment(ref _nextTicket);
                    }

                    _logger.LogDebug("[Shard {Index}] Object taken from shard (current size: {Size})", Index, _queue.Count);
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
                if (useFairMode && Interlocked.Read(ref _nextTicket) == myTicket)
                {
                    Interlocked.Increment(ref _nextTicket);
                }
            }
        }

        public void Remove(HayateObject<T> w)
        {
            if (w == null) return;

            var ww = _queue.ToList();

            if (ww.Remove(w))
            {
                //Interlocked.Decrement(ref _availableCount);

                // 重建队列
                _queue.Clear();
                foreach (var l in ww) _queue.Enqueue(l);
                _logger.LogDebug("[Shard {Index}] Object removed from shard (current size: {Size})", Index, _queue.Count);
            }
        }

        public IEnumerable<HayateObject<T>> GetAll() => _queue.ToArray();

        public void Clear()
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
            _logger.LogInformation("[Shard {Index}] Shard cleared, {Count} objects destroyed", Index, clearedCount);
        }
    }

    #endregion
}