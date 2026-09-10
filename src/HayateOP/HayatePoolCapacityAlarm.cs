namespace DotNetCore.HayateOP;

/// <summary>
/// Pool capacity alarm level.
/// </summary>
/// <remarks>
/// Semantics: derived from the usage ratio (borrowed objects / MaxPoolSize) compared against <c>WarnAtRatio</c> / <c>CriticalAtRatio</c>;
/// a callback fires only once on a state transition (it silently resets and re-arms when the ratio falls back).
/// </remarks>
public enum HayatePoolCapacityAlarmLevel
{
    /// <summary>Normal water level (below the warning threshold).</summary>
    Normal = 0,

    /// <summary>Warning water level (usage ratio &gt;= WarnAtRatio and below CriticalAtRatio).</summary>
    Warning = 1,

    /// <summary>Critical water level (usage ratio &gt;= CriticalAtRatio).</summary>
    Critical = 2
}

/// <summary>
/// Pool capacity alarm event arguments.
/// </summary>
public sealed class HayatePoolCapacityAlarmEventArgs
{
    /// <summary>The name of the pool that raised the alarm.</summary>
    public string PoolName { get; }

    /// <summary>The alarm level (Warning / Critical; Normal does not raise a callback).</summary>
    public HayatePoolCapacityAlarmLevel Level { get; }

    /// <summary>The usage ratio at trigger time (borrowed count / MaxPoolSize, 0~1).</summary>
    public double UsageRatio { get; }

    /// <summary>The number of borrowed objects at trigger time.</summary>
    public int BorrowedCount { get; }

    /// <summary>The pool capacity upper bound (MaxPoolSize).</summary>
    public int MaxPoolSize { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="HayatePoolCapacityAlarmEventArgs"/> class.
    /// </summary>
    /// <param name="poolName">The name of the pool that raised the alarm.</param>
    /// <param name="level">The alarm level (Warning or Critical; Normal never raises a callback).</param>
    /// <param name="usageRatio">The usage ratio at trigger time (borrowed count / MaxPoolSize, 0~1).</param>
    /// <param name="borrowedCount">The number of borrowed objects at trigger time.</param>
    /// <param name="maxPoolSize">The pool capacity upper bound (MaxPoolSize).</param>
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

    /// <summary>
    /// Returns a human-readable description of the capacity alarm, including the level and usage ratio.
    /// </summary>
    /// <returns>A formatted alarm string.</returns>
    public override string ToString()
    {
        return $"[{PoolName}] CapacityAlarm {Level}: usage {UsageRatio:P1} ({BorrowedCount}/{MaxPoolSize})";
    }
}
