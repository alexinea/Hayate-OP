namespace DotNetCore.HayateOP.Tests;

public class AutoScalingTests
{
    private class TestObject { }

    [Fact]
    public void EnableAutoScaling_ShouldAllowPoolSizeChange()
    {
        // Arrange
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithEnableAutoScaling(true)
            .WithMinSize(10)
            .WithMaxSize(100)
            .Build();

        // Act
        var stats = pool.GetStats();

        // Assert：初始大小为MinSize=10，不再是100
        Assert.Equal(10, stats.MinSize);
        Assert.Equal(10, stats.CurrentSize);
    }

    [Fact]
    public void DisableAutoScaling_ShouldFixPoolSize()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithEnableAutoScaling(false)
            .WithMinSize(10)
            .WithMaxSize(100)
            .Build();

        var options = new HayatePoolOptions { EnableAutoScaling = false, MinPoolSize = 10, MaxPoolSize = 100 };
        options.ApplyFeatureSwitches();
        Assert.Equal(10, options.MaxPoolSize);
    }

    [Fact]
    public void AcquireTimeout_ShouldTriggerForceScaleUp()
    {
        // Arrange
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithEnableAutoScaling(true)
            .WithMinSize(1)
            .WithMaxSize(10)
            .WithAcquireTimeout(TimeSpan.FromMilliseconds(100))
            .WithScaleUpStep(2)
            .WithScaleUpCooldownSeconds(0)
            .Build();

        // 耗尽池
        var obj1 = pool.Acquire();
        var initialSize = pool.GetStats().CurrentSize;

        // Act：超时触发扩容
        try
        {
            pool.Acquire(TimeSpan.FromMilliseconds(50));
        }
        catch (TimeoutException)
        {
            // 预期超时
        }

        // Assert：池大小真实增加
        Assert.True(pool.GetStats().CurrentSize > initialSize);
    }

    [Fact]
    public void ScaleDown_ShouldNotGoBelowMinSize()
    {
        // Arrange
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithEnableAutoScaling(true)
            .WithMinSize(5)
            .WithMaxSize(20)
            .WithScaleUpThreshold(0.91)
            .WithScaleDownThreshold(0.9)
            .WithScaleDownCooldownSeconds(0)
            .Build();

        // Act：等待缩容执行
        Thread.Sleep(6000);
        var stats = pool.GetStats();

        // Assert
        Assert.True(stats.CurrentSize >= 5);
    }
}