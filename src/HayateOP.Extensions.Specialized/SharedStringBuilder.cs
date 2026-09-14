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
}

/// <summary>
/// Exclusive access to the shared builder, taken from <see cref="SharedStringBuilder.Acquire"/>. Holding
/// it blocks every other acquirer; disposing it clears the builder and releases the lock.
/// </summary>
/// <remarks>
/// Disposal is once-only: a second <see cref="Dispose"/> is a no-op rather than a second lock release.
/// After disposal, <see cref="StringBuilder"/> and <see cref="ToString"/> throw
/// <see cref="ObjectDisposedException"/> — the builder belongs to the next acquirer.
/// </remarks>
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
