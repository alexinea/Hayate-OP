using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// Fine-grained affinity policy -- ShardAffinityMode (None/Thread/Custom).
/// Acceptance: Thread mode stably hits the same shard from the same thread; None keeps its current semantics;
/// the Custom delegate takes effect and safely falls back on exception/out-of-range; default None has zero extra hot-path overhead (start index always 0).
/// </summary>
public class ShardAffinityTests
{
    private sealed class TestObject { }

    [Fact]
    public void NoneMode_ShouldKeepSequentialSemantics()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("affinity-none")
            .WithMinSize(4)
            .WithMaxSize(8)
            .WithShardCount(4)
            .WithShardAffinity(HayateShardAffinityMode.None)
            .Build();

        // None mode: sequential scan from shard 0 -- the first borrowed object must come from shard 0
        Assert.Equal(4, pool.GetStats().PooledCount);
        var a = pool.Acquire();
        Assert.Equal(0, GetShardIndex(pool, a));
        pool.Release(a);
    }

    [Fact]
    public void ThreadMode_SameThreadShouldStablyHitAffinityShard()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("affinity-thread")
            .WithMinSize(8)
            .WithMaxSize(16)
            .WithShardCount(4)
            .WithShardAffinity(HayateShardAffinityMode.Thread)
            .Build();

        Assert.Equal(8, pool.GetStats().PooledCount);

        // Same thread repeatedly borrows/returns: each round's first borrow (when the start shard has stock) must hit the same affinity shard.
        // After return the object goes back to its source shard, and the start shard always has stock (2 idle per shard, only 1 borrowed per round).
        var firstIndexes = new HashSet<int>();
        for (var round = 0; round < 30; round++)
        {
            var obj = pool.Acquire();
            firstIndexes.Add(GetShardIndex(pool, obj));
            pool.Release(obj);
        }

        // All first objects "directly hit from the idle pool" on the same thread come from the same shard (the core stability assertion)
        Assert.Single(firstIndexes);
    }

    [Fact]
    public void ThreadMode_ThreadIdsShouldMapToDeterminedShard()
    {
        // Pure-function verification of the start shard: when the pool is full (every shard has stock), under Thread mode
        // the first borrowed object's ShardIndex should equal the golden-ratio hash mapping result.
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("affinity-thread-hash")
            .WithMinSize(8)
            .WithMaxSize(16)
            .WithShardCount(4)
            .WithShardAffinity(HayateShardAffinityMode.Thread)
            .Build();

        var expected = (int)((uint)Environment.CurrentManagedThreadId * 2654435761u % 4u);
        var obj = pool.Acquire();
        Assert.Equal(expected, GetShardIndex(pool, obj));
        pool.Release(obj);
    }

    [Fact]
    public void CustomMode_ShouldStartFromSelectorIndex()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("affinity-custom")
            .WithMinSize(4)
            .WithMaxSize(8)
            .WithShardCount(4)
            .WithCustomShardAffinity(() => 2)
            .Build();

        // Custom: the delegate points to shard 2, so the first borrowed object should come from shard 2
        var a = pool.Acquire();
        Assert.Equal(2, GetShardIndex(pool, a));
        pool.Release(a);
    }

    [Fact]
    public void CustomMode_OutOfRangeAndThrowingSelectors_ShouldFallBackSafely()
    {
        // Out-of-range fallback: the delegate returns 99 (out of range) -> sequential scan semantics
        using var pool1 = new HayatePoolBuilder<TestObject>()
            .WithPoolName("affinity-oob")
            .WithMinSize(4)
            .WithMaxSize(8)
            .WithShardCount(4)
            .WithCustomShardAffinity(() => 99)
            .Build();
        var a1 = pool1.Acquire();
        Assert.Equal(0, GetShardIndex(pool1, a1));
        pool1.Release(a1);

        // Delegate throws -> fall back to sequential scan, borrow is not interrupted
        using var pool2 = new HayatePoolBuilder<TestObject>()
            .WithPoolName("affinity-throw")
            .WithMinSize(4)
            .WithMaxSize(8)
            .WithShardCount(4)
            .WithCustomShardAffinity(() => throw new InvalidOperationException("selector boom"))
            .Build();
        var a2 = pool2.Acquire();
        Assert.Equal(0, GetShardIndex(pool2, a2));
        pool2.Release(a2);
    }

    [Fact]
    public void CustomMode_WithoutSelector_ShouldFallBackToNoneOnBuild()
    {
        // ApplyFeatureSwitches normalization: Custom but no delegate -> falls back to None (does not reject the build)
        var options = new HayatePoolOptions { ShardAffinityMode = HayateShardAffinityMode.Custom };
        options.ApplyFeatureSwitches();
        Assert.Equal(HayateShardAffinityMode.None, options.ShardAffinityMode);

        // Unknown enum value also falls back to None
        var options2 = new HayatePoolOptions { ShardAffinityMode = (HayateShardAffinityMode)99 };
        options2.ApplyFeatureSwitches();
        Assert.Equal(HayateShardAffinityMode.None, options2.ShardAffinityMode);

        // Builder should fail fast when an unknown enum value is passed
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HayatePoolBuilder<TestObject>().WithShardAffinity((HayateShardAffinityMode)42));
    }

    [Fact]
    public async Task ThreadMode_ConcurrentThreads_ShouldBorrowWithoutConflict()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("affinity-concurrent")
            .WithMinSize(8)
            .WithMaxSize(32)
            .WithShardCount(4)
            .WithShardAffinity(HayateShardAffinityMode.Thread)
            .WithEnableMetrics(true)
            .Build();

        // Multi-threaded concurrent borrow/return: affinity only changes the scan start, not concurrency correctness
        var tasks = Enumerable.Range(0, 8).Select(async _ =>
        {
            for (var i = 0; i < 200; i++)
            {
                var obj = pool.Acquire();
                Assert.NotNull(obj);
                pool.Release(obj);
            }
        });
        await Task.WhenAll(tasks);

        var stats = pool.GetStats();
        Assert.Equal(8, stats.PooledCount);
        Assert.True(stats.TotalAcquired >= 1600);
    }

    /// <summary>The borrowed object's ShardIndex is recorded on the borrow path and read via reflection on the registry.</summary>
    private static int GetShardIndex(IHayateObjectPool<TestObject> pool, TestObject obj)
    {
        var w = GetWrapped(pool, obj);
        Assert.NotNull(w);
        return w.ShardIndex;
    }

    /// <summary>Same reflection helper as in LeakDetectionTests: probes the registry table shard by shard via _shards to fetch the wrapped object.</summary>
    private static HayateObject<TestObject> GetWrapped(IHayateObjectPool<TestObject> pool, TestObject item)
    {
        var shardsField = pool.GetType().GetField("_shards", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.NotNull(shardsField);
        var shards = (Array)shardsField.GetValue(pool)!;

        foreach (var shard in shards)
        {
            var objectsField = shard.GetType().GetField("_objects", BindingFlags.Instance | BindingFlags.NonPublic);
            if (objectsField == null) continue;

            var map = objectsField.GetValue(shard)!;
            var tryGet = map.GetType().GetMethod("TryGetValue")!;
            var args = new object?[] { item, null };
            tryGet.Invoke(map, args);
            if (args[1] != null) return (HayateObject<TestObject>)args[1]!;
        }

        return null!;
    }
}
