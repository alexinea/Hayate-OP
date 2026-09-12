namespace DotNetCore.HayateOP;

/// <summary>
/// Single-pool summary returned by the management endpoint's pool list.
/// Data comes from the full registry enumeration via <see cref="IHayateObjectPoolRegistry.GetAll"/>.
/// </summary>
/// <param name="PoolName">The logical pool name.</param>
/// <param name="ElementType">The fully qualified name of the pooled element type; null for the non-generic implementation.</param>
/// <param name="RegisteredAt">The registration (approximately construction) time.</param>
/// <param name="PooledCount">The current number of idle objects in the pool.</param>
/// <param name="BorrowedCount">The current number of borrowed objects.</param>
public sealed record HayatePoolSummary(
    string PoolName,
    string? ElementType,
    System.DateTimeOffset RegisteredAt,
    int PooledCount,
    int BorrowedCount);
