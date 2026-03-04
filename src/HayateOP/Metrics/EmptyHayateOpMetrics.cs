namespace DotNetCore.HayateOP.Metrics;

public class EmptyHayateOpMetrics : IHayateOpMetrics
{
    public void RecordObjectAcquired(string poolName, object item, double elapsedMilliseconds)
    {
    }

    public void RecordObjectReturned(string poolName, object item, bool isValid)
    {
    }

    public void RecordObjectMiss(string poolName, object item)
    {
    }

    public void RecordPoolScaled(string poolName, string action, int oldSize, int newSize)
    {
    }

    public static IHayateOpMetrics Instance { get; } = new EmptyHayateOpMetrics();
}