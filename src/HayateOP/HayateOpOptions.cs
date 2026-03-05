using System;

namespace DotNetCore.HayateOP;

/// <summary>
/// Hayate 对象池运行参数。
/// </summary>
/// <remarks>
/// 该配置用于平衡吞吐、延迟与资源占用。时间类配置优先使用 <see cref="TimeSpan"/>；
/// 带 <c>Ms</c> 后缀的配置单位为毫秒。
/// </remarks>
public class HayateOpOptions
{
    /// <summary>
    /// 池允许的最大并发借用数。默认值：<see cref="HayateOpConsts.DEFAULT_MAX_CONCURRENT"/>（20）。
    /// </summary>
    /// <remarks>
    /// 用途：限制同一时刻可借出的对象总量。特例：突发流量下会更早触发拒绝策略。边界：建议大于等于 1。推荐值区间：16~512。
    /// </remarks>
    public int MaxConcurrent { get; set; } = HayateOpConsts.DEFAULT_MAX_CONCURRENT;

    /// <summary>
    /// 池预热的最小对象数。默认值：<see cref="HayateOpConsts.DEFAULT_MIN_POOL_SIZE"/>（5）。
    /// </summary>
    /// <remarks>
    /// 用途：启动阶段预创建对象，降低冷启动抖动。特例：设置为 0 时不预热。边界：建议大于等于 0，且不大于 <see cref="MaxPoolSize"/>。推荐值区间：0~64。
    /// </remarks>
    public int MinPoolSize { get; set; } = HayateOpConsts.DEFAULT_MIN_POOL_SIZE;

    /// <summary>
    /// 池允许维护的最大对象数。默认值：<see cref="HayateOpConsts.DEFAULT_MAX_POOL_SIZE"/>（50）。
    /// </summary>
    /// <remarks>
    /// 用途：限制内存与下游资源上限。特例：上限过小会放大等待与超时。边界：建议大于等于 1，且不小于 <see cref="MinPoolSize"/>。推荐值区间：32~2048。
    /// </remarks>
    public int MaxPoolSize { get; set; } = HayateOpConsts.DEFAULT_MAX_POOL_SIZE;

    /// <summary>
    /// 是否启用指标采集。默认值：<c>false</c>。
    /// </summary>
    /// <remarks>
    /// 用途：输出池运行指标用于观测。特例：高频路径下开启会增加少量开销。边界：布尔开关。推荐值区间：测试/生产建议开启，极限性能压测可关闭。
    /// </remarks>
    public bool EnableMetrics { get; set; } = false;

    /// <summary>
    /// 伸缩策略检查周期（毫秒）。默认值：<see cref="HayateOpConsts.DEFAULT_SCALING_INTERVAL_MILLISECONDS"/>（5000ms）。
    /// </summary>
    /// <remarks>
    /// 用途：控制扩缩容决策频率。特例：过小会导致频繁调整，过大则响应慢。边界：建议大于 0。推荐值区间：1000~10000ms。
    /// </remarks>
    public int ScalingIntervalMs { get; set; } = HayateOpConsts.DEFAULT_SCALING_INTERVAL_MILLISECONDS;

    /// <summary>
    /// 扩容触发阈值（使用率）。默认值：<see cref="HayateOpConsts.DEFAULT_SCALE_UP_THRESHOLD"/>（0.8）。
    /// </summary>
    /// <remarks>
    /// 用途：当使用率达到阈值时倾向扩容。特例：与 <see cref="ScaleDownThreshold"/> 过近会造成抖动。边界：建议在 0~1 之间，且大于缩容阈值。推荐值区间：0.70~0.90。
    /// </remarks>
    public double ScaleUpThreshold { get; set; } = HayateOpConsts.DEFAULT_SCALE_UP_THRESHOLD;

    /// <summary>
    /// 缩容触发阈值（使用率）。默认值：<see cref="HayateOpConsts.DEFAULT_SCALE_DOWN_THRESHOLD"/>（0.2）。
    /// </summary>
    /// <remarks>
    /// 用途：当使用率长期低于阈值时倾向缩容。特例：过高会导致对象频繁销毁重建。边界：建议在 0~1 之间，且小于扩容阈值。推荐值区间：0.10~0.40。
    /// </remarks>
    public double ScaleDownThreshold { get; set; } = HayateOpConsts.DEFAULT_SCALE_DOWN_THRESHOLD;

    /*
     * Validate
     */

    /// <summary>
    /// 借出前是否校验对象有效性。默认值：<c>false</c>。
    /// </summary>
    /// <remarks>
    /// 用途：在 <c>Get</c> 前剔除失效对象。特例：启用后会增加借出路径延迟。边界：布尔开关。推荐值区间：对象易失效时开启；纯内存轻对象可关闭。
    /// </remarks>
    public bool ValidateOnBorrow { get; set; }

    /// <summary>
    /// 归还前是否校验对象有效性。默认值：<c>false</c>。
    /// </summary>
    /// <remarks>
    /// 用途：在归还阶段过滤异常对象。特例：当前版本主流程未使用该开关，可视为预留配置。边界：布尔开关。推荐值区间：保持默认，待实现接入后再按业务开启。
    /// </remarks>
    public bool ValidateOnReturn { get; set; }

    /// <summary>
    /// 是否对空闲对象进行周期校验（属性名沿用当前实现）。默认值：<c>false</c>。
    /// </summary>
    /// <remarks>
    /// 用途：定期清理空闲队列中的无效对象。特例：当前版本主流程未使用该开关，可视为预留配置。边界：布尔开关。推荐值区间：保持默认。
    /// </remarks>
    public bool ValidateWhiteIdle { get; set; }

    /// <summary>
    /// 周期校验间隔（毫秒）。默认值：<see cref="HayateOpConsts.DEFAULT_VALIDATE_INTERVAL_MILLISECONDS"/>（30000ms）。
    /// </summary>
    /// <remarks>
    /// 用途：控制后台校验任务频率。特例：即使相关开关未开启，间隔过小也会增加定时器唤醒频率。边界：建议大于 0。推荐值区间：10000~60000ms。
    /// </remarks>
    public int ValidateIntervalMs { get; set; } = HayateOpConsts.DEFAULT_VALIDATE_INTERVAL_MILLISECONDS;

    /*
     * Eviction
     */

    /// <summary>
    /// 对象最大存活时长。默认值：<c>TimeSpan.FromMinutes(10)</c>。
    /// </summary>
    /// <remarks>
    /// 用途：限制对象生命周期，降低陈旧状态风险。特例：外部连接类对象可设置更短。边界：建议大于 <see cref="TimeSpan.Zero"/>。推荐值区间：5~60 分钟。
    /// </remarks>
    public TimeSpan MaxLifeTime { get; set; } = TimeSpan.FromMinutes(HayateOpConsts.DEFAULT_MAX_LIFE_TIME_MINUTES);

    /// <summary>
    /// 对象最大空闲时长。默认值：<c>TimeSpan.FromMinutes(5)</c>。
    /// </summary>
    /// <remarks>
    /// 用途：清理长期未使用对象，回收资源。特例：低频业务可适当放宽以减少重建。边界：建议大于等于 <see cref="TimeSpan.Zero"/>。推荐值区间：1~30 分钟。
    /// </remarks>
    public TimeSpan MaxIdleTime { get; set; } = TimeSpan.FromMinutes(HayateOpConsts.DEFAULT_MAX_IDLE_TIME_MINUTES);

    /// <summary>
    /// 软最小可驱逐空闲时长。默认值：<c>TimeSpan.FromMinutes(2)</c>。
    /// </summary>
    /// <remarks>
    /// 用途：给空闲对象保留最短驻留时间，减少抖动。特例：资源紧张时仍可能被驱逐。边界：建议大于等于 <see cref="TimeSpan.Zero"/>，且不大于 <see cref="MaxIdleTime"/>。推荐值区间：0.5~10 分钟。
    /// </remarks>
    public TimeSpan SoftMinEvictableIdleTime { get; set; } = TimeSpan.FromMinutes(HayateOpConsts.DEFAULT_MIN_EVICTION_IDLE_TIME_MINUTES);
    
    /// <summary>
    /// 驱逐扫描周期（毫秒）。默认值：<see cref="HayateOpConsts.DEFAULT_EVICTION_INTERVAL_MILLISECONDS"/>（30000ms）。
    /// </summary>
    /// <remarks>
    /// 用途：控制驱逐任务执行频率。特例：扫描过于频繁会提高 CPU 与锁竞争。边界：建议大于 0。推荐值区间：10000~60000ms。
    /// </remarks>
    public int EvictionIntervalMs { get; set; } = HayateOpConsts.DEFAULT_EVICTION_INTERVAL_MILLISECONDS;

    /// <summary>
    /// 每次驱逐扫描的样本数。默认值：<see cref="HayateOpConsts.DEFAULT_EVICTION_RUNS_PER_EVICTION"/>（10）。
    /// </summary>
    /// <remarks>
    /// 用途：控制单次驱逐开销与清理力度。特例：值过小会延迟清理，值过大会影响峰值延迟。边界：建议大于等于 1。推荐值区间：5~128。
    /// </remarks>
    public int NumTestsPerEvictionRun { get; set; } = HayateOpConsts.DEFAULT_EVICTION_RUNS_PER_EVICTION;
    
    /*
     * Timeout
     */

    /// <summary>
    /// 默认借用超时时间。默认值：<c>TimeSpan.FromSeconds(5)</c>。
    /// </summary>
    /// <remarks>
    /// 用途：作为无参 <c>Get</c> 的等待上限。特例：在阻塞策略下，超时后会进入拒绝策略分支。边界：建议大于 <see cref="TimeSpan.Zero"/>。推荐值区间：1~30 秒。
    /// </remarks>
    public TimeSpan DefaultGetTimeout { get; set; } = TimeSpan.FromSeconds(HayateOpConsts.DEFAULT_GET_TIMEOUT_SECONDS);

    /*
     * Fair Semaphore
     */

    /// <summary>
    /// 是否启用公平信号量语义。默认值：<c>false</c>。
    /// </summary>
    /// <remarks>
    /// 用途：用于控制排队公平性策略。特例：当前版本主流程未使用该开关，可视为预留配置。边界：布尔开关。推荐值区间：保持默认。
    /// </remarks>
    public bool UseFairSemaphore { get; set; } = false;

    /*
     * Leak Detection
     */

    /// <summary>
    /// 泄漏检测阈值。默认值：<c>TimeSpan.FromMinutes(30)</c>（由 <see cref="HayateOpConsts.DEFAULT_LEAK_DETECTION_THRESHOLD_SECONDS"/> 构造）。
    /// </summary>
    /// <remarks>
    /// 用途：定义借出对象多久未归还才视为疑似泄漏。特例：常量名为“SECONDS”，但当前默认表达式按“分钟”构造。边界：建议大于 <see cref="TimeSpan.Zero"/>。推荐值区间：10 秒~10 分钟（请按实际耗时调整）。
    /// </remarks>
    public TimeSpan LeakDetectionThreshold { get; set; } = TimeSpan.FromMinutes(HayateOpConsts.DEFAULT_LEAK_DETECTION_THRESHOLD_SECONDS);

    /// <summary>
    /// 是否启用泄漏检测。默认值：<c>false</c>。
    /// </summary>
    /// <remarks>
    /// 用途：记录借出调用栈等信息用于排查未归还对象。特例：开启后会增加少量内存与字符串开销。边界：布尔开关。推荐值区间：联调和排障阶段开启，稳定高压生产可按需关闭。
    /// </remarks>
    public bool EnableLeakDetection { get; set; } = false;

    /*
     * Reject policy
     */

    /// <summary>
    /// 借用失败时的拒绝策略。默认值：<see cref="HayateOpRejectPolicy.BlockTimeout"/>。
    /// </summary>
    /// <remarks>
    /// 用途：定义等待超时后的处理动作。特例：当前实现对 <c>Abort</c> 与 <c>CreateNew</c> 有显式分支，其他值会抛出 <see cref="InvalidOperationException"/>。边界：必须是有效枚举值。推荐值区间：通用场景使用 <c>BlockTimeout</c>，降级兜底可选 <c>CreateNew</c>。
    /// </remarks>
    public HayateOpRejectPolicy RejectPolicy { get; set; } = HayateOpRejectPolicy.BlockTimeout;

    /*
     * Creation retry
     */

    /// <summary>
    /// 创建失败后的最大重试次数。默认值：<see cref="HayateOpConsts.DEFAULT_CREATION_RETRY_COUNT"/>（3）。
    /// </summary>
    /// <remarks>
    /// 用途：提高瞬时失败时的创建成功率。特例：设置为 0 将直接失败不重试。边界：建议大于等于 0。推荐值区间：1~5。
    /// </remarks>
    public int CreationRetryCount { get; set; } = HayateOpConsts.DEFAULT_CREATION_RETRY_COUNT;

    /// <summary>
    /// 创建重试间隔。默认值：<c>TimeSpan.FromMilliseconds(100)</c>（当前使用 <see cref="HayateOpConsts.DEFAULT_CREATION_RETRY_DELAY_MILLISECONDS"/>）。
    /// </summary>
    /// <remarks>
    /// 用途：控制连续重试之间的退避时间。特例：当前默认值较大（30s），与“创建重试延迟”常见预期不一致。边界：建议大于等于 <see cref="TimeSpan.Zero"/>。推荐值区间：50~1000ms。
    /// </remarks>
    public TimeSpan CreationRetryDelay { get; set; } = TimeSpan.FromMilliseconds(HayateOpConsts.DEFAULT_CREATION_RETRY_DELAY_MILLISECONDS);
    
    /*
     * Sharding
     */

    /// <summary>
    /// 分片数量。默认值：<see cref="HayateOpConsts.DEFAULT_SHARD_COUNT"/>（4）。
    /// </summary>
    /// <remarks>
    /// 用途：通过多分片降低并发争用。特例：分片过多会提升管理成本并放大预热偏差。边界：建议大于等于 1。推荐值区间：2~16。
    /// </remarks>
    public int ShardCount { get; set; } = HayateOpConsts.DEFAULT_SHARD_COUNT;
    
    /*
     * Generational pool
     */
    
    /// <summary>
    /// 对象晋升代际的阈值（毫秒）。默认值：<see cref="HayateOpConsts.DEFAULT_GEN_THRESHOLD_MILLISECONDS"/>（30000ms）。
    /// </summary>
    /// <remarks>
    /// 用途：标记长期存活对象，供策略层做分代管理。特例：阈值过小会导致对象过早晋升。边界：建议大于等于 0。推荐值区间：5000~120000ms。
    /// </remarks>
    public int GenerationThresholdMs { get; set; } = HayateOpConsts.DEFAULT_GEN_THRESHOLD_MILLISECONDS;
    
}