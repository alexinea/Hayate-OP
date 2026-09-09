namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// M4：关闭泄漏检测时的回查告警通路（LeakSuspectedCount）。
/// EnableLeakDetection=false 时 TakeSnapshot 按同一阈值统计疑似泄漏，
/// 仅计数、不取证（无 AcquireTrace 采集）、不回收，L1 取证语义不变。
/// </summary>
public class LeakSuspectedTests
{
    private class TestObject { }

    [Fact]
    public void LeakDetectionOff_BorrowedBeyondThreshold_ShouldCountSuspected()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(2)
            .WithMaxSize(4)
            .WithEnableLeakDetection(false)
            .WithEnableEviction(false)
            .WithEnableAutoScaling(false)
            .WithLeakDetectionThreshold(TimeSpan.FromMilliseconds(150))
            .Build();

        // 借出 1 个并滞留（其余保持空闲），等待超过回查阈值
        var obj = pool.Acquire();
        Thread.Sleep(300);

        var snapshot = pool.TakeSnapshot();
        Assert.True(snapshot.LeakSuspectedCount >= 1,
            $"借出超阈值未归还对象应计入 LeakSuspectedCount，实际 {snapshot.LeakSuspectedCount}");
        Assert.Equal(0, snapshot.LeakCount);
        Assert.Empty(snapshot.LeakTraces);

        // 持续滞留 → 累计计数继续递增（与 LeakDetectedCount 的累计口径一致）
        Thread.Sleep(100);
        var snapshot2 = pool.TakeSnapshot();
        Assert.True(snapshot2.LeakSuspectedCount > snapshot.LeakSuspectedCount,
            "滞留期间重复快照应继续累计疑似计数");

        // 归还后不再新增疑似计数（累计值保持不变）
        pool.Release(obj);
        var snapshot3 = pool.TakeSnapshot();
        Assert.Equal(snapshot2.LeakSuspectedCount, snapshot3.LeakSuspectedCount);
    }

    [Fact]
    public void LeakDetectionOff_AllFeaturesOff_ShouldStillCountSuspected()
    {
        // 2.5 行为配套：LastBorrowedAt 在借出路径无条件记录，
        // 即使 eviction / leakDetection / autoScaling 全关，回查通路依然可用
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(1)
            .WithMaxSize(2)
            .WithEnableLeakDetection(false)
            .WithEnableEviction(false)
            .WithEnableAutoScaling(false)
            .WithEnableValidation(false)
            .WithEnableGenerationOptimization(false)
            .WithLeakDetectionThreshold(TimeSpan.FromMilliseconds(150))
            .Build();

        var obj = pool.Acquire();
        Thread.Sleep(300);

        var snapshot = pool.TakeSnapshot();
        Assert.True(snapshot.LeakSuspectedCount >= 1,
            "全功能关闭配置下借出超阈值对象仍应被回查统计");

        pool.Release(obj);
    }

    [Fact]
    public void LeakDetectionOn_ShouldNotCountSuspected()
    {
        // 检测开启时走原有 LeakDetectedCount 通路，两计数互不重复计账
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(1)
            .WithMaxSize(2)
            .WithEnableLeakDetection(true)
            .WithEnableEviction(false)
            .WithEnableAutoScaling(false)
            .WithLeakDetectionThreshold(TimeSpan.FromMilliseconds(100))
            .Build();

        var obj = pool.Acquire();
        Thread.Sleep(250);

        var snapshot = pool.TakeSnapshot();
        Assert.True(snapshot.LeakCount >= 1, "检测开启时借出超阈值对象应计入 LeakDetectedCount");
        Assert.Equal(0, snapshot.LeakSuspectedCount);

        pool.Release(obj);
    }

    [Fact]
    public void LeakDetectionOff_ShouldExposeSuspectedCountInStats()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(1)
            .WithMaxSize(2)
            .WithEnableLeakDetection(false)
            .WithEnableEviction(false)
            .WithEnableAutoScaling(false)
            .WithLeakDetectionThreshold(TimeSpan.FromMilliseconds(150))
            .Build();

        var obj = pool.Acquire();
        Thread.Sleep(300);

        Assert.True(pool.TakeSnapshot().LeakSuspectedCount >= 1);
        Assert.True(pool.GetStats().LeakSuspectedCount >= 1,
            "LeakSuspectedCount 应同步暴露到 GetStats");

        pool.Release(obj);
    }
}
