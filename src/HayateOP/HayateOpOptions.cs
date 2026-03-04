namespace DotNetCore.HayateOP;

/// <summary>
/// 选项配置
/// </summary>
public class HayateOpOptions
{
    /// <summary>
    /// 最大并发
    /// </summary>
    public int MaxConcurrent { get; set; } = HayateOpConsts.DEFAULT_MAX_CONCURRENT;

    /// <summary>
    /// 最小容量
    /// </summary>
    public int MinPoolSize { get; set; } = HayateOpConsts.DEFAULT_MIN_POOL_SIZE;
    
    /// <summary>
    /// 最大容量
    /// </summary>
    public int MaxPoolSize { get; set; } = HayateOpConsts.DEFAULT_MAX_POOL_SIZE;

    /// <summary>
    /// 是否开启指标收集
    /// </summary>
    public bool EnableMetrics { get; set; } = false;
    
    /// <summary>
    /// 伸缩检查间隔，单位为毫秒
    /// </summary>
    public int ScalingIntervalMilliseconds { get; set; } = HayateOpConsts.DEFAULT_SCALING_INTERVAL_MILLISECONDS;
    
    /// <summary>
    /// 扩容阈值，范围为0-1，表示当前使用率达到多少时进行扩容
    /// </summary>
    public double ScaleUpThreshold { get; set; } = HayateOpConsts.DEFAULT_SCALE_UP_THRESHOLD;
    
    /// <summary>
    /// 缩容阈值，范围为0-1，表示当前使用率达到多少时进行缩容
    /// </summary>
    public double ScaleDownThreshold { get; set; } = HayateOpConsts.DEFAULT_SCALE_DOWN_THRESHOLD;
}