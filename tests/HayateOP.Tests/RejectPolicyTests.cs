namespace DotNetCore.HayateOP.Tests;

public class RejectPolicyTests
{
    private class TestObject { }

    [Fact]
    public void RejectPolicy_Abort_ShouldThrowImmediatelyWhenNoIdleObjects()
    {
        // Arrange
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(1)
            .WithMaxSize(1)
            .WithRejectPolicy(HayatePoolRejectPolicy.Abort)
            .WithEnableAutoScaling(false)
            .Build();

        // 耗尽池
        var obj1 = pool.Acquire();

        // Act & Assert：无空闲对象时立即抛异常，不等待
        var exception = Assert.Throws<InvalidOperationException>(() => pool.Acquire());
        Assert.Contains("无可用对象", exception.Message);
    }

    [Fact]
    public void RejectPolicy_Block_ShouldWaitUntilObjectAvailable()
    {
        // Arrange
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(1)
            .WithMaxSize(1)
            .WithRejectPolicy(HayatePoolRejectPolicy.Block)
            .Build();

        var obj1 = pool.Acquire();
        TestObject obj2 = null;

        // Act：100ms后归还对象
        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            pool.Release(obj1);
        });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        obj2 = pool.Acquire();
        sw.Stop();

        // Assert
        Assert.NotNull(obj2);
        Assert.Same(obj1, obj2);
        Assert.True(sw.ElapsedMilliseconds >= 80);
    }

    [Fact]
    public void RejectPolicy_BlockTimeout_ShouldThrowAfterTimeout()
    {
        // Arrange
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(1)
            .WithMaxSize(1)
            .WithRejectPolicy(HayatePoolRejectPolicy.BlockTimeout)
            .WithAcquireTimeout(TimeSpan.FromMilliseconds(100))
            .Build();

        var obj1 = pool.Acquire();

        // Act & Assert
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var exception = Assert.Throws<TimeoutException>(() => pool.Acquire());
        sw.Stop();

        Assert.Contains("超时", exception.Message);
        Assert.True(sw.ElapsedMilliseconds >= 80 && sw.ElapsedMilliseconds <= 200);
    }

    [Fact]
    public void RejectPolicy_CreateNew_ShouldReturnNewObjectWhenTimeout()
    {
        // Arrange
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(1)
            .WithMaxSize(1)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateNew)
            .WithAcquireTimeout(TimeSpan.FromMilliseconds(100))
            .WithEnableMetrics(true)
            .Build();

        var obj1 = pool.Acquire();
        var initialCount = pool.GetStats().CurrentSize;

        // Act
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var obj2 = pool.Acquire();
        sw.Stop();

        // Assert
        Assert.NotNull(obj2);
        Assert.NotSame(obj1, obj2);
        Assert.True(sw.ElapsedMilliseconds >= 80);
        Assert.Equal(1, pool.GetStats().TotalMissed);
        Assert.Equal(initialCount, pool.GetStats().CurrentSize); // 新对象不加入池
    }

    [Fact]
    public void RejectPolicy_CreateNew_ShouldNotAddNewObjectToPool()
    {
        // Arrange
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(1)
            .WithMaxSize(1)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateNew)
            .WithAcquireTimeout(TimeSpan.FromMilliseconds(50))
            .Build();

        var obj1 = pool.Acquire();

        // Act
        var obj2 = pool.Acquire();
        pool.Release(obj2);

        // Assert：新对象不加入池，空闲数为0
        Assert.Equal(0, pool.GetStats().PooledCount);
    }
}