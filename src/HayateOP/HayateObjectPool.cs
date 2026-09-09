using System.Collections.Concurrent;
using System.Diagnostics;
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
    // PR-D A1：Stopwatch timestamp（0 = 从未扩缩容，等效原 DateTime.MinValue 语义）
    private long _lastScaleUpTime;
    private long _lastScaleDownTime;

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

    // PR-D L5：冷池自举防重标志。0=无人认领，1=已有线程正在冷启动创建。
    // CAS 赢家负责创建首个对象，输家回落到正常等待路径；创建结束（含异常）即复位，
    // 保证池再次清空后仍可二次自举。
    private int _coldBootClaimed;

    // M12：容量告警状态机（0=Normal, 1=Warning, 2=Critical）。
    // 仅在状态翻转时触发一次回调（去抖）；回落到低水位静默复位，重新整备后可再次触发。
    private int _capacityAlarmLevel;
    private readonly bool _capacityAlarmEnabled;

    // M4：泄漏回查告警计数（EnableLeakDetection=false 时 TakeSnapshot 按同一阈值
    // 统计「借出超阈值未归还」的疑似泄漏次数，与 LeakDetectedCount 并列、互不替代）。
    private long _leakSuspectedCount;

    // M18：分片亲和模式（构造期快照）。None 为默认且零开销（起始索引恒 0）；
    // Thread 按线程 ID 稳定映射起始分片；Custom 走用户委托（异常/越界/null 回落顺序扫描）。
    private readonly HayateShardAffinityMode _affinityMode;
    private readonly Func<int> _customShardAffinity;

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

        // M12：容量告警默认（WarnAtRatio=0 且 CriticalAtRatio=0）完全禁用——
        // 构造期固化为只读标志，禁用时借/还路径仅多一次可预测分支（JIT 消除友好）。
        _capacityAlarmEnabled = _options.WarnAtRatio > 0 || _options.CriticalAtRatio > 0;

        // PR-D L1：取证配置随构造快照（采样分母经 IsValid/ApplyFeatureSwitches 已钳制 ≥1，此处再兜底）
        _leakTraceCaptureMode = _options.LeakTraceCaptureMode;
        _leakTraceSampleRate = Math.Max(1, _options.LeakTraceSampleRate);

        // M18：affinity 配置随构造快照（ApplyFeatureSwitches 已保证 Custom 模式必有委托）
        _affinityMode = _options.ShardAffinityMode;
        _customShardAffinity = _options.CustomShardAffinity;

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

        // M18：affinity 起始分片每次 Acquire 仅求值一次（None 恒 0，零额外开销）。
        var affinityStart = _affinityMode == HayateShardAffinityMode.None ? 0 : SelectStartShardIndex();

        while (true)
        {
            // M18：从亲和分片起环形扫描（等价 foreach 顺序扫描当 start=0）。
            for (var offset = 0; offset < _shards.Length; offset++)
            {
                var hop = affinityStart + offset;
                var shard = _shards[hop >= _shards.Length ? hop - _shards.Length : hop];

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
                        w.LastBorrowedAt = Stopwatch.GetTimestamp();
                    }

                    //if (_options.EnableLeakDetection)
                    if (_enableLeakDetection)
                    {
                        // PR-D L1：取证与检测解耦。泄漏扫描（阈值判定 + LeakCount）只依赖 LastBorrowedAt，
                        // 零取证开销；调用栈按 LeakTraceCaptureMode 独立控制——
                        // Off（默认）不抓栈（2.0 及之前每次借出抓全栈，37.5μs / 28.7KB 量级）；
                        // Sampled 每 N 次借出抓 1 次（第 1 次必抓）；EveryAcquire 维持旧行为，显式 opt-in。
                        // M16（2.5）：采集载体改 HayateLeaseContext（AsyncLocal 异步流 + 包装侧快照引用），
                        // 租约 ID 单调递增，并发借还各自持有独立上下文实例，不再相互覆盖。
                        if (_leakTraceCaptureMode == HayateLeakTraceCaptureMode.EveryAcquire)
                        {
                            CaptureLeaseContext(w);
                        }
                        else if (_leakTraceCaptureMode == HayateLeakTraceCaptureMode.Sampled &&
                                 (Interlocked.Increment(ref _leakTraceCounter) - 1) % _leakTraceSampleRate == 0)
                        {
                            CaptureLeaseContext(w);
                        }
                    }

                    // M4：借出时间戳无条件记录。原条件门控（eviction/leakDetection/generation）
                    // 会使「全关」配置下 LastBorrowedAt 恒为 0，M4 泄漏回查（LeakSuspectedCount）
                    // 结构性失效；单次 QPC（Stopwatch.GetTimestamp）无分配、无时区换算，
                    // 成本远低于 PR-D A1 移除的 DateTime.UtcNow，可接受。
                    w.LastBorrowedAt = Stopwatch.GetTimestamp();

                    // M20：累计借出次数。借出瞬间包装对象由本线程独占（TryTake 已摘链认领，
                    // 驱逐/校验无法认领 Borrowed 对象），普通自增即可，无需 Interlocked。
                    w.LeaseCount++;

                    // 分代升级，仅开启分代优化时执行
                    //if (_options.EnableGenerationOptimization &&
                    if (_enableGenerationOptimization &&
                        (Stopwatch.GetTimestamp() - w.CreatedAt) * 1000.0 / Stopwatch.Frequency > _options.GenerationThresholdMs)
                    {
                        w.Generation = 1;
                    }

                    Interlocked.Increment(ref _totalAcquired);

                    // M12：容量告警埋点（禁用时内部立即返回）
                    CheckCapacityAlarm();

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
                        // PR-D L5：冷池自举——池完全空时按需创建首个对象，确定性消除首借挂起
                        var coldBoot = TryColdBootAcquire((long)sw.Elapsed.TotalMilliseconds);
                        if (coldBoot is not null) return coldBoot;

                        // T06：无限等待直到获取到对象。挂起等待归还信号（切片封顶重检间隔），
                        // 信号到达立即醒来重试 TryTake；取代原 SpinOnce 忙等（CPU 100%）。
                        // Block 策略不设超时，timeout 参数不参与判定（与原行为一致）。
                        _blockGate.Wait(BlockWaitSliceMs);
                        continue;
                    }

                case HayatePoolRejectPolicy.BlockTimeout:
                    {
                        // PR-D L5：冷池自举——池完全空时按需创建首个对象，确定性消除首借超时
                        var coldBoot = TryColdBootAcquire((long)elapsed.TotalMilliseconds);
                        if (coldBoot is not null) return coldBoot;

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
                            // M4：借出时间戳无条件记录（同 Acquire 主路径，保障泄漏回查可用）
                            w.LastBorrowedAt = Stopwatch.GetTimestamp();
                            // M20：累计借出次数（新建即借出，包装对象此刻由本线程独占）
                            w.LeaseCount++;
                            _policy.OnAcquire(w.Value);
                            Interlocked.Increment(ref _totalAcquired);

                            // M12：容量告警埋点（禁用时内部立即返回）
                            CheckCapacityAlarm();

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
        // M18：affinity 起始分片每次 AcquireAsync 仅求值一次（None 恒 0，零额外开销）。
        var affinityStart = _affinityMode == HayateShardAffinityMode.None ? 0 : SelectStartShardIndex();

        while (!cancellationToken.IsCancellationRequested)
        {
            // M18：从亲和分片起环形扫描（等价 foreach 顺序扫描当 start=0）。
            for (var offset = 0; offset < _shards.Length; offset++)
            {
                var hop = affinityStart + offset;
                var shard = _shards[hop >= _shards.Length ? hop - _shards.Length : hop];

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

                    // M4：借出时间戳无条件记录（同步路径一致，保障泄漏回查可用）
                    w.LastBorrowedAt = Stopwatch.GetTimestamp();

                    // M20：累计借出次数（TryTake 已摘链认领，包装对象此刻由本线程独占）
                    w.LeaseCount++;

                    Interlocked.Increment(ref _totalAcquired);

                    // M12：容量告警埋点（禁用时内部立即返回）
                    CheckCapacityAlarm();

                    return w.Value;
                }
            }

            // T07：取代原 Task.Delay(1ms) 轮询（异步路径空转、空载 CPU 开销）。
            // 挂起等待归还信号或取消——信号由 Release 成功回池时发出（与同步 Acquire
            // 共用 _blockGate，单一信号源），计数持久化语义保证无丢失唤醒。
            // 设计说明：执行计划原草图为本方法引入 Channel<T> 推送对象，但对象回池后
            // 所有权仍属分片链表，channel 再持引用会造成双重所有权；所需语义与 T06
            // 信号门同构，故直接复用 SemaphoreSlim（WaitAsync 异步原生），零新增状态。

            // PR-D L5：冷池自举——池完全空时按需创建首个对象。异步路径原本无限等
            // 归还信号，Min=0 冷池首借将永久挂起直到取消，自举是唯一的确定性出口。
            var coldBoot = TryColdBootAcquire(0);
            if (coldBoot is not null) return coldBoot;

            await _blockGate.WaitAsync(cancellationToken);
        }

        throw new TaskCanceledException();
    }

    /// <summary>
    /// PR-E A3（=M5 统一 CT 传递）：异步获取池化对象，带超时边界。
    /// 实现方式：链接 CTS 组合「外部取消 + 超时」两路取消源转调无超时版本，
    /// 零新增池状态；超时语义与同步 <see cref="Acquire(TimeSpan)"/> 对齐——
    /// 超时抛 <see cref="TimeoutException"/>（含 missed 计数与强制扩容一步），
    /// 外部取消传播 <see cref="TaskCanceledException"/>，二者可通过取消源区分。
    /// </summary>
    public async Task<T> AcquireAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout == Timeout.InfiniteTimeSpan)
        {
            return await AcquireAsync(cancellationToken).ConfigureAwait(false);
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(timeout);
        try
        {
            return await AcquireAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 仅超时触发（外部 CT 未取消）→ 与同步 Acquire(timeout) 的 BlockTimeout 分支对齐
            if (_enableMetrics) Interlocked.Increment(ref _totalMissed);
            if (_enableAutoScaling) ForceScaleUpOneStep();
            throw new TimeoutException($"HayatePool [{_name}] 获取对象超时（异步），超时时间：{timeout.TotalSeconds}s");
        }
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
                w.LastReleasedAt = Stopwatch.GetTimestamp();
                w.LeaseTimeMs = (long)((w.LastReleasedAt - w.LastBorrowedAt) * 1000.0 / Stopwatch.Frequency);
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

            // M16：租约结束——清空当前异步流的租约上下文（Current 归 null；
            // AsyncLocal 写值有执行上下文复制成本，仅取证开启方付费）。
            if (_enableLeakDetection && _leakTraceCaptureMode != HayateLeakTraceCaptureMode.Off)
            {
                HayateLeaseContext.DetachFromFlow();
            }

            // T06：对象已成功回池，唤醒一个等待中的 Acquire（Block/BlockTimeout/CreateNew）。
            // 无等待者时计数累积，由借出路径 Wait(0) 消费，不会泄漏。
            try { _blockGate.Release(); }
            catch (SemaphoreFullException)
            {
                // int.MaxValue 计数上限保护，正常负载下不可达；吞掉以保证 Release 路径不中断。
            }

            // M12：容量告警埋点——归还使借出水位的回落在此处被感知（状态翻转时复位/再触发）。
            CheckCapacityAlarm();

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
                var w = new HayateObject<T>(o) { ShardIndex = targetShard.Index, OwnerPoolName = _name };
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
            if ((Stopwatch.GetTimestamp() - _lastScaleUpTime) / (double)Stopwatch.Frequency < _options.ScaleUpCooldownSeconds) return;

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

            _lastScaleUpTime = Stopwatch.GetTimestamp();
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

    /// <summary>
    /// PR-D L5：冷池自举。池完全空（无空闲且无借出）且容量上限 &gt; 0 时，
    /// 同步创建首个对象直接借出（原子防重，并发首借仅创建一个），
    /// 消除 Min=0 冷池「等超时者补货」的不确定性（T12 ColdStart 实测 2/5 与 5/5 超时两种时序）。
    /// 仅由 Block / BlockTimeout 策略与异步等待路径调用——CreateNew 保持「等满超时后创建」语义（L6），
    /// Abort 保持直接拒绝语义。
    /// </summary>
    /// <returns>自举借出的对象；返回 <c>null</c> 表示本调用未认领自举（池非空 / 达上限 / 他线程正在创建），调用方应继续正常等待。</returns>
    private T TryColdBootAcquire(long waitTimeMs)
    {
        if (_options.MaxPoolSize <= 0) return null;
        if (TrackedObjectCount != 0) return null;
        if (Interlocked.CompareExchange(ref _coldBootClaimed, 1, 0) != 0) return null;

        try
        {
            // 双检：CAS 期间可能有并发归还 / 扩容使池非空——此时回落正常等待路径即可。
            if (TrackedObjectCount != 0) return null;

            var shard = _shards[(int)(Interlocked.Increment(ref _createCursor) - 1) % _shards.Length];
            var w = CreateWrappedObject(shard);   // 内部已完成 TryTrack 登记（含 _totalCreated 计数）
            w.Location = HayateObjectLocation.Borrowed;
            // M4：借出时间戳无条件记录（同 Acquire 主路径，保障泄漏回查可用）
            w.LastBorrowedAt = Stopwatch.GetTimestamp();
            // M20：累计借出次数（自举即借出，包装对象此刻由本线程独占）
            w.LeaseCount++;
            _policy.OnAcquire(w.Value);
            Interlocked.Increment(ref _totalAcquired);

            // M12：容量告警埋点（禁用时内部立即返回）
            CheckCapacityAlarm();

            if (_enableMetrics)
            {
                UpdateWaitTimeStats(waitTimeMs);
                _metrics.RecordObjectAcquired(_name, w.Value, waitTimeMs);
            }

            _logger.LogInformation("Pool [{PoolName}] cold-boot acquired on demand (pool was empty)", _name);
            return w.Value;
        }
        finally
        {
            // 创建成功或失败都必须复位，池再次清空后仍可自举；
            // CreateWrappedObject 抛异常时异常向调用方传播（与 CreateNew 路径行为一致）。
            Interlocked.Exchange(ref _coldBootClaimed, 0);
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
            // M16：销毁时清空租约上下文引用，栈帧随上下文可被 GC（AsyncLocal 流侧
            // 由各调用方 Release 时自行 Detach，此处不动他流上下文——AsyncLocal 语义如此）。
            w.LeaseContext = null;
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
    /// M18：计算本次借出的起始分片索引。
    /// None 恒返 0（调用方以 <c>_affinityMode != None</c> 前置短路，保证默认路径零开销）；
    /// Thread 按托管线程 ID 黄金比例散列稳定映射（同线程恒优先命中同一分片，且与分片数无公约数耦合）；
    /// Custom 走用户委托，null/越界/异常一律回落 0（借出路径健壮性优先，绝不因策略缺失中断借出）。
    /// </summary>
    private int SelectStartShardIndex()
    {
        switch (_affinityMode)
        {
            case HayateShardAffinityMode.Thread:
                return (int)((uint)Environment.CurrentManagedThreadId * 2654435761u % (uint)_shards.Length);

            case HayateShardAffinityMode.Custom:
                try
                {
                    var idx = _customShardAffinity?.Invoke();
                    if (idx.HasValue && (uint)idx.Value < (uint)_shards.Length)
                        return idx.Value;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "CustomShardAffinity selector failed; falling back to sequential scan");
                }
                return 0;

            default:
                return 0;
        }
    }

    /// <summary>
    /// M16：借出路径租约上下文采集。创建不可变租约（租约 ID + 帧数组 + 借出时刻），
    /// 同时写入包装对象（快照取证用）与当前异步流（AsyncLocal，调用方可经
    /// <see cref="HayateLeaseContext.Current"/> 读取；并发借还各自隔离，不相互覆盖）。
    /// 帧采集 fNeedFileInfo:false——不解析源文件/行号，避免 PDB I/O。
    /// </summary>
    private void CaptureLeaseContext(HayateObject<T> w)
    {
        var frames = new StackTrace(fNeedFileInfo: false).GetFrames() ?? Array.Empty<StackFrame>();
        var ctx = new HayateLeaseContext(frames, Stopwatch.GetTimestamp());
        w.LeaseContext = ctx;
        ctx.AttachToFlow();
    }

    /// <summary>
    /// M10/M16：将租约上下文格式化为单行文本（供快照 LeakTraces 输出）。
    /// 帧之间以 " <- " 连接（调用方向：最外层帧在前），帧格式
    /// <c>Type.Method+0x偏移</c>；无上下文/无帧时回退占位文本。
    /// </summary>
    private static string FormatLeaseTrace(HayateLeaseContext ctx)
    {
        var frames = ctx?.Frames;
        if (frames is null || frames.Length == 0)
            return "No stack trace available";

        var sb = new System.Text.StringBuilder(frames.Length * 48);
        if (ctx.LeaseId > 0)
        {
            sb.Append("[Lease ").Append(ctx.LeaseId).Append("] ");
        }

        for (var i = 0; i < frames.Length; i++)
        {
            var method = frames[i].GetMethod();
            if (method is null) continue;

            if (sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(" <- ");
            sb.Append(method.DeclaringType?.Name).Append('.').Append(method.Name)
              .Append("+0x").Append(frames[i].GetNativeOffset().ToString("X"));
        }

        return sb.Length > 0 ? sb.ToString() : "No stack trace available";
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

    /// <summary>
    /// M12：容量告警检查。使用率口径 = 借出数 / MaxPoolSize（借出数由
    /// 「真实存活总数 - 空闲数」派生，与 TakeSnapshot 同口径）。
    /// 状态翻转去抖：只有经 CAS 成功翻转状态的线程触发一次回调；
    /// 回落低水位静默复位（不触发回调），重新越线可再次触发。
    /// 禁用（WarnAtRatio=0 且 CriticalAtRatio=0）时由调用方的只读标志短路，零开销。
    /// </summary>
    private void CheckCapacityAlarm()
    {
        if (!_capacityAlarmEnabled) return;

        var max = _options.MaxPoolSize;
        if (max <= 0) return;

        var borrowed = TrackedObjectCount - _shards.Sum(s => s.Count);
        if (borrowed < 0) borrowed = 0;
        var ratio = (double)borrowed / max;

        var critical = _options.CriticalAtRatio;
        var warn = _options.WarnAtRatio;
        int desired =
            critical > 0 && ratio >= critical ? 2 :
            warn > 0 && ratio >= warn ? 1 :
            0;

        var current = Volatile.Read(ref _capacityAlarmLevel);
        if (current == desired) return;

        // 并发翻转：CAS 赢家负责触发回调，输家直接放弃（水位事件允许最终一致）。
        if (Interlocked.CompareExchange(ref _capacityAlarmLevel, desired, current) != current) return;

        // 回落到 Normal：静默复位，仅整备状态机，不打扰用户。
        if (desired == 0) return;

        try
        {
            var level = desired == 2 ? HayatePoolCapacityAlarmLevel.Critical : HayatePoolCapacityAlarmLevel.Warning;
            var args = new HayatePoolCapacityAlarmEventArgs(_name, level, ratio, borrowed, max);

            if (desired == 2)
            {
                _logger.LogWarning("Pool [{PoolName}] capacity CRITICAL: usage {Ratio:P1} ({Borrowed}/{Max})",
                    _name, ratio, borrowed, max);
                _options.OnCapacityCritical?.Invoke(args);
            }
            else
            {
                _logger.LogWarning("Pool [{PoolName}] capacity WARNING: usage {Ratio:P1} ({Borrowed}/{Max})",
                    _name, ratio, borrowed, max);
                _options.OnCapacityWarning?.Invoke(args);
            }
        }
        catch (Exception ex)
        {
            // 用户回调异常不得影响借还主流程
            _logger.LogError(ex, "Capacity alarm callback failed for pool [{PoolName}]", _name);
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
            var now = Stopwatch.GetTimestamp();
            int sampleCapacity = _options.NumTestsPerEvictionRun < 1 ? 1 : _options.NumTestsPerEvictionRun;
            var evictionSample = new HayateObject<T>[sampleCapacity];

            foreach (var shard in _shards)
            {
                // 分批检查（PR-D A2：复用采样缓冲，避免每次对整条空闲链表 ToArray 的长尾分配）
                int evictedCount = 0;
                int sampleCount = shard.SnapshotHead(evictionSample, evictionSample.Length);

                for (int samplerIndex = 0; samplerIndex < sampleCount; samplerIndex++)
                {
                    var w = evictionSample[samplerIndex];
                    // 跳过正在使用的对象
                    if (w.IsBorrowed) continue;

                    // 判断是否需要驱逐（PR-D A1：Stopwatch ticks → 秒换算）
                    var isExpired = (now - w.CreatedAt) / (double)Stopwatch.Frequency > _options.MaxLifeTime.TotalSeconds;
                    var isIdleTooLong = (now - w.LastReleasedAt) / (double)Stopwatch.Frequency > _options.MaxIdleTime.TotalSeconds;
                    var isSoftIdle = (now - w.LastReleasedAt) / (double)Stopwatch.Frequency > _options.SoftMinEvictableIdleTime.TotalSeconds;

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
#if NET48
            // .NET Framework 4.8 不支持 Math.Clamp（netcoreapp2.0+ / netstandard2.1 才引入），
            // 用 Max/Min 组合等价实现；net6+ 等高版本走 #else 分支的原生 Math.Clamp。
            targetSize = Math.Max(_options.MinPoolSize, Math.Min(_options.MaxPoolSize, targetSize));
#else
            targetSize = Math.Clamp(targetSize, _options.MinPoolSize, _options.MaxPoolSize);
#endif

            // 扩容冷却时间 3 秒，缩容冷却时间 15 秒，防止频繁抖动
            var canScaleUp = (Stopwatch.GetTimestamp() - _lastScaleUpTime) / (double)Stopwatch.Frequency >= _options.ScaleUpCooldownSeconds;
            var canScaleDown = (Stopwatch.GetTimestamp() - _lastScaleDownTime) / (double)Stopwatch.Frequency >= _options.ScaleDownCooldownSeconds;

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

                _lastScaleUpTime = Stopwatch.GetTimestamp();
                _logger.LogInformation("Pool [{PoolName}] scaled UP. {CurrentCapacity} → {TargetCapacity}", _name, currentTotal, targetSize);
                if (_enableMetrics)
                {
                    _metrics.RecordPoolScaled(_name, "UP", currentTotal, targetSize);
                }
            }
            // S1（2.4 行为变更）：移除缩容互斥门控。原 `totalIdle < currentTotal * 0.4f`
            // 要求占用率 >0.6 才放行，而策略层（ThresholdScalingStrategy）要求占用率
            // <ScaleDownThreshold(默认0.2) 才给出缩小目标——两者永远无法同时成立，
            // 导致默认配置下缩容分支为死代码（autoScaling 只扩不缩）。
            // 现缩容判定完全信任策略层：targetSize < currentTotal && 冷却期已过即放行；
            // 抖动由 canScaleDown 冷却 + ScaleDownStep 步长双重防线约束。
            else if (targetSize < currentTotal && canScaleDown)
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
                _lastScaleDownTime = Stopwatch.GetTimestamp();
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
            LeakSuspectedCount = Interlocked.Read(ref _leakSuspectedCount),
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
        // M20：逐对象生命周期明细（快照为诊断路径，O(n) 汇总可接受）
        var details = new List<HayatePoolObjectDetail>();
        // 快照 Timestamp 保留墙钟时间（对外语义不变）；泄漏判定改用 Stopwatch ticks（PR-D A1）
        var wallClock = DateTimeOffset.UtcNow;
        var now = Stopwatch.GetTimestamp();

        // M10：泄漏取证扫描与 M4 回查告警共用一次登记表遍历（两分支判定条件与 2.4/2.5
        // 既有语义逐一保持一致），同时顺带产出 M20 的逐对象明细，避免三遍 O(n) 扫描。
        foreach (var shard in _shards)
        {
            foreach (var w in shard.TrackedValues)
            {
                details.Add(new HayatePoolObjectDetail
                {
                    ShardIndex = w.ShardIndex,
                    IsBorrowed = w.IsBorrowed,
                    LeaseCount = w.LeaseCount,
                    CreatedAtTick = w.CreatedAtTick,
                    LeaseTimeMs = w.LeaseTimeMs,
                    Generation = w.Generation,
                    OwnerPoolName = w.OwnerPoolName
                });

                if (_enableLeakDetection)
                {
                    // T13/穿插修复 + T09：泄漏扫描必须遍历分片登记表（全部存活包装对象）。
                    // 原实现遍历 shard.GetAll()（仅池内空闲对象），而 TryTake 会把借出对象
                    // 物理摘出分片链表——借出对象从未被扫描，泄漏检测结构上恒不触发。
                    // 检查泄露（PR-D A1：Stopwatch ticks → 秒换算）
                    if (w.IsBorrowed &&
                        (now - w.LastBorrowedAt) / (double)Stopwatch.Frequency > _options.LeakDetectionThreshold.TotalSeconds)
                    {
                        // M16（2.5）：LeakTraces 仍为文本形态，由池侧按租约上下文格式化
                        //（含租约 ID 前缀；2.4 及之前为 Environment.StackTrace 全文原样输出）
                        leakTraces.Add(FormatLeaseTrace(w.LeaseContext));
                        Interlocked.Increment(ref _leakDetectedCount);
                    }
                }
                else
                {
                    // M4（2.5）：泄漏检测关闭时的回查告警通路。按同一 LeakDetectionThreshold
                    // 统计「借出超阈值未归还」的疑似泄漏次数（LeakSuspectedCount），与
                    // LeakDetectedCount 并列——只计数、不取证（无 AcquireStackFrames 采集）、
                    // 不触发任何回收行为，L1 取证三模式语义完全不变。
                    // LastBorrowedAt 自 2.5 起在借出路径无条件记录，因此「全功能关闭」配置下
                    // 回查依然可用（2.4 及之前该时间戳受功能开关门控，存在恒 0 的可能）。
                    if (w.IsBorrowed && w.LastBorrowedAt != 0 &&
                        (now - w.LastBorrowedAt) / (double)Stopwatch.Frequency > _options.LeakDetectionThreshold.TotalSeconds)
                    {
                        Interlocked.Increment(ref _leakSuspectedCount);
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
            Timestamp = wallClock,
            PooledCount = pooledCount,
            BorrowedCount = borrowedCount,
            TotalCreated = Interlocked.Read(ref _totalCreated),
            TotalMissed = Interlocked.Read(ref _totalMissed),
            TotalAcquired = Interlocked.Read(ref _totalAcquired),
            LeakCount = Interlocked.Read(ref _leakDetectedCount),
            LeakSuspectedCount = Interlocked.Read(ref _leakSuspectedCount),
            LeakTraces = leakTraces.AsReadOnly(),
            ObjectDetails = details.AsReadOnly()
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