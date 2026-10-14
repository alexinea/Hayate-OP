namespace DotNetCore.HayateOP;

/// <summary>
/// Why the pool destroyed a pooled object, reported to an
/// <see cref="Policies.IHayateObjectLifecycleObserver{T}"/> so a policy can tell a teardown it caused
/// from one the pool's own maintenance caused.
/// </summary>
/// <remarks>
/// <para>
/// A value reaches the observer through <c>OnEvict</c>, with one exception:
/// <see cref="ValidationFailed"/> is delivered to <c>OnValidationFailed</c> instead. The two hooks
/// partition this enum rather than overlapping, so a policy that only wants validation failures never
/// has to filter them out of the general hook.
/// </para>
/// <para>
/// The reasons are the ones the engine can actually distinguish at a destroy site. A pool running the
/// lean fast path keeps no per-object state and never validates, so it can report the reasons it can
/// judge and <see cref="ValidationFailed"/> is unreachable there.
/// </para>
/// <para>
/// This enum is not the same thing as <see cref="HayateEvictReason"/>: that one categorises what the
/// caller asked a manual <c>Evict</c> to remove, while this one records why the engine removed the
/// object. A manual eviction reports <see cref="Evicted"/> here, whichever of the three
/// <see cref="HayateEvictReason"/> categories it used.
/// </para>
/// </remarks>
public enum HayateDestroyReason
{
    /// <summary>
    /// <c>Validate</c> answered <c>false</c> -- on borrow, on return, or while the object sat idle.
    /// Delivered to <c>OnValidationFailed</c> rather than to <c>OnEvict</c>.
    /// </summary>
    ValidationFailed = 0,

    /// <summary>
    /// The pool's eviction rule retired the object: the background eviction run, or a manual
    /// <see cref="IHayateObjectPool{T}.Evict(HayateEvictReason)"/>.
    /// </summary>
    Evicted = 1,

    /// <summary>
    /// The pool had nowhere to park the object: a shard already at its idle capacity, the return-path
    /// soft ceiling, a pre-warm or auto-scaling grow that lost the race for the last slot.
    /// </summary>
    CapacityOverflow = 2,

    /// <summary>
    /// <c>OnRelease</c> refused the object, or the release path threw before the object could be
    /// parked.
    /// </summary>
    ReturnRejected = 3,

    /// <summary>
    /// The pool was drained -- <c>Clear()</c>, <c>Dispose()</c>, or their asynchronous twins -- and the
    /// object was idle at that moment.
    /// </summary>
    PoolCleared = 4,

    /// <summary>
    /// The auto-scaling strategy shrank the pool and this object did not survive the shrink.
    /// </summary>
    ScaledDown = 5,

    /// <summary>
    /// Abandoned-object recovery reclaimed the object: it had been borrowed and not returned within the
    /// configured window.
    /// </summary>
    Abandoned = 6,

    /// <summary>
    /// The object outlived <see cref="HayatePoolOptions.MaxLifeTime"/> and was rotated out on the borrow
    /// path, a freshly created replacement taking its place.
    /// </summary>
    Rotated = 7,

    /// <summary>
    /// The object was handed back by a caller the pool does not recognise as its owner: a foreign
    /// instance, or a second return that the shard rejects because it already holds the wrapper. A
    /// <c>null</c> return is a misuse too, but there is no object to destroy, so it never reaches this hook.
    /// </summary>
    Misuse = 8
}
