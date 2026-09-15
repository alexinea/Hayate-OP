using System;
using System.Buffers;
using System.Globalization;
using System.Text;

namespace DotNetCore.HayateOP.Specialized;

/// <summary>
/// The shared fast-append writer behind the specialized package's generic <c>Append&lt;T&gt;</c>
/// surfaces and format helpers (Z4a). Values travel as their concrete generic type: on net6+ an
/// <c>ISpanFormattable</c> value formats straight into a scratch buffer and lands
/// in the target as a span — no intermediate string, no boxing; on net48 (and for the rare
/// <see cref="IFormattable"/>-only types everywhere) the path degrades to
/// <see cref="IFormattable.ToString(string, System.IFormatProvider)"/>, the same intermediate string
/// <c>StringBuilder.Append(int)</c> would produce there, so the legacy target is parity by
/// construction. Strings append directly on every target.
/// </summary>
internal static class FormatWriter
{
    // Scratch for the TryFormat fast path: every built-in ISpanFormattable value with a normal format
    // string fits comfortably; a rented buffer backs the rare overflow, and a value that outgrows the
    // rented buffer throws rather than renting without bound.
    private const int StackBufferSize = 128;
    private const int OverflowBufferSize = 512;

    /// <summary>
    /// Appends <paramref name="value"/> to <paramref name="target"/> honoring an optional
    /// <c>{index,alignment:format}</c>-style request: <paramref name="format"/> passes through to the
    /// value's formatter, and a non-zero <paramref name="alignment"/> pads the formatted result
    /// (positive right-aligns, negative left-aligns) without ever materializing a padded copy.
    /// </summary>
    public static void Append<T>(StringBuilder target, T? value, string? format = null, int alignment = 0)
    {
        if (value is null)
        {
            if (alignment != 0)
            {
                AppendAligned(target, string.Empty, alignment);
            }

            return;
        }

        if (value is string text)
        {
            AppendAligned(target, text, alignment);
            return;
        }

        // Exact-type dispatch to the named overloads: a type check against a concrete type in generic
        // code is resolved per instantiation without boxing, while the interface checks below box a
        // value-type argument on their shared path. The common primitives therefore cost nothing, and
        // custom types fall through to the interface branches as before.
        if (value is int i32) { Append(target, i32, format, alignment); return; }
        if (value is long i64) { Append(target, i64, format, alignment); return; }
        if (value is double d) { Append(target, d, format, alignment); return; }
        if (value is float f) { Append(target, f, format, alignment); return; }
        if (value is decimal m) { Append(target, m, format, alignment); return; }
        if (value is short h) { Append(target, h, format, alignment); return; }
        if (value is byte b) { Append(target, b, format, alignment); return; }
        if (value is uint u) { Append(target, u, format, alignment); return; }
        if (value is ulong ul) { Append(target, ul, format, alignment); return; }
        if (value is ushort us) { Append(target, us, format, alignment); return; }
        if (value is sbyte sb) { Append(target, sb, format, alignment); return; }
        if (value is DateTime dt) { Append(target, dt, format, alignment); return; }
        if (value is DateTimeOffset dto) { Append(target, dto, format, alignment); return; }
        if (value is TimeSpan ts) { Append(target, ts, format, alignment); return; }
        if (value is Guid g) { Append(target, g, format, alignment); return; }

#if NET6_0_OR_GREATER
        if (value is ISpanFormattable spanFormattable)
        {
            Span<char> scratch = stackalloc char[StackBufferSize];
            if (spanFormattable.TryFormat(scratch, out var written, format, default))
            {
                AppendAlignedSpan(target, scratch.Slice(0, written), alignment);
                return;
            }

            var rented = ArrayPool<char>.Shared.Rent(OverflowBufferSize);
            try
            {
                if (!spanFormattable.TryFormat(rented, out written, format, default))
                {
                    throw new FormatException(FormatTooLongMessage);
                }

                AppendAlignedSpan(target, rented.AsSpan(0, written), alignment);
            }
            finally
            {
                ArrayPool<char>.Shared.Return(rented);
            }

            return;
        }
#endif

        // The IFormattable branch covers custom formatters and carries the whole net48 path for
        // format-supported values. A null format still routes through IFormattable.ToString(null),
        // matching string.Format - an Object.ToString() fallback would drop custom default
        // formatting. It costs one intermediate string, exactly what StringBuilder.Append(int)
        // costs on net48 anyway.
        if (value is IFormattable formattable)
        {
            AppendAligned(target, formattable.ToString(format, null), alignment);
            return;
        }

        // The unconstrained receiver makes the compiler treat the ToString result as maybe-null; an
        // empty append is the no-op a null would have been.
        AppendAligned(target, value.ToString() ?? string.Empty, alignment);
    }

    // Named primitive overloads (Z4a).
    //
    // The generic Append<T> box-frames the interface cast for value types (a `value is ISpanFormattable`
    // check materializes a 24-byte box per call on the shared-instantiation path). The named overloads
    // route through a struct-constrained generic, whose per-type instantiations devirtualize
    // TryFormat and write without boxing - the same reason ZString and Cosmos ship full sets of named
    // appends.

    /// <summary>Appends an <see cref="int"/> honoring an optional format specifier and alignment.</summary>
    public static void Append(StringBuilder target, int value, string? format = null, int alignment = 0)
        => AppendSpanFormattable(target, value, format, alignment);

    /// <summary>Appends a <see cref="long"/> honoring an optional format specifier and alignment.</summary>
    public static void Append(StringBuilder target, long value, string? format = null, int alignment = 0)
        => AppendSpanFormattable(target, value, format, alignment);

    /// <summary>Appends a <see cref="short"/> honoring an optional format specifier and alignment.</summary>
    public static void Append(StringBuilder target, short value, string? format = null, int alignment = 0)
        => AppendSpanFormattable(target, value, format, alignment);

    /// <summary>Appends a <see cref="byte"/> honoring an optional format specifier and alignment.</summary>
    public static void Append(StringBuilder target, byte value, string? format = null, int alignment = 0)
        => AppendSpanFormattable(target, value, format, alignment);

    /// <summary>Appends a <see cref="uint"/> honoring an optional format specifier and alignment.</summary>
    public static void Append(StringBuilder target, uint value, string? format = null, int alignment = 0)
        => AppendSpanFormattable(target, value, format, alignment);

    /// <summary>Appends a <see cref="ulong"/> honoring an optional format specifier and alignment.</summary>
    public static void Append(StringBuilder target, ulong value, string? format = null, int alignment = 0)
        => AppendSpanFormattable(target, value, format, alignment);

    /// <summary>Appends a <see cref="ushort"/> honoring an optional format specifier and alignment.</summary>
    public static void Append(StringBuilder target, ushort value, string? format = null, int alignment = 0)
        => AppendSpanFormattable(target, value, format, alignment);

    /// <summary>Appends an <see cref="sbyte"/> honoring an optional format specifier and alignment.</summary>
    public static void Append(StringBuilder target, sbyte value, string? format = null, int alignment = 0)
        => AppendSpanFormattable(target, value, format, alignment);

    /// <summary>Appends a <see cref="double"/> honoring an optional format specifier and alignment.</summary>
    public static void Append(StringBuilder target, double value, string? format = null, int alignment = 0)
        => AppendSpanFormattable(target, value, format, alignment);

    /// <summary>Appends a <see cref="float"/> honoring an optional format specifier and alignment.</summary>
    public static void Append(StringBuilder target, float value, string? format = null, int alignment = 0)
        => AppendSpanFormattable(target, value, format, alignment);

    /// <summary>Appends a <see cref="decimal"/> honoring an optional format specifier and alignment.</summary>
    public static void Append(StringBuilder target, decimal value, string? format = null, int alignment = 0)
        => AppendSpanFormattable(target, value, format, alignment);

    /// <summary>Appends a <see cref="DateTime"/> honoring an optional format specifier and alignment.</summary>
    public static void Append(StringBuilder target, DateTime value, string? format = null, int alignment = 0)
        => AppendSpanFormattable(target, value, format, alignment);

    /// <summary>Appends a <see cref="DateTimeOffset"/> honoring an optional format specifier and alignment.</summary>
    public static void Append(StringBuilder target, DateTimeOffset value, string? format = null, int alignment = 0)
        => AppendSpanFormattable(target, value, format, alignment);

    /// <summary>Appends a <see cref="TimeSpan"/> honoring an optional format specifier and alignment.</summary>
    public static void Append(StringBuilder target, TimeSpan value, string? format = null, int alignment = 0)
        => AppendSpanFormattable(target, value, format, alignment);

    /// <summary>Appends a <see cref="Guid"/> honoring an optional format specifier and alignment.</summary>
    public static void Append(StringBuilder target, Guid value, string? format = null, int alignment = 0)
        => AppendSpanFormattable(target, value, format, alignment);

#if NET6_0_OR_GREATER
    private static void AppendSpanFormattable<T>(StringBuilder target, T value, string? format, int alignment)
        where T : struct, ISpanFormattable
    {
        Span<char> scratch = stackalloc char[StackBufferSize];
        if (value.TryFormat(scratch, out var written, format, default))
        {
            AppendAlignedSpan(target, scratch.Slice(0, written), alignment);
            return;
        }

        var rented = ArrayPool<char>.Shared.Rent(OverflowBufferSize);
        try
        {
            if (!value.TryFormat(rented, out written, format, default))
            {
                throw new FormatException(FormatTooLongMessage);
            }

            AppendAlignedSpan(target, rented.AsSpan(0, written), alignment);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }
#else
    private static void AppendSpanFormattable<T>(StringBuilder target, T value, string? format, int alignment)
        where T : struct, IFormattable
    {
        // net48 has no ISpanFormattable: the named overloads degrade to IFormattable.ToString, the
        // same intermediate string the builders' own primitive appends cost there.
        AppendAligned(target, value.ToString(format, null), alignment);
    }
#endif

    private static void AppendAligned(StringBuilder target, string text, int alignment)
    {
        if (alignment == 0)
        {
            target.Append(text);
            return;
        }

        var padding = (alignment > 0 ? alignment : -alignment) - text.Length;
        AppendPadded(target, text.AsSpan(), padding, alignment);
    }

#if NET6_0_OR_GREATER
    private static void AppendAlignedSpan(StringBuilder target, ReadOnlySpan<char> text, int alignment)
    {
        if (alignment == 0)
        {
            target.Append(text);
            return;
        }

        var padding = (alignment > 0 ? alignment : -alignment) - text.Length;
        AppendPadded(target, text, padding, alignment);
    }
#endif

    /// <summary>Pads <paramref name="text"/> with <paramref name="padding"/> spaces around the append.</summary>
    private static void AppendPadded(StringBuilder target, ReadOnlySpan<char> text, int padding, int alignment)
    {
        // Positive alignment right-aligns (padding first), negative left-aligns; spaces go straight
        // into the builder instead of a padded copy of the text.
#if NET6_0_OR_GREATER
        if (padding > 0 && alignment > 0)
        {
            target.Append(' ', padding);
        }

        target.Append(text);

        if (padding > 0 && alignment < 0)
        {
            target.Append(' ', padding);
        }
#else
        if (padding > 0 && alignment > 0)
        {
            target.Append(' ', padding);
        }

        target.Append(text.ToString());

        if (padding > 0 && alignment < 0)
        {
            target.Append(' ', padding);
        }
#endif
    }

    internal const string FormatTooLongMessage =
        "The formatted value did not fit the append buffer; shorten the format specifier or write the value in parts.";
}
