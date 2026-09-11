using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// Wrapper recycling: destroyed <c>HayateObject&lt;T&gt;</c> wrappers are parked on a per-shard
/// spare stack (bounded by the shard max size) and reused by the next object creation, removing
/// the per-wrapper allocation from create/destroy churn without changing any observable semantics.
/// <para>
/// Acceptance: wrappers are actually reused (the spare stack drains while pooled objects are still
/// freshly created), a recycled wrapper carries pristine lease state (lease count / lease time /
/// generation reset, fresh creation timestamp), a parked wrapper does not keep its destroyed pooled
/// value alive, and stats / snapshots are unaffected by recycling.
/// </para>
/// <para>
/// The spare stack is internal state (the test project has no InternalsVisibleTo), so the parked
/// count is read via reflection — same approach as ShardAtomicRemovalTests.
/// </para>
/// </summary>
public class WrapperRecycleTests
{
    private sealed class TestObject
    {
        public int Id { get; set; }
    }

    #region Helpers

    /// <summary>
    /// Reads the parked-wrapper count of shard 0 via reflection (single-shard pools only).
    /// </summary>
    private static int GetSpareCount(IHayateObjectPool<TestObject> pool)
    {
        var field = pool.GetType().GetField("_shards", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);

        var arr = field!.GetValue(pool) as Array;
        Assert.NotNull(arr);
        Assert.Equal(1, arr!.Length);

        var shard = arr.GetValue(0)!;
        var spareField = shard.GetType().GetField("_spareCount", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(spareField);

        return (int)spareField!.GetValue(shard)!;
    }

    /// <summary>
    /// Borrow once, return, then evict the (now touched) object so its wrapper is parked on the
    /// spare stack. The strong value reference lives only inside this frame, so the caller can
    /// verify the destroyed value is collectable through the returned WeakReference.
    /// </summary>
    private static WeakReference BorrowReturnAndEvict(IHayateObjectPool<TestObject> pool)
    {
        var v = pool.Acquire();
        Assert.NotNull(v);
        var wr = new WeakReference(v);
        pool.Release(v);
        Assert.Equal(1, pool.Evict(HayateEvictReason.Touched));
        return wr;
    }

    /// <summary>
    /// Single-shard pool with all background timers off: deterministic and free of interference.
    /// Auto-scaling stays on (with a huge scaling interval) — when it is off, configuration
    /// normalization would clamp the capacity; MinSize=0 keeps the scaling callback idle.
    /// </summary>
    private static IHayateObjectPool<TestObject> BuildPool(int maxSize, string name)
    {
        return new HayatePoolBuilder<TestObject>()
            .WithPoolName(name)
            .WithEnableSharding(true)
            .WithShardCount(1)
            .WithMinSize(0)
            .WithMaxSize(maxSize)
            .WithEnableEviction(false)
            .WithEnableValidation(false)
            .WithEnableAutoScaling(true)
            .WithScalingInterval(600000)
            .WithEnableLeakDetection(false)
            .WithEnableGenerationOptimization(false)
            .WithEnableMetrics(true)
            .Build();
    }

    #endregion

    [Fact(Timeout = 30_000)]
    public void O2_DestroyedWrapperIsReusedByNextCreation()
    {
        using var pool = BuildPool(maxSize: 4, name: "o2-reuse");

        // Cycle 1: empty pool -> cold boot creates object #1 in a fresh wrapper.
        BorrowReturnAndEvict(pool);
        Assert.Equal(1, GetSpareCount(pool));
        Assert.Equal(1, pool.GetStats().TotalCreated);

        // Cycle 2: the parked wrapper must be reused. The pooled object itself is still freshly
        // created (TotalCreated counts objects, not wrappers) and the spare stack drains to zero.
        var v2 = pool.Acquire();
        Assert.NotNull(v2);
        Assert.Equal(0, GetSpareCount(pool));
        Assert.Equal(2, pool.GetStats().TotalCreated);
        pool.Release(v2);

        // Cycle 3: return / evict again and confirm the spare stack refills.
        Assert.Equal(1, pool.Evict(HayateEvictReason.Touched));
        Assert.Equal(1, GetSpareCount(pool));

        // Parked wrappers are not tracked anywhere: the pool is empty and the snapshot must not
        // expose them.
        Assert.Equal(0, pool.GetStats().PooledCount);
        Assert.Empty(pool.TakeSnapshot().ObjectDetails);
    }

    [Fact(Timeout = 30_000)]
    public void O2_RecycledWrapperCarriesPristineLeaseState()
    {
        using var pool = BuildPool(maxSize: 4, name: "o2-pristine");

        // Cycle 1: lease once (lease count 1, lease duration recorded on return), then evict.
        var v1 = pool.Acquire();
        Assert.NotNull(v1);
        var firstCreatedAtTick = pool.TakeSnapshot().ObjectDetails[0].CreatedAtTick;
        pool.Release(v1);
        Assert.Equal(1, pool.Evict(HayateEvictReason.Touched));

        // Wall-clock granularity guard: on .NET Framework DateTimeOffset.UtcNow advances in ~15.6ms
        // steps, so give the next creation a guaranteed step boundary, otherwise the fresh-timestamp
        // assertion below could observe two identical tick values.
        Thread.Sleep(50);

        // Cycle 2: the recycled wrapper wraps a fresh object. The stale lease state from cycle 1
        // must be fully reset: the first lease of the recycled wrapper reports LeaseCount=1 (not
        // the cumulative 2), LeaseTimeMs=0 (not the previous lease duration), Generation=0, and a
        // fresh creation timestamp.
        var v2 = pool.Acquire();
        Assert.NotNull(v2);

        var details = pool.TakeSnapshot().ObjectDetails;
        Assert.Single(details);
        var detail = details[0];
        Assert.Equal(1, detail.LeaseCount);
        Assert.Equal(0, detail.LeaseTimeMs);
        Assert.Equal(0, detail.Generation);
        Assert.True(detail.CreatedAtTick > firstCreatedAtTick,
            "recycled wrapper should carry a fresh creation timestamp");

        pool.Release(v2);
    }

    [Fact(Timeout = 30_000)]
    public void O2_ParkedWrapperDoesNotRootDestroyedValue()
    {
        using var pool = BuildPool(maxSize: 4, name: "o2-noroot");

        var wr = BorrowReturnAndEvict(pool);
        Assert.Equal(1, GetSpareCount(pool)); // the wrapper is parked, holding no value reference

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(wr.IsAlive, "a parked wrapper must not keep its destroyed pooled value alive");
    }

    [Fact(Timeout = 30_000)]
    public void O2_ConcurrentRecycleCyclesKeepPoolConsistent()
    {
        using var pool = BuildPool(maxSize: 8, name: "o2-concurrent");

        // Concurrent borrow/return churn plus periodic proactive eviction drives create/destroy
        // interleaving: evicted wrappers are parked while other threads cold-boot create, which
        // exercises the spare stack's concurrent push/pop paths.
        const int threadCount = 4;
        var errors = new ConcurrentQueue<Exception>();
        var stop = DateTime.UtcNow.AddSeconds(2);

        void Worker()
        {
            try
            {
                var i = 0;
                while (DateTime.UtcNow < stop)
                {
                    var v = pool.Acquire();
                    Assert.NotNull(v);
                    if (++i % 8 == 0) pool.Evict(HayateEvictReason.Touched);
                    pool.Release(v);
                }
            }
            catch (Exception ex)
            {
                errors.Enqueue(ex);
            }
        }

        var threads = new Thread[threadCount];
        for (var t = 0; t < threadCount; t++)
        {
            threads[t] = new Thread(Worker) { IsBackground = true };
            threads[t].Start();
        }
        foreach (var t in threads) t.Join();

        Assert.Empty(errors);

        // The spare stack never exceeds the shard capacity.
        Assert.True(GetSpareCount(pool) <= 8, "spare stack must stay bounded by the shard max size");

        // Final drain: every created object was borrowed at least once, so a touched eviction
        // empties the pool completely and the pool remains fully usable afterwards.
        pool.Evict(HayateEvictReason.Touched);
        Assert.Equal(0, pool.GetStats().PooledCount);
        Assert.True(pool.GetStats().TotalCreated >= 1);
    }
}
