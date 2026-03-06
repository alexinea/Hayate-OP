using DotNetCore.HayateOP.Modules;
using Microsoft.Extensions.Options;

namespace DotNetCore.HayateOP.Metrics;

public class HayateOpDiagnosticsService : IHayateOpModule
{
    private readonly IHayateMetrics _metrics;
    private readonly HayatePoolOptions _options;

    public HayateOpDiagnosticsService(IOptions<HayatePoolOptions>? options)
    {
        _options = options?.Value ?? new HayatePoolOptions();
        _metrics = _options.EnableMetrics ? new HayateDiagnostics(options) : EmptyHayateMetrics.Instance;
    }
    
    public void RecordObjectAcquired(string poolName, object item, double elapsedMilliseconds)
    {
        if (_options.EnableMetrics && HayateDiagnostics.Source.IsEnabled(HayateDiagnostics.ObjectGet))
        {
            HayateDiagnostics.Source.Write($"{poolName}_{HayateDiagnostics.ObjectGet}", item);
        }
    }

    public void RecordObjectReturned(string poolName, object item, bool isValid)
    {
        if (_options.EnableMetrics && HayateDiagnostics.Source.IsEnabled(HayateDiagnostics.ObjectReturn))
        {
            HayateDiagnostics.Source.Write($"{poolName}_{HayateDiagnostics.ObjectReturn}", item);
        }
    }

    public void RecordObjectMiss(string poolName, object item)
    {
        if (_options.EnableMetrics && HayateDiagnostics.Source.IsEnabled(HayateDiagnostics.ObjectMiss))
        {
            HayateDiagnostics.Source.Write($"{poolName}_{HayateDiagnostics.ObjectMiss}", item);
        }
    }
}