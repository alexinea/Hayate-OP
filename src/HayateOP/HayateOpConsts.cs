// ReSharper disable InconsistentNaming
namespace DotNetCore.HayateOP;

/// <summary>
/// Hayate 对象池的默认常量定义。
/// 这些常量用于 <see cref="HayateOpOptions"/> 的默认配置值。
/// </summary>
public static class HayateOpConsts
{
    /// <summary>
    /// 默认最大并发借用数（对应 <see cref="HayateOpOptions.MaxConcurrent"/>）。
    /// </summary>
    public const int DEFAULT_MAX_CONCURRENT = 20;

    /// <summary>
    /// 默认最小池容量（对应 <see cref="HayateOpOptions.MinPoolSize"/>）。
    /// </summary>
    public const int DEFAULT_MIN_POOL_SIZE = 5;

    /// <summary>
    /// 默认最大池容量（对应 <see cref="HayateOpOptions.MaxPoolSize"/>）。
    /// </summary>
    public const int DEFAULT_MAX_POOL_SIZE = 50;

    /// <summary>
    /// 默认伸缩检查间隔，单位：毫秒（对应 <see cref="HayateOpOptions.ScalingIntervalMs"/>）。
    /// </summary>
    public const int DEFAULT_SCALING_INTERVAL_MILLISECONDS = 5000;

    /// <summary>
    /// 默认扩容阈值，范围 0~1（对应 <see cref="HayateOpOptions.ScaleUpThreshold"/>）。
    /// </summary>
    public const double DEFAULT_SCALE_UP_THRESHOLD = 0.8;

    /// <summary>
    /// 默认缩容阈值，范围 0~1（对应 <see cref="HayateOpOptions.ScaleDownThreshold"/>）。
    /// </summary>
    public const double DEFAULT_SCALE_DOWN_THRESHOLD = 0.2;

    /*
     * Validate
     */

    /// <summary>
    /// 默认校验扫描间隔，单位：毫秒（对应 <see cref="HayateOpOptions.ValidateIntervalMs"/>）。
    /// </summary>
    public const int DEFAULT_VALIDATE_INTERVAL_MILLISECONDS = 30000;

    /*
     * Eviction
     */

    /// <summary>
    /// 默认对象最大存活时间，单位：分钟（对应 <see cref="HayateOpOptions.MaxLifeTime"/>）。
    /// </summary>
    public const int DEFAULT_MAX_LIFE_TIME_MINUTES = 10;

    /// <summary>
    /// 默认对象最大空闲时间，单位：分钟（对应 <see cref="HayateOpOptions.MaxIdleTime"/>）。
    /// </summary>
    public const int DEFAULT_MAX_IDLE_TIME_MINUTES = 5;
    
    /// <summary>
    /// 默认软最小可驱逐空闲时间，单位：分钟（对应 <see cref="HayateOpOptions.SoftMinEvictableIdleTime"/>）。
    /// </summary>
    public const int DEFAULT_MIN_EVICTION_IDLE_TIME_MINUTES = 2;

    /// <summary>
    /// 默认驱逐检查间隔，单位：毫秒（对应 <see cref="HayateOpOptions.EvictionIntervalMs"/>）。
    /// </summary>
    public const int DEFAULT_EVICTION_INTERVAL_MILLISECONDS = 30000;

    /// <summary>
    /// 默认每次驱逐检查的样本数量（对应 <see cref="HayateOpOptions.NumTestsPerEvictionRun"/>）。
    /// </summary>
    public const int DEFAULT_EVICTION_RUNS_PER_EVICTION = 10;
    
    /*
     * Timeout
     */

    /// <summary>
    /// 默认获取超时时间，单位：秒（对应 <see cref="HayateOpOptions.DefaultGetTimeout"/>）。
    /// </summary>
    public const int DEFAULT_GET_TIMEOUT_SECONDS = 5;

    /*
     * Leak Detection
     */

    /// <summary>
    /// 默认泄漏检测阈值常量，单位：秒（用于 <see cref="HayateOpOptions.LeakDetectionThreshold"/>）。
    /// </summary>
    public const int DEFAULT_LEAK_DETECTION_THRESHOLD_SECONDS = 30;


    /*
     * Creation retry
     */

    /// <summary>
    /// 默认创建失败重试次数（对应 <see cref="HayateOpOptions.CreationRetryCount"/>）。
    /// </summary>
    public const int DEFAULT_CREATION_RETRY_COUNT = 3;

    /// <summary>
    /// 默认创建重试延迟，单位：毫秒（建议用于 <see cref="HayateOpOptions.CreationRetryDelay"/>）。
    /// </summary>
    public const int DEFAULT_CREATION_RETRY_DELAY_MILLISECONDS = 100;

    /*
     * Sharding
     */

    /// <summary>
    /// 默认分片数量（对应 <see cref="HayateOpOptions.ShardCount"/>）。
    /// </summary>
    public const int DEFAULT_SHARD_COUNT = 4;

    /*
     * Generational pool
     */

    /// <summary>
    /// 默认代际升级阈值，单位：毫秒（对应 <see cref="HayateOpOptions.GenerationThresholdMs"/>）。
    /// </summary>
    public const int DEFAULT_GEN_THRESHOLD_MILLISECONDS = 30000;
}