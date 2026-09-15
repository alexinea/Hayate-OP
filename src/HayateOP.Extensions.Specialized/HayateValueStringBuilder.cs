using System;
using System.Buffers;
using System.Text;

namespace DotNetCore.HayateOP.Specialized;

/// <summary>
/// A zero-allocation, stack-only string builder in the ZString / Cosmos <c>ValueStringBuilder</c>
/// shape: a <c>ref struct</c> whose character buffer is rented from <see cref="ArrayPool{T}.Shared"/>,
/// doubled when it grows, and handed back the moment <see cref="ToString"/> materializes the result —
/// the one and only allocation of a build.
/// </summary>
/// <remarks>
/// This type complements <see cref="StringBuilderPool"/> rather than replacing it. The transient
/// "build → produce the string" flow (log lines, message formatting) runs here with no heap traffic
/// beyond the final string; long-lived builders that cross methods or need pool observability stay on
/// the pool. The builder never touches the HayateOP engine — <see cref="ArrayPool{T}.Shared"/> is its
/// pool — so it has no statistics, no validation and no capacity ceiling, just the raw fast path.<br />
/// Appends follow ZString's shape and return <c>void</c>: the builder is a mutable ref struct, so
/// chained calls like <c>sb.Append(a).Append(b)</c> would run on struct copies and lose their
/// position state — call the methods in sequence instead.<br />
/// <b>Lifecycle.</b> Rent with <c>new</c> (or <c>using var</c>, which disposes at the end of the
/// block), append, and call <see cref="ToString"/>; the buffer returns to the pool right after the
/// string is materialized, so the borrow ends at the call, exactly like
/// <see cref="PooledStringBuilder.ToStringReturn"/>. <see cref="Dispose"/> is idempotent, and a second
/// <see cref="ToString"/> on an already-materialized builder throws
/// <see cref="ObjectDisposedException"/> rather than reading a buffer that now belongs to someone
/// else. Every member other than <see cref="Dispose"/> throws <see cref="ObjectDisposedException"/>
/// once the builder has ended.<br />
/// <b>Views.</b> <see cref="AsSpan"/> and <see cref="TryCopyTo"/> hand out the current content without
/// ending the borrow, but a view is invalidated by growth (the content moves to a new buffer) and must
/// not outlive the builder — after <see cref="ToString"/> or <see cref="Dispose"/> the span points
/// into a pool buffer that the next renter owns.<br />
/// This is a <c>ref struct</c>: the compiler keeps it on the stack, so it cannot be boxed, captured by
/// a lambda, or held across an <c>await</c> — the misuse cases are compile-time errors, not runtime
/// hazards. A thread-static fast path (ZString's thread cache, with its no-nesting rule) is a possible
/// future enhancement and deliberately not part of this first version.
/// </remarks>
/// <example>
/// <code>
/// using var sb = new HayateValueStringBuilder();
/// sb.Append("order: ");
/// sb.Append(42);
/// var text = sb.ToString();   // the only allocation; the buffer is back in the pool here
/// </code>
/// </example>
public ref struct HayateValueStringBuilder
{
    /// <summary>
    /// The capacity rented for a builder that does not declare one: 256 characters, the
    /// Cosmos <c>ValueStringBuilder</c> default.
    /// </summary>
    public const int DefaultInitialCapacity = 256;

    // Bound for the TryFormat grow-and-retry loop: each retry doubles the buffer, so 24 doublings
    // cover every realistic formatted value many times over before this reports a runaway format.
    private const int MaxFormatGrowthRetries = 24;

    // The rented buffer; null once ToString or Dispose handed it back. Every accessor checks it, so a
    // use-after-return surfaces as an ObjectDisposedException instead of a read into a foreign buffer.
    private char[]? _buffer;

    // The pool the buffer was rented from; ArrayPool<char>.Shared unless the caller supplied one.
    private ArrayPool<char>? _pool;

    // The number of characters written so far; _buffer may be longer.
    private int _pos;

    /// <summary>
    /// Rents a builder from <see cref="ArrayPool{T}.Shared"/> with the default initial capacity of
    /// <see cref="DefaultInitialCapacity"/> characters.
    /// </summary>
    /// <remarks>
    /// The parameterless constructor is declared explicitly on purpose: a struct call with no
    /// arguments binds to the implicit zero-filling constructor when every declared constructor only
    /// has optional parameters, which would hand out an already-ended builder. For the same reason the
    /// other constructors take their capacity without a default.
    /// </remarks>
    public HayateValueStringBuilder()
        : this(DefaultInitialCapacity)
    {
    }

    /// <summary>
    /// Rents a builder from <see cref="ArrayPool{T}.Shared"/> with the given initial capacity.
    /// </summary>
    /// <param name="initialCapacity">The number of characters to reserve up front; the pool may hand
    /// back a larger buffer. Must not be negative.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="initialCapacity"/> is
    /// negative.</exception>
    public HayateValueStringBuilder(int initialCapacity)
        : this(null, initialCapacity)
    {
    }

    /// <summary>
    /// Rents a builder from the given pool with the given initial capacity.
    /// </summary>
    /// <param name="pool">The pool to rent from and return to; <c>null</c> means
    /// <see cref="ArrayPool{T}.Shared"/>.</param>
    /// <param name="initialCapacity">The number of characters to reserve up front; the pool may hand
    /// back a larger buffer. Must not be negative.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="initialCapacity"/> is
    /// negative.</exception>
    public HayateValueStringBuilder(ArrayPool<char>? pool, int initialCapacity)
    {
        if (initialCapacity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(initialCapacity), initialCapacity,
                "The initial capacity must not be negative.");
        }

        _pool = pool;
        _buffer = (pool ?? ArrayPool<char>.Shared).Rent(initialCapacity);
        _pos = 0;
    }

    /// <summary>
    /// The number of characters written so far.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The builder has ended — the buffer was returned by
    /// <see cref="ToString"/> or <see cref="Dispose"/>.</exception>
    public int Length
    {
        get
        {
            ThrowIfEnded();
            return _pos;
        }
    }

    /// <summary>
    /// The current buffer capacity; the pool may have rented a larger buffer than was asked for.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The builder has ended.</exception>
    public int Capacity
    {
        get
        {
            ThrowIfEnded();
            return _buffer!.Length;
        }
    }

    /// <summary>
    /// The character at <paramref name="index"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside
    /// <c>[0, Length)</c>.</exception>
    /// <exception cref="ObjectDisposedException">The builder has ended.</exception>
    public char this[int index]
    {
        get
        {
            ThrowIfEnded();
            if ((uint)index >= (uint)_pos)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index,
                    "The index must be inside the builder's current content.");
            }

            return _buffer![index];
        }
    }

    /// <summary>
    /// Appends a single character.
    /// </summary>
    /// <param name="value">The character to append.</param>
    /// <exception cref="ObjectDisposedException">The builder has ended.</exception>
    public void Append(char value)
    {
        ThrowIfEnded();
        EnsureCapacityFor(1);
        _buffer![_pos++] = value;
    }

    /// <summary>
    /// Appends a string; <c>null</c> appends nothing, like <see cref="StringBuilder.Append(string)"/>.
    /// </summary>
    /// <param name="value">The text to append; may be <c>null</c>.</param>
    /// <exception cref="ObjectDisposedException">The builder has ended.</exception>
    public void Append(string? value)
    {
        if (value is null)
        {
            return;
        }

        Append(value.AsSpan());
    }

    /// <summary>
    /// Appends the given span of characters.
    /// </summary>
    /// <param name="value">The characters to append.</param>
    /// <exception cref="ObjectDisposedException">The builder has ended.</exception>
    public void Append(ReadOnlySpan<char> value)
    {
        ThrowIfEnded();
        if (value.IsEmpty)
        {
            return;
        }

        EnsureCapacityFor(value.Length);
        value.CopyTo(_buffer!.AsSpan(_pos));
        _pos += value.Length;
    }

    /// <summary>
    /// Appends the value through its concrete type (Z4a): strings append directly, values whose type
    /// implements <c>ISpanFormattable</c> or <c>IFormattable</c> format through it (on net6+ straight
    /// into the rented buffer, on net48 through an intermediate string — the same cost the builders'
    /// own primitive appends have there), and everything else falls back to
    /// <see cref="object.ToString"/>.
    /// </summary>
    /// <typeparam name="T">The value's type.</typeparam>
    /// <param name="value">The value to append; <c>null</c> appends nothing.</param>
    /// <param name="format">An optional format specifier, passed to the value's formatter.</param>
    /// <remarks>
    /// For the built-in primitives prefer the named overloads (<see cref="Append(int, string?)"/> and
    /// friends): the generic path box-frames its interface check on value types, a 24-byte box per
    /// call, while the named overloads write through a struct-constrained helper with no boxing at
    /// all — the same reason ZString and Cosmos ship full sets of named appends.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The builder has ended.</exception>
    /// <exception cref="FormatException">The formatted value did not fit the buffer after repeated
    /// growth.</exception>
    public void Append<T>(T? value, string? format = null)
    {
        ThrowIfEnded();
        if (value is null)
        {
            return;
        }

        if (value is string text)
        {
            Append(text);
            return;
        }

#if NET6_0_OR_GREATER
        if (value is ISpanFormattable spanFormattable)
        {
            AppendSpanFormattableSlow(spanFormattable, format);
            return;
        }
#endif

        if (value is IFormattable formattable)
        {
            Append(formattable.ToString(format, null));
            return;
        }

        // The unconstrained receiver makes the compiler treat the ToString result as maybe-null; an
        // empty append is the no-op a null would have been.
        Append(value.ToString() ?? string.Empty);
    }

    /// <summary>Appends an <see cref="int"/>; see <see cref="Append{T}"/> for the format contract.</summary>
    public void Append(int value, string? format = null) => AppendSpanFormattable(value, format);

    /// <summary>Appends a <see cref="long"/>; see <see cref="Append{T}"/> for the format contract.</summary>
    public void Append(long value, string? format = null) => AppendSpanFormattable(value, format);

    /// <summary>Appends a <see cref="short"/>; see <see cref="Append{T}"/> for the format contract.</summary>
    public void Append(short value, string? format = null) => AppendSpanFormattable(value, format);

    /// <summary>Appends a <see cref="byte"/>; see <see cref="Append{T}"/> for the format contract.</summary>
    public void Append(byte value, string? format = null) => AppendSpanFormattable(value, format);

    /// <summary>Appends a <see cref="uint"/>; see <see cref="Append{T}"/> for the format contract.</summary>
    public void Append(uint value, string? format = null) => AppendSpanFormattable(value, format);

    /// <summary>Appends a <see cref="ulong"/>; see <see cref="Append{T}"/> for the format contract.</summary>
    public void Append(ulong value, string? format = null) => AppendSpanFormattable(value, format);

    /// <summary>Appends a <see cref="ushort"/>; see <see cref="Append{T}"/> for the format contract.</summary>
    public void Append(ushort value, string? format = null) => AppendSpanFormattable(value, format);

    /// <summary>Appends an <see cref="sbyte"/>; see <see cref="Append{T}"/> for the format contract.</summary>
    public void Append(sbyte value, string? format = null) => AppendSpanFormattable(value, format);

    /// <summary>Appends a <see cref="double"/>; see <see cref="Append{T}"/> for the format contract.</summary>
    public void Append(double value, string? format = null) => AppendSpanFormattable(value, format);

    /// <summary>Appends a <see cref="float"/>; see <see cref="Append{T}"/> for the format contract.</summary>
    public void Append(float value, string? format = null) => AppendSpanFormattable(value, format);

    /// <summary>Appends a <see cref="decimal"/>; see <see cref="Append{T}"/> for the format contract.</summary>
    public void Append(decimal value, string? format = null) => AppendSpanFormattable(value, format);

    /// <summary>Appends a <see cref="DateTime"/>; see <see cref="Append{T}"/> for the format contract.</summary>
    public void Append(DateTime value, string? format = null) => AppendSpanFormattable(value, format);

    /// <summary>Appends a <see cref="DateTimeOffset"/>; see <see cref="Append{T}"/> for the format contract.</summary>
    public void Append(DateTimeOffset value, string? format = null) => AppendSpanFormattable(value, format);

    /// <summary>Appends a <see cref="TimeSpan"/>; see <see cref="Append{T}"/> for the format contract.</summary>
    public void Append(TimeSpan value, string? format = null) => AppendSpanFormattable(value, format);

    /// <summary>Appends a <see cref="Guid"/>; see <see cref="Append{T}"/> for the format contract.</summary>
    public void Append(Guid value, string? format = null) => AppendSpanFormattable(value, format);

#if NET6_0_OR_GREATER
    /// <summary>
    /// Writes a struct-constrained <c>ISpanFormattable</c> value into the rented buffer, doubling on a
    /// failed fit; the constraint devirtualizes TryFormat per type, so nothing boxes.
    /// </summary>
    private void AppendSpanFormattable<T>(T value, string? format)
        where T : struct, ISpanFormattable
    {
        ThrowIfEnded();
        var attempts = 0;
        while (true)
        {
            if (value.TryFormat(_buffer!.AsSpan(_pos), out var written, format, default))
            {
                _pos += written;
                return;
            }

            // The remaining space did not fit the formatted value: double the buffer and retry.
            if (++attempts > MaxFormatGrowthRetries)
            {
                throw new FormatException(FormatWriter.FormatTooLongMessage);
            }

            EnsureCapacityFor(_buffer!.Length - _pos + 1);
        }
    }

    /// <summary>The boxed-shape entry the generic path uses for reference-type formatters.</summary>
    private void AppendSpanFormattableSlow(ISpanFormattable value, string? format)
    {
        var attempts = 0;
        while (true)
        {
            if (value.TryFormat(_buffer!.AsSpan(_pos), out var written, format, default))
            {
                _pos += written;
                return;
            }

            if (++attempts > MaxFormatGrowthRetries)
            {
                throw new FormatException(FormatWriter.FormatTooLongMessage);
            }

            EnsureCapacityFor(_buffer!.Length - _pos + 1);
        }
    }
#else
    /// <summary>
    /// net48 has no ISpanFormattable: the named overloads degrade to IFormattable.ToString, the same
    /// intermediate string the builders' own primitive appends cost there.
    /// </summary>
    private void AppendSpanFormattable<T>(T value, string? format)
        where T : struct, IFormattable
    {
        ThrowIfEnded();
        Append(value.ToString(format, null));
    }
#endif

    /// <summary>
    /// Appends the environment's default line terminator, like <see cref="StringBuilder.AppendLine()"/>.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The builder has ended.</exception>
    public void AppendLine() => Append(Environment.NewLine);

    /// <summary>
    /// Appends a string followed by the environment's default line terminator; <c>null</c> appends
    /// only the terminator, like <see cref="StringBuilder.AppendLine(string)"/>.
    /// </summary>
    /// <param name="value">The text to append before the terminator; may be <c>null</c>.</param>
    /// <exception cref="ObjectDisposedException">The builder has ended.</exception>
    public void AppendLine(string? value)
    {
        Append(value);
        Append(Environment.NewLine);
    }

    /// <summary>
    /// Resets the builder to empty while keeping the rented buffer, so the next build reuses it.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The builder has ended.</exception>
    public void Clear()
    {
        ThrowIfEnded();
        _pos = 0;
    }

    /// <summary>
    /// Copies the builder's current content into <paramref name="destination"/> without ending the
    /// borrow.
    /// </summary>
    /// <param name="destination">The span to copy into; large enough or the copy reports failure.</param>
    /// <param name="charsWritten">The number of characters copied; <c>0</c> when the copy failed.</param>
    /// <returns><c>true</c> when the content fit; <c>false</c> when <paramref name="destination"/> was
    /// too small (nothing is written).</returns>
    /// <remarks>
    /// Unlike <see cref="ToString"/>, the buffer stays rented — the builder keeps working after the
    /// copy. The copy is a single pass over the buffer; no intermediate string is allocated.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The builder has ended.</exception>
    public bool TryCopyTo(Span<char> destination, out int charsWritten)
    {
        ThrowIfEnded();
        if (destination.Length < _pos)
        {
            charsWritten = 0;
            return false;
        }

        _buffer!.AsSpan(0, _pos).CopyTo(destination);
        charsWritten = _pos;
        return true;
    }

    /// <summary>
    /// A read-only view of the builder's current content, without copying and without ending the
    /// borrow.
    /// </summary>
    /// <remarks>
    /// The view points into the rented buffer: it stays valid only while the builder does not grow or
    /// end. Growing (an append that exceeds <see cref="Capacity"/>) moves the content to a new buffer,
    /// and <see cref="ToString"/> / <see cref="Dispose"/> hand the old buffer back to the pool, where
    /// the next renter overwrites it — consume the view before either happens.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The builder has ended.</exception>
    public ReadOnlySpan<char> AsSpan()
    {
        ThrowIfEnded();
        return _buffer!.AsSpan(0, _pos);
    }

    /// <summary>
    /// Materializes the content as a new string and ends the builder: the rented buffer returns to its
    /// pool immediately after the string is produced, making this the build's one and only allocation.
    /// </summary>
    /// <returns>The builder's current content.</returns>
    /// <remarks>
    /// This is dispose-by-ToString, the convention ZString and Cosmos established for this shape: the
    /// borrow ends at the call, so <c>return sb.ToString();</c> inside a <c>using</c> block is safe and
    /// the trailing <see cref="Dispose"/> is a no-op. Calling <see cref="ToString"/> (or anything else
    /// but <see cref="Dispose"/>) again throws <see cref="ObjectDisposedException"/> — the buffer now
    /// belongs to the pool.
    /// </remarks>
    public override string ToString()
    {
        ThrowIfEnded();
        var text = new string(_buffer!, 0, _pos);
        ReturnBuffer();
        return text;
    }

    /// <summary>
    /// Returns the rented buffer to its pool without producing a string. Called automatically at the
    /// end of a <c>using</c> block.
    /// </summary>
    /// <remarks>
    /// Happens exactly once: every later call is a no-op, and so is the dispose after
    /// <see cref="ToString"/> already ended the builder. After the buffer is returned it belongs to the
    /// pool — do not read from it again (every accessor throws
    /// <see cref="ObjectDisposedException"/>).
    /// </remarks>
    public void Dispose()
    {
        // Idempotent by construction: the null check is the once-only guard, shared with ToString's
        // dispose-by-ToString path.
        ReturnBuffer();
    }

    /// <summary>Makes room for <paramref name="count"/> more characters, growing by doubling.</summary>
    private void EnsureCapacityFor(int count)
    {
        var required = _pos + count;
        var buffer = _buffer!;
        if (required <= buffer.Length)
        {
            return;
        }

        // Double the current reservation; if that still misses the request, take the request.
        var newSize = buffer.Length << 1;
        if (newSize < required)
        {
            newSize = required;
        }

        var pool = _pool ?? ArrayPool<char>.Shared;
        var grown = pool.Rent(newSize);
        buffer.AsSpan(0, _pos).CopyTo(grown);
        _buffer = grown;
        pool.Return(buffer);
    }

    private void ReturnBuffer()
    {
        var buffer = _buffer;
        if (buffer is null)
        {
            // Already ended by ToString or Dispose; the buffer belongs to the pool.
            return;
        }

        _buffer = null;
        _pos = 0;
        (_pool ?? ArrayPool<char>.Shared).Return(buffer);
    }

    private void ThrowIfEnded()
    {
        if (_buffer is null)
        {
            throw new ObjectDisposedException(nameof(HayateValueStringBuilder),
                "The value string builder has ended; ToString materialized the content (or Dispose returned " +
                "the buffer), so the builder must be rented again to be used.");
        }
    }
}
