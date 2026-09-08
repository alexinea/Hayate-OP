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

        // T13：原断言 LeakCount >= 0 为恒真（掩盖了 T04 后泄漏检测失效的缺陷）。
        // 修复后 TakeSnapshot 遍历 _objectMap（含借出对象），借出超阈值未归还必须被检出。
        // 单次快照仅扫描一次，借出 1 个超阈值对象 → LeakCount 恰为 1 且含调用栈。
        Assert.True(snapshot.LeakCount >= 1,
            $"借出超阈值对象应被检出泄漏，实际 LeakCount={snapshot.LeakCount}");
        Assert.NotEmpty(snapshot.LeakTraces);
    }
}