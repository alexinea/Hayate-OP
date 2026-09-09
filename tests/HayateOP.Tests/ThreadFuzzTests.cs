using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// PR-E A4（=M9 线程安全验证）：多线程 acquire/release/scale/evict/管理操作混合 fuzz。
/// 目标：N 轮混合压测零死锁（xunit Timeout 看门狗兜底）、零泄漏（TotalAcquired==TotalReleased）、
/// 零异常逃逸、容量全程有界。
/// 轮次可通过环境变量 HAYATE_FUZZ_ROUNDS 缩放（沿用 HAYATE_PRESSURE_LONG 惯例），默认 150。
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
            .WithEnableMetrics(true)   // TotalAcquired/TotalReleased 计数受 metrics 门控，断言需开启
            .WithEnableAutoScaling(false)
            .WithMinSize(4)
            .WithMaxSize(12)
            .WithRejectPolicy(HayatePoolRejectPolicy.BlockTimeout)
            .Build();

        // Act：8 线程 × N 轮同步借还；借出/归还间偶发让步制造窗口竞争
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

        // Assert：零泄漏 + 计数一致 + 容量有界
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
            .WithEnableMetrics(true)   // 计数断言需开启 metrics（同 ColdBootTests 踩坑）
            .WithEnableAutoScaling(false)
            .WithMinSize(4)
            .WithMaxSize(12)
            .WithRejectPolicy(HayatePoolRejectPolicy.BlockTimeout)
            .Build();

        // Act：异步路径（含 A3 超时重载）混合借还
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
            .WithEnableMetrics(true)   // 计数断言需开启 metrics（同 ColdBootTests 踩坑）
            .WithEnableAutoScaling(true)   // 借还压力触发扩缩容，与 worker 竞争
            .WithMinSize(2)
            .WithMaxSize(16)
            .WithRejectPolicy(HayatePoolRejectPolicy.BlockTimeout)
            .Build();

        // 后台读路径竞争线程：GetStats / TakeSnapshot 与借还、扩缩容并发
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
            // Act：同步/异步借还混跑，扩缩容并发
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

        // Assert：全程无死锁（Timeout 看门狗兜底）、零泄漏、容量有界
        var stats = pool.GetStats();
        Assert.Equal(workers * Rounds, stats.TotalAcquired);
        Assert.Equal(workers * Rounds, stats.TotalReleased);
        Assert.InRange(stats.CurrentSize, 2, 16);
    }

    [Fact(Timeout = 120000)]
    public async Task Fuzz_ConcurrentClearAndAcquire_RemainsUsableNoCrash()
    {
        // Clear 与借还并发：空闲对象被销毁、借出对象归还时按「不属于池」安全销毁——
        // fuzz 验证该路径无崩溃、无死锁，且 Clear 后池仍可正常借还。
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

        // Assert：Clear 风暴后池仍可用
        var obj2 = pool.Acquire(TimeSpan.FromSeconds(10));
        Assert.NotNull(obj2);
        pool.Release(obj2);
    }
}
