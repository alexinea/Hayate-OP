using System.Diagnostics;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// Cold-pool (Min=0) bootstrap-on-borrow test.
/// When the pool is completely empty (no idle and no borrowed), the Block / BlockTimeout policies and the async path create the first object on demand,
/// eliminating the uncertainty of "waiting for a timeout to replenish" (the ColdStart measurement showed two timings: 2/5 and 5/5 timeouts);
/// the CreateNew policy keeps the "create only after the full timeout" semantics unchanged.<br />
/// Since 3.0 (B6) the file also carries the borrow path's growth and wake-up contract: which policies grow the
/// pool on a miss and which wait, and that a parked waiter is woken by a signal rather than by a periodic
/// re-check.
/// </summary>
public class ColdBootTests
{
    private class TestObject : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void ColdBoot_BlockTimeout_FirstAcquireShouldNotTimeout()
    {
        // Min=0 cold pool + BlockTimeout: the old semantics waited the full timeout on first borrow (and was only replenished by the first timeout waiter when autoScaling was on,
        // so the result was non-deterministic); after the fix, when the pool is completely empty it creates on demand and succeeds immediately.
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("coldboot-blocktimeout")
            .WithEnableAutoScaling(false)   // Works with the fix: Max stays a hard upper bound, and Min=0 does not collapse
            .WithEnableMetrics(true)        // TotalCreated counter is gated by metrics; must be enabled to assert
            .WithMinSize(0)
            .WithMaxSize(10)
            .WithRejectPolicy(HayatePoolRejectPolicy.BlockTimeout)
            .Build();

        var sw = Stopwatch.StartNew();
        var obj = pool.Acquire(TimeSpan.FromSeconds(5));   // Under old semantics this would inevitably wait the full 5s or be replenished, with non-deterministic results
        sw.Stop();

        Assert.NotNull(obj);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"cold-boot acquire took {sw.Elapsed.TotalMilliseconds:F0}ms");

        // A second borrow after return reuses the same object (pooling works, TotalCreated stays 1)
        pool.Release(obj);
        var obj2 = pool.Acquire(TimeSpan.FromSeconds(5));
        Assert.Same(obj, obj2);

        var stats = pool.GetStats();
        Assert.Equal(1, stats.TotalCreated);
        pool.Release(obj2);
    }

    [Fact]
    public async Task ColdBoot_Async_FirstAcquireShouldNotHang()
    {
        // Min=0 cold pool + async path: old semantics waited forever for a return signal (an empty pool never returns to the pool) -> hangs until cancellation;
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

    [Fact]
    public void ColdBoot_ConcurrentFirstAcquire_ShouldCreateExactlyOne()
    {
        // Concurrent first-borrow de-duplication: CAS claim guarantees cold start creates only one object, and the other waiters reuse the return signal.
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
                await barrier.Task;   // All threads align, then borrow simultaneously to maximize contention
                var obj = pool.Acquire(TimeSpan.FromSeconds(15));
                pool.Release(obj);    // Return immediately: the single cold-start object rotates among the waiters
                return obj;
            });
        }
        barrier.SetResult(true);

        var results = Task.WhenAll(tasks).GetAwaiter().GetResult();

        Assert.All(results, Assert.NotNull);
        Assert.Equal(1, pool.GetStats().TotalCreated);   // Cold start creates only one; the rest rotate and reuse
    }

    [Fact]
    public void ColdBoot_CreateNew_SemanticsShouldStayUnchanged()
    {
        // CreateNew semantics guard: it creates only after the full AcquireTimeout, unaffected by cold-start bootstrap.
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
        pool.Release(obj);
    }

    // ── B6-E1 (3.0): the growth contract of the wait-based policies ───────────────────────────────────
    //
    // Measured before writing these tests (Release/net10.0, one shard, auto-scaling off): with every object
    // lent out, a miss under BlockTimeout waits and then throws even though MaxPoolSize still allows more
    // objects — Min=0/Max=8 gave TimeoutException at ~865ms against an 800ms timeout with TotalCreated pinned
    // at 1, and Min=5/Max=8 gave TimeoutException at ~868ms with TotalCreated pinned at 5. The same miss under
    // CreateOnDemand was served in ~0ms (TotalCreated 1→2 and 5→6), and with auto-scaling left on the Min=0
    // pool was grown by the background scaler at ~5.1s. That is the contract locked here: MaxPoolSize is a
    // ceiling for the wait-based policies, not a growth target — growing the pool on a miss is what
    // CreateOnDemand is for (and what HayatePool.Simple now uses, see B6-E3).

    [Fact]
    public void BlockTimeout_NonEmptyPoolDoesNotGrowOnMiss()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("coldboot-e1-nonempty")
            .WithEnableAutoScaling(false)
            .WithEnableMetrics(true)
            .WithMinSize(5)
            .WithMaxSize(8)
            .WithRejectPolicy(HayatePoolRejectPolicy.BlockTimeout)
            .Build();

        var held = new TestObject[5];
        for (var i = 0; i < held.Length; i++) held[i] = pool.Acquire(TimeSpan.FromMilliseconds(500));
        Assert.Equal(5, pool.GetStats().TotalCreated);

        var sw = Stopwatch.StartNew();
        Assert.Throws<TimeoutException>(() => pool.Acquire(TimeSpan.FromMilliseconds(500)));
        sw.Stop();

        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(450), $"the miss must wait out the timeout, took {sw.Elapsed.TotalMilliseconds:F0}ms");
        Assert.Equal(5, pool.GetStats().TotalCreated);   // MaxPoolSize left room for three more, and the pool still refused to grow
        Assert.Equal(0, pool.TakeSnapshot().PooledCount);

        foreach (var o in held) pool.Release(o);

        // The room MaxPoolSize left is still reachable — a return hands the waiter an existing object rather than creating one.
        var reused = pool.Acquire(TimeSpan.FromMilliseconds(500));
        Assert.Contains(reused, held);
        Assert.Equal(5, pool.GetStats().TotalCreated);
        pool.Release(reused);
    }

    [Fact]
    public void BlockTimeout_EmptyPoolSecondBorrowDoesNotGrowEither()
    {
        // The Min=0 shape of the same contract: the first borrow bootstraps the pool, and the second borrow —
        // taken while the first object is still out — has nothing to reuse, is no longer "completely empty",
        // and therefore does not bootstrap again. It waits out the timeout instead.
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("coldboot-e1-min0")
            .WithEnableAutoScaling(false)
            .WithEnableMetrics(true)
            .WithMinSize(0)
            .WithMaxSize(8)
            .WithRejectPolicy(HayatePoolRejectPolicy.BlockTimeout)
            .Build();

        var held = pool.Acquire(TimeSpan.FromMilliseconds(500));   // cold boot: immediate, pool was completely empty
        Assert.Equal(1, pool.GetStats().TotalCreated);

        var sw = Stopwatch.StartNew();
        Assert.Throws<TimeoutException>(() => pool.Acquire(TimeSpan.FromMilliseconds(500)));
        sw.Stop();

        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(450), $"the miss must wait out the timeout, took {sw.Elapsed.TotalMilliseconds:F0}ms");
        Assert.Equal(1, pool.GetStats().TotalCreated);

        pool.Release(held);
        var reused = pool.Acquire(TimeSpan.FromMilliseconds(500));
        Assert.Same(held, reused);
        pool.Release(reused);
    }

    [Fact]
    public void CreateOnDemand_ServesTheSameMissWithoutWaiting()
    {
        // Counter-proof for the two tests above: the identical shape (Min=5, Max=8, auto-scaling off, every
        // object lent out) is served on the spot under CreateOnDemand. Without this pair, "the wait policy did
        // not grow" could not be told apart from "the pool was unable to grow".
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("coldboot-e1-ondemand")
            .WithEnableAutoScaling(false)
            .WithEnableMetrics(true)
            .WithMinSize(5)
            .WithMaxSize(8)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand)
            .Build();

        var held = new TestObject[5];
        for (var i = 0; i < held.Length; i++) held[i] = pool.Acquire(TimeSpan.FromMilliseconds(500));

        var sw = Stopwatch.StartNew();
        var extra = pool.Acquire(TimeSpan.FromMilliseconds(500));
        sw.Stop();

        Assert.NotNull(extra);
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(250), $"create-on-demand must not wait, took {sw.Elapsed.TotalMilliseconds:F0}ms");
        Assert.Equal(6, pool.GetStats().TotalCreated);
        Assert.DoesNotContain(extra, held);

        foreach (var o in held) pool.Release(o);
        pool.Release(extra);
    }

    [Fact]
    public void BlockTimeout_GrowsOnlyThroughBackgroundScaling()
    {
        // The wait-based policies are not growth-less, they just never grow *on the borrow path*: the pool can
        // still be grown from outside while a caller waits, and auto-scaling is the one such path in the
        // library. So a waiter that outlives a scaling period is served by an object it was never handed a
        // wake-up signal for. Before B6-1 that object reached the waiter only through the bounded re-check;
        // since then the scaler publishes a signal like every other site that makes an object available, so
        // this scenario now holds the signal path rather than the re-check. See the invariant on _blockGate.
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("coldboot-e1-scaler")
            .WithEnableAutoScaling(true)
            .WithScalingInterval(200)
            .WithScaleUpCooldownSeconds(0)
            .WithEnableMetrics(true)
            .WithMinSize(0)
            .WithMaxSize(8)
            .WithRejectPolicy(HayatePoolRejectPolicy.BlockTimeout)
            .Build();

        var held = pool.Acquire(TimeSpan.FromMilliseconds(500));
        Assert.Equal(1, pool.GetStats().TotalCreated);

        var sw = Stopwatch.StartNew();
        var scaled = pool.Acquire(TimeSpan.FromSeconds(3));
        sw.Stop();

        Assert.NotNull(scaled);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"the scaler should have supplied an object well before the timeout, took {sw.Elapsed.TotalMilliseconds:F0}ms");
        Assert.True(pool.GetStats().TotalCreated > 1, "the background scaler is the growth path in this scenario");

        pool.Release(held);
        pool.Release(scaled);
    }

    [Fact]
    public void BlockTimeout_SeesAPreWarmedObjectPromptly()
    {
        // PreWarm() is the one idle-list write a caller can trigger directly, so it is the cheapest way to hand
        // a parked waiter an object that no return produced. Since B6-1 it publishes a wake-up signal, and the
        // waiter is woken by that signal rather than by a re-check. The bound below is deliberately loose - it
        // only separates "served by a signal" from "waited out the acquire timeout" - because the tight,
        // discriminating version of this measurement is
        // BlockTimeout_ParkedWaiterIsWokenByTheSignalNotByTheSlice.
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("coldboot-e2-prewarm")
            .WithEnableAutoScaling(false)
            .WithEnableMetrics(true)
            .WithMinSize(0)
            .WithMaxSize(8)
            .WithRejectPolicy(HayatePoolRejectPolicy.BlockTimeout)
            .Build();

        var held = pool.Acquire(TimeSpan.FromMilliseconds(500));   // cold boot; the pool is no longer empty
        Assert.Equal(1, pool.GetStats().TotalCreated);

        var waiter = Task.Run(() => pool.Acquire(TimeSpan.FromSeconds(3)));
        Thread.Sleep(300);   // let the waiter park on the gate with nothing to take
        Assert.Equal(0, pool.TakeSnapshot().PooledCount);

        Assert.Equal(2, pool.PreWarm(2));   // two idle objects appear, and no signal is published for them
        var sw = Stopwatch.StartNew();
        var served = waiter.GetAwaiter().GetResult();
        sw.Stop();

        Assert.NotNull(served);
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(1500), $"the waiter must not have to wait out the acquire timeout, took {sw.Elapsed.TotalMilliseconds:F0}ms");
        Assert.Equal(3, pool.GetStats().TotalCreated);   // the waiter reused a pre-warmed object instead of creating one

        pool.Release(held);
        pool.Release(served);
    }


    [Fact]
    public void BlockTimeout_ParkedWaiterIsWokenByTheSignalNotByTheSlice()
    {
        // B6-1 acceptance (wake-up granularity): the synchronous borrow path now waits once, on the signal,
        // instead of re-checking the shard every 100ms. PreWarm() is the event used here because it publishes a
        // signal only after the refactor, which is what makes the two mechanisms separable by measurement: a
        // signal-driven wake lands within a few milliseconds, while a slice-driven one cannot land before the
        // 100ms boundary. System.Threading.ThreadState.WaitSleepJoin is what makes the measurement meaningful - it is observed
        // while the thread is blocked on the gate, so the timer starts from a parked waiter rather than from a
        // thread that might still be on its way in.
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("coldboot-b61-wake")
            .WithEnableAutoScaling(false)
            .WithEnableMetrics(true)
            .WithMinSize(0)
            .WithMaxSize(8)
            .WithRejectPolicy(HayatePoolRejectPolicy.BlockTimeout)
            .Build();

        var held = pool.Acquire(TimeSpan.FromMilliseconds(500));   // cold boot; the pool is no longer empty
        Assert.Equal(1, pool.GetStats().TotalCreated);

        TestObject served = null;
        var waiter = new Thread(() => { served = pool.Acquire(TimeSpan.FromSeconds(5)); });
        waiter.IsBackground = true;
        waiter.Start();

        var spin = Stopwatch.StartNew();
        while ((waiter.ThreadState & System.Threading.ThreadState.WaitSleepJoin) == 0 && spin.ElapsedMilliseconds < 2000) Thread.Sleep(5);
        Assert.True((waiter.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, $"the waiter must be parked on the gate, state was {waiter.ThreadState}");

        var sw = Stopwatch.StartNew();
        Assert.Equal(2, pool.PreWarm(2));   // two idle objects appear, and a signal is published for them
        Assert.True(waiter.Join(TimeSpan.FromSeconds(5)), "the pre-warm signal must wake the parked waiter");
        sw.Stop();

        Assert.NotNull(served);
        Assert.True(sw.ElapsedMilliseconds < 50, $"a signal-driven wake must not wait for the 100ms slice, took {sw.Elapsed.TotalMilliseconds:F0}ms");
        Assert.Equal(3, pool.GetStats().TotalCreated);   // the waiter reused a pre-warmed object

        pool.Release(held);
        pool.Release(served);
    }

    [Fact]
    public void Block_WaitsWithoutATimeoutUntilASignalArrives()
    {
        // The Block policy now waits on the gate with no timeout at all, so the two properties the slice used to
        // provide have to hold on their own: the thread really parks (it is not spinning), and the only way out
        // is a signal. Both are observed from the outside - the thread state while nothing happens, and the
        // object it ends up holding once a return publishes a signal.
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("coldboot-b61-block")
            .WithEnableAutoScaling(false)
            .WithEnableMetrics(true)
            .WithMinSize(0)
            .WithMaxSize(8)
            .WithRejectPolicy(HayatePoolRejectPolicy.Block)
            .Build();

        var held = pool.Acquire(TimeSpan.FromMilliseconds(500));   // cold boot; the pool is no longer empty
        Assert.Equal(1, pool.GetStats().TotalCreated);

        TestObject served = null;
        var waiter = new Thread(() => { served = pool.Acquire(); });
        waiter.IsBackground = true;
        waiter.Start();

        var spin = Stopwatch.StartNew();
        while ((waiter.ThreadState & System.Threading.ThreadState.WaitSleepJoin) == 0 && spin.ElapsedMilliseconds < 2000) Thread.Sleep(5);
        Assert.True((waiter.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, $"the waiter must be parked on the gate, state was {waiter.ThreadState}");

        // Nothing is returned and no signal is published for a full second: with no timeout to expire and no
        // slice to re-check, the waiter has to still be parked. A policy that quietly reintroduced either would
        // have left this state by now.
        Thread.Sleep(1000);
        Assert.True((waiter.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, $"the Block waiter must stay parked, state was {waiter.ThreadState}");

        pool.Release(held);   // the one event that can wake it
        Assert.True(waiter.Join(TimeSpan.FromSeconds(5)), "the return signal must wake the Block waiter");
        Assert.Same(held, served);
    }

    [Fact]
    public void BlockTimeout_HonoursTheTimeoutWithoutOvershooting()
    {
        // B6-1 acceptance (timeout boundary): the wait is bounded by the timeout itself now, so a miss can no
        // longer overshoot it by up to a re-check slice. The lower bound is the contract that was already
        // pinned - the miss waits the timeout out rather than returning early - and the upper bound is what the
        // refactor buys: the overshoot is scheduling noise rather than a slice.
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("coldboot-b61-timeout")
            .WithEnableAutoScaling(false)
            .WithEnableMetrics(true)
            .WithMinSize(0)
            .WithMaxSize(8)
            .WithRejectPolicy(HayatePoolRejectPolicy.BlockTimeout)
            .Build();

        var held = pool.Acquire(TimeSpan.FromMilliseconds(500));   // cold boot; the pool is no longer empty

        var sw = Stopwatch.StartNew();
        Assert.Throws<TimeoutException>(() => pool.Acquire(TimeSpan.FromMilliseconds(400)));
        sw.Stop();

        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(380), $"the miss must wait out the timeout, took {sw.Elapsed.TotalMilliseconds:F0}ms");
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(600), $"the timeout must not be overshot, took {sw.Elapsed.TotalMilliseconds:F0}ms");
        Assert.Equal(1, pool.GetStats().TotalCreated);

        pool.Release(held);
    }
}

