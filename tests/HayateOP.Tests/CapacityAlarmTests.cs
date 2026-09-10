using DotNetCore.HayateOP.Metrics;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// Capacity alarm thresholds (WarnAtRatio / CriticalAtRatio + state-transition debounce callback).
/// </summary>
public class CapacityAlarmTests
{
    private class TestObject { }

    private sealed class ThrowingMetrics : IHayateMetrics
    {
        public void RecordObjectAcquired(string poolName, object item, double elapsedMilliseconds) { }
        public void RecordObjectReleased(string poolName, object item, bool isValid) { }
        public void RecordObjectMiss(string poolName) { }
        public void RecordPoolScaled(string poolName, string action, int oldSize, int newSize) { }
    }

    [Fact(Timeout = 30_000)]
    public void WarningThreshold_ShouldFireOnce_AndNotRefireAtSameLevel()
    {
        var warnCount = new int[1];
        var criticalCount = new int[1];

        // Min=Max=10 and auto-scaling/eviction off, so the borrow water level is fully controlled by the case with no background interference
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(10)
            .WithMaxSize(10)
            .WithEnableAutoScaling(false)
            .WithEnableEviction(false)
            .WithCapacityAlarm(0.8, 0.95)
            .WithOnCapacityWarning(_ => Interlocked.Increment(ref warnCount[0]))
            .WithOnCapacityCritical(_ => Interlocked.Increment(ref criticalCount[0]))
            .Build();

        var items = new List<TestObject>();

        // Borrow 8 -> utilization 0.8 >= 0.8, first warning fires
        for (var i = 0; i < 8; i++) items.Add(pool.Acquire());
        Assert.Equal(1, warnCount[0]);
        Assert.Equal(0, criticalCount[0]);

        // Borrow 1 more -> 0.9 is still at the Warning level; state has not flipped, so it must not fire again
        items.Add(pool.Acquire());
        Assert.Equal(1, warnCount[0]);

        // Return 1 -> 0.8 is still at the Warning level, no fire
        pool.Release(items[items.Count - 1]);
        items.RemoveAt(items.Count - 1);
        Assert.Equal(1, warnCount[0]);

        // Return 1 more -> 0.7 falls back to Normal, silently resets
        pool.Release(items[items.Count - 1]);
        items.RemoveAt(items.Count - 1);
        Assert.Equal(1, warnCount[0]);

        // Borrow back up to 0.8 -> state flips, second fire
        items.Add(pool.Acquire());
        items.Add(pool.Acquire());
        Assert.Equal(2, warnCount[0]);
        Assert.Equal(0, criticalCount[0]);

        foreach (var item in items) pool.Release(item);
    }

    [Fact(Timeout = 30_000)]
    public void CriticalThreshold_ShouldFire_WhenFullyDrained()
    {
        var warnCount = new int[1];
        var criticalCount = new int[1];
        HayatePoolCapacityAlarmEventArgs? lastArgs = null;

        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(10)
            .WithMaxSize(10)
            .WithEnableAutoScaling(false)
            .WithEnableEviction(false)
            .WithCapacityAlarm(0.5, 0.95)
            .WithOnCapacityWarning(args =>
            {
                Interlocked.Increment(ref warnCount[0]);
                Interlocked.Exchange(ref lastArgs, args);
            })
            .WithOnCapacityCritical(args =>
            {
                Interlocked.Increment(ref criticalCount[0]);
                Interlocked.Exchange(ref lastArgs, args);
            })
            .Build();

        var items = new List<TestObject>();

        // Borrow 5 -> 0.5 triggers warning
        for (var i = 0; i < 5; i++) items.Add(pool.Acquire());
        Assert.Equal(1, warnCount[0]);

        // Borrow to full pool -> 1.0 >= 0.95, state flips and triggers Critical (Normal -> Critical jumps directly, no warning re-issued)
        for (var i = 5; i < 10; i++) items.Add(pool.Acquire());
        Assert.Equal(1, warnCount[0]);
        Assert.Equal(1, criticalCount[0]);

        // Verify the event-argument shape
        var args = lastArgs;
        Assert.NotNull(args);
        Assert.Equal(HayatePoolCapacityAlarmLevel.Critical, args.Level);
        Assert.Equal(1.0, args.UsageRatio, 5);
        Assert.Equal(10, args.BorrowedCount);
        Assert.Equal(10, args.MaxPoolSize);
        Assert.Equal(typeof(TestObject).Name, args.PoolName);

        // Return all -> falls back and resets; borrow to full again -> Critical fires again (re-entrant after debounce)
        foreach (var item in items) pool.Release(item);
        for (var i = 0; i < 10; i++) items.Add(pool.Acquire());
        Assert.Equal(2, criticalCount[0]);

        foreach (var item in items) pool.Release(item);
    }

    [Fact(Timeout = 30_000)]
    public void DisabledByDefault_ShouldNotFireCallbacks()
    {
        // Default WarnAtRatio=0 / CriticalAtRatio=0: even a full-pool borrow does not fire when no alarm is configured
        var fired = new int[1];

        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(10)
            .WithMaxSize(10)
            .WithEnableAutoScaling(false)
            .WithEnableEviction(false)
            .WithOnCapacityWarning(_ => Interlocked.Increment(ref fired[0]))
            .WithOnCapacityCritical(_ => Interlocked.Increment(ref fired[0]))
            .Build();

        var items = new List<TestObject>();
        for (var i = 0; i < 10; i++) items.Add(pool.Acquire());

        Assert.Equal(0, Volatile.Read(ref fired[0]));
        Assert.Equal(0, pool.GetOptions().WarnAtRatio);
        Assert.Equal(0, pool.GetOptions().CriticalAtRatio);

        foreach (var item in items) pool.Release(item);
    }

    [Fact(Timeout = 30_000)]
    public void CallbackException_ShouldNotBreakAcquire()
    {
        // A user callback exception must be swallowed by the pool (logged) and must not affect the borrow main flow
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(10)
            .WithMaxSize(10)
            .WithEnableAutoScaling(false)
            .WithEnableEviction(false)
            .WithCapacityAlarm(0.5, 0.95)
            .WithOnCapacityWarning(_ => throw new InvalidOperationException("callback boom"))
            .Build();

        var items = new List<TestObject>();
        for (var i = 0; i < 5; i++) items.Add(pool.Acquire());

        Assert.Equal(5, items.Count);
        Assert.All(items, Assert.NotNull);

        foreach (var item in items) pool.Release(item);
    }

    [Fact(Timeout = 30_000)]
    public void InvalidThresholds_ShouldBeRejectedByBuilder()
    {
        var builder = new HayatePoolBuilder<TestObject>();

        Assert.Throws<ArgumentOutOfRangeException>(() => builder.WithCapacityAlarm(-0.1));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.WithCapacityAlarm(1.5));
        // When both levels are enabled, Critical must not be below Warning
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.WithCapacityAlarm(0.9, 0.8));
    }

    [Fact(Timeout = 30_000)]
    public void ThresholdNormalization_ShouldClampToValidRange()
    {
        // A threshold greater than 1 is clamped to 1 by ApplyFeatureSwitches (verified indirectly via Build)
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(1)
            .WithMaxSize(2)
            .WithEnableAutoScaling(false)
            .WithEnableEviction(false)
            .Configure(o =>
            {
                o.WarnAtRatio = 2.0;
                o.CriticalAtRatio = 3.0;
            })
            .Build();

        var options = pool.GetOptions();
        Assert.Equal(1.0, options.WarnAtRatio, 5);
        Assert.Equal(1.0, options.CriticalAtRatio, 5);
    }
}
