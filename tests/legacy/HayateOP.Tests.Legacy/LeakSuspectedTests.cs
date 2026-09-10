namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// The suspected-leak back-check channel (LeakSuspectedCount) when leak detection is off.
/// With EnableLeakDetection=false, TakeSnapshot counts suspected leaks using the same threshold,
/// counting only, without capture (no AcquireTrace collection), and without reclamation; the capture semantics are unchanged.
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

        // Borrow 1 and keep it (the rest stay idle), wait beyond the back-check threshold
        var obj = pool.Acquire();
        Thread.Sleep(300);

        var snapshot = pool.TakeSnapshot();
        Assert.True(snapshot.LeakSuspectedCount >= 1,
            $"A borrowed object past the threshold without return should count toward LeakSuspectedCount; actual {snapshot.LeakSuspectedCount}");
        Assert.Equal(0, snapshot.LeakCount);
        Assert.Empty(snapshot.LeakTraces);

        // Continuous retention -> the cumulative count keeps increasing (consistent with LeakDetectedCount's cumulative accounting)
        Thread.Sleep(100);
        var snapshot2 = pool.TakeSnapshot();
        Assert.True(snapshot2.LeakSuspectedCount > snapshot.LeakSuspectedCount,
            "Repeated snapshots during retention should keep accumulating the suspected count");

        // After return no new suspected count is added (the cumulative value stays unchanged)
        pool.Release(obj);
        var snapshot3 = pool.TakeSnapshot();
        Assert.Equal(snapshot2.LeakSuspectedCount, snapshot3.LeakSuspectedCount);
    }

    [Fact]
    public void LeakDetectionOff_AllFeaturesOff_ShouldStillCountSuspected()
    {
        // 2.5 behavioral companion: LastBorrowedAt is recorded unconditionally on the borrow path,
        // so the back-check channel stays available even with eviction / leakDetection / autoScaling all off
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
            "With all features off, a borrowed object past the threshold should still be counted by the back-check");

        pool.Release(obj);
    }

    [Fact]
    public void LeakDetectionOn_ShouldNotCountSuspected()
    {
        // When detection is on it uses the existing LeakDetectedCount channel; the two counters never double-count
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
        Assert.True(snapshot.LeakCount >= 1, "With detection on, a borrowed object past the threshold should count toward LeakDetectedCount");
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
            "LeakSuspectedCount should also be exposed via GetStats");

        pool.Release(obj);
    }
}
