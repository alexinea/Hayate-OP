using System;

namespace DotNetCore.HayateOP;

public class HayatePoolDetail
{
    public HayatePoolDetail(string poolName, HayatePoolOptions config, HayatePoolStats stats)
    {
        PoolName = poolName;
        Config = config;
        Stats = stats;
        UpdatedAt = DateTime.UtcNow;
    }

    public string PoolName { get; set; }
    public HayatePoolOptions Config { get; set; }
    public HayatePoolStats Stats { get; set; }
    public DateTime UpdatedAt { get; set; }
}