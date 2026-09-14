using System.IO;
using System.Threading;
using DotNetCore.HayateOP;

namespace DotNetCore.HayateOP.Specialized;

/// <summary>
/// A <see cref="MemoryStream"/> that knows the pool it came from: disposing it returns it to that pool
/// instead of closing it, so a borrow/return pair can be written with <c>using</c> exactly as P89OP's
/// <c>PooledMemoryStream</c> allows.
/// </summary>
/// <remarks>
/// Disposing a stream that sits in a <see cref="MemoryStreamPool"/> hands it back through the pool's
/// ordinary return path — validation, the capacity guard and the reset to an empty stream all behave
/// exactly as for a manual <c>Release</c> — and the stream stays open: the buffer is kept alive for the
/// next borrower, which is the whole point of pooling streams. The stream is closed for real only when
/// the pool itself destroys it (a returned stream that failed validation — most commonly one that grew
/// past the pool's maximum capacity — is destroyed, and the pool's destroy path calls the underlying
/// <see cref="MemoryStream.Dispose(bool)"/>).<br />
/// Disposal is once-per-borrow: a second <see cref="Dispose"/> of the same borrow is a no-op rather
/// than a duplicate return, because returning the same object twice would let two borrowers share it
/// later. After the pool has destroyed a stream, using it throws
/// <see cref="ObjectDisposedException"/>, like any closed stream.<br />
/// A stream created directly (never handed out by a pool) owns nobody and behaves like an ordinary
/// <see cref="MemoryStream"/>: disposing it closes it.
/// </remarks>
/// <example>
/// <code>
/// using var stream = MemoryStreamPool.Instance.GetObject();
/// stream.Write(payload, 0, payload.Length);   // returned to the pool at the end of the block
/// </code>
/// </example>
public class PooledMemoryStream : MemoryStream
{
    // The pool that created (and therefore owns) this stream; null once the pool destroys the stream or
    // when it was created standalone. Volatile so a disposing thread never routes a return through a
    // stale owner after the pool has detached the stream.
    private volatile IHayateObjectPool<PooledMemoryStream>? _owner;

    // 1 = the stream sits in the pool (or is about to), 0 = a borrower holds it. The CAS from 0 to 1 in
    // Dispose is the once-per-borrow guard: only the first dispose of a borrow performs the return.
    private int _idle = 1;

    /// <summary>
    /// Creates a standalone stream with the given initial capacity.
    /// </summary>
    /// <param name="capacity">The initial capacity of the backing buffer.</param>
    /// <remarks>
    /// A standalone stream is not registered with any pool; disposing it closes the buffer. Only
    /// <see cref="MemoryStreamPool"/> produces pooled instances.
    /// </remarks>
    public PooledMemoryStream(int capacity) : base(capacity)
    {
    }

    /// <summary>
    /// Creates a standalone stream with no reserved capacity; the buffer grows on first write.
    /// </summary>
    /// <remarks>
    /// The parameterless constructor exists so generic pool frameworks that demand a default
    /// constructor can handle the type (HayateOP's <see cref="HayatePoolBuilder{T}"/> requires one);
    /// <see cref="MemoryStreamPool"/> uses it and then reserves the configured minimum capacity.
    /// </remarks>
    public PooledMemoryStream()
    {
    }

    /// <summary>
    /// Disposing a pooled stream returns it to its pool (the stream stays open, ready for the next
    /// borrower); disposing a standalone stream closes the buffer. Called automatically at the end of a
    /// <c>using</c> block.
    /// </summary>
    /// <remarks>
    /// The return goes through the pool's ordinary return path, so capacity validation and the reset to
    /// an empty stream behave exactly as for a manual <c>Release</c> — and a stream that grew past the
    /// pool's maximum capacity is destroyed on return instead of being parked with a huge buffer. The
    /// return happens exactly once per borrow; every later call is a no-op.
    /// </remarks>
    protected override void Dispose(bool disposing)
    {
        var owner = _owner;
        if (owner is null)
        {
            // Standalone or detached by the pool: this is a real disposal, closing the buffer.
            base.Dispose(disposing);
            return;
        }

        // Once-per-borrow claim: the first dispose flips the state and returns the stream, later
        // disposals of the same borrow observe the flipped state and do nothing.
        if (Interlocked.CompareExchange(ref _idle, 1, 0) == 0)
        {
            // Deliberately not calling base.Dispose: a returned stream stays open — that is what makes
            // it reusable. Only the pool's destroy path (through OnDetachedFromPool) closes the buffer.
            owner.Release(this);
        }
    }

    /// <summary>
    /// Returns a string that represents the stream (position and length), as P89OP's wrapper does.
    /// </summary>
    public override string ToString() => $"{nameof(Position)}: {Position}, {nameof(Length)}: {Length}";

    /// <summary>Registers the owning pool; called by the pool's policy at creation time.</summary>
    internal void BindOwner(IHayateObjectPool<PooledMemoryStream> owner) => _owner = owner;

    /// <summary>Marks the stream as borrowed so the next dispose performs the return.</summary>
    internal void OnBorrowed() => Interlocked.Exchange(ref _idle, 0);

    /// <summary>
    /// Marks the stream as idle; called by the pool's policy on every successful return, including a
    /// manual <c>Release</c>, so a dispose after a manual release cannot route a second return.
    /// </summary>
    internal void MarkIdle() => Interlocked.Exchange(ref _idle, 1);

    /// <summary>
    /// Detaches the pool before the pool destroys the stream, so the disposal that follows is a real
    /// buffer disposal instead of another routed return.
    /// </summary>
    internal void OnDetachedFromPool() => _owner = null;
}
