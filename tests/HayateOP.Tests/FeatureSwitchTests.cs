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

    [Fact(Timeout = 60000)]
    public void DisableAutoScaling_ShouldKeepExplicitMaxAsHardCap()
    {
        // PR-D L9（2.1 行为变更）：关闭自动扩缩不再强制 MaxPoolSize = MinPoolSize。
        // Max 保留用户显式值作为硬上限；扩容回调/超时强扩均受 EnableAutoScaling 门控，不会突破。
        var options = new HayatePoolOptions
        {
            EnableAutoScaling = false,
            MinPoolSize = 2,
            MaxPoolSize = 8
        };
        options.ApplyFeatureSwitches();

        Assert.Equal(8, options.MaxPoolSize);

        // 构建池验证：预热到 MinSize=2，容量上限 8
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithEnableAutoScaling(false)
            .WithMinSize(2)
            .WithMaxSize(8)
            .Build();

        var stats = pool.GetStats();
        Assert.Equal(2, stats.CurrentSize); // 预热到 Min，Max 不参与初始化
    }

    [Fact(Timeout = 60000)]
    public void DisableAutoScaling_WithZeroMin_ShouldNotCollapseCapacity()
    {
        // L9 塌缩陷阱回归：Min=0 + autoScaling off，旧语义 Max 塌缩为 0，
        // 一切归还都被分片 max=0 静默拒绝；新语义 Max 保留，借还往返正常。
        // 注：Min=0 冷池 + 默认 BlockTimeout 策略空池借出必然超时（无归还者唤醒），
        // 故用 CreateNew 策略（等满短超时后创建）验证借还往返，不依赖 L5 自举行为。
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithEnableAutoScaling(false)
            .WithMinSize(0)
            .WithMaxSize(10)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateNew)
            .Build();

        var obj = pool.Acquire(TimeSpan.FromMilliseconds(300));
        Assert.NotNull(obj);
        pool.Release(obj); // 旧语义此处归还即被销毁（容量 0）

        var stats = pool.GetStats();
        Assert.Equal(1, stats.PooledCount); // 对象回池而非被拒（旧语义容量 0 会拒绝）
    }

    [Fact]
    public void DisableAutoScaling_ShouldLiftMaxWhenBelowMin()
    {
        // 保序防御：Max < Min 时抬升 Max = Min，避免 IsValid 校验失败（维持静默修正惯例）。
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