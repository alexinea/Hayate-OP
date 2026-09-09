using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// M18（2.5）：细粒度 affinity 策略——ShardAffinityMode（None/Thread/Custom）。
/// 验收：Thread 模式同线程稳定命中同 shard；None 现状语义不变；
/// Custom 委托生效且异常/越界安全回落；默认 None 热路径零额外开销（起始索引恒 0）。
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

        // None 模式：从 0 号分片顺序扫描——首个借出对象必来自 0 号分片
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

        // 同一线程反复借还：每轮首个借出（起始分片有货时）必命中同一亲和分片。
        // 归还后对象回源分片，起始分片始终有货（每分片 2 个空闲，每轮仅借 1 个）。
        var firstIndexes = new HashSet<int>();
        for (var round = 0; round < 30; round++)
        {
            var obj = pool.Acquire();
            firstIndexes.Add(GetShardIndex(pool, obj));
            pool.Release(obj);
        }

        // 同一线程所有「从空闲池直接命中」的首个对象来自同一分片（稳定性核心断言）
        Assert.Single(firstIndexes);
    }

    [Fact]
    public void ThreadMode_ThreadIdsShouldMapToDeterminedShard()
    {
        // 起始分片纯函数性验证：池满（每分片都有货）时，Thread 模式下
        // 借出的第一个对象的 ShardIndex 应等于黄金比例散列映射结果。
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

        // Custom：委托指向 2 号分片，首个借出对象应来自 2 号分片
        var a = pool.Acquire();
        Assert.Equal(2, GetShardIndex(pool, a));
        pool.Release(a);
    }

    [Fact]
    public void CustomMode_OutOfRangeAndThrowingSelectors_ShouldFallBackSafely()
    {
        // 越界回落：委托返回 99（越界）→ 顺序扫描语义
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

        // 委托抛异常 → 回落顺序扫描，借出不中断
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
        // ApplyFeatureSwitches 规范化：Custom 但无委托 → 回落 None（不拒绝构建）
        var options = new HayatePoolOptions { ShardAffinityMode = HayateShardAffinityMode.Custom };
        options.ApplyFeatureSwitches();
        Assert.Equal(HayateShardAffinityMode.None, options.ShardAffinityMode);

        // 未知枚举值同样回落 None
        var options2 = new HayatePoolOptions { ShardAffinityMode = (HayateShardAffinityMode)99 };
        options2.ApplyFeatureSwitches();
        Assert.Equal(HayateShardAffinityMode.None, options2.ShardAffinityMode);

        // Builder 传入未知枚举值应快速失败
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

        // 多线程并发借还：affinity 只改变扫描起点，不改变并发正确性
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

    /// <summary>借出对象的 ShardIndex 在借出路径记录，经登记表反射读取。</summary>
    private static int GetShardIndex(IHayateObjectPool<TestObject> pool, TestObject obj)
    {
        var w = GetWrapped(pool, obj);
        Assert.NotNull(w);
        return w.ShardIndex;
    }

    /// <summary>与 LeakDetectionTests 同款反射助手：经 _shards 逐分片探测登记表取包装对象。</summary>
    private static HayateObject<TestObject> GetWrapped(IHayateObjectPool<TestObject> pool, TestObject item)
    {
        var shardsField = pool.GetType().GetField("_shards", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.NotNull(shardsField);
        var shards = (Array)shardsField.GetValue(pool)!;

        foreach (var shard in shards)
        {
            var objectsField = shard.GetType().GetField("_objects", BindingFlags.Instance | BindingFlags.NonPublic);
            if (objectsField == null) continue;

            var map = objectsField.GetValue(shard);
            var tryGet = map.GetType().GetMethod("TryGetValue")!;
            var args = new object[] { item, null };
            tryGet.Invoke(map, args);
            if (args[1] != null) return (HayateObject<TestObject>)args[1];
        }

        return null!;
    }
}
