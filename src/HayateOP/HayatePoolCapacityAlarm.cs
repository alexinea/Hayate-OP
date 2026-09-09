namespace DotNetCore.HayateOP;

/// <summary>
/// 池容量告警级别（M12）。
/// </summary>
/// <remarks>
/// 语义：按「借出对象数 / MaxPoolSize」的使用率与 <c>WarnAtRatio</c> / <c>CriticalAtRatio</c>
/// 比较得出；仅在状态翻转时触发一次回调（回落静默复位、重新整备）。
/// </remarks>
public enum HayatePoolCapacityAlarmLevel
{
    /// <summary>正常水位（低于告警阈值）。</summary>
    Normal = 0,

    /// <summary>警告水位（使用率 ≥ WarnAtRatio 且低于 CriticalAtRatio）。</summary>
    Warning = 1,

    /// <summary>危急水位（使用率 ≥ CriticalAtRatio）。</summary>
    Critical = 2
}

/// <summary>
/// 池容量告警事件参数（M12）。
/// </summary>
public sealed class HayatePoolCapacityAlarmEventArgs
{
    /// <summary>触发告警的池名称。</summary>
    public string PoolName { get; }

    /// <summary>告警级别（Warning / Critical；Normal 不触发回调）。</summary>
    public HayatePoolCapacityAlarmLevel Level { get; }

    /// <summary>触发时刻的使用率（借出数 / MaxPoolSize，0~1）。</summary>
    public double UsageRatio { get; }

    /// <summary>触发时刻的借出对象数。</summary>
    public int BorrowedCount { get; }

    /// <summary>池容量上限（MaxPoolSize）。</summary>
    public int MaxPoolSize { get; }

    public HayatePoolCapacityAlarmEventArgs(
        string poolName,
        HayatePoolCapacityAlarmLevel level,
        double usageRatio,
        int borrowedCount,
        int maxPoolSize)
    {
        PoolName = poolName;
        Level = level;
        UsageRatio = usageRatio;
        BorrowedCount = borrowedCount;
        MaxPoolSize = maxPoolSize;
    }

    public override string ToString()
    {
        return $"[{PoolName}] CapacityAlarm {Level}: usage {UsageRatio:P1} ({BorrowedCount}/{MaxPoolSize})";
    }
}
