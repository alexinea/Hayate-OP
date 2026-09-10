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

    [Fact]
    public void DisableAutoScaling_ShouldKeepExplicitMaxAsHardCap()
    {
        // Behavior change: disabling auto-scaling no longer forces MaxPoolSize = MinPoolSize.
        // Max keeps the user's explicit value as a hard upper bound; both the scale-up callback and the timeout force-scale are gated by EnableAutoScaling and cannot exceed it.
        var options = new HayatePoolOptions
        {
            EnableAutoScaling = false,
            MinPoolSize = 2,
            MaxPoolSize = 8
        };
        options.ApplyFeatureSwitches();

        Assert.Equal(8, options.MaxPoolSize);

        // Verify via the built pool: warm up to MinSize=2, capacity cap 8
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithEnableAutoScaling(false)
            .WithMinSize(2)
            .WithMaxSize(8)
            .Build();

        var stats = pool.GetStats();
        Assert.Equal(2, stats.CurrentSize); // warmed to Min, Max does not participate in initialization
    }

    [Fact]
    public void DisableAutoScaling_WithZeroMin_ShouldNotCollapseCapacity()
    {
        // Collapse trap regression: Min=0 + autoScaling off; the old semantics collapsed Max to 0,
        // and every return was silently rejected by the shard's max=0; the new semantics keeps Max, so borrow/return round-trips work normally.
        // Note: a Min=0 cold pool with the default BlockTimeout policy would always time out on an empty-pool borrow (no returner to wake it),
        // so we use the CreateNew policy (creates after the short timeout elapses) to verify the borrow/return round-trip, without relying on the cold-start bootstrap behavior.
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithEnableAutoScaling(false)
            .WithMinSize(0)
            .WithMaxSize(10)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateNew)
            .Build();

        var obj = pool.Acquire(TimeSpan.FromMilliseconds(300));
        Assert.NotNull(obj);
        pool.Release(obj); // under old semantics the return would be destroyed here (capacity 0)

        var stats = pool.GetStats();
        Assert.Equal(1, stats.PooledCount); // the object returns to the pool instead of being rejected (old semantics with capacity 0 would reject)
    }

    [Fact]
    public void DisableAutoScaling_ShouldLiftMaxWhenBelowMin()
    {
        // Order-preservation guard: when Max < Min, lift Max = Min to avoid an IsValid validation failure (preserving the silent-correction convention).
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