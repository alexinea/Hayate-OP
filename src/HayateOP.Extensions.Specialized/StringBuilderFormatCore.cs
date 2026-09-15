using System;
using System.Collections.Generic;
using System.Text;

namespace DotNetCore.HayateOP.Specialized;

// The shared formatting core behind the generic no-params helpers: StringBuilderPool.Format/Concat/Join
// and the SharedStringBuilder.Build overloads (Z6) all parse the common string.Format grammar through
// this class, writing values via FormatWriter's direct paths. The argument carriers are structs, so
// the values travel without an object[] and without boxing.
internal static class StringBuilderFormatCore
{
    /// <summary>
    /// Renders one argument: strings append directly, net6+ <c>ISpanFormattable</c> values format
    /// straight into a scratch buffer (Z4a's direct write), everything else falls back to
    /// <see cref="IFormattable"/> or <see cref="object.ToString"/>. No <c>object[]</c> is built and
    /// no argument is boxed, because the value travels as its concrete generic type.
    /// </summary>
    internal static void AppendValue<T>(StringBuilder builder, T? value, string? format, int alignment)
        => FormatWriter.Append(builder, value, format, alignment);

    /// <summary>
    /// SharedStringBuilder's lock-path formatter (Z6): writes <paramref name="format"/> against the
    /// shared builder whose lock the caller holds, and returns the snapshot. The caller owns the lock
    /// discipline; this clears, parses, writes and snapshots.
    /// </summary>
    /// <param name="shared">The shared builder, held under the caller's lock.</param>
    /// <param name="arguments">The argument carrier for this arity.</param>
    /// <param name="format">A composite format string.</param>
    internal static string BuildStatic<TArguments>(StringBuilder shared, TArguments arguments, string format)
        where TArguments : IFormatArguments
    {
        if (format is null)
        {
            throw new ArgumentNullException(nameof(format));
        }

        shared.Clear();
        FormatInto(shared, format, arguments);
        return shared.ToString();
    }

    /// <summary>
    /// Writes <paramref name="format"/> into <paramref name="builder"/>, resolving holes against
    /// <paramref name="arguments"/>. The grammar is the common <c>string.Format</c> subset:
    /// <c>{{</c>/<c>}}</c> escapes, <c>{index}</c>, <c>{index,alignment}</c> and
    /// <c>{index,alignment:format}</c>.
    /// </summary>
    internal static void FormatInto<TArguments>(StringBuilder builder, string format, TArguments arguments)
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
    internal static int AppendHole<TArguments>(StringBuilder builder, string format, int bracePosition, TArguments arguments)
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
    internal static int ParsePositiveInt(string format, ref int position)
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

    internal const string InputStringNotCorrectMessage = "Input string was not in a correct format.";
    internal const string IndexOutOfRangeMessage = "Index (zero based) must be greater than or equal to zero and less than the size of the argument list.";

    /// <summary>
    /// The per-arity argument carrier the format helpers dispatch through: a struct, so the arguments
    /// reach the format core without boxing, and the <c>Append</c> switch runs as a constrained call.
    /// </summary>
    internal interface IFormatArguments
    {
        int Count { get; }

        void Append(StringBuilder builder, int index, string? format, int alignment);
    }

    internal readonly struct FormatArguments1<T1> : IFormatArguments
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

    internal readonly struct FormatArguments2<T1, T2> : IFormatArguments
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

    internal readonly struct FormatArguments3<T1, T2, T3> : IFormatArguments
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

    internal readonly struct FormatArguments4<T1, T2, T3, T4> : IFormatArguments
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

    internal readonly struct FormatArguments5<T1, T2, T3, T4, T5> : IFormatArguments
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

    internal readonly struct FormatArguments6<T1, T2, T3, T4, T5, T6> : IFormatArguments
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

    internal readonly struct FormatArguments7<T1, T2, T3, T4, T5, T6, T7> : IFormatArguments
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

    internal readonly struct FormatArguments8<T1, T2, T3, T4, T5, T6, T7, T8> : IFormatArguments
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
