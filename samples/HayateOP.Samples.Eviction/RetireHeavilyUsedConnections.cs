using System;
using DotNetCore.HayateOP;

namespace DotNetCore.HayateOP.Samples.Eviction;

/// <summary>
/// A custom eviction rule: retire a connection once it has been leased at least <see cref="_maxLeaseCount"/>
/// times. This models "wear-out" — connections that have served many requests are recycled even though they
/// are still young and idle-fresh, spreading reuse across the fleet instead of hammering a few.
/// </summary>
/// <remarks>
/// Install with <c>WithEvictionPolicy</c>. The background maintenance run is the ONLY caller of
/// <see cref="ShouldEvict"/>: manual <c>Evict(HayateEvictReason)</c> uses the built-in reason rules
/// (Touched / Idle / Expired), not this policy. A policy that never returns <c>true</c> turns idle
/// eviction off (the pool still reclaims abandoned objects and still honours an explicit
/// <c>Evict</c> call).
/// </remarks>
internal sealed class RetireHeavilyUsedConnections : IHayateEvictionPolicy<Connection>
{
    private readonly int _maxLeaseCount;

    public RetireHeavilyUsedConnections(int maxLeaseCount) => _maxLeaseCount = maxLeaseCount;

    /// <inheritdoc />
    public bool ShouldEvict(in HayateEvictionCandidate<Connection> candidate) =>
        candidate.LeaseCount >= _maxLeaseCount;
}
