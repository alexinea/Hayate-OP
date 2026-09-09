using DotNetCore.HayateOP.Metrics;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// M12：容量告警阈值（WarnAtRatio / CriticalAtRatio + 状态翻转去抖回调）。
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

    [Fact]
    public void WarningThreshold_ShouldFireOnce_AndNotRefireAtSameLevel()
    {
        var warnCount = new int[1];
        var criticalCount = new int[1];

        // Min=Max=10 且关闭扩缩/驱逐，保证借出水位完全由用例控制、无后台干扰
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

        // 借出 8 个 → 使用率 0.8 ≥ 0.8，首次触发警告
        for (var i = 0; i < 8; i++) items.Add(pool.Acquire());
        Assert.Equal(1, warnCount[0]);
        Assert.Equal(0, criticalCount[0]);

        // 再借 1 个 → 0.9 仍处于 Warning 级别，状态未翻转，不得重复触发
        items.Add(pool.Acquire());
        Assert.Equal(1, warnCount[0]);

        // 归还 1 个 → 0.8 仍在 Warning 级别，不触发
        pool.Release(items[items.Count - 1]);
        items.RemoveAt(items.Count - 1);
        Assert.Equal(1, warnCount[0]);

        // 再归还 1 个 → 0.7 回落 Normal，静默复位
        pool.Release(items[items.Count - 1]);
        items.RemoveAt(items.Count - 1);
        Assert.Equal(1, warnCount[0]);

        // 重新借出至 0.8 → 状态翻转，第二次触发
        items.Add(pool.Acquire());
        items.Add(pool.Acquire());
        Assert.Equal(2, warnCount[0]);
        Assert.Equal(0, criticalCount[0]);

        foreach (var item in items) pool.Release(item);
    }

    [Fact]
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

        // 借出 5 个 → 0.5 触发警告
        for (var i = 0; i < 5; i++) items.Add(pool.Acquire());
        Assert.Equal(1, warnCount[0]);

        // 借出至满池 → 1.0 ≥ 0.95，状态翻转触发危急（正常 → 危急直接跳档，不补发警告）
        for (var i = 5; i < 10; i++) items.Add(pool.Acquire());
        Assert.Equal(1, warnCount[0]);
        Assert.Equal(1, criticalCount[0]);

        // 事件参数口径校验
        var args = lastArgs;
        Assert.NotNull(args);
        Assert.Equal(HayatePoolCapacityAlarmLevel.Critical, args.Level);
        Assert.Equal(1.0, args.UsageRatio, 5);
        Assert.Equal(10, args.BorrowedCount);
        Assert.Equal(10, args.MaxPoolSize);
        Assert.Equal(typeof(TestObject).Name, args.PoolName);

        // 归还全部 → 回落复位；再次满借 → 危急再次触发（去抖后可重入）
        foreach (var item in items) pool.Release(item);
        for (var i = 0; i < 10; i++) items.Add(pool.Acquire());
        Assert.Equal(2, criticalCount[0]);

        foreach (var item in items) pool.Release(item);
    }

    [Fact]
    public void DisabledByDefault_ShouldNotFireCallbacks()
    {
        // 默认 WarnAtRatio=0 / CriticalAtRatio=0：未配置告警时即使满池借出也不触发
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

    [Fact]
    public void CallbackException_ShouldNotBreakAcquire()
    {
        // 用户回调抛异常必须被池吞掉（记录日志），不得影响借出主流程
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

    [Fact]
    public void InvalidThresholds_ShouldBeRejectedByBuilder()
    {
        var builder = new HayatePoolBuilder<TestObject>();

        Assert.Throws<ArgumentOutOfRangeException>(() => builder.WithCapacityAlarm(-0.1));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.WithCapacityAlarm(1.5));
        // 两档同时启用时危急不得低于警告
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.WithCapacityAlarm(0.9, 0.8));
    }

    [Fact]
    public void ThresholdNormalization_ShouldClampToValidRange()
    {
        // 大于 1 的阈值经 ApplyFeatureSwitches 钳制为 1（经 Build 间接验证）
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
