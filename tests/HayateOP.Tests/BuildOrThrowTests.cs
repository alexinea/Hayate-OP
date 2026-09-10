namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// BuildOrThrow(bool): the fault-tolerant build entry point.
/// The default Build() behavior is unchanged (throws InvalidOperationException on failure);
/// BuildOrThrow(false) degrades on build failure to return a usable empty pool plus an error log.
/// </summary>
public class BuildOrThrowTests
{
    private class TestObject { }

    [Fact(Timeout = 30_000)]
    public void Build_InvalidOptions_ShouldThrow()
    {
        // Baseline: the default Build() throws on invalid configuration (this case bypasses Builder parameter validation via Configure)
        var builder = new HayatePoolBuilder<TestObject>()
            .Configure(o => o.DefaultAcquireTimeout = TimeSpan.Zero);

        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Fact(Timeout = 30_000)]
    public void BuildOrThrow_Default_ShouldThrowLikeBuild()
    {
        var builder = new HayatePoolBuilder<TestObject>()
            .Configure(o => o.DefaultAcquireTimeout = TimeSpan.Zero);

        Assert.Throws<InvalidOperationException>(() => builder.BuildOrThrow());
    }

    [Fact(Timeout = 30_000)]
    public void BuildOrThrow_False_InvalidOptions_ShouldReturnUsableEmptyPool()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("degraded-pool")
            .Configure(o => o.DefaultAcquireTimeout = TimeSpan.Zero)
            .BuildOrThrow(false);

        Assert.NotNull(pool);

        // The degraded pool is an empty pool: no pre-warmed objects
        var stats = pool.GetStats();
        Assert.Equal(0, stats.PooledCount);
        Assert.Equal(0, stats.CurrentSize);

        // The degraded pool keeps reject-policy semantics: a BlockTimeout empty-pool borrow throws TimeoutException on a short timeout
        Assert.Throws<TimeoutException>(() => pool.Acquire(TimeSpan.FromMilliseconds(200)));

        // The degraded pool can be safely disposed
        pool.Dispose();
    }

    [Fact(Timeout = 30_000)]
    public void BuildOrThrow_False_MetricsMismatch_ShouldDegradeToEmptyPool()
    {
        // Behavior change (2.2): a custom metrics is explicitly registered but EnableMetrics is not enabled -> Build fails fast;
        // BuildOrThrow(false) should degrade to an empty pool instead of crashing
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("metrics-mismatch-pool")
            .WithMetrics(new ThrowingMetrics())
            .BuildOrThrow(false);

        Assert.NotNull(pool);
        Assert.Equal(0, pool.GetStats().CurrentSize);
    }

    [Fact(Timeout = 30_000)]
    public void BuildOrThrow_False_ValidOptions_ShouldBuildNormally()
    {
        // With valid configuration BuildOrThrow(false) is equivalent to Build() (including normal warm-up)
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(3)
            .WithMaxSize(5)
            .BuildOrThrow(false);

        Assert.NotNull(pool);
        Assert.Equal(3, pool.GetStats().PooledCount);

        var obj = pool.Acquire();
        Assert.NotNull(obj);
        pool.Release(obj);
    }

    private sealed class ThrowingMetrics : DotNetCore.HayateOP.Metrics.IHayateMetrics
    {
        public void RecordObjectAcquired(string poolName, object item, double elapsedMilliseconds) { }
        public void RecordObjectReleased(string poolName, object item, bool isValid) { }
        public void RecordObjectMiss(string poolName) { }
        public void RecordPoolScaled(string poolName, string action, int oldSize, int newSize) { }
    }
}
