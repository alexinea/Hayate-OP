using System;

namespace DotNetCore.HayateOP.Buffers;

/// <summary>
/// Declares one bucket of a <see cref="HayateBufferPool{T}"/>: every array the bucket parks has
/// exactly <see cref="ArraySize"/> elements, and the bucket keeps at most <see cref="Capacity"/> of
/// them after they are returned.
/// </summary>
/// <remarks>
/// The definition is a plain declarative record — the pool itself validates, orders and owns the
/// buckets. Arrays are created on demand when a bucket runs dry, so the capacity bounds only the
/// <i>retained</i> set: a burst may have any number of buffers outstanding at once, and the buffers it
/// returns past the bucket's capacity are dropped for the garbage collector instead of parked.
/// </remarks>
/// <example>
/// <code>
/// var pool = new HayateBufferPool&lt;byte&gt;(
///     new HayateSegmentDefinition(700),
///     new HayateSegmentDefinition(1400, 2),
///     new HayateSegmentDefinition(2000, 2));
/// </code>
/// </example>
public sealed class HayateSegmentDefinition
{
    /// <summary>
    /// Creates the definition of one bucket: fixed-size arrays of <paramref name="arraySize"/> elements,
    /// up to <paramref name="capacity"/> of them retained after return.
    /// </summary>
    /// <param name="arraySize">The exact element count of every array this bucket holds; it is also the
    /// largest request size the bucket serves (requests above it route to a bigger bucket).</param>
    /// <param name="capacity">How many returned arrays the bucket keeps before it starts dropping them.
    /// Defaults to 1 — the conservative choice, because a buffer's retained memory is what a bucketed
    /// pool is asked to bound.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="arraySize"/> is not greater than
    /// zero, or <paramref name="capacity"/> is not greater than zero.</exception>
    public HayateSegmentDefinition(long arraySize, int capacity = 1)
    {
        if (arraySize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(arraySize), arraySize,
                "The array size must be greater than zero.");
        }

        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity,
                "The bucket capacity must be greater than zero.");
        }

        ArraySize = arraySize;
        Capacity = capacity;
    }

    /// <summary>The exact element count of every array this bucket holds.</summary>
    public long ArraySize { get; }

    /// <summary>How many returned arrays the bucket retains before it drops the excess.</summary>
    public int Capacity { get; }

    /// <inheritdoc />
    public override string ToString() => $"{ArraySize}x{Capacity}";
}
