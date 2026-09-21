using DotNetCore.HayateOP;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DotNetCore.HealthChecks;

/// <summary>
/// A Microsoft.Extensions.Diagnostics.HealthChecks probe over a single object pool.
/// </summary>
/// <typeparam name="T">The pooled element type.</typeparam>
/// <remarks>
/// <para>
/// Three questions are asked, in this order, and the first one that fails decides the result:
/// </para>
/// <list type="number">
/// <item><description>Is the pool's own circuit breaker open? Then nothing can be borrowed at all —
/// <c>Unhealthy</c>.</description></item>
/// <item><description>Are all concurrent slots busy? The pool still works, but the next borrower waits —
/// <c>Degraded</c>.</description></item>
/// <item><description>If an <see cref="IHayateObjectHealthProbe{T}"/> is configured, does a real borrowed
/// object still work? A bad answer is <c>Unhealthy</c>.</description></item>
/// </list>
/// <para>
/// The third question is the only one that can cost anything, so it is asked last and only when it can be
/// answered without waiting: a health check that blocks is worse than one that reports less, so when every
/// slot is busy the probe is skipped and the degraded answer stands.
/// </para>
/// </remarks>
public class HayateOpHealthCheck<T> : IHealthCheck where T : class
{
    private readonly IHayateObjectPool<T> _pool;
    private readonly IHayateObjectHealthProbe<T>? _probe;

    /// <summary>
    /// Creates a health check over <paramref name="pool"/>.
    /// </summary>
    /// <param name="pool">The pool to report on.</param>
    /// <param name="probe">An optional object-level probe; see <see cref="IHayateObjectHealthProbe{T}"/>.
    /// When it is <c>null</c> the check reports only what the pool's own counters and circuit breaker say.
    /// </param>
    public HayateOpHealthCheck(IHayateObjectPool<T> pool, IHayateObjectHealthProbe<T>? probe = null)
    {
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _probe = probe;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var stats = _pool.GetStats();
        var data = BuildData(stats, _pool.CheckAvailable());

        if (!_pool.CheckAvailable())
        {
            return HealthCheckResult.Unhealthy("Object pool circuit breaker is open", data: data);
        }

        if (stats.AvailableSlots <= 0)
        {
            return HealthCheckResult.Degraded("Object pool concurrent slots exhausted", data: data);
        }

        if (_probe is null)
        {
            return HealthCheckResult.Healthy("Object pool healthy", data: data);
        }

        T borrowed;
        try
        {
            // Zero timeout: probe only when an object is there for the taking. Waiting here would make the
            // health check as slow as the pool is busy, which is exactly when the host needs an answer.
            borrowed = _pool.Acquire(TimeSpan.Zero);
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Object pool handed out no object to probe", ex, data);
        }

        try
        {
            var healthy = await _probe.IsHealthyAsync(borrowed, cancellationToken).ConfigureAwait(false);

            return healthy
                ? HealthCheckResult.Healthy("Object pool healthy; object health probe passed", data: data)
                : HealthCheckResult.Unhealthy("Object health probe reported a pooled object as unusable", data: data);
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Object health probe failed", ex, data);
        }
        finally
        {
            _pool.Release(borrowed);
        }
    }

    /// <summary>
    /// Copies the counters a health payload can act on out of <paramref name="stats"/>, under the same names
    /// <see cref="HayatePoolStats"/> gives them.
    /// </summary>
    /// <remarks>
    /// Deliberately the counting fields only. The timing and allocation averages are in
    /// <see cref="HayatePoolStats"/> too, but they describe how the pool has been used rather than whether
    /// it is usable, and two of them are seeded at <see cref="double.MaxValue"/> until their first sample —
    /// a health payload that reports a minimum wait time of 1.8e308 is worse than one that omits it.
    /// </remarks>
    private static Dictionary<string, object> BuildData(HayatePoolStats stats, bool available)
    {
        return new Dictionary<string, object>
        {
            { "PooledCount", stats.PooledCount },
            { "MinPoolSize", stats.MinSize },
            { "CurrentSize", stats.CurrentSize },
            { "AvailableSlots", stats.AvailableSlots },
            { "TotalCreated", stats.TotalCreated },
            { "TotalReleased", stats.TotalReleased },
            { "TotalAcquired", stats.TotalAcquired },
            { "TotalDestroyed", stats.TotalDestroyed },
            { "TotalMissed", stats.TotalMissed },
            { "LeakDetectedCount", stats.LeakDetectedCount },
            { "LeakSuspectedCount", stats.LeakSuspectedCount },
            { "AbandonedRemovedCount", stats.AbandonedRemovedCount },
            { "LifetimeRotatedCount", stats.LifetimeRotatedCount },
            { "CircuitBreakerOpen", !available }
        };
    }
}