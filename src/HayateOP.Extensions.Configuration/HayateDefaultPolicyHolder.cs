using DotNetCore.HayateOP.Policies;
using System;

namespace DotNetCore.HayateOP.Extensions.Configuration;

/// <summary>
/// Records the object policy the configuration-driven registrations seeded when the caller supplied
/// none, so <c>BuildConfiguredPool&lt;T&gt;</c> can tell that fallback apart from a policy the caller
/// registered and leave the pool to derive its own policy instead of pinning this one.
/// </summary>
/// <remarks>
/// The mirror of the marker the DependencyInjection package keeps. Each package owns the registration
/// and the pool build it pairs with, so each keeps its own; neither is part of a public surface.<br />
/// The fallback is recognised by <b>identity</b>, not by type. A caller may register
/// <see cref="HayateObjectPolicies.Default{T}"/> itself; that is the caller's policy, and it is pinned
/// like any other, even though it is an instance of the type the library would have seeded itself.<br />
/// Nothing is recorded until the container resolves the seeded registration, so a container where the
/// caller's own policy won leaves this holder empty, and <see cref="IsFallback"/> answers
/// <see langword="false"/> for that policy — which is the answer that pins it.
/// </remarks>
/// <typeparam name="T">The pooled object type.</typeparam>
internal sealed class HayateDefaultPolicyHolder<T> where T : class
{
    // Written by the seeded registration's factory and read by BuildConfiguredPool<T> on the same cold
    // path. Volatile because two pools of one type may be built concurrently, and the holder must not
    // hand out a stale "not the fallback" answer that would pin the library default.
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
