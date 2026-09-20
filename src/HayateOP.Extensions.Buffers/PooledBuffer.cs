using System;
using System.Threading;

namespace DotNetCore.HayateOP.Buffers;

/// <summary>
/// A buffer rented from a <see cref="HayateBufferPool{T}"/>: a <typeparamref name="T"/> array whose
/// length is the renting bucket's fixed size, handed back to its bucket by <see cref="Dispose"/>.
/// </summary>
/// <remarks>
/// The handle exists so the <c>using</c> pattern returns the buffer — the pool never takes a bare
/// array back, because it could not tell which bucket a caller-modified array belongs to. A handle is
/// single-use: disposing it twice, or returning it through the pool after disposing, is a no-op, so a
/// <c>using</c> block and a manual <c>Return</c> never fight over the same buffer.<br />
/// The array's previous contents are <b>not</b> cleared on return — the same contract
/// <c>ArrayPool&lt;T&gt;.Shared</c> has. Inspect only the elements you wrote, or clear the buffer
/// yourself if the next reader could otherwise observe stale data.
/// </remarks>
/// <typeparam name="T">The element type of the pooled array.</typeparam>
public sealed class PooledBuffer<T> : IDisposable
{
    private readonly HayateBufferPool<T>.Bucket _bucket;

    internal HayateBufferPool<T>.Bucket Bucket => _bucket;
    private int _returned;

    internal PooledBuffer(HayateBufferPool<T>.Bucket bucket, T[] array)
    {
        _bucket = bucket;
        Array = array;
    }

    /// <summary>The pooled array. Its length never changes while the handle is held.</summary>
    public T[] Array { get; }

    /// <summary>The number of elements in <see cref="Array"/> — the renting bucket's fixed size.</summary>
    public int Length => Array.Length;

    /// <summary>
    /// Returns the buffer to the bucket it came from. Idempotent: a second call (or a return through
    /// the pool afterwards) does nothing and throws nothing.
    /// </summary>
    /// <remarks>
    /// The bucket decides between parking and dropping: inside its capacity the array is parked for
    /// the next rent of the same size, past it the array is simply released to the garbage collector.
    /// </remarks>
    public void Dispose()
    {
        // The CAS makes double disposal harmless — "using" plus an explicit Return is a legitimate
        // caller shape, and the loser of the race must not park a second (aliased) copy.
        if (Interlocked.CompareExchange(ref _returned, 1, 0) != 0)
        {
            return;
        }

        _bucket.Return(Array);
    }
}
