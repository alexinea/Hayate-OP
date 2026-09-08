namespace DotNetCore.HayateOP.Metrics;

public class EmptyHayateMetrics : IHayateMetrics
{
    public void RecordObjectAcquired(string poolName, object item, double elapsedMilliseconds)
    {
    }

    public void RecordObjectReleased(string poolName, object item, bool isValid)
    {
    }

    public void RecordObjectMiss(string poolName)
    {
    }

    public void RecordPoolScaled(string poolName, string action, int oldSize, int newSize)
    {
    }

    public static IHayateMetrics Instance { get; } = new EmptyHayateMetrics();
}