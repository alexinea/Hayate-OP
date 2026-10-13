using System;
using System.Threading;

namespace DotNetCore.HayateOP.Specialized;

/// <summary>
/// A <see cref="CancellationTokenSource"/> that knows the pool it came from: disposing it returns it to
/// that pool instead of ending its life, so a rental can be written as
/// <c>using var cts = CancellationTokenSourcePool.Instance.Rent();</c>.
/// </summary>
/// <remarks>
/// Disposing a source that sits in a <see cref="CancellationTokenSourcePool"/> hands it back through the
/// pool's ordinary return path, so the cleanup a returned source needs — dropping the upstream link a
/// <see cref="CancellationTokenSourcePool.RentLinked(CancellationToken)"/> rental added, and the reset
/// that makes the source fit for an unrelated rental — behaves exactly as it does for a manual
/// <see cref="CancellationTokenSourcePool.Release"/>. The source stays alive: it is released for good
/// only when the pool destroys it, which happens for a canceled source, for any source returned on a
/// runtime that cannot reset one (see <see cref="CancellationTokenSourcePool.IsReuseSupported"/>), and
/// for a source that comes back after the pool has been cleared or disposed. Clearing or disposing the
/// pool drops the sources it holds rather than releasing them, and costs nothing here: a parked source
/// has already been reset — its timer disarmed and its callbacks dropped — so it holds nothing that
/// needs releasing, and it is collected like any other garbage.<br />
/// Disposal is once-per-rental: a second <c>Dispose()</c> of the same rental is a no-op rather
/// than a duplicate return, because returning the same source twice would let two renters share it later.
/// After the pool has destroyed a source, using it throws <see cref="ObjectDisposedException"/>, like any
/// disposed source.<br />
/// A source created directly (never handed out by a pool) owns nobody and behaves like an ordinary
/// <see cref="CancellationTokenSource"/>: disposing it releases it.
/// </remarks>
/// <example>
/// <code>
/// using var cts = CancellationTokenSourcePool.Instance.Rent();
/// var task = LongRunningAsync(cts.Token);   // the source is back in the pool after the block
/// </code>
/// </example>
public class PooledCancellationTokenSource : CancellationTokenSource
{
    // The pool that created (and therefore owns) this source; null once the pool destroys the source or
    // when it was created standalone. Volatile so a disposing thread never routes a return through a stale
    // owner after the pool has detached the source.
    private volatile CancellationTokenSourcePool? _owner;

    // 1 = the source sits in the pool (or is about to), 0 = a renter holds it. The CAS from 0 to 1 in
    // Dispose is the once-per-rental guard: only the first dispose of a rental performs the return.
    private int _idle = 1;

    // The registration that links this source to the token RentLinked was given; empty for a plain Rent.
    private CancellationTokenRegistration _upstreamRegistration;

    /// <summary>
    /// Creates a standalone source: it belongs to no pool, and disposing it releases it.
    /// </summary>
    /// <remarks>
    /// The parameterless constructor exists so the pool engine's builder — which requires a default
    /// constructor on the pooled type — can create the source; <see cref="CancellationTokenSourcePool"/>
    /// binds the owning pool immediately afterwards. Only that pool produces pooled instances.
    /// </remarks>
    public PooledCancellationTokenSource()
    {
    }

    /// <summary>
    /// Disposing a pooled source returns it to its pool (the source stays usable, ready for the next
    /// renter); disposing a standalone, pool-destroyed, or collected source releases it for good. Called
    /// automatically at the end of a <c>using</c> block.
    /// </summary>
    /// <remarks>
    /// The return goes through the pool's ordinary return path, so the upstream link is dropped and the
    /// reset that makes the source reusable runs exactly as for a manual
    /// <see cref="CancellationTokenSourcePool.Release"/>. The return happens exactly once per rental;
    /// every later call is a no-op.
    /// </remarks>
    /// <param name="disposing"><c>true</c> when called from <c>Dispose()</c>; <c>false</c> from the
    /// finalizer, where the source is unreachable and must be released rather than returned.</param>
    protected override void Dispose(bool disposing)
    {
        var owner = _owner;
        if (owner is null || !disposing)
        {
            // Standalone, detached by the pool, or collected while still rented: this is a real disposal.
            // The base class releases the timer here, which is why the finalizer path must reach it too —
            // returning a source nobody can reach would park it in the pool forever.
            base.Dispose(disposing);
            return;
        }

        // Once-per-rental claim: the first dispose flips the state and returns the source, later disposals
        // of the same rental observe the flipped state and do nothing.
        if (Interlocked.CompareExchange(ref _idle, 1, 0) == 0)
        {
            // Deliberately not calling base.Dispose: a returned source stays usable — that is what makes it
            // reusable. Only the pool's destroy path (through OnDetachedFromPool) releases it.
            owner.Release(this);
        }
    }

    /// <summary>
    /// Links this source to <paramref name="upstreamToken"/>, so canceling the upstream token cancels the
    /// source and everything listening to it. Called by
    /// <see cref="CancellationTokenSourcePool.RentLinked(CancellationToken)"/>.
    /// </summary>
    /// <param name="upstreamToken">The token to follow; a token that cannot be canceled is not registered.</param>
    internal void RegisterUpstream(CancellationToken upstreamToken)
    {
        // Register with useSynchronizationContext: false so the callback cannot be posted back to a UI
        // context the renter happened to be on. This overload exists on every target framework, so the
        // pool needs no per-framework fork for it (unlike the reset itself, see
        // CancellationTokenSourceReset).
        _upstreamRegistration = upstreamToken.Register(
            static state => ((CancellationTokenSource)state!).Cancel(),
            this,
            useSynchronizationContext: false);
    }

    /// <summary>Marks the source as rented so the next dispose performs the return.</summary>
    internal void OnBorrowed() => Interlocked.Exchange(ref _idle, 0);

    /// <summary>
    /// Marks the source as idle; called by the pool on every successful return, including a manual
    /// <see cref="CancellationTokenSourcePool.Release"/>, so a dispose after a manual release cannot route
    /// a second return.
    /// </summary>
    internal void MarkIdle() => Interlocked.Exchange(ref _idle, 1);

    /// <summary>
    /// Drops the upstream link before the source is parked or destroyed.
    /// </summary>
    /// <remarks>
    /// Disposing the registration waits for an upstream callback that is already running, so a source
    /// cannot be handed to the next renter while the previous renter's cancellation is still executing,
    /// and an upstream token that is canceled after the return can no longer reach the source.
    /// </remarks>
    internal void UnlinkUpstream()
    {
        var registration = _upstreamRegistration;
        _upstreamRegistration = default;
        registration.Dispose();
    }

    /// <summary>
    /// Detaches the pool before the pool destroys the source, so the disposal that follows is a real
    /// release instead of another routed return, and drops the upstream link for the same reason a return
    /// does.
    /// </summary>
    internal void OnDetachedFromPool()
    {
        _owner = null;
        UnlinkUpstream();
    }

    /// <summary>Registers the owning pool; called by the pool's policy at creation time.</summary>
    internal void BindOwner(CancellationTokenSourcePool owner) => _owner = owner;
}
