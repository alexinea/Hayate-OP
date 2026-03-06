using System;

namespace DotNetCore.HayateOP;

public class HayatePoolStatsDetail
{
    public HayatePoolStatsDetail(string poolName, HayatePoolStats stats, HayatePoolSnapshot snapshot)
    {
        PoolName = poolName;
        Stats = stats;
        Snapshot = snapshot;
        Timestamp = DateTime.UtcNow;
    }

    public string PoolName { get; set; }
    public HayatePoolStats Stats { get; set; }
    public HayatePoolSnapshot Snapshot { get; set; }
    public DateTime Timestamp { get; set; }
}