using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace HayateOP.Metrics;

public class HayateOpDiagnostics : IHayateOpMetrics, IHayateOpModule
{
    public static readonly DiagnosticSource Source = new DiagnosticListener("HayateOP");

    public const string ObjectGet = "HayateOP.Object.Get";
    public const string ObjectReturn = "HayateOP.Object.Return";
    public const string ObjectMiss = "HayateOP.Object.Miss";

    private readonly HayateOpOptions _options;

    public HayateOpDiagnostics(IOptions<HayateOpOptions>? options)
    {
        _options = options?.Value ?? new HayateOpOptions();
    }

    public void RecordObjectAcquired(string poolName, object item, double elapsedMilliseconds)
    {
        if (_options.EnableMetrics && Source.IsEnabled(ObjectGet))
        {
            Source.Write($"{poolName}_{ObjectGet}", item);
        }
    }

    public void RecordObjectReturned(string poolName, object item, bool isValid)
    {
        if (_options.EnableMetrics && Source.IsEnabled(ObjectReturn))
        {
            Source.Write($"{poolName}_{ObjectReturn}", item);
        }
    }

    public void RecordObjectMiss(string poolName, object item)
    {
        if (_options.EnableMetrics && Source.IsEnabled(ObjectMiss))
        {
            Source.Write($"{poolName}_{ObjectMiss}", item);
        }
    }
}