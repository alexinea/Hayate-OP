namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// Allocation tracking (EnableAllocationTracking).
/// <para>
/// Semantics: an optional switch, off by default (zero extra overhead, counter always 0); once enabled it counts, per the synchronous borrow / return paths,
/// thread allocation delta (bytes) and sample count, exposed via <c>GetStats()</c> / <c>TakeSnapshot()</c>.
/// Tracking is diagnostic only and takes part in no pool behavior decision.
/// </para>
/// <note>
/// net48 / netstandard2.0 lack <c>GC.GetAllocatedBytesForCurrentThread()</c>,
/// so on those target frameworks the byte count is always 0 (sample count still accumulates normally) -- assertions are written as "bytes >= 0, exact sample count".
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

        // Bytes are a non-negative statistic; on higher TFM versions the borrow path inevitably allocates (wrapper / linked-list node)
        Assert.True(stats.AcquireAllocatedBytes >= 0);
        Assert.True(stats.ReleaseAllocatedBytes >= 0);
        Assert.Equal(stats.AcquireAllocatedBytes / (double)iterations, stats.AverageAcquireAllocatedBytes, 3);
        Assert.Equal(stats.ReleaseAllocatedBytes / (double)iterations, stats.AverageReleaseAllocatedBytes, 3);

        // Snapshot and stats use the same measurement
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
