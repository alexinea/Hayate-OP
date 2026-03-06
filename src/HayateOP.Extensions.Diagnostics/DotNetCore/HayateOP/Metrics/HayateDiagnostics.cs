using System.Diagnostics;
using DotNetCore.HayateOP.Modules;
using Microsoft.Extensions.Options;

namespace DotNetCore.HayateOP.Metrics;

public class HayateDiagnostics : IHayateMetrics, IHayateOpModule
{
    public static readonly DiagnosticSource Source = new DiagnosticListener("HayateOP");

    public const string ObjectGet = "HayateOP.Object.Get";
    public const string ObjectReturn = "HayateOP.Object.Return";
    public const string ObjectMiss = "HayateOP.Object.Miss";
    public const string PoolScaled = "HayateOp.Pool.Scaled";

    private readonly HayatePoolOptions _options;

    public HayateDiagnostics(IOptions<HayatePoolOptions>? options)
    {
        _options = options?.Value ?? new HayatePoolOptions();
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

    public void RecordPoolScaled(string poolName, string action, int oldSize, int newSize)
    {
        if (_options.EnableMetrics && Source.IsEnabled(PoolScaled))
        {
            Source.Write($"{poolName}_{PoolScaled}", new { Action = action, OldSize = oldSize, NewSize = newSize });
        }
    }
}