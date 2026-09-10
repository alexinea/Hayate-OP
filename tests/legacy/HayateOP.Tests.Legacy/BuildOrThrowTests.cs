namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// BuildOrThrow(bool) fault-tolerant build entry point.
/// The default Build() behavior is unchanged (throws InvalidOperationException on failure);
/// BuildOrThrow(false) degrades on build failure, returning a usable empty pool + an error log.
/// </summary>
public class BuildOrThrowTests
{
    private class TestObject { }

    [Fact]
    public void Build_InvalidOptions_ShouldThrow()
    {
        // Baseline: the default Build() throws on illegal configuration (this case bypasses Builder argument validation via Configure)
        var builder = new HayatePoolBuilder<TestObject>()
            .Configure(o => o.DefaultAcquireTimeout = TimeSpan.Zero);

        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Fact]
    public void BuildOrThrow_Default_ShouldThrowLikeBuild()
    {
        var builder = new HayatePoolBuilder<TestObject>()
            .Configure(o => o.DefaultAcquireTimeout = TimeSpan.Zero);

        Assert.Throws<InvalidOperationException>(() => builder.BuildOrThrow());
    }

    [Fact]
    public void BuildOrThrow_False_InvalidOptions_ShouldReturnUsableEmptyPool()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("degraded-pool")
            .Configure(o => o.DefaultAcquireTimeout = TimeSpan.Zero)
            .BuildOrThrow(false);

        Assert.NotNull(pool);

        // The degraded pool is empty: no warmed-up objects
        var stats = pool.GetStats();
        Assert.Equal(0, stats.PooledCount);
        Assert.Equal(0, stats.CurrentSize);

        // The degraded pool keeps the reject-policy semantics: a BlockTimeout empty-pool borrow throws TimeoutException on the short timeout
        Assert.Throws<TimeoutException>(() => pool.Acquire(TimeSpan.FromMilliseconds(200)));

        // The degraded pool can be disposed safely
        pool.Dispose();
    }

    [Fact]
    public void BuildOrThrow_False_MetricsMismatch_ShouldDegradeToEmptyPool()
    {
        // Behavior change (2.2): registering a custom metrics implementation without enabling EnableMetrics -> Build fails fast;
        // BuildOrThrow(false) should degrade to an empty pool instead of crashing
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("metrics-mismatch-pool")
            .WithMetrics(new ThrowingMetrics())
            .BuildOrThrow(false);

        Assert.NotNull(pool);
        Assert.Equal(0, pool.GetStats().CurrentSize);
    }

    [Fact]
    public void BuildOrThrow_False_ValidOptions_ShouldBuildNormally()
    {
        // With valid configuration, BuildOrThrow(false) is equivalent to Build() (including normal warm-up)
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
