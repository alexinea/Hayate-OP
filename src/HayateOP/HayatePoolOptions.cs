using System;
using DotNetCore.HayateOP.Common;

namespace DotNetCore.HayateOP;

/// <summary>
/// Hayate 对象池运行参数。
/// </summary>
/// <remarks>
/// 该配置用于平衡吞吐、延迟与资源占用。时间类配置优先使用 <see cref="TimeSpan"/>；
/// 带 <c>Ms</c> 后缀的配置单位为毫秒。
/// </remarks>
public class HayatePoolOptions
{
    /// <summary>
    /// 池预热的最小对象数。<br />
    /// 默认值：<see cref="HayateConstant.DEFAULT_MIN_POOL_SIZE"/>（5）。
    /// </summary>
    /// <remarks>
    /// 用途：启动阶段预创建对象，降低冷启动抖动。<br />
    /// 特例：设置为 0 时不预热。<br />
    /// 边界：建议大于等于 0，且不大于 <see cref="MaxPoolSize"/>。<br />
    /// 推荐值区间：0~64。
    /// </remarks>
    public int MinPoolSize { get; set; } = HayateConstant.DEFAULT_MIN_POOL_SIZE;

    /// <summary>
    /// 池允许维护的最大对象数。<br />
    /// 默认值：<see cref="HayateConstant.DEFAULT_MAX_POOL_SIZE"/>（50）。
    /// </summary>
    /// <remarks>
    /// 用途：限制内存与下游资源上限。<br />
    /// 特例：上限过小会放大等待与超时。<br />
    /// 边界：建议大于等于 1，且不小于 <see cref="MinPoolSize"/>。<br />
    /// 推荐值区间：32~2048。
    /// </remarks>
    public int MaxPoolSize { get; set; } = HayateConstant.DEFAULT_MAX_POOL_SIZE;

    #region 超时

    /// <summary>
    /// 默认借用超时时间。<br />
    /// 默认值：<c>TimeSpan.FromSeconds(5)</c>。
    /// </summary>
    /// <remarks>
    /// 用途：作为无参 <c>Acquire</c> 的等待上限。<br />
    /// 特例：在阻塞策略下，超时后会进入拒绝策略分支。<br />
    /// 边界：建议大于 <see cref="TimeSpan.Zero"/>。<br />
    /// 推荐值区间：1~30 秒。
    /// </remarks>
    public TimeSpan DefaultAcquireTimeout { get; set; } = TimeSpan.FromSeconds(HayateConstant.DEFAULT_ACQUIRE_TIMEOUT_SECONDS);

    #endregion

    #region 拒绝策略

    /// <summary>
    /// 借用失败时的拒绝策略。<br />
    /// 默认值：<see cref="HayatePoolRejectPolicy.BlockTimeout"/>。
    /// </summary>
    /// <remarks>
    /// 用途：定义等待超时后的处理动作。<br />
    /// 特例：当前实现对 <c>Abort</c> 与 <c>CreateNew</c> 有显式分支，其他值会抛出 <see cref="InvalidOperationException"/>。<br />
    /// 边界：必须是有效枚举值。<br />
    /// 推荐值区间：通用场景使用 <c>BlockTimeout</c>，降级兜底可选 <c>CreateNew</c>。
    /// </remarks>
    public HayatePoolRejectPolicy RejectPolicy { get; set; } = HayatePoolRejectPolicy.BlockTimeout;

    #endregion

    #region 创建重试

    /*
     * Creation retry
     */

    /// <summary>
    /// 创建失败后的最大重试次数。<br />
    /// 默认值：<see cref="HayateConstant.DEFAULT_CREATION_RETRY_COUNT"/>（3）。
    /// </summary>
    /// <remarks>
    /// 用途：提高瞬时失败时的创建成功率。<br />
    /// 特例：设置为 0 将直接失败不重试。<br />
    /// 边界：建议大于等于 0。<br />
    /// 推荐值区间：1~5。
    /// </remarks>
    public int CreationRetryCount { get; set; } = HayateConstant.DEFAULT_CREATION_RETRY_COUNT;

    /// <summary>
    /// 创建重试间隔。<br />
    /// 默认值：<c>TimeSpan.FromMilliseconds(100)</c>（当前使用 <see cref="HayateConstant.DEFAULT_CREATION_RETRY_DELAY_MILLISECONDS"/>）。
    /// </summary>
    /// <remarks>
    /// 用途：控制连续重试之间的退避时间。<br />
    /// 特例：当前默认值较大（30s），与“创建重试延迟”常见预期不一致。<br />
    /// 边界：建议大于等于 <see cref="TimeSpan.Zero"/>。<br />
    /// 推荐值区间：50~1000ms。
    /// </remarks>
    public TimeSpan CreationRetryDelay { get; set; } = TimeSpan.FromMilliseconds(HayateConstant.DEFAULT_CREATION_RETRY_DELAY_MILLISECONDS);

    #endregion

    #region 分片策略

    /// <summary>
    /// 启用分片功能（关闭后强制单分片，所有分片相关配置失效）
    /// </summary>
    public bool EnableSharding { get; set; } = true;

    /// <summary>
    /// 分片数量。默认值：<see cref="HayateConstant.DEFAULT_SHARD_COUNT"/>（4）。
    /// </summary>
    /// <remarks>
    /// 用途：通过多分片降低并发争用。<br />
    /// 特例：分片过多会提升管理成本并放大预热偏差。<br />
    /// 边界：建议大于等于 1。<br />
    /// 推荐值区间：2~16。
    /// </remarks>
    public int ShardCount { get; set; } = HayateConstant.DEFAULT_SHARD_COUNT;

    /// <summary>
    /// 是否启用公平信号量语义。<br />
    /// 默认值：<c>false</c>。
    /// </summary>
    /// <remarks>
    /// 用途：用于控制排队公平性策略。<br />
    /// 特例：当前版本主流程未使用该开关，可视为预留配置。<br />
    /// 边界：布尔开关。推荐值区间：保持默认。
    /// </remarks>
    public bool UseFairMode { get; set; } = false;

    #endregion

    #region 扩缩容策略

    /// <summary>
    /// 启用自动扩缩容（关闭后池大小固定为MinPoolSize，所有扩缩容相关配置失效）
    /// </summary>
    public bool EnableAutoScaling { get; set; } = true;

    /// <summary>
    /// 伸缩策略检查周期（毫秒）。<br />
    /// 默认值：<see cref="HayateConstant.DEFAULT_SCALING_INTERVAL_MILLISECONDS"/>（5000ms）。
    /// </summary>
    /// <remarks>
    /// 用途：控制扩缩容决策频率。<br />
    /// 特例：过小会导致频繁调整，过大则响应慢。<br />
    /// 边界：建议大于 0。<br />
    /// 推荐值区间：1000~10000ms。
    /// </remarks>
    public int ScalingIntervalMs { get; set; } = HayateConstant.DEFAULT_SCALING_INTERVAL_MILLISECONDS;

    /// <summary>
    /// 扩容触发阈值（使用率）。<br />
    /// 默认值：<see cref="HayateConstant.DEFAULT_SCALE_UP_THRESHOLD"/>（0.8）。
    /// </summary>
    /// <remarks>
    /// 用途：当使用率达到阈值时倾向扩容。<br />
    /// 特例：与 <see cref="ScaleDownThreshold"/> 过近会造成抖动。<br />
    /// 边界：建议在 0~1 之间，且大于缩容阈值。<br />
    /// 推荐值区间：0.70~0.90。
    /// </remarks>
    public double ScaleUpThreshold { get; set; } = HayateConstant.DEFAULT_SCALE_UP_THRESHOLD;

    /// <summary>
    /// 缩容触发阈值（使用率）。<br />
    /// 默认值：<see cref="HayateConstant.DEFAULT_SCALE_DOWN_THRESHOLD"/>（0.2）。
    /// </summary>
    /// <remarks>
    /// 用途：当使用率长期低于阈值时倾向缩容。<br />
    /// 特例：过高会导致对象频繁销毁重建。<br />
    /// 边界：建议在 0~1 之间，且小于扩容阈值。<br />
    /// 推荐值区间：0.10~0.40。
    /// </remarks>
    public double ScaleDownThreshold { get; set; } = HayateConstant.DEFAULT_SCALE_DOWN_THRESHOLD;
    public int ScaleUpCooldownSeconds { get; set; } = HayateConstant.DEFAULT_SCALE_UP_COOLDOWN_SECONDS;

    public int ScaleDownCooldownSeconds { get; set; } = HayateConstant.DEFAULT_SCALE_DOWN_COOLDOWN_SECONDS;

    public int ScaleUpStep { get; set; } = HayateConstant.DEFAULT_SCALE_UP_STEP;

    /// <summary>
    /// 缩容步长（每次缩容减少的对象数）。<br />
    /// 默认值：<see cref="HayateConstant.DEFAULT_SCALE_DOWN_STEP"/>（5）。
    /// </summary>
    /// <remarks>
    /// 用途：与 <see cref="ScaleUpStep"/> 解耦，便于业务侧"扩容激进、缩容保守"调优。<br />
    /// 边界：必须 ≥ 1；若超过当前可用对象数，结果会被钳制为 <see cref="MinPoolSize"/>。
    /// </remarks>
    public int ScaleDownStep { get; set; } = HayateConstant.DEFAULT_SCALE_DOWN_STEP;

    #endregion

    #region 对象验证

    /// <summary>
    /// 启用对象验证（关闭后所有借出/归还/空闲验证逻辑失效）
    /// </summary>
    public bool EnableValidation { get; set; } = true;

    /// <summary>
    /// 借出前是否校验对象有效性。<br />
    /// 默认值：<c>false</c>。
    /// </summary>
    /// <remarks>
    /// 用途：在 <c>Acquire</c> 前剔除失效对象。<br />
    /// 特例：启用后会增加借出路径延迟。<br />
    /// 边界：布尔开关。<br />
    /// 推荐值区间：对象易失效时开启；纯内存轻对象可关闭。
    /// </remarks>
    public bool ValidateOnBorrow { get; set; }

    /// <summary>
    /// 归还前是否校验对象有效性。<br />
    /// 默认值：<c>false</c>。
    /// </summary>
    /// <remarks>
    /// 用途：在归还阶段过滤异常对象。<br />
    /// 特例：当前版本主流程未使用该开关，可视为预留配置。<br />
    /// 边界：布尔开关。<br />
    /// 推荐值区间：保持默认，待实现接入后再按业务开启。
    /// </remarks>
    public bool ValidateOnReturn { get; set; }

    /// <summary>
    /// 是否对空闲对象进行周期校验（属性名沿用当前实现）。<br />
    /// 默认值：<c>false</c>。
    /// </summary>
    /// <remarks>
    /// 用途：定期清理空闲队列中的无效对象。<br />
    /// 特例：当前版本主流程未使用该开关，可视为预留配置。<br />
    /// 边界：布尔开关。<br />
    /// 推荐值区间：保持默认。
    /// </remarks>
    public bool ValidateWhileIdle { get; set; }

    /// <summary>
    /// 周期校验间隔（毫秒）。<br />
    /// 默认值：<see cref="HayateConstant.DEFAULT_VALIDATE_INTERVAL_MILLISECONDS"/>（30000ms）。
    /// </summary>
    /// <remarks>
    /// 用途：控制后台校验任务频率。<br />
    /// 特例：即使相关开关未开启，间隔过小也会增加定时器唤醒频率。<br />
    /// 边界：建议大于 0。<br />
    /// 推荐值区间：10000~60000ms。
    /// </remarks>
    public int ValidateIntervalMs { get; set; } = HayateConstant.DEFAULT_VALIDATE_INTERVAL_MILLISECONDS;


    #endregion

    #region 驱逐策略

    /// <summary>
    /// 启用空闲对象驱逐（关闭后不执行驱逐逻辑，所有驱逐相关配置失效）
    /// </summary>
    public bool EnableEviction { get; set; } = true;

    /// <summary>
    /// 对象最大存活时长。<br />
    /// 默认值：<c>TimeSpan.FromMinutes(10)</c>。
    /// </summary>
    /// <remarks>
    /// 用途：限制对象生命周期，降低陈旧状态风险。<br />
    /// 特例：外部连接类对象可设置更短。<br />
    /// 边界：建议大于 <see cref="TimeSpan.Zero"/>。<br />
    /// 推荐值区间：5~60 分钟。
    /// </remarks>
    public TimeSpan MaxLifeTime { get; set; } = TimeSpan.FromMinutes(HayateConstant.DEFAULT_MAX_LIFE_TIME_MINUTES);

    /// <summary>
    /// 对象最大空闲时长。<br />
    /// 默认值：<c>TimeSpan.FromMinutes(5)</c>。
    /// </summary>
    /// <remarks>
    /// 用途：清理长期未使用对象，回收资源。<br />
    /// 特例：低频业务可适当放宽以减少重建。<br />
    /// 边界：建议大于等于 <see cref="TimeSpan.Zero"/>。<br />
    /// 推荐值区间：1~30 分钟。
    /// </remarks>
    public TimeSpan MaxIdleTime { get; set; } = TimeSpan.FromMinutes(HayateConstant.DEFAULT_MAX_IDLE_TIME_MINUTES);

    /// <summary>
    /// 软最小可驱逐空闲时长。<br />
    /// 默认值：<c>TimeSpan.FromMinutes(2)</c>。
    /// </summary>
    /// <remarks>
    /// 用途：给空闲对象保留最短驻留时间，减少抖动。<br />
    /// 特例：资源紧张时仍可能被驱逐。<br />
    /// 边界：建议大于等于 <see cref="TimeSpan.Zero"/>，且不大于 <see cref="MaxIdleTime"/>。<br />
    /// 推荐值区间：0.5~10 分钟。
    /// </remarks>
    public TimeSpan SoftMinEvictableIdleTime { get; set; } = TimeSpan.FromMinutes(HayateConstant.DEFAULT_MIN_EVICTION_IDLE_TIME_MINUTES);

    /// <summary>
    /// 驱逐扫描周期（毫秒）。<br />
    /// 默认值：<see cref="HayateConstant.DEFAULT_EVICTION_INTERVAL_MILLISECONDS"/>（30000ms）。
    /// </summary>
    /// <remarks>
    /// 用途：控制驱逐任务执行频率。<br />
    /// 特例：扫描过于频繁会提高 CPU 与锁竞争。<br />
    /// 边界：建议大于 0。<br />
    /// 推荐值区间：10000~60000ms。
    /// </remarks>
    public int EvictionIntervalMs { get; set; } = HayateConstant.DEFAULT_EVICTION_INTERVAL_MILLISECONDS;

    /// <summary>
    /// 每次驱逐扫描的样本数。<br />
    /// 默认值：<see cref="HayateConstant.DEFAULT_EVICTION_RUNS_PER_EVICTION"/>（10）。
    /// </summary>
    /// <remarks>
    /// 用途：控制单次驱逐开销与清理力度。<br />
    /// 特例：值过小会延迟清理，值过大会影响峰值延迟。<br />
    /// 边界：建议大于等于 1。<br />
    /// 推荐值区间：5~128。
    /// </remarks>
    public int NumTestsPerEvictionRun { get; set; } = HayateConstant.DEFAULT_EVICTION_RUNS_PER_EVICTION;

    #endregion

    #region 分代策略

    /// <summary>
    /// 启用分代优化（关闭后所有对象均为年轻代，不跳过验证）
    /// </summary>
    public bool EnableGenerationOptimization { get; set; } = true;

    /// <summary>
    /// 对象晋升代际的阈值（毫秒）。<br />
    /// 默认值：<see cref="HayateConstant.DEFAULT_GEN_THRESHOLD_MILLISECONDS"/>（30000ms）。
    /// </summary>
    /// <remarks>
    /// 用途：标记长期存活对象，供策略层做分代管理。<br />
    /// 特例：阈值过小会导致对象过早晋升。<br />
    /// 边界：建议大于等于 0。<br />
    /// 推荐值区间：5000~120000ms。
    /// </remarks>
    public int GenerationThresholdMs { get; set; } = HayateConstant.DEFAULT_GEN_THRESHOLD_MILLISECONDS;

    /// <summary>
    /// 老年代数据验证的间隔次数配置项
    /// </summary>
    /// <value>
    /// 默认值：3；
    /// 推荐值区间：1 ~ 10；
    /// 边界限制：最小值为1，最大值无硬性限制（建议不超过20）；
    /// </value>
    /// <remarks>
    /// 【核心用途】：控制老年代数据的验证频率，属性值 N 表示每执行 N 次常规检查流程，才对老年代数据执行 1 次全量验证，
    /// 用于平衡老年代数据验证的完整性与性能开销（老年代全量验证耗时较长，高频验证会降低整体处理效率）。
    /// 
    /// 【特例说明】：
    /// 1. 当值为 1 时：每次常规检查流程都会触发老年代全量验证，适用于数据一致性要求极高、性能敏感度低的场景（如金融核心数据校验）；
    /// 2. 当值 ≤ 0 时：框架会自动修正为默认值 3，不允许关闭老年代验证（若需完全关闭，需单独配置 OldGenerationValidationEnabled = false）；
    /// 3. 当值 > 10 时：验证频率过低，可能导致老年代数据异常累积过久，增加问题排查难度，仅建议在纯性能优先、数据容错率高的场景临时使用。
    /// 
    /// 【推荐值区间说明】：
    /// - 常规业务场景（平衡性能与验证完整性）：3 ~ 5；
    /// - 高性能低一致性场景：6 ~ 10；
    /// - 高一致性低性能场景：1 ~ 2；
    /// </remarks>
    public int OldGenerationValidationInterval { get; set; } = HayateConstant.DEFAULT_OLD_GEN_VALIDATION_INTERVAL;

    #endregion

    #region 泄露检测

    /// <summary>
    /// 启用对象泄漏检测（关闭后不记录调用堆栈，不执行泄漏扫描）
    /// </summary>
    public bool EnableLeakDetection { get; set; } = true;

    /// <summary>
    /// 泄漏检测阈值。<br />
    /// 默认值：<c>TimeSpan.FromMinutes(30)</c>（由 <see cref="HayateConstant.DEFAULT_LEAK_DETECTION_THRESHOLD_SECONDS"/> 构造）。
    /// </summary>
    /// <remarks>
    /// 用途：定义借出对象多久未归还才视为疑似泄漏。<br />
    /// 特例：常量名为“SECONDS”，但当前默认表达式按“分钟”构造。<br />
    /// 边界：建议大于 <see cref="TimeSpan.Zero"/>。<br />
    /// 推荐值区间：10 秒~10 分钟（请按实际耗时调整）。
    /// </remarks>
    public TimeSpan LeakDetectionThreshold { get; set; } = TimeSpan.FromMinutes(HayateConstant.DEFAULT_LEAK_DETECTION_THRESHOLD_SECONDS);

    #endregion

    #region 统计指标

    /// <summary>
    /// 是否启用指标采集。<br />
    /// 默认值：<c>false</c>。
    /// </summary>
    /// <remarks>
    /// 用途：输出池运行指标用于观测。<br />
    /// 特例：高频路径下开启会增加少量开销。<br />
    /// 边界：布尔开关。<br />
    /// 推荐值区间：测试/生产建议开启，极限性能压测可关闭。
    /// </remarks>
    public bool EnableMetrics { get; set; } = false;

    #endregion

    public HayatePoolOptions CopyTo()
    {
        var options = new HayatePoolOptions();
        return CopyTo(options);
    }

    public HayatePoolOptions CopyTo(HayatePoolOptions options)
    {
        // 空值校验，保证方法健壮性
        if (options == null)
        {
            throw new ArgumentNullException(nameof(options), "目标配置实例不能为 null");
        }

        // 基础池大小配置
        options.MinPoolSize = this.MinPoolSize;
        options.MaxPoolSize = this.MaxPoolSize;

        // 超时配置
        options.DefaultAcquireTimeout = this.DefaultAcquireTimeout;

        // 拒绝策略
        options.RejectPolicy = this.RejectPolicy;

        // 创建重试配置
        options.CreationRetryCount = this.CreationRetryCount;
        options.CreationRetryDelay = this.CreationRetryDelay;

        // 分片策略
        options.EnableSharding = this.EnableSharding;
        options.ShardCount = this.ShardCount;
        options.UseFairMode = this.UseFairMode;

        // 扩缩容策略
        options.EnableAutoScaling = this.EnableAutoScaling;
        options.ScalingIntervalMs = this.ScalingIntervalMs;
        options.ScaleUpThreshold = this.ScaleUpThreshold;
        options.ScaleDownThreshold = this.ScaleDownThreshold;
        options.ScaleUpCooldownSeconds = this.ScaleUpCooldownSeconds;
        options.ScaleDownCooldownSeconds = this.ScaleDownCooldownSeconds;
        options.ScaleUpStep = this.ScaleUpStep;
        options.ScaleDownStep = this.ScaleDownStep;

        // 对象验证配置
        options.EnableValidation = this.EnableValidation;
        options.ValidateOnBorrow = this.ValidateOnBorrow;
        options.ValidateOnReturn = this.ValidateOnReturn;
        options.ValidateWhileIdle = this.ValidateWhileIdle;
        options.ValidateIntervalMs = this.ValidateIntervalMs;

        // 驱逐策略
        options.EnableEviction = this.EnableEviction;
        options.MaxLifeTime = this.MaxLifeTime;
        options.MaxIdleTime = this.MaxIdleTime;
        options.SoftMinEvictableIdleTime = this.SoftMinEvictableIdleTime;
        options.EvictionIntervalMs = this.EvictionIntervalMs;
        options.NumTestsPerEvictionRun = this.NumTestsPerEvictionRun;

        // 分代策略
        options.EnableGenerationOptimization = this.EnableGenerationOptimization;
        options.GenerationThresholdMs = this.GenerationThresholdMs;
        options.OldGenerationValidationInterval = this.OldGenerationValidationInterval;

        // 泄露检测
        options.EnableLeakDetection = this.EnableLeakDetection;
        options.LeakDetectionThreshold = this.LeakDetectionThreshold;

        // 统计指标
        options.EnableMetrics = this.EnableMetrics;

        return options;
    }

    //public HayatePoolOptions AutoCopy(HayatePoolOptions options)
    //{
    //    options ??= new();

    //    var properties = typeof(HayatePoolOptions).GetProperties();
    //    foreach (var prop in properties)
    //    {
    //        if (!prop.CanRead || !prop.CanWrite) continue;
    //        prop.SetValue(options, prop.GetValue(this));
    //    }

    //    return options;
    //}

    public void ApplyFeatureSwitches()
    {
        // 关闭分片：弹性单分片 + 非公平模式
        if (!EnableSharding)
        {
            ShardCount = 1;
            UseFairMode = false;
        }

        // 关闭自动扩缩容：强制池大小固定
        if (!EnableAutoScaling)
        {
            MaxPoolSize = MinPoolSize;
        }

        // 关闭验证：强制所有验证开关关闭
        if (!EnableValidation)
        {
            ValidateOnBorrow = false;
            ValidateOnReturn = false;
            ValidateWhileIdle = false;
        }
    }

    public bool IsValid()
    {
        ApplyFeatureSwitches();

        // 基础配置校验
        if (MinPoolSize < 0 || MaxPoolSize < MinPoolSize) return false;
        if (DefaultAcquireTimeout <= TimeSpan.Zero) return false;
        if (ShardCount < 1 || ShardCount > 32) return false;
        if (CreationRetryCount < 0) return false;
        if (DefaultAcquireTimeout <= TimeSpan.Zero) return false;

        // 开启扩缩容时的校验
        if (EnableAutoScaling)
        {
            if (ScaleUpThreshold <= ScaleDownThreshold) return false;
            if (ScaleUpThreshold < 0 || ScaleUpThreshold > 1) return false;
            if (ScaleDownThreshold < 0 || ScaleDownThreshold > 1) return false;
            if (ScaleUpStep < 1) return false;
            if (ScaleDownStep < 1) return false;
        }

        // 时间验证
        if (EnableEviction)
        {
            if (MaxLifeTime <= TimeSpan.Zero) return false;
            if (MaxIdleTime <= TimeSpan.Zero) return false;
            if (SoftMinEvictableIdleTime <= TimeSpan.Zero) return false;
        }

        if (EnableSharding)
        {
            if (GenerationThresholdMs < 1000) return false;
            if (OldGenerationValidationInterval < 1) return false;
        }

        if (EnableLeakDetection)
        {
            if (LeakDetectionThreshold <= TimeSpan.Zero) return false;
        }

        return true;
    }
}