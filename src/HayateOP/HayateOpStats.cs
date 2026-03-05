namespace DotNetCore.HayateOP;

/// <summary>
/// 对象池统计信息
/// Statistics information for object pool
/// </summary>
public class HayateOpStats
{
    /// <summary>
    /// 当前池中可用的对象数量
    /// Current number of available objects in the pool
    /// </summary>
    public int PooledCount { get; set; }
    
    /// <summary>
    /// 累计创建的对象总数
    /// Total number of objects created since pool initialization
    /// </summary>
    public long TotalCreated { get; set; }
    
    /// <summary>
    /// 累计归还到池中的对象总数
    /// Total number of objects returned to the pool
    /// </summary>
    public long TotalReturned { get; set; }
    
    /// <summary>
    /// 累计未命中次数（需要创建新对象的次数）
    /// Total number of cache misses (times when new objects had to be created)
    /// </summary>
    public long TotalMissed { get; set; }
    
    /// <summary>
    /// 可用槽位数量
    /// Number of available slots in the pool
    /// </summary>
    public int AvailableSlots { get; set; }
    
    /// <summary>
    /// 对象池的最小容量
    /// Minimum capacity of the object pool
    /// </summary>
    public int MinSize { get; set; }
    
    /// <summary>
    /// 对象池的当前容量
    /// Current capacity of the object pool
    /// </summary>
    public int CurrentSize { get; set; }
    
    /// <summary>
    /// 检测到的对象泄漏次数
    /// Number of detected object leaks
    /// </summary>
    public long LeakDetectedCount { get; set; }
    
    /// <summary>
    /// 平均等待时间（毫秒）
    /// Average wait time in milliseconds
    /// </summary>
    public double AverageWaitTimeMs { get; set; }
    
    /// <summary>
    /// 平均租用时间（毫秒）
    /// Average lease time in milliseconds
    /// </summary>
    public double AverageLeaseTimeMs { get; set; }
    
    /// <summary>
    /// 最大等待时间（毫秒）
    /// Maximum wait time in milliseconds
    /// </summary>
    public double MaxWaitTimeMs { get; set; }
    
    /// <summary>
    /// 最大租用时间（毫秒）
    /// Maximum lease time in milliseconds
    /// </summary>
    public double MaxLeaseTimeMs { get; set; }
    
    /// <summary>
    /// 最小等待时间（毫秒）
    /// Minimum wait time in milliseconds
    /// </summary>
    public double MinWaitTimeMs { get; set; }
    
    /// <summary>
    /// 最小租用时间（毫秒）
    /// Minimum lease time in milliseconds
    /// </summary>
    public double MinLeaseTimeMs { get; set; }
    
    /// <summary>
    /// 等待时间记录次数
    /// Number of wait time records
    /// </summary>
    public long WaitTimeCount { get; set; }
    
    /// <summary>
    /// 租用时间记录次数
    /// Number of lease time records
    /// </summary>
    public long LeaseTimeCount { get; set; }
}