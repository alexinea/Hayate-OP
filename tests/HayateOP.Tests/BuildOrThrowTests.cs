namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// M13：BuildOrThrow(bool) 容错构建入口。
/// 默认 Build() 行为不变（失败抛 InvalidOperationException）；
/// BuildOrThrow(false) 在构建失败时降级返回可用空池 + 错误日志。
/// </summary>
public class BuildOrThrowTests
{
    private class TestObject { }

    [Fact(Timeout = 30_000)]
    public void Build_InvalidOptions_ShouldThrow()
    {
        // 基线：默认 Build() 在配置非法时抛出（本用例经 Configure 绕过 Builder 参数校验）
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

        // 降级池为空池：无预热对象
        var stats = pool.GetStats();
        Assert.Equal(0, stats.PooledCount);
        Assert.Equal(0, stats.CurrentSize);

        // 降级池保留拒绝策略语义：BlockTimeout 空池借出按短超时抛 TimeoutException
        Assert.Throws<TimeoutException>(() => pool.Acquire(TimeSpan.FromMilliseconds(200)));

        // 降级池可安全 Dispose
        pool.Dispose();
    }

    [Fact(Timeout = 30_000)]
    public void BuildOrThrow_False_MetricsMismatch_ShouldDegradeToEmptyPool()
    {
        // 2.2 行为变更：显式注册自定义 metrics 但未开启 EnableMetrics → Build 快速失败；
        // BuildOrThrow(false) 应降级为空池而非崩溃
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
        // 合法配置下 BuildOrThrow(false) 与 Build() 等价（含正常预热）
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
