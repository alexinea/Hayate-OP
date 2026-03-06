using System;

namespace DotNetCore.HayateOP;

public class HayatePoolOverview
{
    public HayatePoolOverview(string message, string version)
    {
        Message = message;
        Version = version;
        Framework = "ASP.NET Core 10";
        Timestamp = DateTime.UtcNow;
    }

    public string Message { get; set; }
    public string Version { get; set; }
    public string Framework { get; set; }
    public DateTime Timestamp { get; set; }
}