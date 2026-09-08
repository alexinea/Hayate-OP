// ReSharper disable InconsistentNaming
namespace DotNetCore.HayateOP.Common;

/// <summary>
/// Hayate 对象池的默认常量定义。
/// 这些常量用于 <see cref="HayatePoolOptions"/> 的默认配置值。
/// </summary>
public static class HayateConstant
{
    ///// <summary>
    ///// 默认最大并发借用数（对应 <see cref="HayatePoolOptions.MaxConcurrent"/>）。
    ///// </summary>
    //public const int DEFAULT_MAX_CONCURRENT = 32;

    /// <summary>
    /// 默认最小池容量（对应 <see cref="HayatePoolOptions.MinPoolSize"/>）。
    /// </summary>
    public const int DEFAULT_MIN_POOL_SIZE = 5;

    /// <summary>
    /// 默认最大池容量（对应 <see cref="HayatePoolOptions.MaxPoolSize"/>）。
    /// </summary>
    public const int DEFAULT_MAX_POOL_SIZE = 50;

    #region 超时

    /*
     * Timeout
     */

    /// <summary>
    /// 默认获取超时时间，单位：秒（对应 <see cref="HayatePoolOptions.DefaultAcquireTimeout"/>）。
    /// </summary>
    public const int DEFAULT_ACQUIRE_TIMEOUT_SECONDS = 5;

    #endregion

    #region 创建重试

    /// <summary>
    /// 默认创建失败重试次数（对应 <see cref="HayatePoolOptions.CreationRetryCount"/>）。
    /// </summary>
    public const int DEFAULT_CREATION_RETRY_COUNT = 3;

    /// <summary>
    /// 默认创建重试延迟，单位：毫秒（建议用于 <see cref="HayatePoolOptions.CreationRetryDelay"/>）。
    /// </summary>
    public const int DEFAULT_CREATION_RETRY_DELAY_MILLISECONDS = 100;

    #endregion

    #region 分片策略

    /// <summary>
    /// 默认分片数量（对应 <see cref="HayatePoolOptions.ShardCount"/>）。
    /// </summary>
    public const int DEFAULT_SHARD_COUNT = 4;

    #endregion

    #region 扩缩容策略

    /// <summary>
    /// 默认伸缩检查间隔，单位：毫秒（对应 <see cref="HayatePoolOptions.ScalingIntervalMs"/>）。
    /// </summary>
    public const int DEFAULT_SCALING_INTERVAL_MILLISECONDS = 5000;

    /// <summary>
    /// 默认扩容阈值，范围 0~1（对应 <see cref="HayatePoolOptions.ScaleUpThreshold"/>）。
    /// </summary>
    public const double DEFAULT_SCALE_UP_THRESHOLD = 0.8;

    /// <summary>
    /// 默认缩容阈值，范围 0~1（对应 <see cref="HayatePoolOptions.ScaleDownThreshold"/>）。
    /// </summary>
    public const double DEFAULT_SCALE_DOWN_THRESHOLD = 0.2;

    public const int DEFAULT_SCALE_UP_COOLDOWN_SECONDS = 3;

    public const int DEFAULT_SCALE_DOWN_COOLDOWN_SECONDS = 15;

    public const int DEFAULT_SCALE_UP_STEP = 5;

    /// <summary>
    /// 默认缩容步长（每次缩容时减少的对象数）。默认与 <see cref="DEFAULT_SCALE_UP_STEP"/> 保持一致，
    /// 业务侧可根据缩容保守度自行调小。
    /// </summary>
    public const int DEFAULT_SCALE_DOWN_STEP = 5;

    #endregion

    #region 对象验证

    /// <summary>
    /// 默认校验扫描间隔，单位：毫秒（对应 <see cref="HayatePoolOptions.ValidateIntervalMs"/>）。
    /// </summary>
    public const int DEFAULT_VALIDATE_INTERVAL_MILLISECONDS = 30000;

    #endregion

    #region 驱逐策略

    /// <summary>
    /// 默认对象最大存活时间，单位：分钟（对应 <see cref="HayatePoolOptions.MaxLifeTime"/>）。
    /// </summary>
    public const int DEFAULT_MAX_LIFE_TIME_MINUTES = 10;

    /// <summary>
    /// 默认对象最大空闲时间，单位：分钟（对应 <see cref="HayatePoolOptions.MaxIdleTime"/>）。
    /// </summary>
    public const int DEFAULT_MAX_IDLE_TIME_MINUTES = 5;

    /// <summary>
    /// 默认软最小可驱逐空闲时间，单位：分钟（对应 <see cref="HayatePoolOptions.SoftMinEvictableIdleTime"/>）。
    /// </summary>
    public const int DEFAULT_MIN_EVICTION_IDLE_TIME_MINUTES = 2;

    /// <summary>
    /// 默认驱逐检查间隔，单位：毫秒（对应 <see cref="HayatePoolOptions.EvictionIntervalMs"/>）。
    /// </summary>
    public const int DEFAULT_EVICTION_INTERVAL_MILLISECONDS = 30000;

    /// <summary>
    /// 默认每次驱逐检查的样本数量（对应 <see cref="HayatePoolOptions.NumTestsPerEvictionRun"/>）。
    /// </summary>
    public const int DEFAULT_EVICTION_RUNS_PER_EVICTION = 10;

    #endregion

    #region 分代策略

    /// <summary>
    /// 默认代际升级阈值，单位：毫秒（对应 <see cref="HayatePoolOptions.GenerationThresholdMs"/>）。
    /// </summary>
    public const int DEFAULT_GEN_THRESHOLD_MILLISECONDS = 30000;

    /// <summary>
    ///  默认老年代校验间隔，单位：次（对应 <see cref="HayatePoolOptions.OldGenerationValidationInterval"/>）。
    /// </summary>
    public const int DEFAULT_OLD_GEN_VALIDATION_INTERVAL = 3;

    #endregion

    #region 泄露检测

    /// <summary>
    /// 默认泄漏检测阈值常量，单位：秒（用于 <see cref="HayatePoolOptions.LeakDetectionThreshold"/>）。
    /// </summary>
    public const int DEFAULT_LEAK_DETECTION_THRESHOLD_SECONDS = 30;

    /// <summary>
    /// 默认泄漏取证采样分母（用于 <see cref="HayatePoolOptions.LeakTraceSampleRate"/>，仅 Sampled 模式生效）。
    /// </summary>
    public const int DEFAULT_LEAK_TRACE_SAMPLE_RATE = 1024;

    #endregion
}