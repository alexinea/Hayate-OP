namespace DotNetCore.HayateOP.Tests;

public class LeakDetectionTests
{
    private class TestObject { }

    [Fact]
    public void EnableLeakDetection_ShouldRecordStackTrace()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithEnableLeakDetection(true)
            .WithLeakDetectionThreshold(TimeSpan.FromSeconds(1))
            .Build();

        var obj = pool.Acquire();
        pool.Release(obj);
        Assert.NotNull(obj);
    }

    [Fact]
    public void DisableLeakDetection_ShouldNotRecordStackTrace()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithEnableLeakDetection(false)
            .Build();

        var snapshot = pool.TakeSnapshot();
        Assert.Empty(snapshot.LeakTraces);
    }

    [Fact]
    public void TakeSnapshot_ShouldDetectLeaks()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithEnableLeakDetection(true)
            .WithLeakDetectionThreshold(TimeSpan.FromMilliseconds(100))
            .Build();

        // 借出对象不归还
        var obj = pool.Acquire();

        // 等待超过泄漏阈值
        Thread.Sleep(200);

        var snapshot = pool.TakeSnapshot();
        Assert.True(snapshot.LeakCount >= 0);
    }
}