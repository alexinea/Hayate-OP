using Microsoft.Extensions.Options;

namespace DotNetCore.HayateOP.Metrics;

public class HayateOpDiagnosticsService : IHayateMetrics
{
    private readonly IHayateMetrics _metrics;
    private readonly HayatePoolOptions _options;

    public HayateOpDiagnosticsService(IOptions<HayatePoolOptions> options)
    {
        _options = options?.Value ?? new HayatePoolOptions();
        _metrics = _options.EnableMetrics ? new HayateDiagnostics(options) : EmptyHayateMetrics.Instance;
    }
    
    public void RecordObjectAcquired(string poolName, object item, double elapsedMilliseconds)
    {
        _metrics.RecordObjectAcquired(poolName, item, elapsedMilliseconds);
    }

    public void RecordObjectReleased(string poolName, object item, bool isValid)
    {
        _metrics.RecordObjectReleased(poolName, item, isValid);
    }

    public void RecordObjectMiss(string poolName, object item)
    {
        _metrics.RecordObjectMiss(poolName, item);
    }

    public void RecordPoolScaled(string poolName, string action, int oldSize, int newSize)
    {
        _metrics.RecordPoolScaled(poolName, action, oldSize, newSize);
    }
}