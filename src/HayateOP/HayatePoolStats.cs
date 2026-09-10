using System;

namespace DotNetCore.HayateOP;

/// <summary>
/// 对象池统计信息
/// Statistics information for object pool
/// </summary>
public class HayatePoolStats
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
    public long TotalReleased { get; set; }

    /// <summary>
    /// 累计未命中次数（需要创建新对象的次数）
    /// Total number of cache misses (times when new objects had to be created)
    /// </summary>
    public long TotalMissed { get; set; }
    
    /// <summary>
    /// 累计被租用的对象总数
    /// </summary>
    public long TotalAcquired { get; set; }

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
    /// 疑似泄漏次数（M4，泄漏检测关闭时的回查告警计数）。<br />
    /// EnableLeakDetection=false 时，TakeSnapshot 按同一 LeakDetectionThreshold 统计
    /// 「借出超阈值未归还」的对象次数；仅计数、不取证、不回收。
    /// </summary>
    public long LeakSuspectedCount { get; set; }

    /// <summary>
    /// 是否启用分配追踪（M3）。
    /// </summary>
    public bool AllocationTrackingEnabled { get; set; }

    /// <summary>
    /// 借出路径累计分配字节数（M3，仅同步 <c>Acquire</c>；分配追踪关闭时为 0）。
    /// </summary>
    public long AcquireAllocatedBytes { get; set; }

    /// <summary>
    /// 归还路径累计分配字节数（M3，仅同步 <c>Release</c>；分配追踪关闭时为 0）。
    /// </summary>
    public long ReleaseAllocatedBytes { get; set; }

    /// <summary>
    /// 借出分配采样次数（M3）。
    /// </summary>
    public long AcquireAllocationSamples { get; set; }

    /// <summary>
    /// 归还分配采样次数（M3）。
    /// </summary>
    public long ReleaseAllocationSamples { get; set; }

    /// <summary>
    /// 借出路径平均分配字节数（M3）。
    /// </summary>
    public double AverageAcquireAllocatedBytes
        => AcquireAllocationSamples > 0 ? (double)AcquireAllocatedBytes / AcquireAllocationSamples : 0;

    /// <summary>
    /// 归还路径平均分配字节数（M3）。
    /// </summary>
    public double AverageReleaseAllocatedBytes
        => ReleaseAllocationSamples > 0 ? (double)ReleaseAllocatedBytes / ReleaseAllocationSamples : 0;

    /// <summary>
    /// 平均等待时间（毫秒）
    /// Average wait time in milliseconds
    /// </summary>
    public double AverageWaitTimeMs => WaitTimeCount > 0 ? (double)WaitTimeSum / WaitTimeCount : 0;

    /// <summary>
    /// 平均租用时间（毫秒）
    /// Average lease time in milliseconds
    /// </summary>
    public double AverageLeaseTimeMs => LeaseTimeCount > 0 ? (double)LeaseTimeSum / LeaseTimeCount : 0;

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
    public double MinWaitTimeMs { get; set; } = double.MaxValue;

    /// <summary>
    /// 最小租用时间（毫秒）
    /// Minimum lease time in milliseconds
    /// </summary>
    public double MinLeaseTimeMs { get; set; } = double.MaxValue;

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

    internal long WaitTimeSum { get; set; }
    internal long LeaseTimeSum { get; set; }

    public override string ToString()
    {
        // 处理最小时间的特殊值（初始为double.MaxValue时显示0）
        var actualMinWaitTime = MinWaitTimeMs == double.MaxValue ? 0 : MinWaitTimeMs;
        var actualMinLeaseTime = MinLeaseTimeMs == double.MaxValue ? 0 : MinLeaseTimeMs;

        // 构建结构化字符串，按类别分组，便于阅读
        var statsString = $@"
=== Hayate Object Pool Statistics ===
[基础容量信息]
  最小容量(MinSize): {MinSize}
  当前容量(CurrentSize): {CurrentSize}
  可用对象数(PooledCount): {PooledCount}
  可用槽位数(AvailableSlots): {AvailableSlots}
[对象生命周期统计]
  累计创建总数(TotalCreated): {TotalCreated}
  累计归还总数(TotalReleased): {TotalReleased}
  累计未命中次数(TotalMissed): {TotalMissed}
  检测到的泄漏次数(LeakDetectedCount): {LeakDetectedCount}
  疑似泄漏次数(LeakSuspectedCount): {LeakSuspectedCount}
  分配追踪(AllocationTrackingEnabled): {AllocationTrackingEnabled}
  借出平均分配字节(AverageAcquireAllocatedBytes): {AverageAcquireAllocatedBytes:F1}
  归还平均分配字节(AverageReleaseAllocatedBytes): {AverageReleaseAllocatedBytes:F1}
  借出分配样本数(AcquireAllocationSamples): {AcquireAllocationSamples}
  归还分配样本数(ReleaseAllocationSamples): {ReleaseAllocationSamples}
[等待时间统计(毫秒)]
  平均等待时间(AverageWaitTime): {AverageWaitTimeMs:F2}
  最大等待时间(MaxWaitTime): {MaxWaitTimeMs:F2}
  最小等待时间(MinWaitTime): {actualMinWaitTime:F2}
  等待时间记录次数(WaitTimeCount): {WaitTimeCount}
[租用时间统计(毫秒)]
  平均租用时间(AverageLeaseTime): {AverageLeaseTimeMs:F2}
  最大租用时间(MaxLeaseTime): {MaxLeaseTimeMs:F2}
  最小租用时间(MinLeaseTime): {actualMinLeaseTime:F2}
  租用时间记录次数(LeaseTimeCount): {LeaseTimeCount}
======================================";

        // 替换换行符为系统原生换行符（兼容Windows/Linux）
        return statsString.Replace("\n", Environment.NewLine);
    }
}