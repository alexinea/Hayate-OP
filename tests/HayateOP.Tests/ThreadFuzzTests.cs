using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// Mixed multithreaded fuzz of acquire/release/scale/evict/management operations.
/// Goal: N rounds of mixed stress with zero deadlock (xunit Timeout watchdog as backstop), zero leak (TotalAcquired==TotalReleased),
/// zero escaped exceptions, and capacity bounded throughout.
/// Rounds can be scaled via the HAYATE_FUZZ_ROUNDS env var (following the HAYATE_PRESSURE_LONG convention), default 150.
/// </summary>
public class ThreadFuzzTests
{
    private class TestObject { }

    private static readonly int Rounds =
        int.TryParse(Environment.GetEnvironmentVariable("HAYATE_FUZZ_ROUNDS"), out var r) && r > 0 ? r : 150;

    private static async Task RunWorkersAsync(int workerCount, Func<int, Task> workerBody)
    {
        var tasks = new List<Task>(workerCount);
        for (var i = 0; i < workerCount; i++)
        {
            var id = i;
            tasks.Add(Task.Run(() => workerBody(id)));
        }
        await Task.WhenAll(tasks);
    }

    [Fact(Timeout = 120000)]
    public async Task Fuzz_SyncAcquireRelease_NoDeadlockNoLeak()
    {
        const int workers = 8;
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("fuzz-sync")
            .WithEnableMetrics(true)   // TotalReleased is metrics-gated, so the balance assertions need it on (TotalAcquired is not gated)
            .WithEnableAutoScaling(false)
            .WithMinSize(4)
            .WithMaxSize(12)
            .WithRejectPolicy(HayatePoolRejectPolicy.BlockTimeout)
            .Build();

        // Act: 8 threads x N rounds of sync borrow/return; occasional yields between borrow/release create window contention
        await RunWorkersAsync(workers, async _ =>
        {
            for (var i = 0; i < Rounds; i++)
            {
                var obj = pool.Acquire(TimeSpan.FromSeconds(10));
                Assert.NotNull(obj);
                if (i % 3 == 0) Thread.Sleep(0);
                pool.Release(obj);
                if (i % 17 == 0) await Task.Yield();
            }
        });

        // Assert: zero leak + consistent counts + bounded capacity
        var stats = pool.GetStats();
        Assert.Equal(workers * Rounds, stats.TotalAcquired);
        Assert.Equal(workers * Rounds, stats.TotalReleased);
        Assert.Equal(0, stats.LeakDetectedCount);
        Assert.InRange(stats.CurrentSize, 4, 12);
    }

    [Fact(Timeout = 120000)]
    public async Task Fuzz_AsyncAcquireRelease_NoDeadlockNoLeak()
    {
        const int workers = 8;
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("fuzz-async")
            .WithEnableMetrics(true)   // count assertions need metrics enabled (same pitfall as ColdBootTests)
            .WithEnableAutoScaling(false)
            .WithMinSize(4)
            .WithMaxSize(12)
            .WithRejectPolicy(HayatePoolRejectPolicy.BlockTimeout)
            .Build();

        // Act: async path (including the timeout overload) mixed borrow/return
        await RunWorkersAsync(workers, async _ =>
        {
            for (var i = 0; i < Rounds; i++)
            {
                var obj = await pool.AcquireAsync(TimeSpan.FromSeconds(10));
                Assert.NotNull(obj);
                if (i % 5 == 0) await Task.Yield();
                pool.Release(obj);
            }
        });

        var stats = pool.GetStats();
        Assert.Equal(workers * Rounds, stats.TotalAcquired);
        Assert.Equal(workers * Rounds, stats.TotalReleased);
        Assert.Equal(0, stats.LeakDetectedCount);
        Assert.InRange(stats.CurrentSize, 4, 12);
    }

    [Fact(Timeout = 120000)]
    public async Task Fuzz_MixedSyncAsyncWithScalingAndReaders_StaysWithinBounds()
    {
        const int workers = 8;
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("fuzz-mixed-scaling")
            .WithEnableMetrics(true)   // count assertions need metrics enabled (same pitfall as ColdBootTests)
            .WithEnableAutoScaling(true)   // borrow/return pressure triggers scaling, competing with workers
            .WithMinSize(2)
            .WithMaxSize(16)
            .WithRejectPolicy(HayatePoolRejectPolicy.BlockTimeout)
            .Build();

        // Background reader-path contention threads: GetStats / TakeSnapshot run concurrently with borrow/return and scaling
        using var stopReaders = new CancellationTokenSource();
        var readerTasks = new List<Task>();
        for (var r = 0; r < 2; r++)
        {
            readerTasks.Add(Task.Run(async () =>
            {
                while (!stopReaders.Token.IsCancellationRequested)
                {
                    _ = pool.GetStats();
                    _ = pool.TakeSnapshot();
                    try { await Task.Delay(1, stopReaders.Token); }
                    catch (OperationCanceledException) { break; }
                }
            }, CancellationToken.None));
        }

        try
        {
            // Act: sync/async borrow/return mixed, scaling concurrent
            await RunWorkersAsync(workers, async id =>
            {
                for (var i = 0; i < Rounds; i++)
                {
                    if (id % 2 == 0)
                    {
                        var obj = pool.Acquire(TimeSpan.FromSeconds(10));
                        pool.Release(obj);
                    }
                    else
                    {
                        var obj = await pool.AcquireAsync(TimeSpan.FromSeconds(10));
                        pool.Release(obj);
                    }
                    if (i % 11 == 0) await Task.Yield();
                }
            });
        }
        finally
        {
            stopReaders.Cancel();
            await Task.WhenAll(readerTasks);
        }

        // Assert: no deadlock throughout (Timeout watchdog backstop), zero leak, bounded capacity
        var stats = pool.GetStats();
        Assert.Equal(workers * Rounds, stats.TotalAcquired);
        Assert.Equal(workers * Rounds, stats.TotalReleased);
        Assert.InRange(stats.CurrentSize, 2, 16);
    }

    [Fact(Timeout = 120000)]
    public async Task Fuzz_ConcurrentClearAndAcquire_RemainsUsableNoCrash()
    {
        // Clear concurrent with borrow/return: idle objects are destroyed, and borrowed objects are safely destroyed on return as "not belonging to the pool" --
        // fuzz verifies this path has no crash, no deadlock, and the pool still works normally after Clear.
        const int workers = 6;
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("fuzz-clear")
            .WithEnableAutoScaling(false)
            .WithMinSize(2)
            .WithMaxSize(8)
            .WithRejectPolicy(HayatePoolRejectPolicy.BlockTimeout)
            .Build();

        var clearTask = Task.Run(async () =>
        {
            for (var c = 0; c < 3; c++)
            {
                await Task.Delay(30);
                pool.Clear();
            }
        });

        await RunWorkersAsync(workers, async _ =>
        {
            for (var i = 0; i < Rounds; i++)
            {
                var obj = pool.Acquire(TimeSpan.FromSeconds(10));
                if (obj is not null)
                {
                    pool.Release(obj);
                }
                if (i % 13 == 0) await Task.Yield();
            }
        });

        await clearTask;

        // Assert: the pool is still usable after the Clear storm
        var obj2 = pool.Acquire(TimeSpan.FromSeconds(10));
        Assert.NotNull(obj2);
        pool.Release(obj2);
    }
}
