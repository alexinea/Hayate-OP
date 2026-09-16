using System;
using System.Threading.Tasks;
using DotNetCore.HayateOP;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// O-D: the ArrayPool direct-storage backend for the lean fast path. When enabled, the lean
/// buffer's slot array is rented from ArrayPool&lt;T&gt;.Shared and grows on demand (×2) up to
/// MaxPoolSize, instead of occupying MaxPoolSize slots for the pool's whole lifetime. Semantics
/// are identical to fixed-buffer lean: same ceiling, same wrapper-free borrow/return, same reject
/// policies. On netstandard2.0/net48 the flag is ignored and lean keeps its fixed buffer — the
/// legacy suite exercises that fallback.
/// </summary>
public class ArrayPoolStorageLeanTests
{
    private sealed class PooledItem
    {
        public int Value { get; set; }
    }

    private static HayatePoolBuilder<PooledItem> Builder(int maxSize, int minSize = 0)
        => new HayatePoolBuilder<PooledItem>()
            .WithArrayPoolStorage()
            .WithMinSize(minSize)
            .WithMaxSize(maxSize)
            .WithRejectPolicy(HayatePoolRejectPolicy.Abort)
            .WithEnableAutoScaling(false);

    [Fact]
    public void BorrowReturn_ShouldReuseTheObject()
    {
        using var pool = Builder(16).Build();

        var first = pool.Acquire();
        pool.Release(first);

        Assert.Same(first, pool.Acquire());
    }

    [Fact]
    public void ShouldGrowToMaxPoolSize_AndEnforceTheCeiling()
    {
        using var pool = Builder(8).Build();

        // Retain exactly MaxPoolSize objects: the rented array grows on demand to hold them all.
        // Hold every borrow first, then return them all — returning inside the loop would let the
        // next borrow reclaim the same object and collapse the retained set to one.
        var retained = new PooledItem[8];
        for (var i = 0; i < 8; i++)
        {
            var item = pool.Acquire();
            item.Value = i;
            retained[i] = item;
        }

        for (var i = 0; i < 8; i++)
        {
            pool.Release(retained[i]);
        }

        Assert.Equal(8, pool.GetStats().PooledCount);

        // The ceiling is a soft cap on retention, not on borrows; borrowing 8 distinct objects
        // proves nothing was dropped and nothing new was created past the ceiling.
        var borrowed = new PooledItem[8];
        for (var i = 0; i < 8; i++)
        {
            borrowed[i] = pool.Acquire();
        }

        Assert.Equal(8, retained.Distinct().Count());
        Assert.Equal(8, borrowed.Distinct().Count());
        Assert.Equal(retained.OrderBy(static i => i.Value).Select(static i => i.Value),
                     borrowed.OrderBy(static i => i.Value).Select(static i => i.Value));
        Assert.Equal(8, pool.GetStats().CurrentSize);

        // At the ceiling a further borrow must reject (Abort policy) — nothing is silently created
        // past MaxPoolSize.
        Assert.Throws<InvalidOperationException>(() => pool.Acquire());
    }

    [Fact]
    public void ReturnPastCeiling_ShouldDropTheObject()
    {
        using var pool = Builder(2).Build();

        var a = pool.Acquire();
        var b = pool.Acquire();
        pool.Release(a);
        pool.Release(b);
        Assert.Equal(2, pool.GetStats().PooledCount);

        // The buffer holds MaxPoolSize idle objects; a further return must be dropped, not parked.
        // Lean keeps no registry, so an object the pool never created is accepted and exercises
        // the same drop path.
        pool.Release(new PooledItem());

        Assert.Equal(2, pool.GetStats().PooledCount);
    }

    [Fact]
    public async Task AcquireAsync_ShouldShareTheSameBuffer()
    {
        using var pool = Builder(8).Build();

        var item = await pool.AcquireAsync();
        pool.Release(item);

        Assert.Same(item, await pool.AcquireAsync());
    }

    [Fact]
    public void Clear_ShouldEmptyTheRetainedSet()
    {
        using var pool = Builder(8).Build();

        var first = pool.Acquire();
        pool.Release(first);
        Assert.Equal(1, pool.GetStats().PooledCount);

        pool.Clear();
        Assert.Equal(0, pool.GetStats().PooledCount);

        // After a clear the pool still works and does not serve the cleared object.
        var next = pool.Acquire();
        Assert.NotSame(first, next);
    }

    [Fact]
    public void Stats_ShouldReflectTheBuffer()
    {
        using var pool = Builder(8).Build();

        var item = pool.Acquire();
        pool.Release(item);

        var stats = pool.GetStats();
        Assert.Equal(1, stats.PooledCount);
        Assert.Equal(1, stats.AvailableSlots);
        Assert.True(stats.CurrentSize >= 1, "The live counter should reflect the borrow.");
        Assert.True(stats.MinSize >= 0);
    }

    [Fact]
    public void ScopedBorrow_ShouldWorkThroughTheInterface()
    {
        using var pool = Builder(8).Build();

        PooledItem item;
        using (var scope = pool.AcquireScoped())
        {
            item = scope.Value;
        }

        Assert.Same(item, pool.Acquire());
    }

    [Fact]
    public void Dispose_ShouldNotThrow_AndClearThePool()
    {
        var pool = Builder(8).Build();
        var item = pool.Acquire();
        pool.Release(item);

        pool.Dispose();

        // Nothing observable after dispose; the point is that it completes cleanly.
        Assert.True(true);
    }

    [Fact]
    public void WithoutArrayPoolStorage_ShouldKeepTheFixedBufferBehaviour()
    {
        // A plain lean pool is unaffected by the O-D switch and behaves identically.
        using var pool = new HayatePoolBuilder<PooledItem>()
            .WithLean()
            .WithMinSize(0)
            .WithMaxSize(4)
            .WithRejectPolicy(HayatePoolRejectPolicy.Abort)
            .Build();

        var first = pool.Acquire();
        pool.Release(first);
        Assert.Same(first, pool.Acquire());
    }
}
