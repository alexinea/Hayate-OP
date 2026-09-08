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
    /// L3：事件名缓存。事件名只依赖 <c>poolName</c>（运行期不变），
    /// 避免每次 <see cref="Source.Write(string, object)"/> 前插值分配新字符串。
    /// 诊断实例通常每池一个；多池共享同一实例时按引用比较失效退化为重建，语义不变。
    /// <c>volatile</c> 保证重建后的 <see cref="EventNameSet"/> 安全发布（字段整体一致可见）。
    /// </summary>
    private volatile EventNameSet _cachedEventNames;

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
