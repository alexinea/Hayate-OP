using DotNetCore.HayateOP.Policies;

namespace DotNetCore.HayateOP.Specialized;

/// <summary>
/// The policy behind <see cref="MemoryStreamPool"/>: creates streams at the configured minimum capacity,
/// guards the capacity window on the way back, and resets returned streams to empty.
/// </summary>
internal sealed class MemoryStreamPoolPolicy : IHayateObjectPolicy<PooledMemoryStream>
{
    private readonly MemoryStreamPool _owner;

    public MemoryStreamPoolPolicy(MemoryStreamPool owner)
    {
        _owner = owner;
    }

    public PooledMemoryStream Create()
    {
        // Pre-sized through the Capacity setter: the parameterless constructor is what the engine's
        // builder constraint demands, the reservation is what P89OP's factory does.
        var stream = new PooledMemoryStream();
        stream.Capacity = _owner.MinimumMemoryStreamCapacity;
        stream.BindOwner(_owner);
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
        var capacity = item.Capacity;
        return capacity >= _owner.MinimumMemoryStreamCapacity
            && capacity <= _owner.MaximumMemoryStreamCapacity;
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
