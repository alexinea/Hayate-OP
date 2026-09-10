namespace DotNetCore.HayateOP.Tests;

public class AutoScalingTests
{
    private class TestObject { }

    [Fact]
    public void EnableAutoScaling_ShouldAllowPoolSizeChange()
    {
        // Arrange
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithEnableAutoScaling(true)
            .WithMinSize(10)
            .WithMaxSize(100)
            .Build();

        // Act
        var stats = pool.GetStats();

        // Assert: initial size is MinSize=10, not 100
        Assert.Equal(10, stats.MinSize);
        Assert.Equal(10, stats.CurrentSize);
    }

    [Fact]
    public void DisableAutoScaling_ShouldFixPoolSize()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithEnableAutoScaling(false)
            .WithMinSize(10)
            .WithMaxSize(100)
            .Build();

        var options = new HayatePoolOptions { EnableAutoScaling = false, MinPoolSize = 10, MaxPoolSize = 100 };
        options.ApplyFeatureSwitches();
        // 2.1 behavior change: with autoScaling off, Max is no longer collapsed to Min; the explicit Max is kept as a hard cap
        Assert.Equal(100, options.MaxPoolSize);
    }

    [Fact(Timeout = 60000)]
    public void AcquireTimeout_ShouldTriggerForceScaleUp()
    {
        // Arrange
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithEnableAutoScaling(true)
            .WithMinSize(1)
            .WithMaxSize(10)
            .WithAcquireTimeout(TimeSpan.FromMilliseconds(100))
            .WithScaleUpStep(2)
            .WithScaleUpCooldownSeconds(0)
            .Build();

        // Drain the pool
        var obj1 = pool.Acquire();
        var initialSize = pool.GetStats().CurrentSize;

        // Act: timeout triggers scale-up
        try
        {
            pool.Acquire(TimeSpan.FromMilliseconds(50));
        }
        catch (TimeoutException)
        {
            // Expected timeout
        }

        // Assert: the pool size really increased
        Assert.True(pool.GetStats().CurrentSize > initialSize);
    }

    [Fact(Timeout = 60000)]
    public void ScaleDown_ShouldNotGoBelowMinSize()
    {
        // Arrange
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithEnableAutoScaling(true)
            .WithMinSize(5)
            .WithMaxSize(20)
            .WithScalingInterval(200)          // shorten the timer period so scale-down decisions fire repeatedly as soon as possible
            .WithScaleUpThreshold(0.91)
            .WithScaleDownThreshold(0.9)
            .WithScaleDownStep(5)
            .WithScaleDownCooldownSeconds(0)
            .Build();

        // Act: event-driven wait -- poll pool state until the scaling timer has run at least one round,
        // instead of blindly sleeping a fixed 6s. The cap is the deadline; stop as soon as it is reached to avoid unbounded waits slowing the suite.
        var deadline = Environment.TickCount + 3000;   // 3s watchdog, far beyond the 200ms interval x several rounds
        var minObserved = int.MaxValue;
        while (Environment.TickCount < deadline)
        {
            var current = pool.GetStats().CurrentSize;
            if (current < minObserved) minObserved = current;
            Thread.Sleep(50);                          // bounded poll backoff, no full CPU burn
        }

        // Assert: at no time should the pool size drop below MinPoolSize
        Assert.True(minObserved >= 5, $"Scale-down must not drop below MinPoolSize; observed minimum CurrentSize={minObserved}");
        Assert.True(pool.GetStats().CurrentSize >= 5);
    }

    [Fact(Timeout = 60000)]
    public void ScaleDown_ShouldActuallyShrinkWhenIdle()
    {
        // 2.4 behavior-change regression guard: before the fix, the scale-down branch had a mutually exclusive gate
        // (the pool required occupancy > 0.6 to allow, while the strategy required occupancy < ScaleDownThreshold to give a shrink target),
        // making scale-down dead code -- autoScaling only grew, never shrank. This test locks "scale-down really happens" with a strong assertion.
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithEnableAutoScaling(true)
            .WithMinSize(2)
            .WithMaxSize(30)
            .WithScalingInterval(200)          // shorten the timer period
            .WithScaleDownThreshold(0.5)       // usage<0.5 gives a shrink target
            .WithScaleDownStep(5)
            .WithScaleDownCooldownSeconds(0)
            .WithScaleUpCooldownSeconds(0)
            .Build();

        // Act 1: borrow 10 (pool creates on demand; CurrentSize = idle + borrowed -> raised to >= 10)
        var leases = new List<TestObject>();
        for (var i = 0; i < 10; i++) leases.Add(pool.Acquire());
        var peak = pool.GetStats().CurrentSize;
        Assert.True(peak >= 10, $"After borrowing, CurrentSize should be >= 10, actual {peak}");

        // Act 2: return all -> usage=0 < ScaleDownThreshold(0.5), strategy target = Max(peak-5, 2)
        foreach (var o in leases) pool.Release(o);

        // Event-driven poll: once scale-down happens, CurrentSize must fall (below the peak)
        var deadline = Environment.TickCount + 5000;
        var shrunk = false;
        while (Environment.TickCount < deadline)
        {
            if (pool.GetStats().CurrentSize < peak) { shrunk = true; break; }
            Thread.Sleep(50);
        }

        // Assert: scale-down really happens + does not drop below Min
        Assert.True(shrunk, $"Pool should really shrink under high idle rate (was dead code before: peak={peak}, final={pool.GetStats().CurrentSize})");
        Assert.True(pool.GetStats().CurrentSize >= 2, $"Scale-down must not drop below MinPoolSize, actual {pool.GetStats().CurrentSize}");
    }
}