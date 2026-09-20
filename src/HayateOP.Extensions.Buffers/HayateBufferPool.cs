using System;
using System.Collections.Generic;
using System.Threading;

namespace DotNetCore.HayateOP.Buffers;

/// <summary>
/// A bucketed array pool: a fixed ladder of size buckets, where each rent request routes — rounded up
/// to the nearest bucket — to the smallest bucket whose arrays can hold it, and a returned array parks
/// in its home bucket until the bucket's capacity is full.
/// </summary>
/// <remarks>
/// Scope note: this is the one piece of surface the core pool deliberately does not carry, because for
/// most .NET workloads <c>System.Buffers.ArrayPool&lt;T&gt;</c> already covers it. This pool exists for
/// callers who want that shape under an explicit, inspectable configuration — the bucket ladder is
/// declared up front, every bucket's retained count is fixed by declaration, and nothing sizes or
/// trims buckets behind the caller's back. It is the aligned counterpart of TinyPools'
/// <c>MemoryPool&lt;T&gt;</c> + <c>SegmentDefinition</c>, under HayateOP naming and validation
/// conventions.<br />
/// Routing is "round up to the nearest declared size": a request is served by the first bucket whose
/// <see cref="HayateSegmentDefinition.ArraySize"/> covers it, so the caller receives an array at least
/// as long as requested and never has to grow. A request above the largest declared size is rejected
/// with <see cref="ArgumentException"/> rather than served by an undeclared bucket — the ladder is
/// exactly what was configured, nothing more.<br />
/// Buckets create arrays on demand and cap only what they <i>retain</i>: any number of buffers may be
/// outstanding at once, and returns past a bucket's <see cref="HayateSegmentDefinition.Capacity"/> are
/// dropped for the garbage collector. There is no background worker of any kind — an idle pool holds
/// its declared buckets and runs nothing.<br />
/// The pool is thread-safe: each bucket guards its parking stack with its own lock, so different
/// bucket sizes do not contend.
/// </remarks>
/// <example>
/// <code>
/// using var pool = new HayateBufferPool&lt;byte&gt;(
///     new HayateSegmentDefinition(256, 4),
///     new HayateSegmentDefinition(1024, 2),
///     new HayateSegmentDefinition(4096, 1));
///
/// using (PooledBuffer&lt;byte&gt; buffer = pool.Rent(300))   // routes to the 1024 bucket
/// {
///     // buffer.Array.Length == 1024 — round up, never grow
/// }
/// </code>
/// </example>
/// <typeparam name="T">The element type of the pooled arrays.</typeparam>
public sealed class HayateBufferPool<T> : IDisposable
{
    /// <summary>The buckets in ascending <see cref="HayateSegmentDefinition.ArraySize"/> order.</summary>
    private readonly Bucket[] _buckets;

    private int _disposed;

    /// <summary>
    /// Creates the pool from one required bucket plus any number of additional ones. The buckets are
    /// stored in ascending size order whatever order the arguments arrive in; the routing then always
    /// picks the smallest bucket that covers a request.
    /// </summary>
    /// <param name="segment">The first bucket. Required, so a pool with no buckets cannot be built.</param>
    /// <param name="otherSegments">Further buckets, in any order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="segment"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Two buckets declare the same
    /// <see cref="HayateSegmentDefinition.ArraySize"/> — the duplicate could never win a route and would
    /// silently split one size's retention across two buckets.</exception>
    public HayateBufferPool(HayateSegmentDefinition segment, params HayateSegmentDefinition[]? otherSegments)
    {
        if (segment is null)
        {
            throw new ArgumentNullException(nameof(segment));
        }

        var definitions = new List<HayateSegmentDefinition>(1 + (otherSegments?.Length ?? 0)) { segment };
        if (otherSegments is not null)
        {
            foreach (var other in otherSegments)
            {
                if (other is null)
                {
                    throw new ArgumentNullException(nameof(otherSegments),
                        "A segment definition inside the list is null.");
                }

                definitions.Add(other);
            }
        }

        // Ascending by size; a stable sort keeps it readable for equal sizes, which are rejected below.
        definitions.Sort((a, b) => a.ArraySize.CompareTo(b.ArraySize));

        _buckets = new Bucket[definitions.Count];
        for (var i = 0; i < definitions.Count; i++)
        {
            if (i > 0 && definitions[i].ArraySize == definitions[i - 1].ArraySize)
            {
                throw new ArgumentException(
                    $"Duplicate segment definition: two buckets declare array size {definitions[i].ArraySize}.",
                    nameof(otherSegments));
            }

            _buckets[i] = new Bucket(this, definitions[i]);
        }
    }

    /// <summary>
    /// The buckets the pool was built from, in ascending size order — the declared ladder, exposed so
    /// diagnostics and tests can see the configuration without reaching into internals.
    /// </summary>
    public IReadOnlyList<HayateSegmentDefinition> Segments
    {
        get
        {
            var result = new HayateSegmentDefinition[_buckets.Length];
            for (var i = 0; i < _buckets.Length; i++)
            {
                result[i] = _buckets[i].Definition;
            }

            return result;
        }
    }

    /// <summary>The largest declared bucket size. Requests above it are rejected, not served undeclared.</summary>
    public long MaxArraySize => _buckets[_buckets.Length - 1].Definition.ArraySize;

    /// <summary>
    /// Rents an array of at least <paramref name="size"/> elements, routing to the first bucket whose
    /// fixed size covers the request.
    /// </summary>
    /// <param name="size">The minimum number of elements the caller needs.</param>
    /// <returns>A handle whose <see cref="PooledBuffer{T}.Array"/> is exactly the bucket's size — at
    /// least <paramref name="size"/>, never grown afterwards. Return it with
    /// <see cref="PooledBuffer{T}.Dispose"/> (or <see cref="Return"/>).</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="size"/> is not greater than zero.</exception>
    /// <exception cref="ArgumentException"><paramref name="size"/> is greater than
    /// <see cref="MaxArraySize"/> — no declared bucket can serve it.</exception>
    /// <exception cref="ObjectDisposedException">The pool has been disposed.</exception>
    public PooledBuffer<T> Rent(long size)
    {
        if (size <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size,
                "The requested size must be greater than zero.");
        }

        var bucket = Route(size);
        if (bucket is null)
        {
            throw new ArgumentException(
                $"The requested size {size} exceeds the largest declared bucket ({MaxArraySize}). " +
                "Declare a bigger bucket or use System.Buffers.ArrayPool<T> for unbounded sizes.",
                nameof(size));
        }

        return new PooledBuffer<T>(bucket, bucket.Rent());
    }

    /// <summary>
    /// Returns a buffer previously rented from this pool. Synonym of disposing the handle; the two are
    /// interchangeable, and whichever comes second is a no-op.
    /// </summary>
    /// <param name="buffer">The buffer to return. A <see langword="null"/> argument is ignored, so a
    /// caller with a nullable buffer in a <c>finally</c> block needs no guard.</param>
    /// <exception cref="ArgumentException"><paramref name="buffer"/> was rented from a different
    /// pool — returning it here would park it in a bucket whose size it does not have.</exception>
    public void Return(PooledBuffer<T>? buffer)
    {
        if (buffer is null)
        {
            return;
        }

        if (!buffer.Bucket.OwnerIs(this))
        {
            throw new ArgumentException("The buffer was not rented from this pool.", nameof(buffer));
        }

        buffer.Dispose();
    }

    /// <summary>
    /// Empties every bucket, dropping all parked arrays. Outstanding buffers keep working: a buffer
    /// returned after a clear parks again (or drops, per the bucket's capacity), it is not invalidated.
    /// </summary>
    public void Clear()
    {
        foreach (var bucket in _buckets)
        {
            bucket.Clear();
        }
    }

    /// <summary>
    /// Disposes the pool and drops every parked array. Further rents fail with
    /// <see cref="ObjectDisposedException"/>; outstanding buffers still return, but their arrays are
    /// dropped rather than parked, because nothing will rent them again.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var bucket in _buckets)
        {
            bucket.Clear();
            bucket.Dispose();
        }
    }

    /// <summary>Finds the first bucket whose size covers <paramref name="size"/>, if any.</summary>
    private Bucket? Route(long size)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(HayateBufferPool<T>));
        }

        foreach (var bucket in _buckets)
        {
            if (bucket.Definition.ArraySize >= size)
            {
                return bucket;
            }
        }

        return null;
    }

    /// <summary>
    /// One declared size's storage: a locked stack of parked arrays, created on demand when it runs
    /// dry, capped at the definition's capacity on the return side.
    /// </summary>
    internal sealed class Bucket
    {
        private readonly object _lock = new();
        private readonly T[]?[] _parked;
        private readonly HayateBufferPool<T> _owner;
        private int _count;
        private int _disposed;

        public Bucket(HayateBufferPool<T> owner, HayateSegmentDefinition definition)
        {
            _owner = owner;
            Definition = definition;
            _parked = new T[definition.Capacity][];
        }

        public HayateSegmentDefinition Definition { get; }

        /// <summary>Takes an array from the stack, or creates a fresh one when the bucket is empty.</summary>
        public T[] Rent()
        {
            lock (_lock)
            {
                if (_count > 0)
                {
                    var item = _parked[--_count]!;
                    _parked[_count] = null;
                    return item;
                }
            }

            return new T[Definition.ArraySize];
        }

        /// <summary>
        /// Parks a returned array, or drops it when the bucket is full (or disposed) — the capacity
        /// constraint lives here and nowhere else.
        /// </summary>
        public void Return(T[] array)
        {
            lock (_lock)
            {
                if (Volatile.Read(ref _disposed) == 0 && _count < _parked.Length)
                {
                    _parked[_count++] = array;
                }
            }

            // Nothing to do on drop: the caller's handle is already spent, and the array goes to the
            // garbage collector. A branch here would only serve logging the pool does not have.
        }

        public void Clear()
        {
            lock (_lock)
            {
                Array.Clear(_parked, 0, _count);
                _count = 0;
            }
        }

        public void Dispose()
        {
            Volatile.Write(ref _disposed, 1);
        }

        public bool OwnerIs(HayateBufferPool<T> owner) => ReferenceEquals(_owner, owner);
    }
}
