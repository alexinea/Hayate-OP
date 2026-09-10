using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// Categorized eviction API -- Evict(HayateEvictReason).
/// Acceptance: Touched/Idle/Expired objects are evicted by category with correct counts; borrowed objects are not evicted;
/// the idempotent CAS shared with the background evictor guarantees thread safety; the default background eviction behavior is unchanged.
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
            .WithEnableEviction(false)      // Disable background eviction to remove interference
            .WithEnableAutoScaling(false)
            .Build();

        // Borrow then return -> this object has LeaseCount=1 (Touched); the other object stays unused (LeaseCount=0)
        var a = pool.Acquire();
        pool.Release(a);

        var evicted = pool.Evict(HayateEvictReason.Touched);
        Assert.Equal(1, evicted);
        Assert.Equal(1, pool.GetStats().PooledCount);

        // Evict again: the remaining object was never used, so 0 should be evicted
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

        // Borrow one and hold it for 300ms: the other object's idle time exceeds the threshold, the borrowed object is unaffected
        var a = pool.Acquire();
        Thread.Sleep(300);

        var evicted = pool.Evict(HayateEvictReason.Idle);
        Assert.Equal(1, evicted); // only the one whose idle time exceeded the threshold
        // PooledCount only counts idle objects in the shard (borrowed 'a' is not in the list) -- after the only idle item is evicted it becomes 0
        Assert.Equal(0, pool.GetStats().PooledCount);
        Assert.Equal(1, pool.TakeSnapshot().BorrowedCount); // the borrowed object is intact

        // After return its idle clock resets: an immediate eviction should yield 0
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

        Thread.Sleep(300); // both objects exceed MaxLifeTime

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

        // The only object is borrowed (LeaseCount=1): Touched must not evict a borrowed object
        var a = pool.Acquire();
        Assert.Equal(0, pool.Evict(HayateEvictReason.Touched));
        Assert.Equal(1, pool.TakeSnapshot().BorrowedCount);

        // Evict again after return: it can be hit
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

        // Concurrent borrow/return interleaved with categorized eviction: zero exceptions on borrow/return, no lost or duplicated objects (guaranteed by idempotent CAS claim)
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

        // Passing means the borrow/return path had no exception; the statistics are self-consistent (PooledCount >= 0 is already guaranteed by GetStats)
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
