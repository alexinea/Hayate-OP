using DotNetCore.HayateOP.Policies;

namespace DotNetCore.HayateOP.Specialized;

/// <summary>
/// The policy behind <see cref="StringBuilderPool"/>: creates builders at the configured capacity, guards
/// the maximum capacity on the way back, and clears returned builders.
/// </summary>
internal sealed class StringBuilderPoolPolicy : IHayateObjectPolicy<PooledStringBuilder>
{
    private readonly StringBuilderPool _owner;

    public StringBuilderPoolPolicy(StringBuilderPool owner)
    {
        _owner = owner;
    }

    public PooledStringBuilder Create()
    {
        // Pre-sized through EnsureCapacity: the parameterless constructor is what the engine's builder
        // constraint demands, the reservation is what P89OP's factory does.
        var pooled = new PooledStringBuilder();
        pooled.StringBuilder.EnsureCapacity(_owner.MinimumStringBuilderCapacity);
        pooled.BindOwner(_owner);
        return pooled;
    }

    public bool OnRelease(PooledStringBuilder item) => true;

    public bool Validate(PooledStringBuilder item)
    {
        // The capacity guard, P89OP-aligned: builders that grew past the maximum are destroyed on return
        // instead of being parked with a huge character buffer. There is no minimum to enforce — a
        // builder's capacity never shrinks below its creation capacity.
        return item.StringBuilder.Capacity <= _owner.MaximumStringBuilderCapacity;
    }

    public void OnAcquire(PooledStringBuilder item)
    {
        item.OnBorrowed();
    }

    public void OnPassivate(PooledStringBuilder item)
    {
        item.MarkIdle();
        item.StringBuilder.Clear();
    }

    public void OnDestroy(PooledStringBuilder item)
    {
        item.OnDetachedFromPool();
    }
}
