using DotNetCore.HayateOP.Metrics;
using DotNetCore.HayateOP.Policies;
using DotNetCore.HayateOP.Scaling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DotNetCore.HayateOP;

public class HayateObjectPoolService<T> : HayateObjectPool<T> where T : class
{
    public HayateObjectPoolService(
        IHayateObjectPolicy<T> policy,
        IOptions<HayatePoolOptions> options,
        IHayateScalingStrategy scalingStrategy,
        ILogger<HayateObjectPool<T>> logger,
        IHayateMetrics metrics)
        : base(policy, options?.Value, scalingStrategy, logger, metrics)
    {
    }
}