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
        // PR-D L9（2.1 行为变更）：autoScaling off 不再塌缩 Max=Min，显式 Max 保留为硬上限
        Assert.Equal(100, options.MaxPoolSize);
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
            .WithScalingInterval(200)          // 缩短定时器周期，让缩容判定尽快反复触发
            .WithScaleUpThreshold(0.91)
            .WithScaleDownThreshold(0.9)
            .WithScaleDownStep(5)
            .WithScaleDownCooldownSeconds(0)
            .Build();

        // Act：事件驱动等待——轮询池状态直到缩放定时器已运行至少一轮，
        // 而不是盲睡固定 6s。上限 = deadline，一旦到点立即停，避免无界等待拖慢套件。
        var deadline = Environment.TickCount + 3000;   // 3s 看门狗，远超 200ms 间隔 × 多轮
        var minObserved = int.MaxValue;
        while (Environment.TickCount < deadline)
        {
            var current = pool.GetStats().CurrentSize;
            if (current < minObserved) minObserved = current;
            Thread.Sleep(50);                          // 有界轮询退避，不吃满 CPU
        }

        // Assert：任何时刻池大小都不应跌破 MinPoolSize
        Assert.True(minObserved >= 5, $"缩容不得跌破 MinPoolSize，实际观察到的最小 CurrentSize={minObserved}");
        Assert.True(pool.GetStats().CurrentSize >= 5);
    }
}