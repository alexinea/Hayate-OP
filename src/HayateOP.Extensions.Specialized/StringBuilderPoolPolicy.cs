using DotNetCore.HayateOP.Policies;

namespace DotNetCore.HayateOP.Specialized;

/// <summary>
/// The policy behind <see cref="StringBuilderPool"/>: creates builders at the configured capacity, guards
/// the maximum capacity on the way back, and clears returned builders.
/// </summary>
/// <remarks>
/// Each capacity tier the pool runs gets its own policy instance. The base tier uses
/// <c>tierCapacity = 0</c> and follows the pool's live minimum; a declared tier pins the capacity the
/// tier serves, so the tier creates builders at that size and accepts them back even when the tier
/// exceeds the pool's global maximum — the borrower declared the need, and parking the buffer is what
/// keeps a mixed-size workload from destroying and recreating oversized builders on every use.
/// </remarks>
internal sealed class StringBuilderPoolPolicy : IHayateObjectPolicy<PooledStringBuilder>
{
    private readonly StringBuilderPool _owner;

    // The capacity this tier creates builders at; 0 for the base tier, which follows the pool's live
    // minimum instead. Never negative.
    private readonly int _tierCapacity;

    // The engine pool this policy creates objects for. The base policy is built before its engine
    // exists, and each capacity tier builds its engine lazily, so the binding is attached right after
    // Build() returns — before the tier is reachable for any acquire, items always bind their real
    // engine and disposing routes the return to the tier that lent the builder out. Until the binding
    // lands, items bind the owning StringBuilderPool, whose Release delegates exactly as before.
    private IHayateObjectPool<PooledStringBuilder>? _engine;

    public StringBuilderPoolPolicy(StringBuilderPool owner, int tierCapacity = 0)
    {
        _owner = owner;
        _tierCapacity = tierCapacity;
    }

    /// <summary>Binds the engine pool created items are returned to; called right after the engine builds.</summary>
    internal void AttachEngine(IHayateObjectPool<PooledStringBuilder> engine) => _engine = engine;

    public PooledStringBuilder Create()
    {
        // Pre-sized through EnsureCapacity: the parameterless constructor is what the engine's builder
        // constraint demands, the reservation is what P89OP's factory does. A declared tier reserves its
        // own capacity (which also covers a minimum that was raised past the tier after it was created);
        // the base tier reserves the live minimum.
        var pooled = new PooledStringBuilder();
        var capacity = _tierCapacity > _owner.MinimumStringBuilderCapacity
            ? _tierCapacity
            : _owner.MinimumStringBuilderCapacity;
        pooled.StringBuilder.EnsureCapacity(capacity);
        pooled.BindOwner(_engine ?? _owner);
        return pooled;
    }

    public bool OnRelease(PooledStringBuilder item) => true;

    public bool Validate(PooledStringBuilder item)
    {
        // The capacity guard, P89OP-aligned: builders that grew past the maximum are destroyed on return
        // instead of being parked with a huge character buffer. There is no minimum to enforce — a
        // builder's capacity never shrinks below its creation capacity. A declared tier accepts builders
        // up to its own declared size even when that is above the global maximum: the buffer exists
        // because a borrower asked for it, and destroying it on every return is exactly the churn the
        // tier exists to avoid. Growth beyond the declared size (and the global maximum) is still
        // destroyed, because nothing declared that envelope.
        var maximum = _tierCapacity > _owner.MaximumStringBuilderCapacity
            ? _tierCapacity
            : _owner.MaximumStringBuilderCapacity;
        return item.StringBuilder.Capacity <= maximum;
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
