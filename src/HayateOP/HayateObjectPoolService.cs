using HayateOP.Metrics;
using HayateOP.Policies;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HayateOP;

public class HayateObjectPoolService<T> : HayateObjectPool<T> where T : class
{
    public HayateObjectPoolService(
        IHayateObjectPolicy<T> policy,
        IOptions<HayateOpOptions> options,
        IHayateOpMetrics metrics,
        ILogger<HayateObjectPool<T>> logger)
        : base(typeof(HayateObjectPoolService<T>).Name, policy, logger, options, metrics, options.Value.MaxConcurrent, options.Value.MaxPoolSize)
    {
    }
}