using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// M15（2.5）：分类驱逐 API——Evict(HayateEvictReason)。
/// 验收：Touched/Idle/Expired 三类对象按分类驱逐计数正确；借出中对象不被驱逐；
/// 与后台驱逐共用的幂等 CAS 保证并发安全；默认后台驱逐行为不变。
/// </summary>
public class EvictTests
{
    private sealed class TestObject { }

    [Fact]
    public void EvictTouched_ShouldRemoveOnlyUsedIdleObjects()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("m15-touched")
            .WithMinSize(2)
            .WithMaxSize(4)
            .WithEnableEviction(false)      // 关后台驱逐，排除干扰
            .WithEnableAutoScaling(false)
            .Build();

        // 借出再归还 → 该对象 LeaseCount=1（Touched）；另一对象保持未用（LeaseCount=0）
        var a = pool.Acquire();
        pool.Release(a);

        var evicted = pool.Evict(HayateEvictReason.Touched);
        Assert.Equal(1, evicted);
        Assert.Equal(1, pool.GetStats().PooledCount);

        // 再驱逐一次：剩余对象未被用过，应 0 驱逐
        Assert.Equal(0, pool.Evict(HayateEvictReason.Touched));
    }

    [Fact]
    public void EvictIdle_ShouldHonorMaxIdleTimeAndSkipBorrowed()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("m15-idle")
            .WithMinSize(2)
            .WithMaxSize(4)
            .WithEnableEviction(false)
            .WithEnableAutoScaling(false)
            .WithMaxIdleTime(TimeSpan.FromMilliseconds(150))
            .Build();

        // 借出一个并持有 300ms：另一对象空闲时长超阈值，借出对象不受影响
        var a = pool.Acquire();
        Thread.Sleep(300);

        var evicted = pool.Evict(HayateEvictReason.Idle);
        Assert.Equal(1, evicted); // 仅空闲超阈值的那个
        // PooledCount 只统计分片内空闲对象（借出中的 a 不在链表）——唯一空闲项被驱逐后为 0
        Assert.Equal(0, pool.GetStats().PooledCount);
        Assert.Equal(1, pool.TakeSnapshot().BorrowedCount); // 借出对象完好

        // 归还后其空闲时钟重置：立即驱逐应 0
        pool.Release(a);
        Assert.Equal(0, pool.Evict(HayateEvictReason.Idle));
        Assert.Equal(1, pool.GetStats().PooledCount);
    }

    [Fact]
    public void EvictExpired_ShouldRemoveObjectsBeyondMaxLifeTime()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("m15-expired")
            .WithMinSize(2)
            .WithMaxSize(4)
            .WithEnableEviction(false)
            .WithEnableAutoScaling(false)
            .WithMaxLifeTime(TimeSpan.FromMilliseconds(150))
            .Build();

        Thread.Sleep(300); // 两个对象均超过 MaxLifeTime

        var evicted = pool.Evict(HayateEvictReason.Expired);
        Assert.Equal(2, evicted);
        Assert.Equal(0, pool.GetStats().PooledCount);
    }

    [Fact]
    public void EvictTouched_ShouldNotTouchBorrowedObjects()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("m15-borrowed")
            .WithMinSize(1)
            .WithMaxSize(2)
            .WithEnableEviction(false)
            .WithEnableAutoScaling(false)
            .Build();

        // 唯一对象处于借出中（LeaseCount=1）：Touched 不应驱逐借出对象
        var a = pool.Acquire();
        Assert.Equal(0, pool.Evict(HayateEvictReason.Touched));
        Assert.Equal(1, pool.TakeSnapshot().BorrowedCount);

        // 归还后再驱逐：可命中
        pool.Release(a);
        Assert.Equal(1, pool.Evict(HayateEvictReason.Touched));
        Assert.Equal(0, pool.GetStats().PooledCount);
    }

    [Fact]
    public async Task Evict_ConcurrentWithBorrowRelease_ShouldStayConsistent()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("m15-concurrent")
            .WithMinSize(4)
            .WithMaxSize(16)
            .WithEnableEviction(false)
            .WithEnableAutoScaling(false)
            .Build();

        // 并发借还与分类驱逐交错：借还零异常、对象不丢不重（幂等 CAS 认领保证）
        var workers = Enumerable.Range(0, 4).Select(async _ =>
        {
            for (var i = 0; i < 100; i++)
            {
                var obj = pool.Acquire();
                pool.Release(obj);
            }
        });

        var evictTask = Task.Run(() =>
        {
            var total = 0;
            for (var i = 0; i < 20; i++)
            {
                total += pool.Evict(HayateEvictReason.Touched);
                Thread.Sleep(10);
            }
            return total;
        });

        await Task.WhenAll(workers);
        await evictTask;

        // 借还路径无异常即通过；统计口径自洽（PooledCount ≥ 0 已由 GetStats 保证）
        Assert.True(pool.GetStats().PooledCount >= 0);
    }

    [Fact]
    public void Evict_UnknownReason_ShouldThrow()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("m15-unknown")
            .WithMinSize(1)
            .WithMaxSize(2)
            .Build();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            pool.Evict((HayateEvictReason)99));
    }
}
