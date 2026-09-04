using System;
using System.Diagnostics;

namespace DotNetCore.HayateOP.Common;

/// <summary>
/// 高性能的值类型秒表，用于精确测量时间间隔。
/// 相比 <see cref="Stopwatch"/> 类，<see cref="ValueStopwatch"/> 为值类型，
/// 避免了堆分配，适合在高性能场景中频繁创建和使用。
/// </summary>
/// <remarks>
/// 该结构体不支持暂停和继续操作，只能用于测量从创建到现在的经过时间。
/// 由于是值类型，应避免频繁的装箱操作。
/// </remarks>
/// <example>
/// <code>
/// // 基本用法：测量某个操作的执行时间
/// var sw = ValueStopwatch.StartNew();
/// Thread.Sleep(100);
/// var elapsed = sw.Elapsed;
/// Console.WriteLine($"执行时间: {elapsed.TotalMilliseconds} ms");
/// 
/// // 在性能关键路径中使用
/// var stopwatch = ValueStopwatch.StartNew();
/// PerformanceTestOperation();
/// if (stopwatch.Elapsed.TotalMilliseconds > 1000)
/// {
///     Console.WriteLine("操作超时");
/// }
/// </code>
/// </example>
internal readonly struct ValueStopwatch
{
    /// <summary>
    /// 秒表启动时的时间戳。
    /// </summary>
    private readonly long _start;

    /// <summary>
    /// 创建并启动一个新的 <see cref="ValueStopwatch"/> 实例。
    /// </summary>
    /// <returns>已启动的 <see cref="ValueStopwatch"/> 实例。</returns>
    public static ValueStopwatch StartNew() => new(Stopwatch.GetTimestamp());

    /// <summary>
    /// 使用指定的启动时间戳初始化 <see cref="ValueStopwatch"/> 实例。
    /// </summary>
    /// <param name="start">启动时的时间戳。</param>
    private ValueStopwatch(long start) => _start = start;

    /// <summary>
    /// 获取从秒表启动到现在的经过时间。
    /// </summary>
    /// <value>从启动到现在所经过的时间跨度。</value>
    public TimeSpan Elapsed => StopwatchUtil.GetElapsedTime(_start, Stopwatch.GetTimestamp());
}

file static class StopwatchUtil
{

#if NET7_0_OR_GREATER

    public static TimeSpan GetElapsedTime(long startingTimestamp)
    {
        return GetElapsedTime(startingTimestamp, Stopwatch.GetTimestamp());
    }

    public static TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp)
    {
        return Stopwatch.GetElapsedTime(startingTimestamp, endingTimestamp);
    }

#else

    public static TimeSpan GetElapsedTime(long startingTimestamp)
    {
        return GetElapsedTime(startingTimestamp, Stopwatch.GetTimestamp());
    }

    public static TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp)
    {
        // 防止时间戳回绕（虽然 Stopwatch 通常是递增的，但做健壮性处理）
        long timestampDelta = endingTimestamp - startingTimestamp;
        if (timestampDelta < 0)
        {
            timestampDelta = 0;
        }

        // 核心算法：将 Stopwatch 滴答数转换为 TimeSpan
        // 公式来源：Stopwatch 类的内部实现逻辑
        long ticks = timestampDelta * TimeSpan.TicksPerSecond / Stopwatch.Frequency;
        return new TimeSpan(ticks);
    }

#endif

}