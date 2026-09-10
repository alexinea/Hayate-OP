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

    [Fact(Timeout = 60000)]
    public void Build_WithCustomMetricsButMetricsDisabled_Throws()
    {
        // Behavior change (2.2): when a custom metrics is explicitly registered but EnableMetrics is not enabled,
        // Build() fails fast instead of silently replacing it with EmptyHayateMetrics.
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

    [Fact(Timeout = 60000)]
    public void Build_MetricsDisabledWithoutCustomMetrics_DoesNotThrow()
    {
        // Default (no custom metrics registered) + metrics disabled: a valid configuration that does not throw.
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

        // Force one statistics event so the pool finishes initialization
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