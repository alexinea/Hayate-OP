using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DotNetCore.HayateOP.Specialized;

/// <content>
/// ZString-style formatting helpers: generic, <c>params</c>-free <c>Format</c>, <c>Concat</c> and
/// <c>Join</c> overloads that borrow one builder from the shared pool, write the arguments straight
/// into it and hand the finished string back — no <c>object[]</c>, no boxing of the arguments, one
/// borrow/return cycle.
/// </content>
public sealed partial class StringBuilderPool
{
    /// <summary>
    /// Formats a composite format string with the given argument, like <c>string.Format</c>
    /// but without boxing the argument into an <c>object[]</c>: the value is written into a pooled
    /// builder through its concrete type.
    /// </summary>
    /// <typeparam name="T1">The argument's type.</typeparam>
    /// <param name="format">A composite format string ({@index},alignment:format} holes, {{ and }}
    /// escapes), matching <c>string.Format</c>'s grammar.</param>
    /// <param name="arg">The value for hole <c>{0}</c>; <c>null</c> renders as empty.</param>
    /// <returns>The formatted string.</returns>
    /// <remarks>
    /// Formatting uses the current culture, as <c>string.Format</c> does. The
    /// helper borrows one builder from <see cref="Instance"/> for the call; the return is atomic with
    /// the conversion, so a failure can never leak the builder. Every arity up to eight arguments is
    /// provided; beyond that, write into a borrowed builder directly.
    /// </remarks>
    public static string Format<T1>(string format, T1? arg)
        => FormatCore(new FormatArguments1<T1>(arg), format);

    /// <summary>Formats a composite format string with two arguments, without an <c>object[]</c> or boxing.</summary>
    /// <typeparam name="T1">The first argument's type.</typeparam>
    /// <typeparam name="T2">The second argument's type.</typeparam>
    /// <param name="format">A composite format string.</param>
    /// <param name="arg1">The value for hole <c>{0}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg2">The value for hole <c>{1}</c>; <c>null</c> renders as empty.</param>
    /// <returns>The formatted string.</returns>
    /// <remarks>See the one-argument overload for grammar, culture and borrowing semantics.</remarks>
    public static string Format<T1, T2>(string format, T1? arg1, T2? arg2)
        => FormatCore(new FormatArguments2<T1, T2>(arg1, arg2), format);

    /// <summary>Formats a composite format string with three arguments, without an <c>object[]</c> or boxing.</summary>
    /// <typeparam name="T1">The first argument's type.</typeparam>
    /// <typeparam name="T2">The second argument's type.</typeparam>
    /// <typeparam name="T3">The third argument's type.</typeparam>
    /// <param name="format">A composite format string.</param>
    /// <param name="arg1">The value for hole <c>{0}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg2">The value for hole <c>{1}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg3">The value for hole <c>{2}</c>; <c>null</c> renders as empty.</param>
    /// <returns>The formatted string.</returns>
    /// <remarks>See the one-argument overload for grammar, culture and borrowing semantics.</remarks>
    public static string Format<T1, T2, T3>(string format, T1? arg1, T2? arg2, T3? arg3)
        => FormatCore(new FormatArguments3<T1, T2, T3>(arg1, arg2, arg3), format);

    /// <summary>Formats a composite format string with four arguments, without an <c>object[]</c> or boxing.</summary>
    /// <typeparam name="T1">The first argument's type.</typeparam>
    /// <typeparam name="T2">The second argument's type.</typeparam>
    /// <typeparam name="T3">The third argument's type.</typeparam>
    /// <typeparam name="T4">The fourth argument's type.</typeparam>
    /// <param name="format">A composite format string.</param>
    /// <param name="arg1">The value for hole <c>{0}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg2">The value for hole <c>{1}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg3">The value for hole <c>{2}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg4">The value for hole <c>{3}</c>; <c>null</c> renders as empty.</param>
    /// <returns>The formatted string.</returns>
    /// <remarks>See the one-argument overload for grammar, culture and borrowing semantics.</remarks>
    public static string Format<T1, T2, T3, T4>(string format, T1? arg1, T2? arg2, T3? arg3, T4? arg4)
        => FormatCore(new FormatArguments4<T1, T2, T3, T4>(arg1, arg2, arg3, arg4), format);

    /// <summary>Formats a composite format string with five arguments, without an <c>object[]</c> or boxing.</summary>
    /// <typeparam name="T1">The first argument's type.</typeparam>
    /// <typeparam name="T2">The second argument's type.</typeparam>
    /// <typeparam name="T3">The third argument's type.</typeparam>
    /// <typeparam name="T4">The fourth argument's type.</typeparam>
    /// <typeparam name="T5">The fifth argument's type.</typeparam>
    /// <param name="format">A composite format string.</param>
    /// <param name="arg1">The value for hole <c>{0}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg2">The value for hole <c>{1}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg3">The value for hole <c>{2}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg4">The value for hole <c>{3}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg5">The value for hole <c>{4}</c>; <c>null</c> renders as empty.</param>
    /// <returns>The formatted string.</returns>
    /// <remarks>See the one-argument overload for grammar, culture and borrowing semantics.</remarks>
    public static string Format<T1, T2, T3, T4, T5>(string format, T1? arg1, T2? arg2, T3? arg3, T4? arg4, T5? arg5)
        => FormatCore(new FormatArguments5<T1, T2, T3, T4, T5>(arg1, arg2, arg3, arg4, arg5), format);

    /// <summary>Formats a composite format string with six arguments, without an <c>object[]</c> or boxing.</summary>
    /// <typeparam name="T1">The first argument's type.</typeparam>
    /// <typeparam name="T2">The second argument's type.</typeparam>
    /// <typeparam name="T3">The third argument's type.</typeparam>
    /// <typeparam name="T4">The fourth argument's type.</typeparam>
    /// <typeparam name="T5">The fifth argument's type.</typeparam>
    /// <typeparam name="T6">The sixth argument's type.</typeparam>
    /// <param name="format">A composite format string.</param>
    /// <param name="arg1">The value for hole <c>{0}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg2">The value for hole <c>{1}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg3">The value for hole <c>{2}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg4">The value for hole <c>{3}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg5">The value for hole <c>{4}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg6">The value for hole <c>{5}</c>; <c>null</c> renders as empty.</param>
    /// <returns>The formatted string.</returns>
    /// <remarks>See the one-argument overload for grammar, culture and borrowing semantics.</remarks>
    public static string Format<T1, T2, T3, T4, T5, T6>(string format, T1? arg1, T2? arg2, T3? arg3, T4? arg4, T5? arg5, T6? arg6)
        => FormatCore(new FormatArguments6<T1, T2, T3, T4, T5, T6>(arg1, arg2, arg3, arg4, arg5, arg6), format);

    /// <summary>Formats a composite format string with seven arguments, without an <c>object[]</c> or boxing.</summary>
    /// <typeparam name="T1">The first argument's type.</typeparam>
    /// <typeparam name="T2">The second argument's type.</typeparam>
    /// <typeparam name="T3">The third argument's type.</typeparam>
    /// <typeparam name="T4">The fourth argument's type.</typeparam>
    /// <typeparam name="T5">The fifth argument's type.</typeparam>
    /// <typeparam name="T6">The sixth argument's type.</typeparam>
    /// <typeparam name="T7">The seventh argument's type.</typeparam>
    /// <param name="format">A composite format string.</param>
    /// <param name="arg1">The value for hole <c>{0}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg2">The value for hole <c>{1}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg3">The value for hole <c>{2}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg4">The value for hole <c>{3}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg5">The value for hole <c>{4}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg6">The value for hole <c>{5}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg7">The value for hole <c>{6}</c>; <c>null</c> renders as empty.</param>
    /// <returns>The formatted string.</returns>
    /// <remarks>See the one-argument overload for grammar, culture and borrowing semantics.</remarks>
    public static string Format<T1, T2, T3, T4, T5, T6, T7>(string format, T1? arg1, T2? arg2, T3? arg3, T4? arg4, T5? arg5, T6? arg6, T7? arg7)
        => FormatCore(new FormatArguments7<T1, T2, T3, T4, T5, T6, T7>(arg1, arg2, arg3, arg4, arg5, arg6, arg7), format);

    /// <summary>Formats a composite format string with eight arguments, without an <c>object[]</c> or boxing.</summary>
    /// <typeparam name="T1">The first argument's type.</typeparam>
    /// <typeparam name="T2">The second argument's type.</typeparam>
    /// <typeparam name="T3">The third argument's type.</typeparam>
    /// <typeparam name="T4">The fourth argument's type.</typeparam>
    /// <typeparam name="T5">The fifth argument's type.</typeparam>
    /// <typeparam name="T6">The sixth argument's type.</typeparam>
    /// <typeparam name="T7">The seventh argument's type.</typeparam>
    /// <typeparam name="T8">The eighth argument's type.</typeparam>
    /// <param name="format">A composite format string.</param>
    /// <param name="arg1">The value for hole <c>{0}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg2">The value for hole <c>{1}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg3">The value for hole <c>{2}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg4">The value for hole <c>{3}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg5">The value for hole <c>{4}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg6">The value for hole <c>{5}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg7">The value for hole <c>{6}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg8">The value for hole <c>{7}</c>; <c>null</c> renders as empty.</param>
    /// <returns>The formatted string.</returns>
    /// <remarks>See the one-argument overload for grammar, culture and borrowing semantics.</remarks>
    public static string Format<T1, T2, T3, T4, T5, T6, T7, T8>(string format, T1? arg1, T2? arg2, T3? arg3, T4? arg4, T5? arg5, T6? arg6, T7? arg7, T8? arg8)
        => FormatCore(new FormatArguments8<T1, T2, T3, T4, T5, T6, T7, T8>(arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8), format);

    /// <summary>
    /// Concatenates two values into one string, like <see cref="string.Concat(object, object)"/> but
    /// without boxing or an intermediate array: the values are written into a pooled builder through
    /// their concrete types.
    /// </summary>
    /// <typeparam name="T1">The first value's type.</typeparam>
    /// <typeparam name="T2">The second value's type.</typeparam>
    /// <param name="arg1">The first value; <c>null</c> contributes nothing.</param>
    /// <param name="arg2">The second value; <c>null</c> contributes nothing.</param>
    /// <returns>The concatenation of both values.</returns>
    /// <remarks>
    /// Values are converted with their <see cref="object.ToString"/> (strings are appended directly,
    /// with no intermediate copy). The helper borrows one builder from <see cref="Instance"/> for the
    /// call; the return is atomic with the conversion, so a failure can never leak the builder. Every
    /// arity up to eight arguments is provided.
    /// </remarks>
    public static string Concat<T1, T2>(T1? arg1, T2? arg2)
        => ConcatCore(new FormatArguments2<T1, T2>(arg1, arg2));

    /// <summary>Concatenates three values into one string, without boxing or intermediate arrays.</summary>
    /// <typeparam name="T1">The first value's type.</typeparam>
    /// <typeparam name="T2">The second value's type.</typeparam>
    /// <typeparam name="T3">The third value's type.</typeparam>
    /// <param name="arg1">The first value; <c>null</c> contributes nothing.</param>
    /// <param name="arg2">The second value; <c>null</c> contributes nothing.</param>
    /// <param name="arg3">The third value; <c>null</c> contributes nothing.</param>
    /// <returns>The concatenation of all values.</returns>
    /// <remarks>See the two-argument overload for conversion and borrowing semantics.</remarks>
    public static string Concat<T1, T2, T3>(T1? arg1, T2? arg2, T3? arg3)
        => ConcatCore(new FormatArguments3<T1, T2, T3>(arg1, arg2, arg3));

    /// <summary>Concatenates four values into one string, without boxing or intermediate arrays.</summary>
    /// <typeparam name="T1">The first value's type.</typeparam>
    /// <typeparam name="T2">The second value's type.</typeparam>
    /// <typeparam name="T3">The third value's type.</typeparam>
    /// <typeparam name="T4">The fourth value's type.</typeparam>
    /// <param name="arg1">The first value; <c>null</c> contributes nothing.</param>
    /// <param name="arg2">The second value; <c>null</c> contributes nothing.</param>
    /// <param name="arg3">The third value; <c>null</c> contributes nothing.</param>
    /// <param name="arg4">The fourth value; <c>null</c> contributes nothing.</param>
    /// <returns>The concatenation of all values.</returns>
    /// <remarks>See the two-argument overload for conversion and borrowing semantics.</remarks>
    public static string Concat<T1, T2, T3, T4>(T1? arg1, T2? arg2, T3? arg3, T4? arg4)
        => ConcatCore(new FormatArguments4<T1, T2, T3, T4>(arg1, arg2, arg3, arg4));

    /// <summary>Concatenates five values into one string, without boxing or intermediate arrays.</summary>
    /// <typeparam name="T1">The first value's type.</typeparam>
    /// <typeparam name="T2">The second value's type.</typeparam>
    /// <typeparam name="T3">The third value's type.</typeparam>
    /// <typeparam name="T4">The fourth value's type.</typeparam>
    /// <typeparam name="T5">The fifth value's type.</typeparam>
    /// <param name="arg1">The first value; <c>null</c> contributes nothing.</param>
    /// <param name="arg2">The second value; <c>null</c> contributes nothing.</param>
    /// <param name="arg3">The third value; <c>null</c> contributes nothing.</param>
    /// <param name="arg4">The fourth value; <c>null</c> contributes nothing.</param>
    /// <param name="arg5">The fifth value; <c>null</c> contributes nothing.</param>
    /// <returns>The concatenation of all values.</returns>
    /// <remarks>See the two-argument overload for conversion and borrowing semantics.</remarks>
    public static string Concat<T1, T2, T3, T4, T5>(T1? arg1, T2? arg2, T3? arg3, T4? arg4, T5? arg5)
        => ConcatCore(new FormatArguments5<T1, T2, T3, T4, T5>(arg1, arg2, arg3, arg4, arg5));

    /// <summary>Concatenates six values into one string, without boxing or intermediate arrays.</summary>
    /// <typeparam name="T1">The first value's type.</typeparam>
    /// <typeparam name="T2">The second value's type.</typeparam>
    /// <typeparam name="T3">The third value's type.</typeparam>
    /// <typeparam name="T4">The fourth value's type.</typeparam>
    /// <typeparam name="T5">The fifth value's type.</typeparam>
    /// <typeparam name="T6">The sixth value's type.</typeparam>
    /// <param name="arg1">The first value; <c>null</c> contributes nothing.</param>
    /// <param name="arg2">The second value; <c>null</c> contributes nothing.</param>
    /// <param name="arg3">The third value; <c>null</c> contributes nothing.</param>
    /// <param name="arg4">The fourth value; <c>null</c> contributes nothing.</param>
    /// <param name="arg5">The fifth value; <c>null</c> contributes nothing.</param>
    /// <param name="arg6">The sixth value; <c>null</c> contributes nothing.</param>
    /// <returns>The concatenation of all values.</returns>
    /// <remarks>See the two-argument overload for conversion and borrowing semantics.</remarks>
    public static string Concat<T1, T2, T3, T4, T5, T6>(T1? arg1, T2? arg2, T3? arg3, T4? arg4, T5? arg5, T6? arg6)
        => ConcatCore(new FormatArguments6<T1, T2, T3, T4, T5, T6>(arg1, arg2, arg3, arg4, arg5, arg6));

    /// <summary>Concatenates seven values into one string, without boxing or intermediate arrays.</summary>
    /// <typeparam name="T1">The first value's type.</typeparam>
    /// <typeparam name="T2">The second value's type.</typeparam>
    /// <typeparam name="T3">The third value's type.</typeparam>
    /// <typeparam name="T4">The fourth value's type.</typeparam>
    /// <typeparam name="T5">The fifth value's type.</typeparam>
    /// <typeparam name="T6">The sixth value's type.</typeparam>
    /// <typeparam name="T7">The seventh value's type.</typeparam>
    /// <param name="arg1">The first value; <c>null</c> contributes nothing.</param>
    /// <param name="arg2">The second value; <c>null</c> contributes nothing.</param>
    /// <param name="arg3">The third value; <c>null</c> contributes nothing.</param>
    /// <param name="arg4">The fourth value; <c>null</c> contributes nothing.</param>
    /// <param name="arg5">The fifth value; <c>null</c> contributes nothing.</param>
    /// <param name="arg6">The sixth value; <c>null</c> contributes nothing.</param>
    /// <param name="arg7">The seventh value; <c>null</c> contributes nothing.</param>
    /// <returns>The concatenation of all values.</returns>
    /// <remarks>See the two-argument overload for conversion and borrowing semantics.</remarks>
    public static string Concat<T1, T2, T3, T4, T5, T6, T7>(T1? arg1, T2? arg2, T3? arg3, T4? arg4, T5? arg5, T6? arg6, T7? arg7)
        => ConcatCore(new FormatArguments7<T1, T2, T3, T4, T5, T6, T7>(arg1, arg2, arg3, arg4, arg5, arg6, arg7));

    /// <summary>Concatenates eight values into one string, without boxing or intermediate arrays.</summary>
    /// <typeparam name="T1">The first value's type.</typeparam>
    /// <typeparam name="T2">The second value's type.</typeparam>
    /// <typeparam name="T3">The third value's type.</typeparam>
    /// <typeparam name="T4">The fourth value's type.</typeparam>
    /// <typeparam name="T5">The fifth value's type.</typeparam>
    /// <typeparam name="T6">The sixth value's type.</typeparam>
    /// <typeparam name="T7">The seventh value's type.</typeparam>
    /// <typeparam name="T8">The eighth value's type.</typeparam>
    /// <param name="arg1">The first value; <c>null</c> contributes nothing.</param>
    /// <param name="arg2">The second value; <c>null</c> contributes nothing.</param>
    /// <param name="arg3">The third value; <c>null</c> contributes nothing.</param>
    /// <param name="arg4">The fourth value; <c>null</c> contributes nothing.</param>
    /// <param name="arg5">The fifth value; <c>null</c> contributes nothing.</param>
    /// <param name="arg6">The sixth value; <c>null</c> contributes nothing.</param>
    /// <param name="arg7">The seventh value; <c>null</c> contributes nothing.</param>
    /// <param name="arg8">The eighth value; <c>null</c> contributes nothing.</param>
    /// <returns>The concatenation of all values.</returns>
    /// <remarks>See the two-argument overload for conversion and borrowing semantics.</remarks>
    public static string Concat<T1, T2, T3, T4, T5, T6, T7, T8>(T1? arg1, T2? arg2, T3? arg3, T4? arg4, T5? arg5, T6? arg6, T7? arg7, T8? arg8)
        => ConcatCore(new FormatArguments8<T1, T2, T3, T4, T5, T6, T7, T8>(arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8));

    /// <summary>
    /// Joins the values with a <see cref="char"/> separator, like <c>string.Join</c> but generic and
    /// without a params array: the values are written into a pooled builder through their concrete type.
    /// </summary>
    /// <typeparam name="T">The values' type.</typeparam>
    /// <param name="separator">The character placed between consecutive values.</param>
    /// <param name="values">The values to join; <c>null</c> values render as empty segments.</param>
    /// <returns>The joined string, or an empty string when <paramref name="values"/> is empty.</returns>
    /// <remarks>
    /// The helper borrows one builder from <see cref="Instance"/> for the call; the return is atomic
    /// with the conversion, so a failure can never leak the builder.
    /// </remarks>
    public static string Join<T>(char separator, IEnumerable<T> values)
    {
        if (values is null)
        {
            throw new ArgumentNullException(nameof(values));
        }

        var pooled = Instance.Acquire();
        try
        {
            var builder = pooled.StringBuilder;
            builder.Clear();
            var first = true;
            foreach (var value in values)
            {
                if (!first)
                {
                    builder.Append(separator);
                }

                first = false;
                AppendValue(builder, value, null, 0);
            }

            return pooled.ToString();
        }
        finally
        {
            pooled.Dispose();
        }
    }

    /// <summary>
    /// Joins the values with a <see cref="string"/> separator, like
    /// <see cref="string.Join(string, IEnumerable{string})"/> but generic and without a params array.
    /// </summary>
    /// <typeparam name="T">The values' type.</typeparam>
    /// <param name="separator">The string placed between consecutive values; <c>null</c> joins with
    /// nothing between them, as <see cref="string.Join(string, IEnumerable{string})"/> does.</param>
    /// <param name="values">The values to join; <c>null</c> values render as empty segments.</param>
    /// <returns>The joined string, or an empty string when <paramref name="values"/> is empty.</returns>
    /// <remarks>See the character-separator overload for borrowing semantics.</remarks>
    public static string Join<T>(string? separator, IEnumerable<T> values)
    {
        if (values is null)
        {
            throw new ArgumentNullException(nameof(values));
        }

        var pooled = Instance.Acquire();
        try
        {
            var builder = pooled.StringBuilder;
            builder.Clear();
            var first = true;
            foreach (var value in values)
            {
                if (!first)
                {
                    builder.Append(separator);
                }

                first = false;
                AppendValue(builder, value, null, 0);
            }

            return pooled.ToString();
        }
        finally
        {
            pooled.Dispose();
        }
    }

    /// <summary>
    /// Renders one argument into a pooled builder: <see cref="string"/> values append directly, values
    /// with a format specifier and <see cref="IFormattable"/> support format through it, everything
    /// else appends through <see cref="object.ToString"/>. No <c>object[]</c> is built and no argument
    /// is boxed, because the value travels as its concrete type <typeparamref name="T"/>.
    /// </summary>
    private static void AppendValue<T>(StringBuilder builder, T? value, string? format, int alignment)
    {
        if (value is null)
        {
            if (alignment != 0)
            {
                AppendAligned(builder, string.Empty, alignment);
            }

            return;
        }

        if (value is string text)
        {
            AppendAligned(builder, text, alignment);
            return;
        }

        if (format is not null && value is IFormattable formattable)
        {
            AppendAligned(builder, formattable.ToString(format, null), alignment);
            return;
        }

        // The unconstrained receiver makes the compiler treat the ToString result as maybe-null; an
        // empty append is the no-op a null would have been.
        AppendAligned(builder, value.ToString() ?? string.Empty, alignment);
    }

    /// <summary>Appends <paramref name="text"/> honoring a <c>{index,alignment}</c> pad request.</summary>
    private static void AppendAligned(StringBuilder builder, string text, int alignment)
    {
        if (alignment == 0)
        {
            builder.Append(text);
            return;
        }

        // Positive alignment right-aligns (padding goes first), negative left-aligns; spaces are
        // emitted directly into the builder instead of padded copies of the text.
        var padding = (alignment > 0 ? alignment : -alignment) - text.Length;
        if (padding > 0 && alignment > 0)
        {
            builder.Append(' ', padding);
        }

        builder.Append(text);

        if (padding > 0 && alignment < 0)
        {
            builder.Append(' ', padding);
        }
    }

    private static string FormatCore<TArguments>(TArguments arguments, string format)
        where TArguments : IFormatArguments
    {
        if (format is null)
        {
            throw new ArgumentNullException(nameof(format));
        }

        var pooled = Instance.Acquire();
        try
        {
            var builder = pooled.StringBuilder;
            builder.Clear();
            FormatInto(builder, format, arguments);
            return pooled.ToString();
        }
        finally
        {
            pooled.Dispose();
        }
    }

    private static string ConcatCore<TArguments>(TArguments arguments)
        where TArguments : IFormatArguments
    {
        var pooled = Instance.Acquire();
        try
        {
            var builder = pooled.StringBuilder;
            builder.Clear();
            for (var index = 0; index < arguments.Count; index++)
            {
                arguments.Append(builder, index, null, 0);
            }

            return pooled.ToString();
        }
        finally
        {
            pooled.Dispose();
        }
    }

    /// <summary>
    /// Writes <paramref name="format"/> into <paramref name="builder"/>, resolving holes against
    /// <paramref name="arguments"/>. The grammar is the common <c>string.Format</c> subset:
    /// <c>{{</c>/<c>}}</c> escapes, <c>{index}</c>, <c>{index,alignment}</c> and
    /// <c>{index,alignment:format}</c>.
    /// </summary>
    private static void FormatInto<TArguments>(StringBuilder builder, string format, TArguments arguments)
        where TArguments : IFormatArguments
    {
        var position = 0;
        while (position < format.Length)
        {
            var c = format[position];
            if (c == '{')
            {
                if (position + 1 < format.Length && format[position + 1] == '{')
                {
                    builder.Append('{');
                    position += 2;
                    continue;
                }

                position = AppendHole(builder, format, position, arguments);
                continue;
            }

            if (c == '}')
            {
                if (position + 1 < format.Length && format[position + 1] == '}')
                {
                    builder.Append('}');
                    position += 2;
                    continue;
                }

                throw new FormatException(InputStringNotCorrectMessage);
            }

            builder.Append(c);
            position++;
        }
    }

    /// <summary>
    /// Parses one format hole starting at <paramref name="bracePosition"/> (a single <c>{</c>), appends
    /// the argument it names, and returns the position just past the hole's closing brace.
    /// </summary>
    private static int AppendHole<TArguments>(StringBuilder builder, string format, int bracePosition, TArguments arguments)
        where TArguments : IFormatArguments
    {
        var position = bracePosition + 1;

        var index = ParsePositiveInt(format, ref position);

        var alignment = 0;
        if (position < format.Length && format[position] == ',')
        {
            position++;
            var leftAlign = false;
            if (position < format.Length && format[position] == '-')
            {
                leftAlign = true;
                position++;
            }

            var magnitude = ParsePositiveInt(format, ref position);
            alignment = leftAlign ? -magnitude : magnitude;
        }

        string? spec = null;
        if (position < format.Length && format[position] == ':')
        {
            position++;
            var start = position;
            var depth = 1;
            while (position < format.Length)
            {
                var c = format[position];
                if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        break;
                    }
                }

                position++;
            }

            if (depth != 0)
            {
                throw new FormatException(InputStringNotCorrectMessage);
            }

            // An explicitly empty spec (":}") formats through IFormattable exactly like string.Format.
            spec = format.Substring(start, position - start);
        }

        if (position >= format.Length || format[position] != '}')
        {
            throw new FormatException(InputStringNotCorrectMessage);
        }

        if (index >= arguments.Count)
        {
            throw new FormatException(IndexOutOfRangeMessage);
        }

        arguments.Append(builder, index, spec, alignment);
        return position + 1;
    }

    /// <summary>Parses a run of decimal digits starting at <paramref name="position"/>, without allocating.</summary>
    private static int ParsePositiveInt(string format, ref int position)
    {
        long value = 0;
        var any = false;
        while (position < format.Length)
        {
            var c = format[position];
            if (c < '0' || c > '9')
            {
                break;
            }

            value = (value * 10) + (c - '0');
            if (value > int.MaxValue)
            {
                throw new FormatException(InputStringNotCorrectMessage);
            }

            any = true;
            position++;
        }

        if (!any)
        {
            throw new FormatException(InputStringNotCorrectMessage);
        }

        return (int)value;
    }

    private const string InputStringNotCorrectMessage = "Input string was not in a correct format.";
    private const string IndexOutOfRangeMessage = "Index (zero based) must be greater than or equal to zero and less than the size of the argument list.";

    /// <summary>
    /// The per-arity argument carrier the format helpers dispatch through: a struct, so the arguments
    /// reach the format core without boxing, and the <c>Append</c> switch runs as a constrained call.
    /// </summary>
    private interface IFormatArguments
    {
        int Count { get; }

        void Append(StringBuilder builder, int index, string? format, int alignment);
    }

    private readonly struct FormatArguments1<T1> : IFormatArguments
    {
        private readonly T1? _arg1;

        public FormatArguments1(T1? arg1) => _arg1 = arg1;

        public int Count => 1;

        public void Append(StringBuilder builder, int index, string? format, int alignment)
        {
            if (index == 0)
            {
                AppendValue(builder, _arg1, format, alignment);
            }
        }
    }

    private readonly struct FormatArguments2<T1, T2> : IFormatArguments
    {
        private readonly T1? _arg1;
        private readonly T2? _arg2;

        public FormatArguments2(T1? arg1, T2? arg2)
        {
            _arg1 = arg1;
            _arg2 = arg2;
        }

        public int Count => 2;

        public void Append(StringBuilder builder, int index, string? format, int alignment)
        {
            switch (index)
            {
                case 0: AppendValue(builder, _arg1, format, alignment); break;
                case 1: AppendValue(builder, _arg2, format, alignment); break;
            }
        }
    }

    private readonly struct FormatArguments3<T1, T2, T3> : IFormatArguments
    {
        private readonly T1? _arg1;
        private readonly T2? _arg2;
        private readonly T3? _arg3;

        public FormatArguments3(T1? arg1, T2? arg2, T3? arg3)
        {
            _arg1 = arg1;
            _arg2 = arg2;
            _arg3 = arg3;
        }

        public int Count => 3;

        public void Append(StringBuilder builder, int index, string? format, int alignment)
        {
            switch (index)
            {
                case 0: AppendValue(builder, _arg1, format, alignment); break;
                case 1: AppendValue(builder, _arg2, format, alignment); break;
                case 2: AppendValue(builder, _arg3, format, alignment); break;
            }
        }
    }

    private readonly struct FormatArguments4<T1, T2, T3, T4> : IFormatArguments
    {
        private readonly T1? _arg1;
        private readonly T2? _arg2;
        private readonly T3? _arg3;
        private readonly T4? _arg4;

        public FormatArguments4(T1? arg1, T2? arg2, T3? arg3, T4? arg4)
        {
            _arg1 = arg1;
            _arg2 = arg2;
            _arg3 = arg3;
            _arg4 = arg4;
        }

        public int Count => 4;

        public void Append(StringBuilder builder, int index, string? format, int alignment)
        {
            switch (index)
            {
                case 0: AppendValue(builder, _arg1, format, alignment); break;
                case 1: AppendValue(builder, _arg2, format, alignment); break;
                case 2: AppendValue(builder, _arg3, format, alignment); break;
                case 3: AppendValue(builder, _arg4, format, alignment); break;
            }
        }
    }

    private readonly struct FormatArguments5<T1, T2, T3, T4, T5> : IFormatArguments
    {
        private readonly T1? _arg1;
        private readonly T2? _arg2;
        private readonly T3? _arg3;
        private readonly T4? _arg4;
        private readonly T5? _arg5;

        public FormatArguments5(T1? arg1, T2? arg2, T3? arg3, T4? arg4, T5? arg5)
        {
            _arg1 = arg1;
            _arg2 = arg2;
            _arg3 = arg3;
            _arg4 = arg4;
            _arg5 = arg5;
        }

        public int Count => 5;

        public void Append(StringBuilder builder, int index, string? format, int alignment)
        {
            switch (index)
            {
                case 0: AppendValue(builder, _arg1, format, alignment); break;
                case 1: AppendValue(builder, _arg2, format, alignment); break;
                case 2: AppendValue(builder, _arg3, format, alignment); break;
                case 3: AppendValue(builder, _arg4, format, alignment); break;
                case 4: AppendValue(builder, _arg5, format, alignment); break;
            }
        }
    }

    private readonly struct FormatArguments6<T1, T2, T3, T4, T5, T6> : IFormatArguments
    {
        private readonly T1? _arg1;
        private readonly T2? _arg2;
        private readonly T3? _arg3;
        private readonly T4? _arg4;
        private readonly T5? _arg5;
        private readonly T6? _arg6;

        public FormatArguments6(T1? arg1, T2? arg2, T3? arg3, T4? arg4, T5? arg5, T6? arg6)
        {
            _arg1 = arg1;
            _arg2 = arg2;
            _arg3 = arg3;
            _arg4 = arg4;
            _arg5 = arg5;
            _arg6 = arg6;
        }

        public int Count => 6;

        public void Append(StringBuilder builder, int index, string? format, int alignment)
        {
            switch (index)
            {
                case 0: AppendValue(builder, _arg1, format, alignment); break;
                case 1: AppendValue(builder, _arg2, format, alignment); break;
                case 2: AppendValue(builder, _arg3, format, alignment); break;
                case 3: AppendValue(builder, _arg4, format, alignment); break;
                case 4: AppendValue(builder, _arg5, format, alignment); break;
                case 5: AppendValue(builder, _arg6, format, alignment); break;
            }
        }
    }

    private readonly struct FormatArguments7<T1, T2, T3, T4, T5, T6, T7> : IFormatArguments
    {
        private readonly T1? _arg1;
        private readonly T2? _arg2;
        private readonly T3? _arg3;
        private readonly T4? _arg4;
        private readonly T5? _arg5;
        private readonly T6? _arg6;
        private readonly T7? _arg7;

        public FormatArguments7(T1? arg1, T2? arg2, T3? arg3, T4? arg4, T5? arg5, T6? arg6, T7? arg7)
        {
            _arg1 = arg1;
            _arg2 = arg2;
            _arg3 = arg3;
            _arg4 = arg4;
            _arg5 = arg5;
            _arg6 = arg6;
            _arg7 = arg7;
        }

        public int Count => 7;

        public void Append(StringBuilder builder, int index, string? format, int alignment)
        {
            switch (index)
            {
                case 0: AppendValue(builder, _arg1, format, alignment); break;
                case 1: AppendValue(builder, _arg2, format, alignment); break;
                case 2: AppendValue(builder, _arg3, format, alignment); break;
                case 3: AppendValue(builder, _arg4, format, alignment); break;
                case 4: AppendValue(builder, _arg5, format, alignment); break;
                case 5: AppendValue(builder, _arg6, format, alignment); break;
                case 6: AppendValue(builder, _arg7, format, alignment); break;
            }
        }
    }

    private readonly struct FormatArguments8<T1, T2, T3, T4, T5, T6, T7, T8> : IFormatArguments
    {
        private readonly T1? _arg1;
        private readonly T2? _arg2;
        private readonly T3? _arg3;
        private readonly T4? _arg4;
        private readonly T5? _arg5;
        private readonly T6? _arg6;
        private readonly T7? _arg7;
        private readonly T8? _arg8;

        public FormatArguments8(T1? arg1, T2? arg2, T3? arg3, T4? arg4, T5? arg5, T6? arg6, T7? arg7, T8? arg8)
        {
            _arg1 = arg1;
            _arg2 = arg2;
            _arg3 = arg3;
            _arg4 = arg4;
            _arg5 = arg5;
            _arg6 = arg6;
            _arg7 = arg7;
            _arg8 = arg8;
        }

        public int Count => 8;

        public void Append(StringBuilder builder, int index, string? format, int alignment)
        {
            switch (index)
            {
                case 0: AppendValue(builder, _arg1, format, alignment); break;
                case 1: AppendValue(builder, _arg2, format, alignment); break;
                case 2: AppendValue(builder, _arg3, format, alignment); break;
                case 3: AppendValue(builder, _arg4, format, alignment); break;
                case 4: AppendValue(builder, _arg5, format, alignment); break;
                case 5: AppendValue(builder, _arg6, format, alignment); break;
                case 6: AppendValue(builder, _arg7, format, alignment); break;
                case 7: AppendValue(builder, _arg8, format, alignment); break;
            }
        }
    }
}
