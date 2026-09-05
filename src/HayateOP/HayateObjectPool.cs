using System.Collections.Concurrent;
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
    private long _totalAcquired;
    private long _leakDetectedCount;
    private readonly HayatePoolStats _stats = new();
    private readonly object _statsLock = new();

    private readonly bool _enableValidation;
    private readonly bool _enableMetrics;
    private readonly bool _enableGenerationOptimization;
    private readonly bool _enableLeakDetection;
    private readonly bool _enableEviction;
    private readonly bool _enableAutoScaling;

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

        // 计算每个分片的初始最大空闲容量
        var shardCount = _options.ShardCount;
        var perShardMax = _options.MaxPoolSize / shardCount;   // FIX: 之前直接赋值 MaxPoolSize，分片容量从未被均分
        var remainderMax = _options.MaxPoolSize % shardCount;

        // 功能开关只读字段（用于JIT死代码消除）
        _enableValidation = _options.EnableValidation;
        _enableAutoScaling = _options.EnableAutoScaling;
        _enableGenerationOptimization = _options.EnableGenerationOptimization;
        _enableLeakDetection = _options.EnableLeakDetection;
        _enableEviction = _options.EnableEviction;
        _enableMetrics = _options.EnableMetrics;

        // 初始化分片
        _shards = new Shard[shardCount];
        for (var i = 0; i < shardCount; i++)
        {
            int shardMax = perShardMax + (i < remainderMax ? 1 : 0);
            _shards[i] = new Shard(_options, i, shardMax, _logger);
        }
        //_shards = Enumerable
        //    .Range(0, _options.ShardCount)
        //    .Select(index => new Shard(_options, index, _logger))
        //    .ToArray();

        PreWarm();

        StartBackgroundTasks();
    }

    #region Initialized

    private void PreWarm()
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

    private void StartBackgroundTasks()
    {
        if (_enableEviction)
        {
            _evictionTimer = new Timer(EvictionCallback, null, _options.EvictionIntervalMs, _options.EvictionIntervalMs);
        }

        if (_enableAutoScaling)
        {
            _scalingTimer = new Timer(ScalingCallback, null, _options.ScalingIntervalMs, _options.ScalingIntervalMs);
        }

        if (_enableValidation)
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
                if (shard.TryTake(out var w))
                {
                    #region 分代验证逻辑

                    // 仅开启分代 + 验证时执行
                    //bool shouldValidate = _options.EnableValidation && _options.ValidateOnBorrow;
                    bool shouldValidate = _enableValidation && _options.ValidateOnBorrow;

                    // 分代，老年代跳过部分验证
                    //if (_options.EnableGenerationOptimization && shouldValidate && w.Generation == 1)
                    if (_enableGenerationOptimization && shouldValidate && w.Generation == 1)
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

                    #region 记录源分片索引（Release 时按此 round-trip，避免 ProcessorId 落到 max=0 分片）

                    // 关键：Acquire 命中即记录来源 shard。同一对象 Release 时回到原 shard，
                    // 杜绝 Thread.GetCurrentProcessorId() % ShardCount 命中 max=0 分片导致对象静默 dispose。
                    w.ShardIndex = shard.Index;

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

                    //if (_options.EnableEviction || _options.EnableLeakDetection || _options.EnableGenerationOptimization)
                    if (_enableEviction || _enableLeakDetection || _enableGenerationOptimization)
                    {
                        w.LastBorrowedAt = DateTime.UtcNow;
                    }

                    //if (_options.EnableLeakDetection)
                    if (_enableLeakDetection)
                    {
                        w.AcquireTrace = Environment.StackTrace;
                    }

                    // 分代升级，仅开启分代优化时执行
                    //if (_options.EnableGenerationOptimization &&
                    if (_enableGenerationOptimization &&
                        DateTime.UtcNow - w.CreatedAt > TimeSpan.FromMilliseconds(_options.GenerationThresholdMs))
                    {
                        w.Generation = 1;
                    }

                    Interlocked.Increment(ref _totalAcquired);

                    #endregion

                    #region 指标统计，仅开启指标时执行

                    //if (_options.EnableMetrics)
                    if (_enableMetrics)
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
            var elapsed = sw.Elapsed;

            //if (sw.Elapsed >= timeout)
            //{
            //    if (_options.EnableMetrics)
            //    {
            //        Interlocked.Increment(ref _totalMissed);
            //    }

            //    // 仅开启扩缩容时，触发强制扩容
            //    if (_enableAutoScaling)
            //    {
            //        ForceScaleUpOneStep();
            //    }

            //    var newObj = _options.RejectPolicy switch
            //    {
            //        HayatePoolRejectPolicy.Abort => throw new TimeoutException($"Pool timeout after {timeout.TotalSeconds} seconds"),
            //        HayatePoolRejectPolicy.Block => throw new TimeoutException("Pool is full, block policy triggered"),
            //        HayatePoolRejectPolicy.BlockTimeout => throw new TimeoutException($"Pool timeout after {timeout.TotalSeconds} seconds (BlockTimeout policy)"),
            //        HayatePoolRejectPolicy.CreateNew => CreateWrappedObject().Value, // 创建新对象（不加入池）
            //        _ => throw new TimeoutException($"HayatePool [{_name}] acquire timeout")
            //    };

            //    if (_options.EnableMetrics)
            //    {
            //        // 记录指标
            //        _metrics.RecordObjectMiss(_name, newObj);
            //    }

            //    return newObj;
            //}

            switch (_options.RejectPolicy)
            {
                case HayatePoolRejectPolicy.Abort:
                    {
                        // 无空闲对象时直接抛异常，不等待
                        if (_enableMetrics) Interlocked.Increment(ref _totalMissed);
                        if (_enableAutoScaling) ForceScaleUpOneStep();
                        throw new InvalidOperationException($"HayatePool [{_name}] 无可用对象，Abort策略拒绝请求");
                    }

                case HayatePoolRejectPolicy.Block:
                    {
                        // 无限等待直到获取到对象
                        spinWait.SpinOnce();
                        continue;
                    }

                case HayatePoolRejectPolicy.BlockTimeout:
                    {
                        // 等待超时后抛异常
                        if (elapsed >= timeout)
                        {
                            if (_enableMetrics) Interlocked.Increment(ref _totalMissed);
                            if (_enableAutoScaling) ForceScaleUpOneStep();
                            throw new TimeoutException($"HayatePool [{_name}] 获取对象超时，超时时间：{timeout.TotalSeconds}s");
                        }
                        spinWait.SpinOnce();
                        continue;
                    }

                case HayatePoolRejectPolicy.CreateNew:
                    {
                        // 超时后创建新对象，不加入池
                        if (elapsed >= timeout)
                        {
                            if (_enableMetrics) Interlocked.Increment(ref _totalMissed);
                            _metrics.RecordObjectMiss(_name, null);
                            return _policy.Create();
                        }
                        spinWait.SpinOnce();
                        continue;
                    }

                default:
                    {
                        throw new ArgumentOutOfRangeException(nameof(_options.RejectPolicy), "未知的拒绝策略");
                    }
            }

            //// 短暂等待后重试
            //spinWait.SpinOnce();
        }
    }

    public async Task<T> AcquireAsync(CancellationToken cancellationToken = default)
    {
        var delay = TimeSpan.FromMilliseconds(1);

        while (!cancellationToken.IsCancellationRequested)
        {
            foreach (var shard in _shards)
            {
                //if (shard.TryTake(out var w, TimeSpan.Zero))
                if (shard.TryTake(out var w))
                {
                    if (_options.ValidateOnBorrow && !_policy.Validate(w.Value))
                    {
                        Destroy(w);
                        continue;
                    }

                    _policy.OnAcquire(w.Value);

                    w.IsBorrowed = true;

                    // D3：记录源分片索引，Release round-trip 用
                    w.ShardIndex = shard.Index;

                    //if (_options.EnableEviction || _options.EnableLeakDetection)
                    if (_enableEviction || _enableLeakDetection)
                    {
                        w.LastBorrowedAt = DateTime.UtcNow;
                    }

                    Interlocked.Increment(ref _totalAcquired);

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

        //if (_options.EnableValidation && _options.ValidateOnReturn && !_policy.Validate(item))
        if (_enableValidation && _options.ValidateOnReturn && !_policy.Validate(item))
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

            //if (_options.EnableEviction || _options.EnableMetrics)
            if (_enableEviction || _enableMetrics)
            {
                w.LastReleasedAt = DateTime.UtcNow;
                w.LeaseTimeMs = (long)(w.LastReleasedAt - w.LastBorrowedAt).TotalMilliseconds;
            }

            // 重置对象；策略层可通过返回 false 拒绝回池（例如对象已损坏或不可复用）
            if (!_policy.OnRelease(item))
            {
                _logger.LogWarning(
                    "Policy rejected object on release. Disposing. Type: {Type}",
                    typeof(T).Name);

                Destroy(w);

                _metrics.RecordObjectReleased(_name, item, false);

                // 维持最小空闲水位：策略层拒绝后池被掏空，主动补充。
                // 仅在开启了自动扩缩容且 MinPoolSize > 0 时触发，避免无意义的开销。
                if (_enableAutoScaling && _options.MinPoolSize > 0 &&
                    _objectMap.Count < _options.MinPoolSize)
                {
                    ForceScaleUpOneStep();
                }

                return;
            }

            #endregion

            #region 指标统计（仅开启指标时执行）

            if (_enableMetrics)
            {
                // 记录租赁时间统计
                UpdateLeaseTimeStats(w.LeaseTimeMs);
                Interlocked.Increment(ref _totalReleased);
                _metrics.RecordObjectReleased(_name, item, true);
            }

            #endregion

            #region 归还到分片

            // 关键修复（D3）：按 Acquire 时记录的 ShardIndex round-trip，
            // 严禁改回 Thread.GetCurrentProcessorId() % _shards.Length——
            // 后者在 MaxPoolSize < ShardCount 时会让 2/3 分片因 max=0 静默 dispose 对象。
            var shardIndex = (uint)w.ShardIndex < (uint)_shards.Length ? w.ShardIndex : 0;
            var shard = _shards[shardIndex];
            if (!shard.Add(w))
            {
                // 分片拒绝接收，两种可能：
                // 1) 分片已满（overflow）——P0-新-1 后 Add 不再自行处置该对象，
                //    由本路径的 Destroy(w) 统一走完整销毁（触发 OnDestroy），避免钩子漏调；
                // 2) 对象已被驱逐 / 空闲校验流程认领 —— 对端线程正在销毁它。
                // 无论哪种，对象都已不可复用，这里统一销毁并把它从全局索引中摘除；
                // Destroy 自带幂等保护，不会二次 Dispose。
                _logger.LogWarning("Object rejected by shard on release. Removing from pool. Type: {Type}, shard: {ShardIndex}",
                    typeof(T).Name, shardIndex);

                Destroy(w);
                _objectMap.TryRemove(w.Value, out _);

                // P1-新-1 修复：分片拒绝销毁了一个对象，池总量可能跌破 MinPoolSize
                // （尤其驱逐线程先认领走一个 InPool 对象、随后本归还对象又被 overflow 的场景）。
                // 与 OnRelease=false 路径一致，仅在开启自动扩缩容且确实低于水位时补一次，
                // 避免延迟敏感业务在驱逐/归还交错下遭遇冷启动。
                if (_enableAutoScaling && _options.MinPoolSize > 0 &&
                    _objectMap.Count < _options.MinPoolSize)
                {
                    ForceScaleUpOneStep();
                }

                return;
            }

            #endregion

            _logger.LogDebug("Object returned to pool. Type: {Type} LeaseTime: {LeaseTime:F2}ms, shard: {ShardIndex}", typeof(T).Name, w.LeaseTimeMs, shardIndex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during object return validation. Disposing. Type: {Type}", typeof(T).Name);
            Destroy(w);
            //if (_options.EnableMetrics)
            if (_enableMetrics)
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
                //_objectMap.TryAdd(o, w);
                if (_objectMap.TryAdd(o, w) && _enableMetrics)
                    Interlocked.Increment(ref _totalCreated);
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
        if (!_enableAutoScaling) return;

        try
        {
            int currentTotal = _objectMap.Count;
            if (currentTotal >= _options.MaxPoolSize) return;
            if ((DateTime.UtcNow - _lastScaleUpTime).TotalSeconds < _options.ScaleUpCooldownSeconds) return;

            // 每次超时 +5 个，防止雪崩
            int add = Math.Min(_options.ScaleUpStep, _options.MaxPoolSize - currentTotal);
            if (add <= 0) return;

            // 更新分片容量，避免对象被丢弃
            UpdateShardMaxSizes();

            var added = 0;

            for (var i = 0; i < add; i++)
            {
                var shard = _shards[i % _shards.Length];
                shard.Add(CreateWrappedObject());
                added++;
            }

            _lastScaleUpTime = DateTime.UtcNow;
            _logger.LogWarning("FORCE SCALE UP Pool [{PoolName}] (because timeout) → total: {Total}, added: {Count}",
                _name, _objectMap.Count, added);

            if (_enableMetrics)
            {
                _metrics.RecordPoolScaled(_name, "ForceUp", currentTotal, currentTotal + add);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Force scale up failed");
        }
    }

    private void UpdateShardMaxSizes()
    {
        var shardCount = _shards.Length;
        var perShardMax = _options.MaxPoolSize / shardCount;
        var remainderMax = _options.MaxPoolSize % shardCount;

        // FIX: 之前循环 bound 用了 perShardMax，会在 MaxPoolSize > ShardCount 时越界 _shards[i]
        for (var i = 0; i < shardCount; i++)
        {
            var newMax = perShardMax + ((i < remainderMax ? 1 : 0));
            _shards[i].UpdateMaxSize(newMax);
        }
    }

    private void Destroy(HayateObject<T> w)
    {
        if (w == null) return;

        // 幂等保护：驱逐、空闲校验、归还拒绝三条路径可能并发命中同一个包装对象，
        // 只有第一个通过 CAS 的调用方真正执行销毁，其余直接返回，避免二次 Dispose。
        if (Interlocked.Exchange(ref w.Destroyed, 1) == 1) return;

        try
        {
            _policy.OnDestroy(w.Value);
            if (w.Value is IDisposable d) d.Dispose();
            _objectMap.TryRemove(w.Value, out _);
            w.Location = HayateObjectLocation.Destroyed;
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
        //if (!_options.EnableEviction) return;
        if (!_enableEviction) return;
        try
        {
            var now = DateTime.UtcNow;
            foreach (var shard in _shards)
            {
                // 分批检查
                var itemsToCheck = shard.GetAll().Take(_options.NumTestsPerEvictionRun).ToArray();
                int evictedCount = 0;

                foreach (var w in itemsToCheck)
                {
                    // 跳过正在使用的对象
                    if (w.IsBorrowed) continue;

                    // 判断是否需要驱逐
                    var isExpired = now - w.CreatedAt > _options.MaxLifeTime;
                    var isIdleTooLong = now - w.LastReleasedAt > _options.MaxIdleTime;
                    var isSoftIdle = now - w.LastReleasedAt > _options.SoftMinEvictableIdleTime;

                    // 软最小空闲逻辑
                    // 只有当空闲数超过最小池大小时才驱逐软空闲对象
                    var shouldEvictSoft = isSoftIdle && shard.Count > _options.MinPoolSize / _shards.Length;

                    if (isExpired || isIdleTooLong || shouldEvictSoft)
                    {
                        // 只有认领成功的调用方才有权销毁：若对象此刻已被借出（Remove 返回 false），
                        // 绝不能销毁它，否则会破坏正在使用它的业务线程。
                        if (shard.Remove(w))
                        {
                            Destroy(w);
                            evictedCount++;

                            _logger.LogInformation("[Shard {Index}] Evicting object. Type: {Type} Expired: {Expired} IdleTooLong: {IdleTooLong} SoftIdle: {SoftIdle}",
                                shard.Index, typeof(T).Name, isExpired, isIdleTooLong, shouldEvictSoft);
                        }
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
        if (!_enableAutoScaling) return;
        try
        {
            var totalIdle = _shards.Sum(s => s.Count);
            var currentTotal = _objectMap.Count;
            if (currentTotal == 0) return;

            var targetSize = _scalingStrategy.CalculateNewSize(currentTotal, totalIdle, _options);
            targetSize = Math.Clamp(targetSize, _options.MinPoolSize, _options.MaxPoolSize);

            // 扩容冷却时间 3 秒，缩容冷却时间 15 秒，防止频繁抖动
            var canScaleUp = (DateTime.UtcNow - _lastScaleUpTime).TotalSeconds >= _options.ScaleUpCooldownSeconds;
            var canScaleDown = (DateTime.UtcNow - _lastScaleDownTime).TotalSeconds >= _options.ScaleDownCooldownSeconds;

            if (targetSize > currentTotal && canScaleUp)
            {
                // 扩容
                UpdateShardMaxSizes();
                var add = targetSize - currentTotal;
                for (var i = 0; i < add; i++)
                {
                    var shard = _shards[i % _shards.Length];
                    shard.Add(CreateWrappedObject());
                }

                _lastScaleUpTime = DateTime.UtcNow;
                _logger.LogInformation("Pool [{PoolName}] scaled UP. {CurrentCapacity} → {TargetCapacity}", _name, currentTotal, targetSize);
                if (_enableMetrics)
                {
                    _metrics.RecordPoolScaled(_name, "UP", currentTotal, targetSize);
                }
            }
            else if (targetSize < currentTotal && canScaleDown && totalIdle < currentTotal * 0.4f)
            {
                // 缩容
                var remove = currentTotal - targetSize;
                int removed = 0;

                foreach (var shard in _shards)
                {
                    if (removed >= remove) break;
                    //if (shard.Count <= _options.MinPoolSize / _shards.Length) continue;

                    //while (shard.Count > _options.MinPoolSize / _shards.Length && removed < remove)
                    //{
                    //    if (shard.TryTake(out var w))
                    //    {
                    //        Destroy(w);
                    //        removed++;
                    //    }
                    //    else break;
                    //}

                    while (removed < remove)
                    {
                        if (shard.TryTake(out var w))
                        {
                            Destroy(w);
                            removed++;
                        }
                        else break;
                    }
                }

                UpdateShardMaxSizes();
                _lastScaleDownTime = DateTime.UtcNow;
                _logger.LogInformation("Pool [{PoolName}] scaled DOWN. {CurrentCapacity} → {TargetCapacity}", _name, currentTotal, targetSize);

                if (_enableMetrics)
                {
                    _metrics.RecordPoolScaled(_name, "DOWN", currentTotal, targetSize);
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
        //if (!_options.EnableValidation || !_options.ValidateWhileIdle) return;
        if (!_enableValidation || !_options.ValidateWhileIdle) return;

        try
        {
            int invalidCount = 0;
            foreach (var shard in _shards)
            {
                foreach (var w in shard.GetAll())
                {
                    if (!w.IsBorrowed && !_policy.Validate(w.Value))
                    {
                        // 认领失败说明对象已被借出或已被其他线程认领，此时不得销毁
                        if (shard.Remove(w))
                        {
                            Destroy(w);
                            invalidCount++;
                            _logger.LogWarning("Idle object failed validation and was evicted. Type: {Type}", typeof(T).Name);
                        }
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
            int totalIdle = _shards.Sum(s => s.Count);
            int totalObjects = _objectMap.Count; // 真实总对象数（空闲+借出）

            return new HayatePoolStats
            {
                PooledCount = _shards.Sum(s => s.Count),
                TotalCreated = Interlocked.Read(ref _totalCreated),
                TotalReleased = Interlocked.Read(ref _totalReleased),
                TotalMissed = Interlocked.Read(ref _totalMissed),
                TotalAcquired = Interlocked.Read(ref _totalAcquired),
                AvailableSlots = totalIdle,
                MinSize = _options.MinPoolSize,
                CurrentSize = totalObjects,
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
        //if (_options.EnableLeakDetection)
        if (_enableLeakDetection)
        {
            foreach (var shard in _shards)
            {
                foreach (var w in shard.GetAll())
                {
                    // 检查泄露
                    if (w.IsBorrowed &&
                        now - w.LastBorrowedAt > _options.LeakDetectionThreshold)
                    {
                        leakTraces.Add(w.AcquireTrace ?? "No stack trace available");
                        Interlocked.Increment(ref _leakDetectedCount);
                    }
                }
            }
        }

        var pooledCount = _shards.Sum(s => s.Count);

        // 借出数由「真实存活包装对象总数 - 池内空闲数」派生（与 GetStats 的
        // totalObjects - totalIdle 口径一致），而不是依赖 Shard.BorrowedCount。
        // 原因：TryTake 先把对象物理摘出分片链表、再置 Borrowed，借出的对象根本不在
        // 分片链表中，因此 Shard.BorrowedCount（统计链表内 IsBorrowed）结构上恒为 0。
        // _objectMap 始终持有所有存活包装对象（空闲 + 借出），直到 Destroy 才移除；
        // 驱逐「已认领未销毁」的极短瞬态会被计入，但被 Destroy 的快速执行所限，可忽略。
        var borrowedCount = _objectMap.Count - pooledCount;
        if (borrowedCount < 0) borrowedCount = 0;

        return new HayatePoolSnapshot
        {
            Timestamp = now,
            PooledCount = pooledCount,
            BorrowedCount = borrowedCount,
            TotalCreated = Interlocked.Read(ref _totalCreated),
            TotalMissed = Interlocked.Read(ref _totalMissed),
            TotalAcquired = Interlocked.Read(ref _totalAcquired),
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
}