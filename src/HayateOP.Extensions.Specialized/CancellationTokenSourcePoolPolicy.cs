using DotNetCore.HayateOP.Policies;

namespace DotNetCore.HayateOP.Specialized;

/// <summary>
/// The policy behind <see cref="CancellationTokenSourcePool"/>: creates sources bound to the pool,
/// refuses to park a source that has been canceled, and resets a returned source so it can serve an
/// unrelated rental.
/// </summary>
/// <remarks>
/// The reset is the interesting half, and it is what makes the pool worth having: a
/// <see cref="System.Threading.CancellationTokenSource"/> that has been used carries the timer and the
/// callbacks its renter left behind, and handing it to the next renter unchanged would let the previous
/// renter's cancellation reach the new one. <see cref="Validate"/> rejects a canceled source before the
/// return path can park it — a source whose cancellation has been requested can never be made fit again —
/// and <see cref="OnRelease"/> then performs the reset that drops the callbacks and disarms the timer. On
/// a runtime without <c>CancellationTokenSource.TryReset()</c> — that is, .NET Framework 4.8 — that reset
/// cannot be performed at all, so <see cref="OnRelease"/> answers <c>false</c> and every returned source
/// is destroyed: the pool retains nothing and the next rental creates a fresh source. The pool reports
/// that through <see cref="CancellationTokenSourcePool.IsReuseSupported"/> rather than leaving it to be
/// discovered.
/// </remarks>
internal sealed class CancellationTokenSourcePoolPolicy : IHayateObjectPolicy<PooledCancellationTokenSource>
{
    private readonly CancellationTokenSourcePool _owner;

    public CancellationTokenSourcePoolPolicy(CancellationTokenSourcePool owner)
    {
        _owner = owner;
    }

    public PooledCancellationTokenSource Create()
    {
        var source = new PooledCancellationTokenSource();
        source.BindOwner(_owner);
        return source;
    }

    public bool OnRelease(PooledCancellationTokenSource item)
    {
        // The reset is the acceptance decision: a source this runtime cannot clean must not be parked.
        return CancellationTokenSourceReset.TryReset(item);
    }

    public bool Validate(PooledCancellationTokenSource item)
    {
        // A source whose cancellation has been requested can never serve another rental, and no reset can
        // bring it back — so it is destroyed on return rather than parked.
        return !item.IsCancellationRequested;
    }

    public void OnAcquire(PooledCancellationTokenSource item) => item.OnBorrowed();

    public void OnPassivate(PooledCancellationTokenSource item)
    {
        // Drop the upstream link first: it belongs to the rental that just ended, and disposing the
        // registration waits out an upstream callback that is still running. Marking the source idle is
        // what makes a dispose after a manual Release a no-op instead of a second return.
        item.UnlinkUpstream();
        item.MarkIdle();
    }

    public void OnDestroy(PooledCancellationTokenSource item) => item.OnDetachedFromPool();
}
