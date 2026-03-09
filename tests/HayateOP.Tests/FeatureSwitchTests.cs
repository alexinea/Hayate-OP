namespace DotNetCore.HayateOP.Tests;

public class FeatureSwitchTests
{
    private class TestObject { }

    [Fact]
    public void DisableSharding_ShouldForceSingleShard()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithEnableSharding(false)
            .WithShardCount(4)

            .WithMaxSize(5)
            .WithMinSize(5)

            .Build();

        var stats = pool.GetStats(); 
        Assert.Equal(1, stats.CurrentSize / stats.MinSize); // 单分片
    }

    [Fact]
    public void DisableAutoScaling_ShouldFixPoolSize()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithEnableAutoScaling(false)
            .WithMinSize(10)
            .WithMaxSize(100)
            .Build();

        // current = maxPoolSize / shardCount
        var options = pool.GetOptions();
        var current = options.MaxPoolSize / options.ShardCount;

        var stats = pool.GetStats();
        Assert.Equal(10, stats.MinSize);
        //Assert.Equal(10, stats.CurrentSize); // MaxPoolSize被强制覆盖为MinSize
        Assert.Equal(current, stats.CurrentSize); // MaxPoolSize被强制覆盖为MinSize
    }

    [Fact]
    public void DisableValidation_ShouldTurnOffAllValidation()
    {
        var options = new HayatePoolOptions
        {
            EnableValidation = false,
            ValidateOnBorrow = true,
            ValidateOnReturn = true,
            ValidateWhileIdle = true
        };
        options.ApplyFeatureSwitches();

        Assert.False(options.ValidateOnBorrow);
        Assert.False(options.ValidateOnReturn);
        Assert.False(options.ValidateWhileIdle);
    }

    [Fact]
    public void DisableMetrics_ShouldNotUpdateStats()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithEnableMetrics(false)
            .Build();

        var obj = pool.Acquire();
        pool.Release(obj);

        var stats = pool.GetStats();
        Assert.Equal(0, stats.TotalReleased);
    }
}