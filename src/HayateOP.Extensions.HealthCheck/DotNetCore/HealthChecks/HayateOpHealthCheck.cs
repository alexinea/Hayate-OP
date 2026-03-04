using DotNetCore.HayateOP;
using DotNetCore.HayateOP.Modules;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DotNetCore.HealthChecks;

public class HayateOpHealthCheck<T> : IHealthCheck, IHayateOpModule where T : class
{
    private readonly IHayateObjectPool<T> _pool;
    
    public HayateOpHealthCheck(IHayateObjectPool<T> pool)
    {
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var stats = _pool.GetStats();
        var data = new Dictionary<string, object>
        {
            { "PooledCount", stats.PooledCount },
            { "TotalCreated", stats.TotalCreated },
            { "TotalReturned", stats.TotalReturned },
            { "TotalMissed", stats.TotalMissed },
            { "AvailableSlots", stats.AvailableSlots }
        };

        var result = stats.AvailableSlots <= 0
            ? HealthCheckResult.Degraded("Object pool concurrent slots exhausted", data: data)
            : HealthCheckResult.Healthy("Object pool healthy", data: data);

        return Task.FromResult(result);
    }
}