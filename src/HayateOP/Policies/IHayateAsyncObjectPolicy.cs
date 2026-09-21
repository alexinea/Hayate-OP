#if NET6_0_OR_GREATER
using System.Threading;
using System.Threading.Tasks;

namespace DotNetCore.HayateOP.Policies;

/// <summary>
/// The asynchronous counterpart of <see cref="IHayateObjectPolicy{T}"/>. It extends that contract
/// rather than replacing it, so an existing policy keeps compiling unchanged.
/// </summary>
/// <remarks>
/// <para>
/// A policy that implements this interface takes the asynchronous path in the engine: the hooks below
/// are the ones the pool calls, and a synchronous entry point (<c>Acquire</c> / <c>Release</c>) waits on
/// them instead of calling the synchronous hooks. Implementing both interfaces is legal, and the
/// asynchronous hooks win where the two overlap.
/// </para>
/// <para>
/// Four of the six synchronous hooks have an asynchronous counterpart. <c>Validate</c> and
/// <c>OnAcquire</c> stay synchronous by design — validation is a hot-path decision with retry
/// semantics, and acquisition notification is bookkeeping rather than I/O.
/// </para>
/// <para>
/// The type is available on <c>net6.0</c> and later only: <c>ValueTask</c> is not a BCL type on
/// <c>netstandard2.0</c> / <c>net48</c>, and the core package keeps its zero-dependency policy. Those
/// targets keep the fully synchronous pool — no pseudo-asynchronous fallback.
/// </para>
/// <para>
/// The dispatch rules that govern this interface are specified in <c>docs/async-policy.md</c>; that
/// page is the design and this type is expected to match it.
/// </para>
/// </remarks>
/// <typeparam name="T">The pooled object type.</typeparam>
public interface IHayateAsyncObjectPolicy<T> : IHayateObjectPolicy<T> where T : class
{
    /// <summary>Creates a new pooled object asynchronously.</summary>
    /// <param name="cancellationToken">Token that cancels the creation.</param>
    /// <returns>A new instance of <typeparamref name="T"/>; must not be <c>null</c>.</returns>
    ValueTask<T> CreateAsync(CancellationToken cancellationToken = default);

    /// <summary>Called asynchronously when an object is returned to the pool.</summary>
    /// <param name="item">The object being returned.</param>
    /// <param name="cancellationToken">Token that cancels the hook.</param>
    /// <returns><c>true</c> to accept the object back into the pool; <c>false</c> to destroy it.</returns>
    ValueTask<bool> OnReleaseAsync(T item, CancellationToken cancellationToken = default);

    /// <summary>Called asynchronously just before an object is placed back into the pool after return.</summary>
    /// <param name="item">The object about to be passivated.</param>
    /// <param name="cancellationToken">Token that cancels the hook.</param>
    ValueTask OnPassivateAsync(T item, CancellationToken cancellationToken = default);

    /// <summary>Called asynchronously when an object is destroyed (removed from the pool for good).</summary>
    /// <param name="item">The object being destroyed.</param>
    /// <param name="cancellationToken">Token that cancels the hook.</param>
    ValueTask OnDestroyAsync(T item, CancellationToken cancellationToken = default);
}
#endif
