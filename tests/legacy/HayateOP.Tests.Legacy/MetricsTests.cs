using DotNetCore.HayateOP.Metrics;
using Moq;

namespace DotNetCore.HayateOP.Tests;

public class MetricsTests
{
    private class TestObject { }

    [Fact]
    public void EnableMetrics_ShouldUpdateStats()
    {
        var mockMetrics = new Mock<IHayateMetrics>();
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithEnableMetrics(true)
            .WithMetrics(mockMetrics.Object)
            .Build();

        var obj = pool.Acquire();
        pool.Release(obj);

        mockMetrics.Verify(m => m.RecordObjectAcquired(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<double>()), Times.Once);
        mockMetrics.Verify(m => m.RecordObjectReleased(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<bool>()), Times.Once);
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

    [Fact]
    public void Build_WithCustomMetricsButMetricsDisabled_Throws()
    {
        // L8（2.2 行为变更）：显式注册自定义 metrics 却未开启 EnableMetrics 时，
        // Build() 快速失败，不再静默替换为 EmptyHayateMetrics。
        var mockMetrics = new Mock<IHayateMetrics>();

        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            using var pool = new HayatePoolBuilder<TestObject>()
                .WithEnableMetrics(false)
                .WithMetrics(mockMetrics.Object)
                .Build();
        });

        Assert.Contains("WithMetrics", ex.Message);
        Assert.Contains("WithEnableMetrics(true)", ex.Message);
    }

    [Fact]
    public void Build_MetricsDisabledWithoutCustomMetrics_DoesNotThrow()
    {
        // 默认（未注册自定义 metrics）+ 禁用 metrics：合法配置，不抛错。
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithEnableMetrics(false)
            .Build();

        Assert.NotNull(pool);
    }

    [Fact]
    public void GetStats_ShouldReturnCorrectValues()
    {
        // Arrange
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithEnableMetrics(true)
            .WithMinSize(5)
            .WithMaxSize(100)
            .Build();

        // 强制触发一次统计，让池完成初始化
        var dummy = pool.Acquire();
        pool.Release(dummy);

        // Act
        var stats = pool.GetStats();

        // Assert
        Assert.Equal(5, stats.PooledCount);
        Assert.Equal(5, stats.CurrentSize);
        Assert.Equal(5, stats.TotalCreated);
    }
}