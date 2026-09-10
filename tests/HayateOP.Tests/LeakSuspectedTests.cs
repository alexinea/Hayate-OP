namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// The suspected-leak callback path (LeakSuspectedCount) when leak detection is off.
/// When EnableLeakDetection=false, TakeSnapshot counts suspected leaks by the same threshold,
/// counting only, without evidence capture (no AcquireTrace collection) and without reclaim; the capture semantics are unchanged.
/// </summary>
public class LeakSuspectedTests
{
    private class TestObject { }

    [Fact(Timeout = 30_000)]
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

        // Borrow 1 and keep it (the rest stay idle), wait beyond the callback threshold
        var obj = pool.Acquire();
        Thread.Sleep(300);

        var snapshot = pool.TakeSnapshot();
        Assert.True(snapshot.LeakSuspectedCount >= 1,
            $"Borrowed object past threshold with no return should be counted in LeakSuspectedCount, actual {snapshot.LeakSuspectedCount}");
        Assert.Equal(0, snapshot.LeakCount);
        Assert.Empty(snapshot.LeakTraces);

        // Continuing to linger -> the cumulative count keeps increasing (consistent with LeakDetectedCount's cumulative measure)
        Thread.Sleep(100);
        var snapshot2 = pool.TakeSnapshot();
        Assert.True(snapshot2.LeakSuspectedCount > snapshot.LeakSuspectedCount,
            "Repeated snapshots during lingering should keep accumulating the suspected count");

        // After return no new suspected count (cumulative value stays unchanged)
        pool.Release(obj);
        var snapshot3 = pool.TakeSnapshot();
        Assert.Equal(snapshot2.LeakSuspectedCount, snapshot3.LeakSuspectedCount);
    }

    [Fact(Timeout = 30_000)]
    public void LeakDetectionOff_AllFeaturesOff_ShouldStillCountSuspected()
    {
        // Behavior companion for 2.5: LastBorrowedAt is recorded unconditionally on the borrow path,
        // so the callback path still works even with eviction / leakDetection / autoScaling all off
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
            "With all features off, a borrowed object past threshold should still be counted by the callback");

        pool.Release(obj);
    }

    [Fact(Timeout = 30_000)]
    public void LeakDetectionOn_ShouldNotCountSuspected()
    {
        // When detection is on it uses the original LeakDetectedCount path; the two counters do not double-count
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
        Assert.True(snapshot.LeakCount >= 1, "With detection on, a borrowed object past threshold should be counted in LeakDetectedCount");
        Assert.Equal(0, snapshot.LeakSuspectedCount);

        pool.Release(obj);
    }

    [Fact(Timeout = 30_000)]
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
            "LeakSuspectedCount should also be exposed in GetStats");

        pool.Release(obj);
    }
}
