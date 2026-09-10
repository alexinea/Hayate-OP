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
        Assert.Equal(1, stats.CurrentSize / stats.MinSize); // single shard
    }

    [Fact(Timeout = 60000)]
    public void DisableAutoScaling_ShouldKeepExplicitMaxAsHardCap()
    {
        // Behavior change (2.1): disabling auto-scaling no longer forces MaxPoolSize = MinPoolSize.
        // Max keeps the user's explicit value as a hard cap; both scale-up callbacks and forced timeout scale-up are gated by EnableAutoScaling and cannot break through.
        var options = new HayatePoolOptions
        {
            EnableAutoScaling = false,
            MinPoolSize = 2,
            MaxPoolSize = 8
        };
        options.ApplyFeatureSwitches();

        Assert.Equal(8, options.MaxPoolSize);

        // Verify the built pool: warm up to MinSize=2, capacity upper bound 8
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithEnableAutoScaling(false)
            .WithMinSize(2)
            .WithMaxSize(8)
            .Build();

        var stats = pool.GetStats();
        Assert.Equal(2, stats.CurrentSize); // warm up to Min; Max does not participate in initialization
    }

    [Fact(Timeout = 60000)]
    public void DisableAutoScaling_WithZeroMin_ShouldNotCollapseCapacity()
    {
        // Collapse-trap regression: Min=0 + autoScaling off; the old semantics collapsed Max to 0,
        // all returns were silently rejected by shard max=0; the new semantics keep Max, so borrow/return round-trips work normally.
        // Note: a Min=0 cold pool + the default BlockTimeout policy means an empty-pool borrow is guaranteed to time out (no returner to wake it),
        // Therefore we use the CreateNew policy (creates after the short timeout elapses) to verify borrow/return round-trips, without relying on the cold-bootstrap behavior.
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithEnableAutoScaling(false)
            .WithMinSize(0)
            .WithMaxSize(10)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateNew)
            .Build();

        var obj = pool.Acquire(TimeSpan.FromMilliseconds(300));
        Assert.NotNull(obj);
        pool.Release(obj); // in the old semantics the returned object would be destroyed here (capacity 0)

        var stats = pool.GetStats();
        Assert.Equal(1, stats.PooledCount); // the object returns to the pool instead of being rejected (old semantics with capacity 0 would reject)
    }

    [Fact]
    public void DisableAutoScaling_ShouldLiftMaxWhenBelowMin()
    {
        // Order-preserving guard: when Max < Min, raise Max = Min to avoid IsValid validation failure (preserving the silent-correction convention).
        var options = new HayatePoolOptions
        {
            EnableAutoScaling = false,
            MinPoolSize = 5,
            MaxPoolSize = 2
        };
        options.ApplyFeatureSwitches();

        Assert.Equal(5, options.MaxPoolSize);
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