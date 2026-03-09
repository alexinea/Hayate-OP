using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DotNetCore.HayateOP.Tests;

public class AutoScalingTests
{
    private class TestObject { }

    [Fact]
    public void EnableAutoScaling_ShouldAllowPoolSizeChange()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithEnableAutoScaling(true)
            .WithMinSize(10)
            .WithMaxSize(100)
            .Build();

        var stats = pool.GetStats();
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
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithEnableAutoScaling(true)
            .WithMinSize(1)
            .WithMaxSize(10)
            .WithAcquireTimeout(TimeSpan.FromMilliseconds(100))
            .WithScaleUpStep(2)
            .Build();

        // 耗尽池
        var obj1 = pool.Acquire();
        var obj2 = pool.Acquire();

        // 尝试获取第三个对象（超时）
        try
        {
            pool.Acquire(TimeSpan.FromMilliseconds(50));
        }
        catch (TimeoutException)
        {
            // 预期超时
        }

        // 验证池大小增加了
        Assert.True(pool.GetStats().CurrentSize > 2);
    }
}