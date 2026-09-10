namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// M3：分配追踪（EnableAllocationTracking）。
/// <para>
/// 语义：可选开关，默认关闭（零额外开销、计数恒 0）；开启后按同步借出 / 归还路径统计
/// 线程分配增量（字节）与样本数，并经 <c>GetStats()</c> / <c>TakeSnapshot()</c> 暴露。
/// 追踪仅作诊断口径，不参与任何池行为决策。
/// </para>
/// <note>
/// net48 / netstandard2.0 缺少 <c>GC.GetAllocatedBytesForCurrentThread()</c>，
/// 这些目标框架下字节数恒 0（样本数仍正常累加）——断言按「字节 ≥ 0、样本数精确」编写。
/// </note>
/// </summary>
public class AllocationTrackingTests
{
    private class TestObject { }

    private static HayatePoolBuilder<TestObject> BaseBuilder(string name)
        => new HayatePoolBuilder<TestObject>()
            .WithPoolName(name)
            .WithMinSize(8)
            .WithMaxSize(32)
            .WithEnableSharding(false)
            .WithEnableAutoScaling(false)
            .WithEnableEviction(false);

    [Fact(Timeout = 30_000)]
    public void DefaultOff_ShouldReportZeroWithoutSampling()
    {
        using var pool = BaseBuilder("m3-off").Build();

        var item = pool.Acquire();
        pool.Release(item);

        var stats = pool.GetStats();
        Assert.False(stats.AllocationTrackingEnabled);
        Assert.Equal(0, stats.AcquireAllocationSamples);
        Assert.Equal(0, stats.ReleaseAllocationSamples);
        Assert.Equal(0, stats.AcquireAllocatedBytes);
        Assert.Equal(0, stats.ReleaseAllocatedBytes);
        Assert.Equal(0d, stats.AverageAcquireAllocatedBytes);
        Assert.Equal(0d, stats.AverageReleaseAllocatedBytes);
    }

    [Fact(Timeout = 30_000)]
    public void Enabled_ShouldSampleBorrowAndReturnPaths()
    {
        const int iterations = 32;

        using var pool = BaseBuilder("m3-on").WithEnableAllocationTracking(true).Build();

        for (var i = 0; i < iterations; i++)
        {
            var item = pool.Acquire();
            pool.Release(item);
        }

        var stats = pool.GetStats();
        Assert.True(stats.AllocationTrackingEnabled);
        Assert.Equal(iterations, stats.AcquireAllocationSamples);
        Assert.Equal(iterations, stats.ReleaseAllocationSamples);

        // 字节数为非负统计量；高版本 TFM 上借出路径必然产生分配（包装器/链表节点）
        Assert.True(stats.AcquireAllocatedBytes >= 0);
        Assert.True(stats.ReleaseAllocatedBytes >= 0);
        Assert.Equal(stats.AcquireAllocatedBytes / (double)iterations, stats.AverageAcquireAllocatedBytes, 3);
        Assert.Equal(stats.ReleaseAllocatedBytes / (double)iterations, stats.AverageReleaseAllocatedBytes, 3);

        // 快照与统计同口径
        var snapshot = pool.TakeSnapshot();
        Assert.True(snapshot.AllocationTrackingEnabled);
        Assert.Equal(stats.AcquireAllocatedBytes, snapshot.AcquireAllocatedBytes);
        Assert.Equal(stats.ReleaseAllocatedBytes, snapshot.ReleaseAllocatedBytes);
    }

    [Fact(Timeout = 30_000)]
    public void Enabled_ShouldNotChangePoolBehaviour()
    {
        using var offPool = BaseBuilder("m3-behaviour-off").Build();
        using var onPool = BaseBuilder("m3-behaviour-on").WithEnableAllocationTracking(true).Build();

        var offItems = new List<TestObject>();
        var onItems = new List<TestObject>();
        for (var i = 0; i < 8; i++)
        {
            offItems.Add(offPool.Acquire());
            onItems.Add(onPool.Acquire());
        }

        Assert.Equal(offPool.GetStats().TotalAcquired, onPool.GetStats().TotalAcquired);
        Assert.Equal(offPool.GetStats().CurrentSize, onPool.GetStats().CurrentSize);
        Assert.Equal(offPool.TakeSnapshot().BorrowedCount, onPool.TakeSnapshot().BorrowedCount);

        foreach (var item in offItems) offPool.Release(item);
        foreach (var item in onItems) onPool.Release(item);

        Assert.Equal(offPool.GetStats().PooledCount, onPool.GetStats().PooledCount);
    }

    [Fact(Timeout = 30_000)]
    public void Options_CopyTo_ShouldCarryAllocationTrackingSwitch()
    {
        var source = new HayatePoolOptions { EnableAllocationTracking = true };
        Assert.True(source.CopyTo().EnableAllocationTracking);

        var source2 = new HayatePoolOptions();
        Assert.False(source2.CopyTo().EnableAllocationTracking);
    }
}
