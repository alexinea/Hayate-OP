using System;
using System.Text;
using System.Threading;

namespace DotNetCore.HayateOP.Specialized;

/// <summary>
/// One <see cref="StringBuilder"/> shared by everyone who wants one, guarded by a single lock — the
/// thread-safe shared builder P89OP used to ship alongside its specialized pools.
/// </summary>
/// <remarks>
/// There is exactly one backing builder: acquiring it blocks every other acquirer until the holder
/// disposes, which is the deliberate trade this type makes. Where a pool hands each borrower its own
/// builder, this type hands every borrower <i>the</i> builder — zero per-use allocation beyond the
/// resulting string, at the price of serialising all users behind one lock.<br />
/// The lock boundary is <b>multi-writer, single-holder</b>: writes from one holder at a time are
/// serialized, and the reader's <see cref="SharedStringBuilderScope.ToString"/> snapshot is taken under
/// the same lock, so no writer can interleave in the middle of a snapshot and no reader can observe a
/// half-written buffer. Hold scopes briefly and never across an <c>await</c>; a long-held scope stalls
/// every other user of the shared builder.<br />
/// Disposal clears the builder and releases the lock, exactly once (a second <see cref="SharedStringBuilderScope.Dispose"/>
/// is a no-op), so the next acquirer starts from an empty builder.
/// </remarks>
/// <example>
/// <code>
/// var text = SharedStringBuilder.Build(sb => sb.Append("order: ").Append(orderId));
///
/// using var scope = SharedStringBuilder.Acquire();
/// scope.StringBuilder.Append("hello").Append(' ').Append("world");
/// var greeting = scope.ToString();   // lock released and builder cleared at the end of the block
/// </code>
/// </example>
public static class SharedStringBuilder
{
    /// <summary>
    /// The initial capacity of the shared builder: 4096 characters, the same default tier the specialized
    /// pools use.
    /// </summary>
    public const int DefaultCapacity = 4 * 1024;

    private static readonly StringBuilder Shared = new(DefaultCapacity);
    private static readonly object Gate = new();

    /// <summary>
    /// Acquires the shared builder, blocking until the previous holder has released it.
    /// </summary>
    /// <returns>A scope granting exclusive access to the shared builder; dispose it to clear the builder
    /// and release the lock.</returns>
    public static SharedStringBuilderScope Acquire()
    {
        Monitor.Enter(Gate);
        try
        {
            return new SharedStringBuilderScope(Shared, Gate);
        }
        catch
        {
            // The scope could not be produced; never leak the lock.
            Monitor.Exit(Gate);
            throw;
        }
    }

    /// <summary>
    /// Runs <paramref name="append"/> against the shared builder under the lock and returns the resulting
    /// string, clearing the builder before releasing the lock.
    /// </summary>
    /// <param name="append">The callback that writes into the shared builder.</param>
    /// <returns>The builder's content after <paramref name="append"/> ran.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="append"/> is <c>null</c>.</exception>
    public static string Build(Action<StringBuilder> append)
    {
        if (append is null) throw new ArgumentNullException(nameof(append));

        Monitor.Enter(Gate);
        try
        {
            append(Shared);
            return Shared.ToString();
        }
        finally
        {
            Shared.Clear();
            Monitor.Exit(Gate);
        }
    }

    /// <summary>
    /// Formats a composite format string with the given argument against the shared builder (Z6): <c>string.Format</c> semantics without boxing the argument into an <c>object[]</c>: the value is written through its concrete type, with Z4a's direct-write
    /// paths for the built-in primitives.
    /// </summary>
    /// <typeparam name="T1">The argument's type.</typeparam>
    /// <param name="arg1">The value for hole <c>{0}</c>; <c>null</c> renders as empty.</param>
    /// <param name="format">A composite format string (<c>{index}</c>, <c>{index,alignment}</c>,
    /// <c>{index,alignment:spec}</c> holes, <c>{{</c>/<c>}}</c> escapes).</param>
    /// <returns>The formatted string.</returns>
    /// <remarks>
    /// The lock discipline is exactly <see cref="Build(Action{StringBuilder})"/>'s: the write and the
    /// snapshot happen under the lock, and the builder is cleared before the lock is released - on
    /// failure paths too. See the one-argument overload for grammar and culture semantics.
    /// </remarks>
    public static string Build<T1>(string format, T1? arg1)
    {
        Monitor.Enter(Gate);
        try
        {
            return StringBuilderFormatCore.BuildStatic(Shared, new StringBuilderFormatCore.FormatArguments1<T1>(arg1), format);
        }
        finally
        {
            Shared.Clear();
            Monitor.Exit(Gate);
        }
    }


    /// <summary>
    /// Formats a composite format string with 2 arguments against the shared builder (Z6), without an <c>object[]</c> or boxing: the value is written through its concrete type, with Z4a's direct-write
    /// paths for the built-in primitives.
    /// </summary>
    /// <typeparam name="T1">The first argument's type.</typeparam>
    /// <typeparam name="T2">The second argument's type.</typeparam>
    /// <param name="arg1">The value for hole <c>{0}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg2">The value for hole <c>{1}</c>; <c>null</c> renders as empty.</param>
    /// <param name="format">A composite format string (<c>{index}</c>, <c>{index,alignment}</c>,
    /// <c>{index,alignment:spec}</c> holes, <c>{{</c>/<c>}}</c> escapes).</param>
    /// <returns>The formatted string.</returns>
    /// <remarks>
    /// The lock discipline is exactly <see cref="Build(Action{StringBuilder})"/>'s: the write and the
    /// snapshot happen under the lock, and the builder is cleared before the lock is released - on
    /// failure paths too. See the one-argument overload for grammar and culture semantics.
    /// </remarks>
    public static string Build<T1, T2>(string format, T1? arg1, T2? arg2)
    {
        Monitor.Enter(Gate);
        try
        {
            return StringBuilderFormatCore.BuildStatic(Shared, new StringBuilderFormatCore.FormatArguments2<T1, T2>(arg1, arg2), format);
        }
        finally
        {
            Shared.Clear();
            Monitor.Exit(Gate);
        }
    }


    /// <summary>
    /// Formats a composite format string with 3 arguments against the shared builder (Z6), without an <c>object[]</c> or boxing: the value is written through its concrete type, with Z4a's direct-write
    /// paths for the built-in primitives.
    /// </summary>
    /// <typeparam name="T1">The first argument's type.</typeparam>
    /// <typeparam name="T2">The second argument's type.</typeparam>
    /// <typeparam name="T3">The third argument's type.</typeparam>
    /// <param name="arg1">The value for hole <c>{0}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg2">The value for hole <c>{1}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg3">The value for hole <c>{2}</c>; <c>null</c> renders as empty.</param>
    /// <param name="format">A composite format string (<c>{index}</c>, <c>{index,alignment}</c>,
    /// <c>{index,alignment:spec}</c> holes, <c>{{</c>/<c>}}</c> escapes).</param>
    /// <returns>The formatted string.</returns>
    /// <remarks>
    /// The lock discipline is exactly <see cref="Build(Action{StringBuilder})"/>'s: the write and the
    /// snapshot happen under the lock, and the builder is cleared before the lock is released - on
    /// failure paths too. See the one-argument overload for grammar and culture semantics.
    /// </remarks>
    public static string Build<T1, T2, T3>(string format, T1? arg1, T2? arg2, T3? arg3)
    {
        Monitor.Enter(Gate);
        try
        {
            return StringBuilderFormatCore.BuildStatic(Shared, new StringBuilderFormatCore.FormatArguments3<T1, T2, T3>(arg1, arg2, arg3), format);
        }
        finally
        {
            Shared.Clear();
            Monitor.Exit(Gate);
        }
    }


    /// <summary>
    /// Formats a composite format string with 4 arguments against the shared builder (Z6), without an <c>object[]</c> or boxing: the value is written through its concrete type, with Z4a's direct-write
    /// paths for the built-in primitives.
    /// </summary>
    /// <typeparam name="T1">The first argument's type.</typeparam>
    /// <typeparam name="T2">The second argument's type.</typeparam>
    /// <typeparam name="T3">The third argument's type.</typeparam>
    /// <typeparam name="T4">The fourth argument's type.</typeparam>
    /// <param name="arg1">The value for hole <c>{0}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg2">The value for hole <c>{1}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg3">The value for hole <c>{2}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg4">The value for hole <c>{3}</c>; <c>null</c> renders as empty.</param>
    /// <param name="format">A composite format string (<c>{index}</c>, <c>{index,alignment}</c>,
    /// <c>{index,alignment:spec}</c> holes, <c>{{</c>/<c>}}</c> escapes).</param>
    /// <returns>The formatted string.</returns>
    /// <remarks>
    /// The lock discipline is exactly <see cref="Build(Action{StringBuilder})"/>'s: the write and the
    /// snapshot happen under the lock, and the builder is cleared before the lock is released - on
    /// failure paths too. See the one-argument overload for grammar and culture semantics.
    /// </remarks>
    public static string Build<T1, T2, T3, T4>(string format, T1? arg1, T2? arg2, T3? arg3, T4? arg4)
    {
        Monitor.Enter(Gate);
        try
        {
            return StringBuilderFormatCore.BuildStatic(Shared, new StringBuilderFormatCore.FormatArguments4<T1, T2, T3, T4>(arg1, arg2, arg3, arg4), format);
        }
        finally
        {
            Shared.Clear();
            Monitor.Exit(Gate);
        }
    }


    /// <summary>
    /// Formats a composite format string with 5 arguments against the shared builder (Z6), without an <c>object[]</c> or boxing: the value is written through its concrete type, with Z4a's direct-write
    /// paths for the built-in primitives.
    /// </summary>
    /// <typeparam name="T1">The first argument's type.</typeparam>
    /// <typeparam name="T2">The second argument's type.</typeparam>
    /// <typeparam name="T3">The third argument's type.</typeparam>
    /// <typeparam name="T4">The fourth argument's type.</typeparam>
    /// <typeparam name="T5">The fifth argument's type.</typeparam>
    /// <param name="arg1">The value for hole <c>{0}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg2">The value for hole <c>{1}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg3">The value for hole <c>{2}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg4">The value for hole <c>{3}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg5">The value for hole <c>{4}</c>; <c>null</c> renders as empty.</param>
    /// <param name="format">A composite format string (<c>{index}</c>, <c>{index,alignment}</c>,
    /// <c>{index,alignment:spec}</c> holes, <c>{{</c>/<c>}}</c> escapes).</param>
    /// <returns>The formatted string.</returns>
    /// <remarks>
    /// The lock discipline is exactly <see cref="Build(Action{StringBuilder})"/>'s: the write and the
    /// snapshot happen under the lock, and the builder is cleared before the lock is released - on
    /// failure paths too. See the one-argument overload for grammar and culture semantics.
    /// </remarks>
    public static string Build<T1, T2, T3, T4, T5>(string format, T1? arg1, T2? arg2, T3? arg3, T4? arg4, T5? arg5)
    {
        Monitor.Enter(Gate);
        try
        {
            return StringBuilderFormatCore.BuildStatic(Shared, new StringBuilderFormatCore.FormatArguments5<T1, T2, T3, T4, T5>(arg1, arg2, arg3, arg4, arg5), format);
        }
        finally
        {
            Shared.Clear();
            Monitor.Exit(Gate);
        }
    }


    /// <summary>
    /// Formats a composite format string with 6 arguments against the shared builder (Z6), without an <c>object[]</c> or boxing: the value is written through its concrete type, with Z4a's direct-write
    /// paths for the built-in primitives.
    /// </summary>
    /// <typeparam name="T1">The first argument's type.</typeparam>
    /// <typeparam name="T2">The second argument's type.</typeparam>
    /// <typeparam name="T3">The third argument's type.</typeparam>
    /// <typeparam name="T4">The fourth argument's type.</typeparam>
    /// <typeparam name="T5">The fifth argument's type.</typeparam>
    /// <typeparam name="T6">The sixth argument's type.</typeparam>
    /// <param name="arg1">The value for hole <c>{0}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg2">The value for hole <c>{1}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg3">The value for hole <c>{2}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg4">The value for hole <c>{3}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg5">The value for hole <c>{4}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg6">The value for hole <c>{5}</c>; <c>null</c> renders as empty.</param>
    /// <param name="format">A composite format string (<c>{index}</c>, <c>{index,alignment}</c>,
    /// <c>{index,alignment:spec}</c> holes, <c>{{</c>/<c>}}</c> escapes).</param>
    /// <returns>The formatted string.</returns>
    /// <remarks>
    /// The lock discipline is exactly <see cref="Build(Action{StringBuilder})"/>'s: the write and the
    /// snapshot happen under the lock, and the builder is cleared before the lock is released - on
    /// failure paths too. See the one-argument overload for grammar and culture semantics.
    /// </remarks>
    public static string Build<T1, T2, T3, T4, T5, T6>(string format, T1? arg1, T2? arg2, T3? arg3, T4? arg4, T5? arg5, T6? arg6)
    {
        Monitor.Enter(Gate);
        try
        {
            return StringBuilderFormatCore.BuildStatic(Shared, new StringBuilderFormatCore.FormatArguments6<T1, T2, T3, T4, T5, T6>(arg1, arg2, arg3, arg4, arg5, arg6), format);
        }
        finally
        {
            Shared.Clear();
            Monitor.Exit(Gate);
        }
    }


    /// <summary>
    /// Formats a composite format string with 7 arguments against the shared builder (Z6), without an <c>object[]</c> or boxing: the value is written through its concrete type, with Z4a's direct-write
    /// paths for the built-in primitives.
    /// </summary>
    /// <typeparam name="T1">The first argument's type.</typeparam>
    /// <typeparam name="T2">The second argument's type.</typeparam>
    /// <typeparam name="T3">The third argument's type.</typeparam>
    /// <typeparam name="T4">The fourth argument's type.</typeparam>
    /// <typeparam name="T5">The fifth argument's type.</typeparam>
    /// <typeparam name="T6">The sixth argument's type.</typeparam>
    /// <typeparam name="T7">The seventh argument's type.</typeparam>
    /// <param name="arg1">The value for hole <c>{0}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg2">The value for hole <c>{1}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg3">The value for hole <c>{2}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg4">The value for hole <c>{3}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg5">The value for hole <c>{4}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg6">The value for hole <c>{5}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg7">The value for hole <c>{6}</c>; <c>null</c> renders as empty.</param>
    /// <param name="format">A composite format string (<c>{index}</c>, <c>{index,alignment}</c>,
    /// <c>{index,alignment:spec}</c> holes, <c>{{</c>/<c>}}</c> escapes).</param>
    /// <returns>The formatted string.</returns>
    /// <remarks>
    /// The lock discipline is exactly <see cref="Build(Action{StringBuilder})"/>'s: the write and the
    /// snapshot happen under the lock, and the builder is cleared before the lock is released - on
    /// failure paths too. See the one-argument overload for grammar and culture semantics.
    /// </remarks>
    public static string Build<T1, T2, T3, T4, T5, T6, T7>(string format, T1? arg1, T2? arg2, T3? arg3, T4? arg4, T5? arg5, T6? arg6, T7? arg7)
    {
        Monitor.Enter(Gate);
        try
        {
            return StringBuilderFormatCore.BuildStatic(Shared, new StringBuilderFormatCore.FormatArguments7<T1, T2, T3, T4, T5, T6, T7>(arg1, arg2, arg3, arg4, arg5, arg6, arg7), format);
        }
        finally
        {
            Shared.Clear();
            Monitor.Exit(Gate);
        }
    }


    /// <summary>
    /// Formats a composite format string with 8 arguments against the shared builder (Z6), without an <c>object[]</c> or boxing: the value is written through its concrete type, with Z4a's direct-write
    /// paths for the built-in primitives.
    /// </summary>
    /// <typeparam name="T1">The first argument's type.</typeparam>
    /// <typeparam name="T2">The second argument's type.</typeparam>
    /// <typeparam name="T3">The third argument's type.</typeparam>
    /// <typeparam name="T4">The fourth argument's type.</typeparam>
    /// <typeparam name="T5">The fifth argument's type.</typeparam>
    /// <typeparam name="T6">The sixth argument's type.</typeparam>
    /// <typeparam name="T7">The seventh argument's type.</typeparam>
    /// <typeparam name="T8">The eighth argument's type.</typeparam>
    /// <param name="arg1">The value for hole <c>{0}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg2">The value for hole <c>{1}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg3">The value for hole <c>{2}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg4">The value for hole <c>{3}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg5">The value for hole <c>{4}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg6">The value for hole <c>{5}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg7">The value for hole <c>{6}</c>; <c>null</c> renders as empty.</param>
    /// <param name="arg8">The value for hole <c>{7}</c>; <c>null</c> renders as empty.</param>
    /// <param name="format">A composite format string (<c>{index}</c>, <c>{index,alignment}</c>,
    /// <c>{index,alignment:spec}</c> holes, <c>{{</c>/<c>}}</c> escapes).</param>
    /// <returns>The formatted string.</returns>
    /// <remarks>
    /// The lock discipline is exactly <see cref="Build(Action{StringBuilder})"/>'s: the write and the
    /// snapshot happen under the lock, and the builder is cleared before the lock is released - on
    /// failure paths too. See the one-argument overload for grammar and culture semantics.
    /// </remarks>
    public static string Build<T1, T2, T3, T4, T5, T6, T7, T8>(string format, T1? arg1, T2? arg2, T3? arg3, T4? arg4, T5? arg5, T6? arg6, T7? arg7, T8? arg8)
    {
        Monitor.Enter(Gate);
        try
        {
            return StringBuilderFormatCore.BuildStatic(Shared, new StringBuilderFormatCore.FormatArguments8<T1, T2, T3, T4, T5, T6, T7, T8>(arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8), format);
        }
        finally
        {
            Shared.Clear();
            Monitor.Exit(Gate);
        }
    }
}
public sealed class SharedStringBuilderScope : IDisposable
{
    private StringBuilder? _builder;
    private object? _gate;

    internal SharedStringBuilderScope(StringBuilder builder, object gate)
    {
        _builder = builder;
        _gate = gate;
    }

    /// <summary>
    /// The shared builder, exclusively held for the lifetime of this scope.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The scope has been disposed — the builder has been
    /// cleared and handed to the next acquirer.</exception>
    public StringBuilder StringBuilder
    {
        get
        {
            var builder = _builder;
            if (builder is null)
            {
                throw new ObjectDisposedException(nameof(SharedStringBuilderScope),
                    "The shared string builder scope has ended; acquire a new scope to use the builder again.");
            }

            return builder;
        }
    }

    /// <summary>
    /// Returns a snapshot of the builder's current content. The snapshot is consistent: the scope still
    /// holds the lock, so no other writer can interleave.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The scope has been disposed.</exception>
    public override string ToString() => StringBuilder.ToString();

    /// <summary>
    /// Clears the builder and releases the lock. Called automatically at the end of a <c>using</c> block.
    /// </summary>
    /// <remarks>
    /// Happens exactly once; every later call is a no-op. Any exception thrown by the scope's body still
    /// passes through the <c>using</c> disposal, so the lock is released on failure paths too.
    /// </remarks>
    public void Dispose()
    {
        var gate = _gate;
        var builder = _builder;
        _builder = null;
        _gate = null;

        if (builder is not null && gate is not null)
        {
            builder.Clear();
            Monitor.Exit(gate);
        }
    }
}
