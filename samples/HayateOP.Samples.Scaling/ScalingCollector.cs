using DotNetCore.HayateOP.Metrics;

namespace DotNetCore.HayateOP.Samples.Scaling;

/// <summary>
/// Captures scaling events so the sample can prove the pool grew and shrank on its own.
/// </summary>
internal sealed class ScalingCollector : IHayateMetrics
{
    public List<string> ScaledEvents { get; } = new();

    public void RecordObjectAcquired(string poolName, object item, double elapsedMilliseconds) { }
    public void RecordObjectReleased(string poolName, object item, bool isValid) { }
    public void RecordObjectMiss(string poolName) { }

    public void RecordPoolScaled(string poolName, string action, int oldSize, int newSize)
        => ScaledEvents.Add($"{action} {oldSize} -> {newSize}");
}
