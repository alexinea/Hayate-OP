namespace DotNetCore.HealthChecks;

/// <summary>
/// Decides whether a single pooled object is still usable — the protocol-level question the pool itself
/// cannot answer.
/// </summary>
/// <typeparam name="T">The pooled element type.</typeparam>
/// <remarks>
/// <para>
/// A pool knows how many objects it holds and whether its own circuit breaker is open, but it has no way to
/// tell whether a pooled connection still answers. That answer needs a round trip, and only the code that
/// owns the protocol can make it. This interface is the seam: the pool supplies the mechanism — borrow an
/// object, hand it over, take the verdict, give the object back — and the implementation supplies the
/// meaning, which for a connection pool is a PING and for anything else is whatever "still works" means
/// there.
/// </para>
/// <para>
/// The probe is optional: without one, <see cref="HayateOpHealthCheck{T}"/> reports exactly what it reported
/// before it existed. It is also only consulted when an object can be borrowed without waiting — a health
/// check that blocks is worse than a health check that says less, so when every slot is busy the probe is
/// skipped and the pool is reported as degraded rather than probed.
/// </para>
/// <para>
/// One borrowed object per check is the whole cost. Implementations should therefore be cheap and bounded:
/// the check runs on whatever schedule the host configured, and a probe that hangs hangs the check.
/// </para>
/// </remarks>
public interface IHayateObjectHealthProbe<T> where T : class
{
    /// <summary>
    /// Reports whether <paramref name="obj"/> is still usable.
    /// </summary>
    /// <param name="obj">An object borrowed from the pool for the duration of this call.</param>
    /// <param name="cancellationToken">Cancels the probe when the health check itself is cancelled.</param>
    /// <returns><c>true</c> when the object is usable; <c>false</c> to fail the health check.</returns>
    /// <remarks>
    /// The object is returned to the pool as soon as this method completes, on every path — including the
    /// throwing one. Implementations must not release it themselves, and must not keep a reference to it
    /// afterwards: once this method returns, the next borrower owns it.
    /// </remarks>
    Task<bool> IsHealthyAsync(T obj, CancellationToken cancellationToken = default);
}
