using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// Per-object metadata (GetTimes, LastGetThreadId, CreateTime).
/// Acceptance: the borrow counter tracks every borrow and agrees with the lease count; the last-borrower
/// thread id is recorded from the borrowing thread, and from whichever thread takes the object next; the
/// creation instant is the wall-clock time the object was made, not a constant; and a snapshot carries the
/// same thread id, so the metadata is reachable without touching the wrapper. Nothing is inherited when a
/// wrapper is recycled for a new object.
/// </summary>
public class ObjectMetadataTests
{
    private class TestObject { }

    private static HayatePoolBuilder<TestObject> BaseBuilder() => new HayatePoolBuilder<TestObject>()
        .WithPoolName("s4-pool")
        .WithMinSize(1)
        .WithMaxSize(2);

    /// <summary>The wrapper is internal state, so the tests reach it the same way the other suites do.</summary>
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

    [Fact(Timeout = 30_000)]
    public void GetTimes_ShouldCountEveryBorrow()
    {
        using var pool = BaseBuilder().Build();

        var item = pool.Acquire();
        var wrapped = GetWrapped(pool, item);

        Assert.Equal(1, wrapped.GetTimes);
        Assert.Equal(wrapped.LeaseCount, wrapped.GetTimes);   // GetTimes is the same counter

        pool.Release(item);

        for (var i = 2; i <= 4; i++)
        {
            var next = pool.Acquire();
            Assert.Same(wrapped, GetWrapped(pool, next));
            Assert.Equal(i, wrapped.GetTimes);
            pool.Release(next);
        }
    }

    [Fact(Timeout = 30_000)]
    public void LastGetThreadId_ShouldIdentifyTheBorrowingThread()
    {
        using var pool = BaseBuilder().Build();

        var item = pool.Acquire();
        Assert.Equal(Environment.CurrentManagedThreadId, GetWrapped(pool, item).LastGetThreadId);
        pool.Release(item);

        // Borrowed from another thread, the metadata names that thread instead. A dedicated thread rather
        // than a pool task: waiting on a task can inline it onto the waiting thread, which would make both
        // ids the same and the assertion meaningless.
        var observed = 0;
        var expected = 0;
        var borrower = new Thread(() =>
        {
            var other = pool.Acquire();
            observed = GetWrapped(pool, other).LastGetThreadId;
            expected = Environment.CurrentManagedThreadId;
            pool.Release(other);
        });
        borrower.Start();
        borrower.Join();

        Assert.Equal(expected, observed);
        Assert.NotEqual(Environment.CurrentManagedThreadId, observed);
    }

    [Fact(Timeout = 30_000)]
    public void LastGetThreadId_ShouldBeRecordedOnTheAsynchronousPath()
    {
        using var pool = BaseBuilder().Build();

        var item = pool.AcquireAsync().GetAwaiter().GetResult();
        var wrapped = GetWrapped(pool, item);
        Assert.Equal(1, wrapped.GetTimes);
        Assert.NotEqual(0, wrapped.LastGetThreadId);
        pool.Release(item);
    }

    [Fact(Timeout = 30_000)]
    public void CreateTime_ShouldBeTheWallClockCreationInstant()
    {
        // Bracketed before the build: a warmed-up pool creates its object during construction, not at the
        // first borrow, so a window opened after Build would already be too late.
        var before = DateTimeOffset.UtcNow;
        using var pool = BaseBuilder().Build();

        var item = pool.Acquire();
        var after = DateTimeOffset.UtcNow;

        var wrapped = GetWrapped(pool, item);
        Assert.InRange(wrapped.CreateTime, before.AddSeconds(-1), after.AddSeconds(1));
        Assert.Equal(TimeSpan.Zero, wrapped.CreateTime.Offset);   // a UTC instant, not an offset-less local time

        pool.Release(item);
    }

    [Fact(Timeout = 30_000)]
    public void Snapshot_ShouldCarryTheLastBorrowerThreadId()
    {
        using var pool = BaseBuilder().Build();

        // A warmed-up object that has never been borrowed reports no thread.
        Assert.All(pool.TakeSnapshot().ObjectDetails, d => Assert.Equal(0, d.LastGetThreadId));

        var item = pool.Acquire();
        var detail = Assert.Single(pool.TakeSnapshot().ObjectDetails);
        Assert.Equal(Environment.CurrentManagedThreadId, detail.LastGetThreadId);
        Assert.Equal(GetWrapped(pool, item).GetTimes, detail.LeaseCount);

        pool.Release(item);
    }

    [Fact(Timeout = 30_000)]
    public void Recycle_ShouldNotInheritThePreviousObjectsMetadata()
    {
        // Max 1: the single object is destroyed, its wrapper is parked on the spare stack, and the next
        // borrow reuses that wrapper for a brand-new object.
        using var pool = BaseBuilder().WithMinSize(0).WithMaxSize(1).Build();

        var first = pool.Acquire();
        var wrapped = GetWrapped(pool, first);
        var firstCreatedAt = wrapped.CreatedAt;
        pool.Release(first);

        Assert.Equal(1, pool.Evict(HayateEvictReason.Touched));

        var second = pool.Acquire();
        Assert.NotSame(first, second);

        var recycled = GetWrapped(pool, second);
        // A recycled wrapper counts its new object's leases from zero and names the current borrower.
        Assert.Equal(1, recycled.GetTimes);
        Assert.Equal(Environment.CurrentManagedThreadId, recycled.LastGetThreadId);
        Assert.True(recycled.CreatedAt > firstCreatedAt);

        pool.Release(second);
    }
}
