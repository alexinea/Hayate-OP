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
        var mockMetrics = new Mock<IHayateMetrics>();
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithEnableMetrics(false)
            .WithMetrics(mockMetrics.Object)
            .Build();

        var obj = pool.Acquire();
        pool.Release(obj);

        var stats = pool.GetStats();
        Assert.Equal(0, stats.TotalReleased);
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