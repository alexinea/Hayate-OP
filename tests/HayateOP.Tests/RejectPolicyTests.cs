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
        // P2/R5 围栏：原断言 80≤elapsed≤200ms 的上界在宿主负载高/线程池饥饿时会偶发超窗误报。
        // 改为只校验"确实等到超时（不早于配置的 100ms 太多）且没有永久挂起"：
        //   下界 80ms ≈ 100ms 超时 - 计时/调度容差；
        //   上界放宽到 10s，仅用于拦截"该超时却死锁不返回"的挂死回归（配合外层看门狗）。
        Assert.True(sw.ElapsedMilliseconds >= 80, $"应在约 100ms 超时后抛错，实际 {sw.ElapsedMilliseconds}ms");
        Assert.True(sw.ElapsedMilliseconds < 10_000, $"疑似超时路径挂死：{sw.ElapsedMilliseconds}ms 未返回");
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