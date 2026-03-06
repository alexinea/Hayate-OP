using System;

namespace DotNetCore.HayateOP;

public class HayatePoolOperationResponse
{
    public HayatePoolOperationResponse(string poolName, string message, DateTime timestamp)
    {
        PoolName = poolName;
        Message = message;
        Timestamp = timestamp;
    }

    public string PoolName { get; set; }
    public string Message { get; set; }
    public DateTime Timestamp { get; set; }
}