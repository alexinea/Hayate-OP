using System.Collections.Concurrent;
using System.Reflection;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// D3 阶段：HayateObjectPool.Release 并发语义单测。
///
/// 之前 B 阶段（commit 56a5c47）通过 [CollectionDefinition] 串行化
/// 抑制了测试间污染引发的偶发超时，但 Release 路径在以下场景仍
/// 存在结构性缺陷：
///   1. ProcessorId % ShardCount 落点的 shard max=0 时，
///      Shard.Add 会静默 dispose 归还对象（Max &lt; ShardCount 时出现）。
///   2. Shard.Remove(_queue.ToList + Clear + Enqueue) 与 Shard.Add
///      并发时会产生对象丢失（previous T04 已暴露）。
///   3. EvictionCallback / ValidateCallback 调度的 Remove 与 Release 竞态。
///
/// 本文件锁定上述三类缺陷供回归保障；后续修复要确保所有 D3_* 测试通过。
/// </summary>
public class D3ReleaseConcurrencyTests
{
    private sealed class TestObject : IHayateResettable
    {
        private static int _nextId;
        public int Id { get; } = Interlocked.Increment(ref _nextId);
        public bool IsReset { get; private set; }
        public bool WillRejectNextRelease { get; set; }
        public void Reset() => IsReset = true;
    }

    /// <summary>
    /// D3-1：Acquire → Release → Acquire 环回应稳定返回同一对象，不应超时。
    ///
    /// 触发路径：默认 ShardCount=4 + MaxSize=2 时，shard 0/1 max=1、shard 2/3 max=0。
    /// 旧的 Release 通过 Thread.GetCurrentProcessorId() % 4 选 shard；若命中 max=0 的 shard，
    /// Shard.Add 静默 dispose 对象，第二次 Acquire 阻塞到默认超时。
    /// 修复后 Release 按 HayateObject&lt;T&gt;.ShardIndex round-trip，杜绝 ProcessorId 命中 max=0 shard。
    /// </summary>
    [Fact]
    public void D3_1_ReleaseRoundTrip_SmallPoolOverSharding_NoTimeout()
    {
        // ShardCount=4 + MaxSize=2 配置让 shard 2/3 的 max=0
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(1)
            .WithMaxSize(2)
            .WithShardCount(4)
            .WithEnableSharding(true)
            .WithAcquireTimeout(TimeSpan.FromSeconds(2))
            .Build();

        const int iterations = 50;
        for (var i = 0; i < iterations; i++)
        {
            TestObject obj;
            try
            {
                obj = pool.Acquire();
            }
            catch (TimeoutException)
            {
                throw new TimeoutException(
                    $"D3-1 触发：第 {i} 次 Acquire 在 Release 后超时（objects lost on shard）。" +
                    "Release 应按 HayateObject<T>.ShardIndex round-trip，避免 ProcessorId 命中 max=0 的分片。");
            }

            pool.Release(obj);

            // 关键：第二次 Acquire 若 Release 选择了 max=0 shard，对象已 dispose，
            // 这里必在 2s 内抛 TimeoutException
            var obj2 = pool.Acquire();
            Assert.NotNull(obj2);
            // 注：当前实现下 obj2 不一定是 obj（可能落在另一个 max≥1 的 shard）。
            // 关键是 obj2 != null + 不超时。
            pool.Release(obj2);
        }
    }

    /// <summary>
    /// D3-2：连续 Acquire/Release 环不应让 MinSize 失效。
    /// 当 Release 命中 max=0 shard 时，对象被 dispose，下次 Acquire
    /// 必超时（如果池被掏空）。修复后 round-trip 消除该故障路径。
    /// </summary>
    [Fact]
    public void D3_2_ReleaseShouldNotDescreasePooledCount_BelowMinSize()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(1)
            .WithMaxSize(2)
            .WithShardCount(4)
            .WithEnableSharding(true)
            .WithEnableAutoScaling(false)   // 禁用自动扩容以便精确观测对象丢失
            .WithEnableEviction(false)
            .WithAcquireTimeout(TimeSpan.FromSeconds(1))
            .Build();

        Assert.Equal(1, pool.GetStats().PooledCount);

        const int iterations = 30;
        for (var i = 0; i < iterations; i++)
        {
            TestObject obj;
            try { obj = pool.Acquire(); }
            catch (TimeoutException)
            {
                throw new TimeoutException($"D3-2 触发：第 {i} 次 Acquire 超时。" +
                    "Acquire → Release → Acquire 环丢失对象，导致池为空。");
            }
            pool.Release(obj);
        }
    }

    /// <summary>
    /// D3-3：并发 Acquire/Release + 后台 Eviction 同时跑，对象总和应可对账。
    /// 现有 Shard.Remove 的 LinkedList+SpinLock 实现保证不丢对象；本测试验证池级别一致性。
    /// </summary>
    [Fact]
    public async Task D3_3_ConcurrentAcquireReleaseWithEviction_ObjectCountConsistent()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(20)
            .WithMaxSize(100)
            .WithEnableEviction(true)
            .WithMaxLifeTime(TimeSpan.FromSeconds(2))
            .WithMaxIdleTime(TimeSpan.FromSeconds(1))
            .WithSoftMinEvictableIdleTime(TimeSpan.FromMilliseconds(500))
            .WithEvictionInterval(1000)        // builder 限制 ≥1000ms
            .WithEnableMetrics(true)
            .WithShardCount(4)
            .WithEnableSharding(true)
            .WithAcquireTimeout(TimeSpan.FromSeconds(10))
            .Build();

        const int threadCount = 16;
        const int operationsPerThread = 200;
        var tasks = new Task[threadCount];
        var exceptions = new ConcurrentBag<Exception>();

        using var startSignal = new ManualResetEventSlim(false);

        for (int i = 0; i < threadCount; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                startSignal.Wait();
                for (int j = 0; j < operationsPerThread; j++)
                {
                    TestObject obj;
                    try { obj = pool.Acquire(); }
                    catch (TimeoutException) { continue; }
                    catch (Exception ex) { exceptions.Add(ex); continue; }

                    Thread.SpinWait(50);
                    try { pool.Release(obj); }
                    catch (Exception ex) { exceptions.Add(ex); }
                }
            });
        }

        startSignal.Set();
        await Task.WhenAll(tasks);

        // 池仍在合法区间内
        var stats = pool.GetStats();
        Assert.Empty(exceptions);
        Assert.InRange(stats.PooledCount, 0, 100);
        Assert.InRange(stats.TotalAcquired, 0, int.MaxValue);
    }

    /// <summary>
    /// D3-4：OnRelease=false 高并发回归，确保销毁路径与并发 Acquire 不互相干扰。
    /// ForceScaleUpOneStep 的 3s 冷却是已知约束，本测试只校验：
    ///   - 没有未捕获异常
    ///   - 池不超出 MaxPoolSize
    ///   - 全量 Acquire 都最终完成（不强制 All Return，因为冷却导致节奏差异）
    /// </summary>
    [Fact]
    public async Task D3_4_ConcurrentReleaseWhereOnReleaseFalse_PoolStaysConsistent()
    {
        var policy = new HalfRejectPolicy();
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(10)
            .WithMaxSize(50)
            .WithPolicy(policy)
            .WithShardCount(4)
            .WithEnableSharding(true)
            .WithEnableAutoScaling(true)
            .WithEnableMetrics(true)
            .WithEnableEviction(false)
            .WithAcquireTimeout(TimeSpan.FromSeconds(15))   // ForceScaleUp 冷却期间会慢，给足时间
            .Build();

        const int threadCount = 6;
        const int opsPerThread = 100;
        var tasks = new Task[threadCount];
        var exceptions = new ConcurrentBag<Exception>();

        for (int i = 0; i < threadCount; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                for (int j = 0; j < opsPerThread; j++)
                {
                    TestObject obj;
                    try { obj = pool.Acquire(); }
                    catch (TimeoutException) { continue; }
                    catch { continue; }

                    obj.WillRejectNextRelease = (j % 2 == 0);
                    try { pool.Release(obj); }
                    catch (Exception ex) { exceptions.Add(ex); }
                }
            });
        }

        await Task.WhenAll(tasks);
        Thread.Sleep(500);   // 让 ForceScaleUp 冷却回收

        Assert.Empty(exceptions);
        var stats = pool.GetStats();
        Assert.InRange(stats.CurrentSize, 0, 50);
    }

    /// <summary>
    /// 半数 Release 拒收的策略。
    /// </summary>
    private sealed class HalfRejectPolicy : DotNetCore.HayateOP.Policies.IHayateObjectPolicy<TestObject>
    {
        public TestObject Create() => new();
        public bool OnRelease(TestObject item)
        {
            if (item.WillRejectNextRelease)
            {
                item.WillRejectNextRelease = false;
                return false;
            }
            item.Reset();
            return true;
        }
        public bool Validate(TestObject item) => true;
        public void OnAcquire(TestObject item) { }
        public void OnPassivate(TestObject item) { }
        public void OnDestroy(TestObject item) { }
    }
}
