using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// Create-on-demand reject policy -- a request that finds no idle object is served by creating one
/// immediately while the pool has room to grow, instead of waiting out the acquire timeout.
/// Acceptance: the first borrow of an empty pool returns synchronously; every concurrent miss below
/// capacity is served by its own object; a request made while the pool is at capacity still waits for a
/// return and then creates rather than throwing; and returned objects are pooled as usual.
/// </summary>
public class CreateOnDemandPolicyTests
{
    private class TestObject { }

    [Fact(Timeout = 30_000)]
    public void CreateOnDemand_EmptyPool_ShouldReturnWithoutWaitingOutTheTimeout()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("on-demand-cold")
            .WithMinSize(0)
            .WithMaxSize(8)
            .WithEnableAutoScaling(false)
            .WithEnableEviction(false)
            .WithEnableValidation(false)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand)
            // Deliberately far above any plausible creation cost: waiting this out would either throw
            // TimeoutException or trip the check below.
            .WithAcquireTimeout(TimeSpan.FromSeconds(2))
            // TotalCreated is metrics-gated, so the "created exactly once" assertions need metrics on.
            .WithEnableMetrics(true)
            .Build();

        var sw = Stopwatch.StartNew();
        var obj = pool.Acquire();
        sw.Stop();

        Assert.NotNull(obj);
        // Bound sits well under the 2s acquire timeout so a wait-out regression still trips it, but with
        // enough headroom for cold-JIT noise on CI runners (the net48 leg has no ready-to-run images).
        Assert.True(sw.ElapsedMilliseconds < 1500,
            $"the first borrow of an empty pool must create synchronously, but it took {sw.ElapsedMilliseconds}ms");
        Assert.Equal(1, pool.GetStats().TotalCreated);
    }

    private static (TestObject[] Objects, long[] AcquireMilliseconds) RunConcurrentBurst(
        IHayateObjectPool<TestObject> pool, int requests)
    {
        var results = new TestObject[requests];
        var acquireMs = new long[requests];
        using var barrier = new Barrier(requests);

        var tasks = Enumerable.Range(0, requests)
            .Select(i => Task.Run(() =>
            {
                barrier.SignalAndWait();
                // Timed from the barrier release, not from the task start: on a 4-core CI runner the
                // thread pool injects the four participants slowly, and that delay sits outside Acquire,
                // where it says nothing about the reject policy.
                var sw = Stopwatch.StartNew();
                results[i] = pool.Acquire();
                acquireMs[i] = sw.ElapsedMilliseconds;
            }))
            .ToArray();

        Task.WaitAll(tasks);
        return (results, acquireMs);
    }

    private static HayatePoolBuilder<TestObject> ConcurrentBurstPoolBuilder(string name) => new HayatePoolBuilder<TestObject>()
        .WithPoolName(name)
        .WithMinSize(0)
        .WithMaxSize(8)
        .WithShardCount(1)
        .WithEnableAutoScaling(false)
        .WithEnableEviction(false)
        .WithEnableValidation(false)
        .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand)
        // Far above any plausible creation cost, so a policy that degraded to waiting out the timeout
        // before creating shows up as >= 2s and trips the bound below (or throws TimeoutException).
        .WithAcquireTimeout(TimeSpan.FromSeconds(2));

    [Fact(Timeout = 30_000)]
    public void CreateOnDemand_BelowCapacityMisses_ShouldEachCreateWithoutWaiting()
    {
        const int requests = 4;

        // Warm-up burst on a throwaway pool: primes the JIT of the acquire/create path and the
        // thread-pool ramp-up, so the measured burst does not pay first-touch costs.
        using (var warmup = ConcurrentBurstPoolBuilder("on-demand-concurrent-warmup").Build())
        {
            RunConcurrentBurst(warmup, requests);
        }

        using var pool = ConcurrentBurstPoolBuilder("on-demand-concurrent").Build();

        var burst = RunConcurrentBurst(pool, requests);

        Assert.All(burst.Objects, Assert.NotNull);
        Assert.Equal(requests, burst.Objects.Distinct().Count());   // one object per request, nothing lent twice
        // The contract is per request: a wait-then-create degradation spends the whole 2s acquire timeout
        // inside Acquire, so the slowest acquire is what has to stay in the milliseconds. The old burst
        // wall-clock bound also counted the thread pool's scheduling of the four barrier participants
        // (1984ms on the net48 leg) and failed a run whose acquires were all fast -- a burst total below
        // the 2s timeout already proved no request waited it out.
        var slowest = burst.AcquireMilliseconds.Max();
        Assert.True(slowest < 1500,
            $"{requests} concurrent misses below capacity must each create without waiting, but the slowest acquire took {slowest}ms");
    }

    [Fact(Timeout = 30_000)]
    public void CreateOnDemand_AtCapacity_ShouldWaitThenCreateInsteadOfThrowing()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("on-demand-capacity")
            .WithMinSize(0)
            .WithMaxSize(2)
            .WithShardCount(1)
            .WithEnableAutoScaling(false)
            .WithEnableEviction(false)
            .WithEnableValidation(false)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand)
            .WithAcquireTimeout(TimeSpan.FromMilliseconds(200))
            .Build();

        // Fill the pool to capacity and keep both objects lent out, so no idle object exists.
        var lent = new[] { pool.Acquire(), pool.Acquire() };
        Assert.Equal(2, pool.TakeSnapshot().BorrowedCount);

        // At capacity the policy waits for a return and then creates anyway -- it must not throw the way a
        // pure timeout policy would.
        var sw = Stopwatch.StartNew();
        var overflow = pool.Acquire();
        sw.Stop();

        Assert.NotNull(overflow);
        Assert.True(sw.ElapsedMilliseconds >= 150,
            $"an at-capacity request must wait for a return before creating, but it returned after {sw.ElapsedMilliseconds}ms");

        // The overflow object is registered as borrowed, so releasing it while the pool is full is rejected
        // and the pool keeps exactly its capacity worth of objects.
        pool.Release(overflow);
        Assert.Equal(2, pool.TakeSnapshot().BorrowedCount);
        foreach (var item in lent) pool.Release(item);
    }

    [Fact(Timeout = 30_000)]
    public void CreateOnDemand_ShouldStillReuseReturnedObjects()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("on-demand-reuse")
            .WithMinSize(0)
            .WithMaxSize(4)
            .WithEnableAutoScaling(false)
            .WithEnableEviction(false)
            .WithEnableValidation(false)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand)
            .WithAcquireTimeout(TimeSpan.FromMilliseconds(200))
            // TotalCreated is metrics-gated, so the "created exactly once" assertions need metrics on.
            .WithEnableMetrics(true)
            .Build();

        var first = pool.Acquire();
        pool.Release(first);

        // Pooling stays intact: the returned object is handed back out instead of a second one being created.
        var second = pool.Acquire();
        Assert.Same(first, second);
        Assert.Equal(1, pool.GetStats().TotalCreated);
    }

    [Fact(Timeout = 30_000)]
    public async Task CreateOnDemand_AsyncAcquireOnEmptyPool_ShouldReturnWithoutWaiting()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("on-demand-async")
            .WithMinSize(0)
            .WithMaxSize(8)
            .WithEnableAutoScaling(false)
            .WithEnableEviction(false)
            .WithEnableValidation(false)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand)
            .WithAcquireTimeout(TimeSpan.FromSeconds(2))
            .Build();

        var sw = Stopwatch.StartNew();
        var obj = await pool.AcquireAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        sw.Stop();

        Assert.NotNull(obj);
        // Same CI-noise headroom as the sync cold test: well under the 2s configured timeout, so a
        // wait-out regression either throws TimeoutException or trips the bound.
        Assert.True(sw.ElapsedMilliseconds < 1500,
            $"the async first borrow of an empty pool must create synchronously, but it took {sw.ElapsedMilliseconds}ms");
    }
}
