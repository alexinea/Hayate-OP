using System;
using System.Buffers;
using System.IO;
using System.Text;
using System.Threading;
using DotNetCore.HayateOP;

namespace DotNetCore.HayateOP.Specialized;

/// <summary>
/// A <see cref="StringBuilder"/> holder that knows the pool it came from: disposing it returns it to that
/// pool instead of dropping it, so a borrow/build pair can be written with <c>using</c>.
/// </summary>
/// <remarks>
/// While the instance is registered with a <see cref="StringBuilderPool"/>, <see cref="Dispose"/> hands it
/// back through the pool's ordinary return path — validation and the clear behave exactly as for a manual
/// <c>Release</c>. The instance itself is kept alive for the next borrower; only the pool can destroy it,
/// and it does so when the builder grew past the pool's maximum capacity.<br />
/// Disposal is once-per-borrow: a second <see cref="Dispose"/> of the same borrow is a no-op rather than
/// a duplicate return. After disposing, the instance must not be used again — if it was already returned
/// and re-borrowed by someone else, disposing it a second time would be a foreign return.<br />
/// An instance created directly (never handed out by a pool) owns nobody: <see cref="Dispose"/> does
/// nothing and the garbage collector reclaims it.
/// </remarks>
/// <example>
/// <code>
/// using var sb = StringBuilderPool.Instance.GetObject();
/// sb.StringBuilder.Append("hello");
/// var text = sb.ToString();   // returned to the pool at the end of the block
/// </code>
/// </example>
public sealed class PooledStringBuilder : IDisposable
{
    // The pool that created (and therefore owns) this instance; null once the pool destroys the instance
    // or when it was created standalone. Volatile so a disposing thread never routes a return through a
    // stale owner after the pool has detached the instance.
    private volatile IHayateObjectPool<PooledStringBuilder>? _owner;

    // 1 = the instance sits in the pool, 0 = a borrower holds it. The CAS from 0 to 1 in Dispose is the
    // once-per-borrow guard: only the first dispose of a borrow performs the return.
    private int _idle = 1;

    /// <summary>
    /// Creates a standalone instance whose builder starts with the given capacity.
    /// </summary>
    /// <param name="capacity">The initial capacity of the builder.</param>
    /// <remarks>
    /// A standalone instance is not registered with any pool; disposing it does nothing. Only
    /// <see cref="StringBuilderPool"/> produces pooled instances.
    /// </remarks>
    public PooledStringBuilder(int capacity)
    {
        StringBuilder = new StringBuilder(capacity);
    }

    /// <summary>
    /// Creates a standalone instance whose builder starts with no reserved capacity.
    /// </summary>
    /// <remarks>
    /// The parameterless constructor exists so generic pool frameworks that demand a default
    /// constructor can handle the type (HayateOP's <see cref="HayatePoolBuilder{T}"/> requires one);
    /// <see cref="StringBuilderPool"/> uses it and then reserves the configured capacity.
    /// </remarks>
    public PooledStringBuilder()
    {
        StringBuilder = new StringBuilder();
    }

    /// <summary>
    /// The builder.
    /// </summary>
    public StringBuilder StringBuilder { get; }

    /// <summary>
    /// Returns the builder's current content, as P89OP's wrapper does.
    /// </summary>
    public override string ToString() => StringBuilder.ToString();

    /// <summary>
    /// Appends the value through its concrete type (Z4a): on net6+ an
    /// <c>ISpanFormattable</c> value formats straight into the builder with no
    /// intermediate string, on net48 the path degrades to <see cref="System.IFormattable"/> — the same
    /// cost as the builder's own primitive appends there. Strings append directly on every target.
    /// </summary>
    /// <typeparam name="T">The value's type.</typeparam>
    /// <param name="value">The value to append; <c>null</c> appends nothing.</param>
    /// <param name="format">An optional format specifier, passed to the value's formatter.</param>
    /// <returns>The same wrapper, for call chaining.</returns>
    /// <remarks>
    /// For the built-in primitives prefer the named overloads (<see cref="Append(int, string?)"/> and
    /// friends): the generic path box-frames its type check on value types under shared-generic
    /// codegen, while the named overloads write through a struct-constrained helper with no boxing.
    /// </remarks>
    public PooledStringBuilder Append<T>(T? value, string? format = null)
    {
        FormatWriter.Append(StringBuilder, value, format);
        return this;
    }

    /// <summary>Appends an <see cref="int"/>; see <see cref="Append{T}"/> for the format contract.</summary>
    public PooledStringBuilder Append(int value, string? format = null)
    {
        FormatWriter.Append(StringBuilder, value, format);
        return this;
    }

    /// <summary>Appends a <see cref="long"/>; see <see cref="Append{T}"/> for the format contract.</summary>
    public PooledStringBuilder Append(long value, string? format = null)
    {
        FormatWriter.Append(StringBuilder, value, format);
        return this;
    }

    /// <summary>Appends a <see cref="short"/>; see <see cref="Append{T}"/> for the format contract.</summary>
    public PooledStringBuilder Append(short value, string? format = null)
    {
        FormatWriter.Append(StringBuilder, value, format);
        return this;
    }

    /// <summary>Appends a <see cref="byte"/>; see <see cref="Append{T}"/> for the format contract.</summary>
    public PooledStringBuilder Append(byte value, string? format = null)
    {
        FormatWriter.Append(StringBuilder, value, format);
        return this;
    }

    /// <summary>Appends a <see cref="uint"/>; see <see cref="Append{T}"/> for the format contract.</summary>
    public PooledStringBuilder Append(uint value, string? format = null)
    {
        FormatWriter.Append(StringBuilder, value, format);
        return this;
    }

    /// <summary>Appends a <see cref="ulong"/>; see <see cref="Append{T}"/> for the format contract.</summary>
    public PooledStringBuilder Append(ulong value, string? format = null)
    {
        FormatWriter.Append(StringBuilder, value, format);
        return this;
    }

    /// <summary>Appends a <see cref="ushort"/>; see <see cref="Append{T}"/> for the format contract.</summary>
    public PooledStringBuilder Append(ushort value, string? format = null)
    {
        FormatWriter.Append(StringBuilder, value, format);
        return this;
    }

    /// <summary>Appends an <see cref="sbyte"/>; see <see cref="Append{T}"/> for the format contract.</summary>
    public PooledStringBuilder Append(sbyte value, string? format = null)
    {
        FormatWriter.Append(StringBuilder, value, format);
        return this;
    }

    /// <summary>Appends a <see cref="double"/>; see <see cref="Append{T}"/> for the format contract.</summary>
    public PooledStringBuilder Append(double value, string? format = null)
    {
        FormatWriter.Append(StringBuilder, value, format);
        return this;
    }

    /// <summary>Appends a <see cref="float"/>; see <see cref="Append{T}"/> for the format contract.</summary>
    public PooledStringBuilder Append(float value, string? format = null)
    {
        FormatWriter.Append(StringBuilder, value, format);
        return this;
    }

    /// <summary>Appends a <see cref="decimal"/>; see <see cref="Append{T}"/> for the format contract.</summary>
    public PooledStringBuilder Append(decimal value, string? format = null)
    {
        FormatWriter.Append(StringBuilder, value, format);
        return this;
    }

    /// <summary>Appends a <see cref="DateTime"/>; see <see cref="Append{T}"/> for the format contract.</summary>
    public PooledStringBuilder Append(DateTime value, string? format = null)
    {
        FormatWriter.Append(StringBuilder, value, format);
        return this;
    }

    /// <summary>Appends a <see cref="DateTimeOffset"/>; see <see cref="Append{T}"/> for the format contract.</summary>
    public PooledStringBuilder Append(DateTimeOffset value, string? format = null)
    {
        FormatWriter.Append(StringBuilder, value, format);
        return this;
    }

    /// <summary>Appends a <see cref="TimeSpan"/>; see <see cref="Append{T}"/> for the format contract.</summary>
    public PooledStringBuilder Append(TimeSpan value, string? format = null)
    {
        FormatWriter.Append(StringBuilder, value, format);
        return this;
    }

    /// <summary>Appends a <see cref="Guid"/>; see <see cref="Append{T}"/> for the format contract.</summary>
    public PooledStringBuilder Append(Guid value, string? format = null)
    {
        FormatWriter.Append(StringBuilder, value, format);
        return this;
    }

    /// <summary>
    /// Copies the builder's current content into <paramref name="destination"/> without materializing
    /// a string (Z5): on net6+ the copy walks the builder's chunk chain straight into the span, so a
    /// consumer that reads text as <see cref="ReadOnlySpan{Char}"/> skips the final string allocation
    /// entirely; on net48 the builder has no span-aware copy, so the path degrades through one
    /// intermediate <see cref="ToString"/> — never worse than the string it replaces.
    /// </summary>
    /// <param name="destination">The span to copy into.</param>
    /// <param name="charsWritten">The number of characters copied; <c>0</c> when the copy failed.</param>
    /// <returns><c>true</c> when the content fit; <c>false</c> when <paramref name="destination"/> was
    /// too small (nothing is written).</returns>
    /// <remarks>
    /// The copy does not end the borrow — the builder keeps working afterwards. It is a snapshot of the
    /// current content; appends after the copy are not reflected in it.
    /// </remarks>
    public bool TryCopyTo(Span<char> destination, out int charsWritten)
    {
        var length = StringBuilder.Length;
        if (destination.Length < length)
        {
            charsWritten = 0;
            return false;
        }

#if NET6_0_OR_GREATER
        var written = 0;
        foreach (var chunk in StringBuilder.GetChunks())
        {
            chunk.Span.CopyTo(destination.Slice(written));
            written += chunk.Length;
        }
#else
        // net48's StringBuilder has no span-aware copy; the intermediate string is the documented
        // degradation, never worse than ToString-then-consume.
        StringBuilder.ToString().AsSpan().CopyTo(destination);
        var written = length;
#endif
        charsWritten = written;
        return true;
    }

#if NET6_0_OR_GREATER
    /// <summary>
    /// Writes the builder's current content to <paramref name="target"/> as UTF-8 bytes (Z5, net6+):
    /// the chunk chain is encoded chunk by chunk into a rented scratch buffer, so a stream consumer
    /// skips the final string without a whole-content allocation of its own.
    /// </summary>
    /// <param name="target">The stream to write to, positioned wherever the caller wants the content.</param>
    /// <remarks>
    /// The write does not end the borrow — the builder keeps working afterwards. The method writes the
    /// content as it stands when called; appends afterwards are not written.<br />
    /// The encoder persists across chunks, so a surrogate pair split by a chunk boundary is still
    /// encoded correctly; the final flush emits whatever the state held back.
    /// </remarks>
    public void WriteTo(Stream target)
    {
        // 1KB scratch: up to 1024 chars per pass even in the astral-plane worst case; larger chunks
        // simply loop through the same buffer.
        var scratch = ArrayPool<byte>.Shared.Rent(1024);
        try
        {
            var encoder = Encoding.UTF8.GetEncoder();
            foreach (var chunk in StringBuilder.GetChunks())
            {
                var span = chunk.Span;
                while (!span.IsEmpty)
                {
                    // Convert (not GetBytes) is the streaming API: it reports how much it consumed on
                    // both sides, so a chunk larger than the scratch buffer loops through it.
                    encoder.Convert(span, scratch, flush: false, out var charsUsed, out var bytesUsed, out _);
                    target.Write(scratch, 0, bytesUsed);
                    span = span.Slice(charsUsed);
                }
            }

            // Emit whatever the encoder held back (a pending surrogate, or nothing in the common case).
            encoder.Convert(default(ReadOnlySpan<char>), scratch, flush: true, out _, out var tail, out _);
            if (tail > 0)
            {
                target.Write(scratch, 0, tail);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }
#endif

    /// <summary>
    /// Converts the builder's content to a string and returns the builder to its pool in one call —
    /// CPL's <c>ToStringReturn</c>: the conversion and the return are one operation, so the borrow ends
    /// exactly where the text is produced.
    /// </summary>
    /// <returns>The builder's current content.</returns>
    /// <remarks>
    /// The return is atomic with the conversion: it runs even when the conversion throws, so a failing
    /// path can never leak the builder out of the pool. Like <see cref="Dispose"/>, the return happens
    /// exactly once per borrow — a second call on an already-returned builder is a no-op return that
    /// observes whatever the next borrower may have written, so treat the call as the end of the borrow,
    /// exactly as the end of a <c>using</c> block.<br />
    /// On a standalone instance (never handed out by a pool) the conversion runs and the return step
    /// does nothing.
    /// </remarks>
    /// <example>
    /// <code>
    /// var sb = StringBuilderPool.Instance.GetObject();
    /// sb.StringBuilder.Append("order: ").Append(42);
    /// var text = sb.ToStringReturn();   // builder returned to the pool here
    /// </code>
    /// </example>
    public string ToStringReturn()
    {
        try
        {
            return StringBuilder.ToString();
        }
        finally
        {
            // The conversion owns the borrow's outcome: whether it produced the string or threw, the
            // builder goes back. Dispose is the exactly-once return path, identical to a using block.
            Dispose();
        }
    }

    /// <summary>
    /// Disposing a pooled instance returns it to its pool; disposing a standalone instance does nothing.
    /// Called automatically at the end of a <c>using</c> block.
    /// </summary>
    /// <remarks>
    /// The return goes through the pool's ordinary return path, so capacity validation and the clear
    /// behave exactly as for a manual <c>Release</c> — and a builder that grew past the pool's maximum
    /// capacity is destroyed here instead of being parked. The return happens exactly once per borrow;
    /// every later call is a no-op.
    /// </remarks>
    public void Dispose()
    {
        var owner = _owner;
        if (owner is null)
        {
            // Standalone or detached by the pool: nothing to return, the GC reclaims the instance.
            return;
        }

        // Once-per-borrow claim: the first dispose flips the state and returns the instance, later
        // disposals of the same borrow observe the flipped state and do nothing.
        if (Interlocked.CompareExchange(ref _idle, 1, 0) == 0)
        {
            owner.Release(this);
        }
    }

    /// <summary>Registers the owning pool; called by the pool's policy at creation time.</summary>
    internal void BindOwner(IHayateObjectPool<PooledStringBuilder> owner) => _owner = owner;

    /// <summary>
    /// The pool this instance currently belongs to (its lending engine pool, or the wrapper pool until
    /// the policy binds the engine); <c>null</c> for standalone or pool-destroyed instances. The pool
    /// routes a manual <c>Release</c> through it so a borrowed builder returns to the tier it came from.
    /// </summary>
    internal IHayateObjectPool<PooledStringBuilder>? HomePool => _owner;

    /// <summary>Marks the instance as borrowed so the next dispose performs the return.</summary>
    internal void OnBorrowed() => Interlocked.Exchange(ref _idle, 0);

    /// <summary>
    /// Marks the instance as idle; called by the pool's policy on every successful return, including a
    /// manual <c>Release</c>, so a dispose after a manual release cannot route a second return.
    /// </summary>
    internal void MarkIdle() => Interlocked.Exchange(ref _idle, 1);

    /// <summary>
    /// Detaches the pool before the pool destroys the instance, so nothing routes a return for a
    /// destroyed instance.
    /// </summary>
    internal void OnDetachedFromPool() => _owner = null;
}
