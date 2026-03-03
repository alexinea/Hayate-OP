namespace DotNetCore.HayateOP.Metrics;

public class EmptyObjectPoolMetrics : IObjectPoolMetrics
{
    public void RecordObjectAcquired(string poolName, object item, bool success, double elapsedMilliseconds)
    {
    }

    public void RecordObjectReturned(string poolName, object item, bool isValid)
    {
    }

    public void RecordObjectCreated(string poolName, object item)
    {
    }
    
    internal static IObjectPoolMetrics Instance { get; } = new EmptyObjectPoolMetrics();
}