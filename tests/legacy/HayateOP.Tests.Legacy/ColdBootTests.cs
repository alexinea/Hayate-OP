using System.Diagnostics;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// PR-D L5：冷池（Min=0）借出路径自举测试。
/// 池完全空（无空闲且无借出）时，Block / BlockTimeout 策略与异步路径按需创建首个对象，
/// 消除「等超时者补货」的不确定性（T12 ColdStart 实测 2/5 与 5/5 超时两种时序）；
/// CreateNew 策略保持「等满超时后创建」语义不变（L6）。
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
        // Min=0 冷池 + BlockTimeout：旧语义首借等满超时（且仅 autoScaling on 时被首个超时者补货，
        // 结果不确定）；L5 后池完全空时按需创建，立即成功。
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("coldboot-blocktimeout")
            .WithEnableAutoScaling(false)   // 与 L9 协同：Max 保留硬上限，Min=0 不塌缩
            .WithEnableMetrics(true)        // TotalCreated 计数受 metrics 门控，需开启才能断言
            .WithMinSize(0)
            .WithMaxSize(10)
            .WithRejectPolicy(HayatePoolRejectPolicy.BlockTimeout)
            .Build();

        var sw = Stopwatch.StartNew();
        var obj = pool.Acquire(TimeSpan.FromSeconds(5));   // 旧语义此处必然等满 5s 或被补货，结果不确定
        sw.Stop();

        Assert.NotNull(obj);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"cold-boot acquire took {sw.Elapsed.TotalMilliseconds:F0}ms");

        // 归还后二次借出复用同一对象（池化生效，TotalCreated 保持 1）
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
        // Min=0 冷池 + 异步路径：旧语义无限等归还信号（池空永不回池）→ 永久挂起直到取消；
        // L5 后按需创建首个对象，确定性完成。
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
        // 并发首借防重：CAS 认领保证冷启动只创建一个对象，其余等待者复用归还信号。
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
                await barrier.Task;   // 全员对齐后同时首借，最大化竞争
                var obj = pool.Acquire(TimeSpan.FromSeconds(15));
                pool.Release(obj);    // 立即归还：单个冷启动对象在等待者间轮转复用
                return obj;
            });
        }
        barrier.SetResult(true);

        var results = Task.WhenAll(tasks).GetAwaiter().GetResult();

        Assert.All(results, Assert.NotNull);
        Assert.Equal(1, pool.GetStats().TotalCreated);   // 冷启动仅创建一个，其余轮转复用
    }

    [Fact]
    public void ColdBoot_CreateNew_SemanticsShouldStayUnchanged()
    {
        // CreateNew 语义守卫（L6）：等满 AcquireTimeout 后才创建，不受 L5 冷启动影响。
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
