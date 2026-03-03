using Microsoft.Extensions.Options;

namespace HayateOP.Metrics;

public class HayateOpDiagnosticsService : IHayateOpModule
{
    private readonly IHayateOpMetrics _metrics;
    private readonly HayateOpOptions _options;

    public HayateOpDiagnosticsService(IOptions<HayateOpOptions>? options)
    {
        _options = options?.Value ?? new HayateOpOptions();
        _metrics = _options.EnableMetrics ? new HayateOpDiagnostics(options) : EmptyHayateOpMetrics.Instance;
    }
    
    public void RecordObjectAcquired(string poolName, object item, double elapsedMilliseconds)
    {
        if (_options.EnableMetrics && HayateOpDiagnostics.Source.IsEnabled(HayateOpDiagnostics.ObjectGet))
        {
            HayateOpDiagnostics.Source.Write($"{poolName}_{HayateOpDiagnostics.ObjectGet}", item);
        }
    }

    public void RecordObjectReturned(string poolName, object item, bool isValid)
    {
        if (_options.EnableMetrics && HayateOpDiagnostics.Source.IsEnabled(HayateOpDiagnostics.ObjectReturn))
        {
            HayateOpDiagnostics.Source.Write($"{poolName}_{HayateOpDiagnostics.ObjectReturn}", item);
        }
    }

    public void RecordObjectMiss(string poolName, object item)
    {
        if (_options.EnableMetrics && HayateOpDiagnostics.Source.IsEnabled(HayateOpDiagnostics.ObjectMiss))
        {
            HayateOpDiagnostics.Source.Write($"{poolName}_{HayateOpDiagnostics.ObjectMiss}", item);
        }
    }
}