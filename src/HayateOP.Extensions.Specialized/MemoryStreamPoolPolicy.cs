using DotNetCore.HayateOP.Policies;

namespace DotNetCore.HayateOP.Specialized;

/// <summary>
/// The policy behind <see cref="MemoryStreamPool"/>: creates streams at the configured minimum capacity,
/// guards the capacity window on the way back, and resets returned streams to empty.
/// </summary>
/// <remarks>
/// Each capacity tier the pool runs gets its own policy instance. The base tier uses
/// <c>tierCapacity = 0</c> and follows the pool's live minimum; a declared tier pins the capacity the
/// tier serves, so the tier creates streams at that size and accepts them back even when the tier
/// exceeds the pool's global maximum — the borrower declared the need, and parking the buffer is what
/// keeps a mixed-size workload from destroying and recreating oversized streams on every use.
/// </remarks>
internal sealed class MemoryStreamPoolPolicy : IHayateObjectPolicy<PooledMemoryStream>
{
    private readonly MemoryStreamPool _owner;

    // The capacity this tier creates streams at; 0 for the base tier, which follows the pool's live
    // minimum instead. Never negative.
    private readonly int _tierCapacity;

    // The engine pool this policy creates objects for; attached right after Build() returns, before the
    // tier is reachable for any acquire (see StringBuilderPoolPolicy for the full rationale).
    private IHayateObjectPool<PooledMemoryStream>? _engine;

    public MemoryStreamPoolPolicy(MemoryStreamPool owner, int tierCapacity = 0)
    {
        _owner = owner;
        _tierCapacity = tierCapacity;
    }

    /// <summary>Binds the engine pool created items are returned to; called right after the engine builds.</summary>
    internal void AttachEngine(IHayateObjectPool<PooledMemoryStream> engine) => _engine = engine;

    public PooledMemoryStream Create()
    {
        // Pre-sized through the Capacity setter: the parameterless constructor is what the engine's
        // builder constraint demands, the reservation is what P89OP's factory does. A declared tier
        // reserves its own capacity (which also covers a minimum that was raised past the tier after it
        // was created); the base tier reserves the live minimum.
        var stream = new PooledMemoryStream();
        var capacity = _tierCapacity > _owner.MinimumMemoryStreamCapacity
            ? _tierCapacity
            : _owner.MinimumMemoryStreamCapacity;
        stream.Capacity = capacity;
        stream.BindOwner(_engine ?? _owner);
        return stream;
    }

    public bool OnRelease(PooledMemoryStream item) => true;

    public bool Validate(PooledMemoryStream item)
    {
        // A borrower that disposed the underlying buffer really meant to destroy it: an unusable stream
        // must never be handed out again.
        if (!item.CanRead || !item.CanWrite || !item.CanSeek)
        {
            return false;
        }

        // The capacity window: streams that grew past the maximum (or sit below the minimum after a
        // configuration change) are destroyed on return instead of being parked with the wrong buffer.
        // A declared tier accepts streams up to its own declared size even when that is above the
        // global maximum, for the same reason the StringBuilder tier does.
        var maximum = _tierCapacity > _owner.MaximumMemoryStreamCapacity
            ? _tierCapacity
            : _owner.MaximumMemoryStreamCapacity;
        var capacity = item.Capacity;
        return capacity >= _owner.MinimumMemoryStreamCapacity && capacity <= maximum;
    }



    public void OnAcquire(PooledMemoryStream item)
    {
        item.OnBorrowed();
    }

    public void OnPassivate(PooledMemoryStream item)
    {
        item.MarkIdle();
        item.Position = 0;
        item.SetLength(0);
    }

    public void OnDestroy(PooledMemoryStream item)
    {
        // The pool disposes the stream right after this hook; detaching here makes that disposal a real
        // buffer disposal instead of another routed return.
        item.OnDetachedFromPool();
    }
}
