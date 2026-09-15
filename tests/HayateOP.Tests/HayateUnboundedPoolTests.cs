using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DotNetCore.HayateOP;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// HayateUnboundedPool (N1): the second pool model, aligned with marklauter's UnboundedPool —
/// ArrayPool-style borrowing that never blocks or rejects, leases that are ownership (returning is
/// optional), and a MaxIdle bound on the resident set: a return past MaxIdle destroys the object
/// instead of parking it. Verified: bursts far beyond any capacity are served, unreturned objects
/// harm nobody, stats track created/missed/acquired/released/destroyed, the reset and validate
/// hooks behave like the bounded engine's, and the pool works as a plain IHayateObjectPool&lt;T&gt;
/// (scoped borrows included).
/// </summary>
public class HayateUnboundedPoolTests
{
    private sealed class PooledItem
    {
        public int Value { get; set; }
    }

    private sealed class ResettableItem : IHayateResettable
    {
        public int Value { get; set; }
        public int ResetCount { get; private set; }

        public void Reset()
        {
            Value = 0;
            ResetCount++;
        }
    }

    private sealed class ValidatableItem : IHayateValidatable
    {
        public bool Healthy { get; set; } = true;

        public bool IsValid() => Healthy;
    }

    [Fact(Timeout = 30_000)]
    public void Acquire_ShouldNeverBlockOrFail_BeyondAnyCapacity()
    {
        using var pool = new HayateUnboundedPool<PooledItem>(maxIdle: 4);

        // Far beyond MaxIdle: every borrow is served instantly, by reuse or creation.
        var items = new List<PooledItem>();
        for (var i = 0; i < 1000; i++)
        {
            items.Add(pool.Acquire());
        }

        Assert.Equal(1000, items.Count);
        Assert.All(items, Assert.NotNull);
    }

    [Fact(Timeout = 30_000)]
    public void NotReturningObjects_ShouldBeHarmless()
    {
        using var pool = new HayateUnboundedPool<PooledItem>(maxIdle: 4);

        // The lease is ownership: dropping objects without returning them leaves the pool intact
        // (the GC collects them; the pool keeps no bookkeeping that could leak).
        for (var i = 0; i < 100; i++)
        {
            _ = pool.Acquire();
        }

        Assert.Equal(0, pool.PooledCount);

        // The pool still works normally afterwards.
        var item = pool.Acquire();
        pool.Release(item);
        Assert.Equal(1, pool.PooledCount);
    }

    [Fact(Timeout = 30_000)]
    public void ReturnWithinMaxIdle_ShouldReuseTheObject()
    {
        using var pool = new HayateUnboundedPool<PooledItem>(maxIdle: 4);

        var first = pool.Acquire();
        pool.Release(first);

        Assert.Same(first, pool.Acquire());
    }

    [Fact(Timeout = 30_000)]
    public void ReturnPastMaxIdle_ShouldDestroyInsteadOfPark()
    {
        using var pool = new HayateUnboundedPool<PooledItem>(maxIdle: 2);

        var a = pool.Acquire();
        var b = pool.Acquire();
        var c = pool.Acquire();

        pool.Release(a);
        pool.Release(b);
        Assert.Equal(2, pool.PooledCount);

        // The resident set is full: this return is destroyed, not parked.
        var statsBefore = pool.GetStats().TotalDestroyed;
        pool.Release(c);
        Assert.Equal(statsBefore + 1, pool.GetStats().TotalDestroyed);
        Assert.Equal(2, pool.PooledCount);

        // The destroyed object is not served again.
        Assert.NotSame(c, pool.Acquire());
    }

    [Fact(Timeout = 30_000)]
    public void Factory_ShouldCreateObjects()
    {
        var created = 0;
        using var pool = new HayateUnboundedPool<PooledItem>(maxIdle: 4, factory: () =>
        {
            created++;
            return new PooledItem { Value = created };
        });

        var item = pool.Acquire();
        Assert.Equal(1, item.Value);
    }

    [Fact(Timeout = 30_000)]
    public void ResettableItems_ShouldBeResetOnReturn()
    {
        using var pool = new HayateUnboundedPool<ResettableItem>(maxIdle: 4);

        var item = pool.Acquire();
        item.Value = 42;
        pool.Release(item);

        var next = pool.Acquire();
        Assert.Same(item, next);
        Assert.Equal(0, next.Value);
        Assert.Equal(1, next.ResetCount);
    }

    [Fact(Timeout = 30_000)]
    public void InvalidItems_ShouldBeDestroyedOnReturn()
    {
        using var pool = new HayateUnboundedPool<ValidatableItem>(maxIdle: 4);

        var broken = pool.Acquire();
        broken.Healthy = false;
        pool.Release(broken);

        Assert.Equal(1, pool.GetStats().TotalDestroyed);
        Assert.Equal(0, pool.PooledCount);

        // The invalid object is not served again.
        Assert.NotSame(broken, pool.Acquire());
    }

    [Fact(Timeout = 30_000)]
    public void GetStats_ShouldTrackTheLifecycle()
    {
        using var pool = new HayateUnboundedPool<PooledItem>(maxIdle: 2);

        var a = pool.Acquire();
        var b = pool.Acquire();
        var c = pool.Acquire();
        var d = pool.Acquire();

        var stats = pool.GetStats();
        Assert.Equal(4, stats.TotalAcquired);
        Assert.Equal(4, stats.TotalCreated);
        Assert.Equal(4, stats.TotalMissed); // every create is a miss in this model

        pool.Release(a);
        pool.Release(b);
        pool.Release(c); // destroyed: MaxIdle = 2

        stats = pool.GetStats();
        Assert.Equal(2, stats.PooledCount);
        Assert.Equal(2, stats.CurrentSize);
        Assert.Equal(0, stats.MinSize);
        Assert.Equal(0, stats.AvailableSlots);
        // All three returns count (c's was the destroy outcome); only two parked.
        Assert.Equal(3, stats.TotalReleased);
        Assert.Equal(1, stats.TotalDestroyed);

        pool.Release(d); // also destroyed
        Assert.Equal(2, pool.GetStats().TotalDestroyed);
        Assert.Equal(4, pool.GetStats().TotalReleased);

        GC.KeepAlive(a);
        GC.KeepAlive(b);
        GC.KeepAlive(c);
        GC.KeepAlive(d);
    }

    [Fact(Timeout = 30_000)]
    public void Clear_ShouldEmptyThePoolAndServeFreshObjects()
    {
        using var pool = new HayateUnboundedPool<PooledItem>(maxIdle: 4);

        var first = pool.Acquire();
        pool.Release(first);
        Assert.Equal(1, pool.PooledCount);

        pool.Clear();
        Assert.Equal(0, pool.PooledCount);
        Assert.Equal(1, pool.GetStats().TotalDestroyed);

        Assert.NotSame(first, pool.Acquire());
    }

    [Fact(Timeout = 30_000)]
    public void Evict_ShouldDestroyEveryParkedObject()
    {
        using var pool = new HayateUnboundedPool<PooledItem>(maxIdle: 8);

        var a = pool.Acquire();
        var b = pool.Acquire();
        pool.Release(a);
        pool.Release(b);

        Assert.Equal(2, pool.Evict(HayateEvictReason.Idle));
        Assert.Equal(0, pool.PooledCount);

        // Borrowed objects are never touched; a second sweep evicts nothing.
        Assert.Equal(0, pool.Evict(HayateEvictReason.Idle));
    }

    [Fact(Timeout = 30_000)]
    public void AcquireAsync_ShouldCompleteSynchronously()
    {
        using var pool = new HayateUnboundedPool<PooledItem>(maxIdle: 4);

        var task = pool.AcquireAsync();
        Assert.True(task.IsCompleted);

        var timeoutTask = pool.AcquireAsync(TimeSpan.FromSeconds(5));
        Assert.True(timeoutTask.IsCompleted);
    }

    [Fact(Timeout = 30_000)]
    public void AcquireWithTimeout_ShouldIgnoreTheTimeout()
    {
        using var pool = new HayateUnboundedPool<PooledItem>(maxIdle: 4);

        // A bounded pool would wait or throw; the unbounded model serves immediately.
        var item = pool.Acquire(TimeSpan.FromMilliseconds(1));
        Assert.NotNull(item);
    }

    [Fact(Timeout = 30_000)]
    public void ScopedBorrow_ShouldWorkThroughTheInterfaceExtension()
    {
        using var pool = new HayateUnboundedPool<PooledItem>(maxIdle: 4);

        PooledItem item;
        using (var scope = pool.AcquireScoped())
        {
            item = scope.Value;
            item.Value = 7;
        }

        // The scope returned the object; the pool reuses it like any manual return.
        Assert.Equal(1, pool.PooledCount);
        Assert.Same(item, pool.Acquire());
    }

    [Fact(Timeout = 30_000)]
    public void AvailabilityFlag_ShouldBeReportableWithoutAffectingBorrows()
    {
        using var pool = new HayateUnboundedPool<PooledItem>(maxIdle: 4);
        Assert.True(pool.CheckAvailable());

        pool.SetUnavailable("no dependency behind this pool");
        Assert.False(pool.CheckAvailable());

        // An unbounded pool has no dependency to fail; borrows are unaffected either way.
        Assert.NotNull(pool.Acquire());

        pool.SetAvailable();
        Assert.True(pool.CheckAvailable());
    }

    [Fact(Timeout = 30_000)]
    public void InvalidConstruction_ShouldThrow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HayateUnboundedPool<PooledItem>(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HayateUnboundedPool<PooledItem>(-1));
        Assert.Throws<ArgumentNullException>(() => new HayateUnboundedPool<PooledItem>(4, null!));
    }

    [Fact(Timeout = 30_000)]
    public void ReleaseNull_ShouldThrow()
    {
        using var pool = new HayateUnboundedPool<PooledItem>(maxIdle: 4);
        Assert.Throws<ArgumentNullException>(() => pool.Release(null!));
    }

    [Fact(Timeout = 30_000)]
    public void ConcurrentBorrowAndReturn_ShouldStayConsistent()
    {
        using var pool = new HayateUnboundedPool<PooledItem>(maxIdle: 8);

        const int threads = 16;
        const int iterations = 500;
        var barrier = new Barrier(threads);
        var failures = new List<Exception>();

        var workers = new List<Thread>();
        for (var t = 0; t < threads; t++)
        {
            workers.Add(new Thread(() =>
            {
                try
                {
                    barrier.SignalAndWait();
                    for (var i = 0; i < iterations; i++)
                    {
                        var item = pool.Acquire();
                        item.Value++;
                        pool.Release(item);
                    }
                }
                catch (Exception ex)
                {
                    lock (failures)
                    {
                        failures.Add(ex);
                    }
                }
            }));
        }

        workers.ForEach(w => w.Start());
        workers.ForEach(w => w.Join());

        Assert.Empty(failures);

        // Every borrow/return round-trip is accounted for; nothing is double-parked.
        var stats = pool.GetStats();
        Assert.Equal(threads * iterations, stats.TotalAcquired);
        Assert.Equal(threads * iterations, stats.TotalReleased);
        Assert.True(stats.PooledCount <= 8, "The resident set must stay within MaxIdle.");
    }
}
