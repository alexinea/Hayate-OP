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
            .Build();

        var stats = pool.GetStats(); 
        Assert.Equal(1, stats.CurrentSize / stats.MinSize); // 单分片
    }

    [Fact]
    public void DisableAutoScaling_ShouldFixPoolSize()
    {
        // Arrange
        var options = new HayatePoolOptions
        {
            EnableAutoScaling = false,
            MinPoolSize = 2,
            MaxPoolSize = 8
        };
        options.ApplyFeatureSwitches();

        // Assert：MaxPoolSize被强制覆盖为MinSize=2
        Assert.Equal(2, options.MaxPoolSize);

        // 构建池验证
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithEnableAutoScaling(false)
            .WithMinSize(2)
            .WithMaxSize(8)
            .Build();

        var stats = pool.GetStats();
        Assert.Equal(2, stats.CurrentSize); // 池大小固定为2，不再是8
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