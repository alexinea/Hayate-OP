using System.Diagnostics;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// Cold-pool (Min=0) borrow-path bootstrap test.
/// When the pool is completely empty (no idle and no borrowed), the Block / BlockTimeout policies and the async path create the first object on demand,
/// eliminating the uncertainty of "waiting for a timeouter to replenish" (ColdStart measurements showed two timing patterns: 2/5 and 5/5 timeouts);
/// the CreateNew policy keeps the "create after the full timeout elapses" semantics unchanged.
/// </summary>
public class ColdBootTests
{
    private class TestObject : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    [Fact(Timeout = 60000)]
    public void ColdBoot_BlockTimeout_FirstAcquireShouldNotTimeout()
    {
        // Min=0 cold pool + BlockTimeout: the old semantics made the first borrow wait the full timeout (and only when autoScaling was on would the first timeouter replenish,
        // with uncertain results); after the fix, when the pool is completely empty it creates on demand and succeeds immediately.
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("coldboot-blocktimeout")
            .WithEnableAutoScaling(false)   // together with: Max keeps its hard cap, Min=0 does not collapse
            .WithEnableMetrics(true)        // TotalCreated count is gated by metrics, so it must be enabled to assert
            .WithMinSize(0)
            .WithMaxSize(10)
            .WithRejectPolicy(HayatePoolRejectPolicy.BlockTimeout)
            .Build();

        var sw = Stopwatch.StartNew();
        var obj = pool.Acquire(TimeSpan.FromSeconds(5));   // old semantics: here it would inevitably wait the full 5s or be replenished, with uncertain results
        sw.Stop();

        Assert.NotNull(obj);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"cold-boot acquire took {sw.Elapsed.TotalMilliseconds:F0}ms");

        // After return, the second borrow reuses the same object (pooling works; TotalCreated stays 1)
        pool.Release(obj);
        var obj2 = pool.Acquire(TimeSpan.FromSeconds(5));
        Assert.Same(obj, obj2);

        var stats = pool.GetStats();
        Assert.Equal(1, stats.TotalCreated);
        pool.Release(obj2);
    }

    [Fact(Timeout = 60000)]
    public async Task ColdBoot_Async_FirstAcquireShouldNotHang()
    {
        // Min=0 cold pool + async path: the old semantics waited forever for a return signal (an empty pool never returns) -> permanently hangs until cancellation;
        // after the fix it creates the first object on demand and completes deterministically.
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("coldboot-async")
            .WithEnableAutoScaling(false)
            .WithMinSize(0)
            .WithMaxSize(10)
            .Build();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var task = pool.AcquireAsync(cts.Token);
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(2)));

        Assert.True(completed == task, "async cold-boot acquire hung (old behavior: waits forever on empty pool)");
        var obj = await task;
        Assert.NotNull(obj);
        pool.Release(obj);
    }

    [Fact(Timeout = 60000)]
    public void ColdBoot_ConcurrentFirstAcquire_ShouldCreateExactlyOne()
    {
        // Prevent duplicate concurrent first borrows: CAS claim guarantees the cold start creates only one object, and the other waiters reuse the return signal.
        const int concurrency = 8;
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("coldboot-concurrent")
            .WithEnableAutoScaling(false)
            .WithEnableMetrics(true)
            .WithMinSize(0)
            .WithMaxSize(concurrency)
            .WithRejectPolicy(HayatePoolRejectPolicy.BlockTimeout)
            .Build();

        var barrier = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = new Task<TestObject>[concurrency];
        for (var i = 0; i < concurrency; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                await barrier.Task;   // all threads align, then borrow simultaneously to maximize contention
                var obj = pool.Acquire(TimeSpan.FromSeconds(15));
                pool.Release(obj);    // release immediately: a single cold-start object rotates and is reused among the waiters
                return obj;
            });
        }
        barrier.SetResult(true);

        var results = Task.WhenAll(tasks).GetAwaiter().GetResult();

        Assert.All(results, Assert.NotNull);
        Assert.Equal(1, pool.GetStats().TotalCreated);   // cold start creates only one; the rest rotate and reuse
    }

    [Fact(Timeout = 60000)]
    public void ColdBoot_CreateNew_SemanticsShouldStayUnchanged()
    {
        // CreateNew semantics guard: it creates only after the full AcquireTimeout elapses, unaffected by the cold-boot fix.
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("coldboot-createnew")
            .WithEnableAutoScaling(false)
            .WithMinSize(0)
            .WithMaxSize(10)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateNew)
            .Build();

        var sw = Stopwatch.StartNew();
        var obj = pool.Acquire(TimeSpan.FromMilliseconds(300));
        sw.Stop();

        Assert.NotNull(obj);
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(250), $"CreateNew must wait for timeout before creating, took {sw.Elapsed.TotalMilliseconds:F0}ms");
        pool.Release(obj);
    }
}
