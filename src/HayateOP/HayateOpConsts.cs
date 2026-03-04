namespace DotNetCore.HayateOP;

public static class HayateOpConsts
{
    /// <summary>
    /// 默认的最大并发数
    /// </summary>
    public const int DEFAULT_MAX_CONCURRENT = 20;
    
    /// <summary>
    /// 默认的最小容量
    /// </summary>
    public const int DEFAULT_MIN_POOL_SIZE = 5;
    
    /// <summary>
    /// 默认的最大容量
    /// </summary>
    public const int DEFAULT_MAX_POOL_SIZE = 50;
    
    /// <summary>
    /// 默认伸缩检查间隔
    /// </summary>
    public const int DEFAULT_SCALING_INTERVAL_MILLISECONDS = 5000;
    
    /// <summary>
    /// 默认的伸缩阈值，分别为扩容和缩容的阈值，范围为0-1，表示当前使用率达到多少时进行扩容或缩容
    /// </summary>
    public const double DEFAULT_SCALE_UP_THRESHOLD = 0.8;
    
    /// <summary>
    /// 默认的缩容阈值，分别为扩容和缩容的阈值，范围为0-1，表示当前使用率达到多少时进行扩容或缩容
    /// </summary>
    public const double DEFAULT_SCALE_DOWN_THRESHOLD = 0.2;
}