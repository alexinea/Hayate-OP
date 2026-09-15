using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

// The argument carriers and the parser live in the shared formatting core (also used by the
// SharedStringBuilder.Build overloads).
using static DotNetCore.HayateOP.Specialized.StringBuilderFormatCore;

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
    /// Borrows one builder from the shared instance, writes <paramref name="format"/> into it and
    /// returns the string; the return is atomic with the conversion, so a failure cannot leak the
    /// builder.
    /// </summary>
    private static string FormatCore<TArguments>(TArguments arguments, string format)
        where TArguments : StringBuilderFormatCore.IFormatArguments
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
            StringBuilderFormatCore.FormatInto(builder, format, arguments);
            return pooled.ToString();
        }
        finally
        {
            pooled.Dispose();
        }
    }

    /// <summary>
    /// Borrows one builder from the shared instance, appends every argument in order and returns the
    /// string; the return is atomic with the conversion, so a failure cannot leak the builder.
    /// </summary>
    private static string ConcatCore<TArguments>(TArguments arguments)
        where TArguments : StringBuilderFormatCore.IFormatArguments
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
}
