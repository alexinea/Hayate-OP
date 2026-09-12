using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace DotNetCore.HayateOP.Metrics;

public class HayateDiagnostics : IHayateMetrics
{
    public static readonly DiagnosticSource Source = new DiagnosticListener("HayateOP");

    public const string ObjectGet = "HayateOP.Object.Acquire";
    public const string ObjectReturn = "HayateOP.Object.OnRelease";
    public const string ObjectMiss = "HayateOP.Object.Miss";
    public const string PoolScaled = "HayateOp.Pool.Scaled";

    private readonly HayatePoolOptions _options;

    /// <summary>
    /// Cache of per-pool event names. Event names depend only on <c>poolName</c> (immutable at
    /// runtime), avoiding a new interpolated string allocation before every
    /// <c>DiagnosticSource.Write(string, object)</c> call. A diagnostics instance is normally one per
    /// pool; when several pools share the same instance the reference comparison fails and falls back
    /// to rebuilding, with unchanged semantics.
    /// <c>volatile</c> ensures the rebuilt <see cref="EventNameSet"/> is safely published (the field
    /// is made coherently visible as a whole).
    /// </summary>
    private volatile EventNameSet? _cachedEventNames;

    public HayateDiagnostics(IOptions<HayatePoolOptions> options)
    {
        _options = options?.Value ?? new HayatePoolOptions();
    }

    public void RecordObjectAcquired(string poolName, object item, double elapsedMilliseconds)
    {
        if (_options.EnableMetrics && Source.IsEnabled(ObjectGet))
        {
            Source.Write(GetEventNames(poolName).Acquire, item);
        }
    }

    public void RecordObjectReleased(string poolName, object item, bool isValid)
    {
        if (_options.EnableMetrics && Source.IsEnabled(ObjectReturn))
        {
            Source.Write(GetEventNames(poolName).Release, item);
        }
    }

    public void RecordObjectMiss(string poolName)
    {
        if (_options.EnableMetrics && Source.IsEnabled(ObjectMiss))
        {
            Source.Write(GetEventNames(poolName).Miss, null);
        }
    }

    public void RecordPoolScaled(string poolName, string action, int oldSize, int newSize)
    {
        if (_options.EnableMetrics && Source.IsEnabled(PoolScaled))
        {
            Source.Write(GetEventNames(poolName).Scaled, new { Action = action, OldSize = oldSize, NewSize = newSize });
        }
    }

    private EventNameSet GetEventNames(string poolName)
    {
        var cached = _cachedEventNames;
        if (cached is not null && ReferenceEquals(cached.PoolName, poolName))
        {
            return cached;
        }

        var created = new EventNameSet(poolName);
        _cachedEventNames = created;
        return created;
    }

    private sealed class EventNameSet
    {
        public string PoolName { get; }

        public string Acquire { get; }

        public string Release { get; }

        public string Miss { get; }

        public string Scaled { get; }

        public EventNameSet(string poolName)
        {
            PoolName = poolName;
            Acquire = $"{poolName}_{ObjectGet}";
            Release = $"{poolName}_{ObjectReturn}";
            Miss = $"{poolName}_{ObjectMiss}";
            Scaled = $"{poolName}_{PoolScaled}";
        }
    }
}
