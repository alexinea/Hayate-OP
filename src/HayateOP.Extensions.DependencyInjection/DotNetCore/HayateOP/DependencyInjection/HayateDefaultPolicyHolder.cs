using DotNetCore.HayateOP.Policies;
using System;

namespace DotNetCore.HayateOP.DependencyInjection;

/// <summary>
/// Records the object policy the container seeders registered when the caller supplied none, so
/// <c>BuildPool&lt;T&gt;</c> can tell that fallback apart from a policy the caller registered and
/// leave the pool to derive its own policy instead of pinning this one.
/// </summary>
/// <remarks>
/// The fallback is recognised by <b>identity</b>, not by type. A caller may register
/// <see cref="HayateObjectPolicies.Default{T}"/> itself, or reach it through
/// <c>HayatePoolBuilder&lt;T&gt;.WithPolicy</c>; that is the caller's policy, and it is pinned like
/// any other, even though it is an instance of the type the library would have seeded itself.<br />
/// Nothing is recorded until the container resolves the seeded registration, so a container where the
/// caller's own policy won leaves this holder empty, and <see cref="IsFallback"/> answers
/// <see langword="false"/> for that policy — which is the answer that pins it.
/// </remarks>
/// <typeparam name="T">The pooled object type.</typeparam>
internal sealed class HayateDefaultPolicyHolder<T> where T : class
{
    // Written by the seeded registration's factory and read by BuildPool<T> on the same cold path.
    // Volatile because the container may build two named pools of one type concurrently, and the
    // holder must not hand out a stale "not the fallback" answer that would pin the library default.
    private volatile IHayateObjectPolicy<T>? _policy;

    /// <summary>Remembers the policy the seeded registration handed to the container.</summary>
    /// <param name="policy">The fallback policy the container is now the owner of.</param>
    internal void Record(IHayateObjectPolicy<T> policy) => _policy = policy;

    /// <summary>
    /// Whether <paramref name="policy"/> is the fallback this holder recorded — that is, whether the
    /// policy the container resolved for the pool being built is the library's own.
    /// </summary>
    /// <param name="policy">The policy the pool is about to be built with.</param>
    /// <returns><see langword="true"/> when there is nothing of the caller's to pin.</returns>
    internal bool IsFallback(IHayateObjectPolicy<T> policy) => ReferenceEquals(_policy, policy);
}
