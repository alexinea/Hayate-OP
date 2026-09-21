#if NET6_0_OR_GREATER
namespace DotNetCore.HayateOP;

/// <summary>
/// The asynchronous disposal surface of a pool (A2, α form): everything
/// <see cref="IHayateObjectPool{T}"/> already is, plus an asynchronous shutdown that drains the
/// pool the way pooled I/O objects are meant to be torn down.
/// </summary>
/// <typeparam name="T">The pooled object type.</typeparam>
/// <remarks>
/// <para>
/// The interface is deliberately empty: it adds <see cref="IAsyncDisposable"/> to the existing
/// pool contract without touching that contract, so an implementation of
/// <see cref="IHayateObjectPool{T}"/> keeps compiling unchanged. The alternative — putting
/// <c>IAsyncDisposable</c> straight onto <see cref="IHayateObjectPool{T}"/> (the β form) — would
/// force every third-party implementer to add a <c>DisposeAsync</c> member, a source-level break
/// that belongs in a major release.
/// </para>
/// <para>
/// <see cref="IAsyncDisposable.DisposeAsync"/> drains: it disposes the objects the pool owns, preferring
/// <see cref="IAsyncDisposable"/> over <see cref="IDisposable"/> on each object, and it awaits the
/// asynchronous destroy hook when the policy implements
/// <see cref="Policies.IHayateAsyncObjectPolicy{T}"/>. The synchronous <see cref="IDisposable.Dispose"/> keeps
/// its current semantics — graceful shutdown does not change meaning for anyone who does not opt
/// in. Callers reach the asynchronous form through one type test:
/// </para>
/// <example>
/// <code>
/// if (pool is IHayateAsyncObjectPool&lt;T&gt; asyncPool) await asyncPool.DisposeAsync();
/// else pool.Dispose();
/// </code>
/// </example>
/// <para>
/// The type is available on <c>net6.0</c> and later only — <see cref="IAsyncDisposable"/> is not a
/// BCL type on <c>netstandard2.0</c> / <c>net48</c>, and those targets keep the synchronous pool
/// (no pseudo-asynchronous fallback). The dispatch rules that govern this surface are specified in
/// <c>docs/async-policy.md</c>; that page is the design and this type is expected to match it.
/// </para>
/// </remarks>
public interface IHayateAsyncObjectPool<T> : IHayateObjectPool<T>, IAsyncDisposable where T : class
{
}
#endif
