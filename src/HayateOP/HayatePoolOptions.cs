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
    ///// <summary>
    ///// 池允许的最大并发借用数。<br />
    ///// 默认值：<see cref="HayateConstant.DEFAULT_MAX_CONCURRENT"/>（32）。
    ///// </summary>
    ///// <remarks>
    ///// 用途：限制同一时刻可借出的对象总量。<br />
    ///// 特例：突发流量下会更早触发拒绝策略。<br />
    ///// 边界：建议大于等于 1。<br />
    ///// 推荐值区间：16~512。
    ///// </remarks>
    //public int MaxConcurrent { get; set; } = HayateConstant.DEFAULT_MAX_CONCURRENT;

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

    /*
     * Scaling
     */

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

    /*
     * Validate
     */

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
    
    /*
     * Eviction
     */

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
    
    /*
     * Timeout
     */

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

    /*
     * Fair Semaphore
     */

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

    /*
     * Leak Detection
     */

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

    /// <summary>
    /// 是否启用泄漏检测。<br />
    /// 默认值：<c>false</c>。
    /// </summary>
    /// <remarks>
    /// 用途：记录借出调用栈等信息用于排查未归还对象。<br />
    /// 特例：开启后会增加少量内存与字符串开销。<br />
    /// 边界：布尔开关。<br />
    /// 推荐值区间：联调和排障阶段开启，稳定高压生产可按需关闭。
    /// </remarks>
    public bool EnableLeakDetection { get; set; } = false;

    /*
     * Reject policy
     */

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
    
    /*
     * Sharding
     */

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
    
    /*
     * Generational pool
     */
    
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
    
    public bool IsValid()
    {
     // 基础数值验证
     if (MinPoolSize < 0 || MaxPoolSize < MinPoolSize) return false;
     if (ScalingIntervalMs < 100) return false;
     if (ScaleUpThreshold < 0 || ScaleUpThreshold > 1) return false;
     if (ScaleDownThreshold < 0 || ScaleDownThreshold > 1) return false;
     if (ScaleDownThreshold >= ScaleUpThreshold) return false;

     // 时间验证
     if (MaxLifeTime <= TimeSpan.Zero) return false;
     if (MaxIdleTime <= TimeSpan.Zero) return false;
     if (SoftMinEvictableIdleTime <= TimeSpan.Zero) return false;
     if (DefaultAcquireTimeout <= TimeSpan.Zero) return false;
     if (LeakDetectionThreshold <= TimeSpan.Zero) return false;

     // 分片/分代验证
     if (ShardCount < 1 || ShardCount > 32) return false;
     if (GenerationThresholdMs < 1000) return false;
     if (OldGenerationValidationInterval < 1) return false;

     return true;
    }
}