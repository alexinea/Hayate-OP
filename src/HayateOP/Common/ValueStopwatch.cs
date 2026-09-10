using System;
using System.Diagnostics;

namespace DotNetCore.HayateOP.Common;

/// <summary>
/// High-performance value-type stopwatch for accurately measuring elapsed time intervals.
/// Unlike the <see cref="Stopwatch"/> class, <see cref="ValueStopwatch"/> is a value type,
/// so it avoids heap allocation and is suitable for frequent creation and use in high-performance scenarios.
/// </summary>
/// <remarks>
/// This struct does not support pause or resume; it only measures the time elapsed from creation to the current moment.
/// Being a value type, frequent boxing should be avoided.
/// </remarks>
/// <example>
/// <code>
/// // Basic usage: measure the execution time of an operation.
/// var sw = ValueStopwatch.StartNew();
/// Thread.Sleep(100);
/// var elapsed = sw.Elapsed;
/// Console.WriteLine($"Elapsed: {elapsed.TotalMilliseconds} ms");
/// 
/// // Use on performance-critical paths.
/// var stopwatch = ValueStopwatch.StartNew();
/// PerformanceTestOperation();
/// if (stopwatch.Elapsed.TotalMilliseconds > 1000)
/// {
///     Console.WriteLine("Operation timed out");
/// }
/// </code>
/// </example>
internal readonly struct ValueStopwatch
{
    /// <summary>
    /// The timestamp captured when the stopwatch started.
    /// </summary>
    private readonly long _start;

    /// <summary>
    /// Creates and starts a new <see cref="ValueStopwatch"/> instance.
    /// </summary>
    /// <returns>The started <see cref="ValueStopwatch"/> instance.</returns>
    public static ValueStopwatch StartNew() => new(Stopwatch.GetTimestamp());

    /// <summary>
    /// Initializes a <see cref="ValueStopwatch"/> instance with the specified start timestamp.
    /// </summary>
    /// <param name="start">The start timestamp.</param>
    private ValueStopwatch(long start) => _start = start;

    /// <summary>
    /// Gets the time elapsed from when the stopwatch started to now.
    /// </summary>
    /// <value>The time span elapsed from start to now.</value>
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
        // Guard against timestamp wrap-around (Stopwatch normally increments monotonically, but handle it defensively).
        long timestampDelta = endingTimestamp - startingTimestamp;
        if (timestampDelta < 0)
        {
            timestampDelta = 0;
        }

        // Core algorithm: convert Stopwatch ticks into a TimeSpan.
        // Formula derived from Stopwatch's internal implementation.
        long ticks = timestampDelta * TimeSpan.TicksPerSecond / Stopwatch.Frequency;
        return new TimeSpan(ticks);
    }

#endif

}