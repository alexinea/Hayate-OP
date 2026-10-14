namespace DotNetCore.HayateOP.Policies;

/// <summary>
/// An optional companion to <see cref="IHayateObjectPolicy{T}"/> that is told <i>why</i> the pool
/// destroyed a pooled object. <see cref="IHayateObjectPolicy{T}.OnDestroy"/> says an object was torn
/// down; these hooks say what decided it.
/// </summary>
/// <remarks>
/// <para>
/// The interface is deliberately separate from <see cref="IHayateObjectPolicy{T}"/> and is probed with
/// <c>is</c> at construction, exactly as the asynchronous contract is. A policy that does not implement
/// it compiles unchanged and pays one null test on the destroy path, and a policy that does implement it
/// keeps its <see cref="IHayateObjectPolicy{T}"/> implementation untouched. Adding the two members to the
/// main interface instead would have forced every implementer in the wild to add them.
/// </para>
/// <para>
/// The two hooks partition the reasons rather than overlapping: a validation failure arrives through
/// <c>OnValidationFailed</c>, and every other destruction arrives through <c>OnEvict</c> with its
/// <see cref="HayateDestroyReason"/>. A policy that wants one entry point can route the first into the
/// second itself.
/// </para>
/// <para>
/// Both hooks run <b>before</b> the object is torn down, so the value is still alive when the observer
/// sees it, and both run exactly once per object: they sit behind the same idempotent claim that makes
/// <see cref="IHayateObjectPolicy{T}.OnDestroy"/> fire once. An exception thrown by either is logged and
/// swallowed -- a misbehaving observer must not turn a destruction into a leak, or abort the drain that
/// was in progress.
/// </para>
/// <para>
/// Different objects can be destroyed concurrently, so these callbacks can run concurrently too. An
/// observer that updates shared state must provide its own synchronization; the pool serializes neither
/// the callbacks nor the policy's other lifecycle methods.
/// </para>
/// <para>
/// The hooks are synchronous by design, on the same reasoning the asynchronous contract uses for
/// <c>Validate</c> and <c>OnAcquire</c>: they are notification and bookkeeping, not I/O. A pool whose
/// policy also implements <c>IHayateAsyncObjectPolicy&lt;T&gt;</c> is still observed through these
/// members.
/// </para>
/// <para>
/// Coverage is the engine's, not a promise: the general-purpose engine reports every reason it can
/// distinguish, while the lean fast path keeps no per-object state and never validates, so it reports
/// the reasons it can judge and never reports <see cref="HayateDestroyReason.ValidationFailed"/>. A
/// drain reports <see cref="HayateDestroyReason.PoolCleared"/> even though it deliberately does not run
/// <see cref="IHayateObjectPolicy{T}.OnDestroy"/> -- the two are independent contracts.
/// </para>
/// <para>
/// This interface is available on every target. The <c>netstandard2.0</c> and <c>net48</c> builds do not
/// support default interface methods, which is why the contract is a separate interface rather than two
/// defaulted members on the main one.
/// </para>
/// </remarks>
/// <typeparam name="T">The pooled object type.</typeparam>
public interface IHayateObjectLifecycleObserver<T> where T : class
{
    /// <summary>Called just before the pool destroys an object, for every reason except
    /// <see cref="HayateDestroyReason.ValidationFailed"/>.</summary>
    /// <param name="item">The object about to be destroyed; still alive and not yet disposed.</param>
    /// <param name="reason">What decided the destruction.</param>
    void OnEvict(T item, HayateDestroyReason reason);

    /// <summary>Called just before the pool destroys an object because <c>Validate</c> answered
    /// <c>false</c> for it -- the borrow-side, return-side and idle-sweep failures alike.</summary>
    /// <param name="item">The object about to be destroyed; still alive and not yet disposed.</param>
    void OnValidationFailed(T item);
}
