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

    // T09：原池级单张 _objectMap（ConcurrentDictionary<T, HayateObject<T>>）已按分片拆分，
    // 迁移至各 Shard 内部的登记表（见 HayateObjectPool.Shard.cs）。登记项随对象的创建/销毁
    // 固定在其归属分片，消除多分片并发写同一张表导致的桶数组峰值不收敛。
    // 池级只保留两个派生视图：
    /// <summary>真实存活对象总数（空闲 + 借出），由各分片登记表求和派生。</summary>
    private int TrackedObjectCount => _shards.Sum(s => s.TrackedCount);

    /// <summary>
    /// T09：按包装对象的归属分片摘除登记项；ShardIndex 异常时兜底全分片扫描（理论不可达，
    /// 登记项在 CreateWrappedObject 即写入目标分片并同步 ShardIndex）。
    /// </summary>
    private void UntrackObject(HayateObject<T> w)
    {
        if (w?.Value is null) return;

        var home = (uint)w.ShardIndex < (uint)_shards.Length ? _shards[w.ShardIndex] : null;
        if (home is not null && home.Untrack(w.Value)) return;

        foreach (var shard in _shards)
        {
            if (shard.Untrack(w.Value)) return;
        }
    }

    /// <summary>T09：仅有裸对象引用（无包装）时，全分片扫描摘除登记项。</summary>
    private void UntrackKey(T o)
    {
        if (o is null) return;

        foreach (var shard in _shards)
        {
            if (shard.Untrack(o)) return;
        }
    }

    // T06：归还事件通知门。Release 成功回池时 Release() 一次，Block/BlockTimeout/CreateNew
    // 等待者以 Wait 取代 SpinWait 忙等，消除等待期 CPU 100% 空转。
    // 计数语义为"可消费的唤醒信号数"；借出成功时以 Wait(0) 消费一个信号，
    // 防止陈旧信号积累导致等待者被逐个伪唤醒形成忙循环。
    private readonly SemaphoreSlim _blockGate = new(0, int.MaxValue);

    // T06：无信号时的挂起等待切片。信号到达会立即唤醒，切片仅封顶无信号时的重检间隔。
    private const int BlockWaitSliceMs = 100;

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

    // T15：CreateNew 分片的轮询游标（避免新建对象全部落在首个分片）
    private int _createCursor;
    private long _leakDetectedCount;

    // PR-D L2：统计数据去全局锁——借/还热路径改为 Interlocked 原子累加，
    // Min/Max 用 CAS 环（long 毫秒存储，GetStats 快照时转 double 汇总）。
    // 更新语义不变；GetStats 不再持全局锁，改为逐字段原子读取的最终一致快照。
    private long _waitTimeSum;
    private long _waitTimeCount;
    private long _waitTimeMaxMs;
    private long _waitTimeMinMs = long.MaxValue;
    private long _leaseTimeSum;
    private long _leaseTimeCount;
    private long _leaseTimeMaxMs;
    private long _leaseTimeMinMs = long.MaxValue;

    private readonly bool _enableValidation;
    private readonly bool _enableMetrics;
    private readonly bool _enableGenerationOptimization;
    private readonly bool _enableLeakDetection;
    private readonly bool _enableEviction;
    private readonly bool _enableAutoScaling;

    // PR-D L1：泄漏取证模式（与泄漏检测解耦）。默认 Off——借出热路径不抓栈。
    private readonly HayateLeakTraceCaptureMode _leakTraceCaptureMode;
    private readonly int _leakTraceSampleRate;
    private int _leakTraceCounter;

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

        // PR-D L1：取证配置随构造快照（采样分母经 IsValid/ApplyFeatureSwitches 已钳制 ≥1，此处再兜底）
        _leakTraceCaptureMode = _options.LeakTraceCaptureMode;
        _leakTraceSampleRate = Math.Max(1, _options.LeakTraceSampleRate);

        // 初始化分片
        _shards = new Shard[shardCount];
        for (var i = 0; i < shardCount; i++)
        {
            int shardMax = perShardMax + (i < remainderMax ? 1 : 0);
            _shards[i] = new Shard(_options, i, shardMax, _logger);
        }

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
                    // T09：登记并入目标分片。分片拒绝（容量已 clamp，理论不可达）时兜底销毁，
                    // 防止产生「已登记但不在任何空闲链表」的孤儿项。
                    var w = CreateWrappedObject(shard);
                    if (!shard.Add(w)) Destroy(w);
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

        while (true)
        {
            foreach (var shard in _shards)
            {
                if (shard.TryTake(out var w))
                {
                    // T06：借出成功即消费一个唤醒信号（若有），保持信号数与池内空闲对象对齐，
                    // 避免等待者被陈旧信号逐个伪唤醒空转。Wait(0) 无信号时立即返回 false。
                    _blockGate.Wait(0);

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

                    // P2-新-1：借出状态不再单独置位——Shard.TryTake 认领时已把 Location
                    // 迁移为 Borrowed，IsBorrowed 是其计算属性（单一事实源）。

                    //if (_options.EnableEviction || _options.EnableLeakDetection || _options.EnableGenerationOptimization)
                    if (_enableEviction || _enableLeakDetection || _enableGenerationOptimization)
                    {
                        w.LastBorrowedAt = DateTime.UtcNow;
                    }

                    //if (_options.EnableLeakDetection)
                    if (_enableLeakDetection)
                    {
                        // PR-D L1：取证与检测解耦。泄漏扫描（阈值判定 + LeakCount）只依赖 LastBorrowedAt，
                        // 零取证开销；调用栈按 LeakTraceCaptureMode 独立控制——
                        // Off（默认）不抓栈（2.0 及之前每次借出抓全栈，37.5μs / 28.7KB 量级）；
                        // Sampled 每 N 次借出抓 1 次（第 1 次必抓）；EveryAcquire 维持旧行为，显式 opt-in。
                        if (_leakTraceCaptureMode == HayateLeakTraceCaptureMode.EveryAcquire)
                        {
                            w.AcquireTrace = Environment.StackTrace;
                        }
                        else if (_leakTraceCaptureMode == HayateLeakTraceCaptureMode.Sampled &&
                                 (Interlocked.Increment(ref _leakTraceCounter) - 1) % _leakTraceSampleRate == 0)
                        {
                            w.AcquireTrace = Environment.StackTrace;
                        }
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
                        // L3：纳入 _enableMetrics 门控（借出路径漏网点）
                        if (_enableMetrics)
                        {
                            _metrics.RecordObjectAcquired(_name, w.Value, waitTime);
                        }
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
                        // T06：无限等待直到获取到对象。挂起等待归还信号（切片封顶重检间隔），
                        // 信号到达立即醒来重试 TryTake；取代原 SpinOnce 忙等（CPU 100%）。
                        // Block 策略不设超时，timeout 参数不参与判定（与原行为一致）。
                        _blockGate.Wait(BlockWaitSliceMs);
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

                        // T06：挂起等待归还信号，切片内无信号则醒来重检超时与分片
                        _blockGate.Wait(BlockWaitSliceMs);
                        continue;
                    }

                case HayatePoolRejectPolicy.CreateNew:
                    {
                        // 超时后创建新对象
                        if (elapsed >= timeout)
                        {
                            if (_enableMetrics) Interlocked.Increment(ref _totalMissed);
                            // L3：纳入 _enableMetrics 门控（超时创建路径漏网点）
                            if (_enableMetrics)
                            {
                                _metrics.RecordObjectMiss(_name);
                            }

                            // T15 修复：创建「已登记」的池内对象（登记即 Borrowed，不进空闲
                            // 链表——避免其他等待者 TryTake 认领导致双重借出），Release 时
                            // 按 ShardIndex 正常回池复用。旧实现返回未登记的裸对象，Release
                            // 反查失败被当作外来对象销毁——每次借还都新建+销毁，池化完全失效。
                            var shard = _shards[(int)(Interlocked.Increment(ref _createCursor) - 1) % _shards.Length];
                            var w = CreateWrappedObject(shard);   // 内部已完成 TryTrack 登记
                            w.Location = HayateObjectLocation.Borrowed;
                            if (_enableEviction || _enableLeakDetection)
                            {
                                w.LastBorrowedAt = DateTime.UtcNow;
                            }
                            _policy.OnAcquire(w.Value);
                            Interlocked.Increment(ref _totalAcquired);

                            var waitTime = (long)sw.Elapsed.TotalMilliseconds;
                            if (_enableMetrics)
                            {
                                UpdateWaitTimeStats(waitTime);
                                _metrics.RecordObjectAcquired(_name, w.Value, waitTime);
                            }

                            return w.Value;
                        }

                        // T06：挂起等待归还信号，切片内无信号则醒来重检超时与分片
                        _blockGate.Wait(BlockWaitSliceMs);
                        continue;
                    }

                default:
                    {
                        throw new ArgumentOutOfRangeException(nameof(_options.RejectPolicy), "未知的拒绝策略");
                    }
            }
        }
    }

    public async Task<T> AcquireAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            foreach (var shard in _shards)
            {
                //if (shard.TryTake(out var w, TimeSpan.Zero))
                if (shard.TryTake(out var w))
                {
                    // T07：借出成功即消费一个唤醒信号（若有），与同步路径保持一致，
                    // 防止陈旧信号积累导致异步等待者伪唤醒空转。
                    _blockGate.Wait(0);

                    if (_options.ValidateOnBorrow && !_policy.Validate(w.Value))
                    {
                        Destroy(w);
                        continue;
                    }

                    _policy.OnAcquire(w.Value);

                    // P2-新-1：TryTake 认领已置 Location=Borrowed，IsBorrowed 为其计算属性。

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

            // T07：取代原 Task.Delay(1ms) 轮询（异步路径空转、空载 CPU 开销）。
            // 挂起等待归还信号或取消——信号由 Release 成功回池时发出（与同步 Acquire
            // 共用 _blockGate，单一信号源），计数持久化语义保证无丢失唤醒。
            // 设计说明：执行计划原草图为本方法引入 Channel<T> 推送对象，但对象回池后
            // 所有权仍属分片链表，channel 再持引用会造成双重所有权；所需语义与 T06
            // 信号门同构，故直接复用 SemaphoreSlim（WaitAsync 异步原生），零新增状态。
            await _blockGate.WaitAsync(cancellationToken);
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

        // 查找对应的包装对象，验证是否属于池中对象。
        // T09：登记表按分片拆分后，按 T 反查需逐分片探测（只读无锁；分片数为个位数，
        // 代价可忽略）。同一对象只会登记在创建分片，任一分片命中即认定属于本池。
        HayateObject<T> w = null;
        foreach (var shard in _shards)
        {
            if (shard.TryGetTracked(item, out w)) break;
        }

        if (w is null)
        {
            _logger.LogWarning("Returned object does not belong to pool. Disposing. Type: {Type}", typeof(T).Name);

            Destroy(item);

            // L3：纳入 _enableMetrics 门控（拒绝路径原本不受门控，是启用 HayateDiagnostics 后的漏网分配点）
            if (_enableMetrics)
            {
                _metrics.RecordObjectReleased(_name, item, false);
            }

            return;
        }

        #region 归还验证，仅开启验证时执行

        //if (_options.EnableValidation && _options.ValidateOnReturn && !_policy.Validate(item))
        if (_enableValidation && _options.ValidateOnReturn && !_policy.Validate(item))
        {
            _logger.LogWarning("Object returned to pool. Disposing. Type: {Type}", typeof(T).Name);

            Destroy(w);

            // L3：纳入 _enableMetrics 门控（验证拒绝路径漏网点）
            if (_enableMetrics)
            {
                _metrics.RecordObjectReleased(_name, item, false);
            }

            return;
        }

        #endregion

        try
        {
            #region 核心归还处理

            // 纯化对象
            _policy.OnPassivate(item);

            // P2-新-1：归还状态不再单独清位——下方 shard.Add 成功即把 Location 迁回
            // InPool（IsBorrowed 随之为 false）；Add 被拒则走 Destroy（Location=Destroyed）。

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

                // L3：纳入 _enableMetrics 门控（策略拒绝路径漏网点）
                if (_enableMetrics)
                {
                    _metrics.RecordObjectReleased(_name, item, false);
                }

                // 维持最小空闲水位：策略层拒绝后池被掏空，主动补充。
                // 仅在开启了自动扩缩容且 MinPoolSize > 0 时触发，避免无意义的开销。
                if (_enableAutoScaling && _options.MinPoolSize > 0 &&
                    TrackedObjectCount < _options.MinPoolSize)
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
                UntrackObject(w);

                // P1-新-1 修复：分片拒绝销毁了一个对象，池总量可能跌破 MinPoolSize
                // （尤其驱逐线程先认领走一个 InPool 对象、随后本归还对象又被 overflow 的场景）。
                // 与 OnRelease=false 路径一致，仅在开启自动扩缩容且确实低于水位时补一次，
                // 避免延迟敏感业务在驱逐/归还交错下遭遇冷启动。
                if (_enableAutoScaling && _options.MinPoolSize > 0 &&
                    TrackedObjectCount < _options.MinPoolSize)
                {
                    ForceScaleUpOneStep();
                }

                return;
            }

            #endregion

            // T06：对象已成功回池，唤醒一个等待中的 Acquire（Block/BlockTimeout/CreateNew）。
            // 无等待者时计数累积，由借出路径 Wait(0) 消费，不会泄漏。
            try { _blockGate.Release(); }
            catch (SemaphoreFullException)
            {
                // int.MaxValue 计数上限保护，正常负载下不可达；吞掉以保证 Release 路径不中断。
            }

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

    private HayateObject<T> CreateWrappedObject(Shard targetShard)
    {
        for (var retry = 0; retry < _options.CreationRetryCount; retry++)
        {
            try
            {
                var o = _policy.Create();

                // T09：登记并入目标分片（同步写入 ShardIndex 作为归属）。
                // 对象 round-trip 永远回到该分片，登记项与对象生命周期同步，
                // TrackedCount 之和即为池的真实存活对象数。
                var w = new HayateObject<T>(o) { ShardIndex = targetShard.Index };
                if (targetShard.TryTrack(o, w))
                {
                    if (_enableMetrics) Interlocked.Increment(ref _totalCreated);
                    return w;
                }

                // 同一键已存在（策略层重复创建同一实例的病态情形）：销毁新实例后重试，
                // 绝不入池——否则 Release 反查会命中旧登记项，新旧包装对象互相污染。
                _logger.LogWarning("Duplicate pooled object instance detected. Retrying. Type: {Type}", typeof(T).Name);
                _policy.OnDestroy(o);
                if (o is IDisposable d) d.Dispose();
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
            int currentTotal = TrackedObjectCount;
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
                // T09：登记并入目标分片；并发下 Add 仍可能被他线程先填满而拒绝，兜底销毁防孤儿登记。
                var w = CreateWrappedObject(shard);
                if (!shard.Add(w)) Destroy(w);
                added++;
            }

            _lastScaleUpTime = DateTime.UtcNow;
            _logger.LogWarning("FORCE SCALE UP Pool [{PoolName}] (because timeout) → total: {Total}, added: {Count}",
                _name, TrackedObjectCount, added);

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
            UntrackObject(w);
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
            UntrackKey(o);
            _logger.LogDebug("Object destroyed. Type: {Type}", typeof(T).Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during object destruction. Type: {Type}", typeof(T).Name);
        }
    }

    private void UpdateWaitTimeStats(long waitTimeMs)
    {
        // L2：无锁更新（原实现持全局 _statsLock，为并发借还的争用点）
        Interlocked.Add(ref _waitTimeSum, waitTimeMs);
        Interlocked.Increment(ref _waitTimeCount);
        InterlockedMax(ref _waitTimeMaxMs, waitTimeMs);
        InterlockedMin(ref _waitTimeMinMs, waitTimeMs);
    }

    private void UpdateLeaseTimeStats(long leaseTimeMs)
    {
        // L2：无锁更新（同上）
        Interlocked.Add(ref _leaseTimeSum, leaseTimeMs);
        Interlocked.Increment(ref _leaseTimeCount);
        InterlockedMax(ref _leaseTimeMaxMs, leaseTimeMs);
        InterlockedMin(ref _leaseTimeMinMs, leaseTimeMs);
    }

    /// <summary>
    /// L2：无锁 Max 归并（CAS 环）。并发下最终收敛到真实最大值。
    /// </summary>
    private static void InterlockedMax(ref long location, long value)
    {
        var current = Volatile.Read(ref location);
        while (value > current)
        {
            var previous = Interlocked.CompareExchange(ref location, value, current);
            if (previous == current) break;
            current = previous;
        }
    }

    /// <summary>
    /// L2：无锁 Min 归并（CAS 环）。并发下最终收敛到真实最小值。
    /// </summary>
    private static void InterlockedMin(ref long location, long value)
    {
        var current = Volatile.Read(ref location);
        while (value < current)
        {
            var previous = Interlocked.CompareExchange(ref location, value, current);
            if (previous == current) break;
            current = previous;
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
            var currentTotal = TrackedObjectCount;
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
                    // T09：登记并入目标分片；Add 被拒时兜底销毁防孤儿登记。
                    var w = CreateWrappedObject(shard);
                    if (!shard.Add(w)) Destroy(w);
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
        // L2：不再持全局 _statsLock——统计字段逐个原子读取（最终一致快照）；
        // 分片计数读取语义与原实现一致（原实现锁内读取同样不构成分片级一致性）。
        int totalIdle = _shards.Sum(s => s.Count);
        int totalObjects = TrackedObjectCount; // 真实总对象数（空闲+借出）

        long waitSum = Volatile.Read(ref _waitTimeSum);
        long waitCount = Volatile.Read(ref _waitTimeCount);
        long waitMax = Volatile.Read(ref _waitTimeMaxMs);
        long waitMin = Volatile.Read(ref _waitTimeMinMs);
        long leaseSum = Volatile.Read(ref _leaseTimeSum);
        long leaseCount = Volatile.Read(ref _leaseTimeCount);
        long leaseMax = Volatile.Read(ref _leaseTimeMaxMs);
        long leaseMin = Volatile.Read(ref _leaseTimeMinMs);

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
            WaitTimeSum = waitSum,
            WaitTimeCount = waitCount,
            LeaseTimeSum = leaseSum,
            LeaseTimeCount = leaseCount,
            MaxWaitTimeMs = waitMax,
            MaxLeaseTimeMs = leaseMax,
            MinWaitTimeMs = waitMin == long.MaxValue ? 0 : waitMin,
            MinLeaseTimeMs = leaseMin == long.MaxValue ? 0 : leaseMin
        };
    }

    public HayatePoolSnapshot TakeSnapshot()
    {
        var leakTraces = new List<string>();
        var now = DateTime.UtcNow;

        // 仅开启泄漏检测时执行扫描
        //if (_options.EnableLeakDetection)
        if (_enableLeakDetection)
        {
            // T13/穿插修复 + T09：泄漏扫描必须遍历分片登记表（全部存活包装对象）。
            // 原实现遍历 shard.GetAll()（仅池内空闲对象），而 TryTake 会把借出对象
            // 物理摘出分片链表——借出对象从未被扫描，泄漏检测结构上恒不触发。
            foreach (var shard in _shards)
            {
                foreach (var w in shard.TrackedValues)
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
        // 分片登记表始终持有所有存活包装对象（空闲 + 借出），直到 Destroy 才移除；
        // 驱逐「已认领未销毁」的极短瞬态会被计入，但被 Destroy 的快速执行所限，可忽略。
        var borrowedCount = TrackedObjectCount - pooledCount;
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
        foreach (var shard in _shards)
        {
            shard.Clear();
            // T09：登记表随分片清空（含借出中对象的登记项，与原池级 map.Clear 语义一致）。
            shard.ClearTracked();
        }
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

        // T06：释放归还信号门（前提与定时器一致：Dispose 时无未完成的 Acquire 等待者）
        _blockGate.Dispose();

        _logger.LogInformation("Object pool disposed. Type: {Type}", typeof(T).Name);
    }

    #endregion
}