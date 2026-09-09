using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DotNetCore.HayateOP.Policies;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// M16（2.5，breaking）：AsyncLocal 租约上下文——HayateLeaseContext。
/// 核心语义：并发借还各自持有独立租约上下文（AsyncLocal 流隔离），不再相互覆盖；
/// Release 结束租约（DetachFromFlow），Destroy 清空包装侧引用。
/// </summary>
public class LeaseContextTests
{
    private sealed class TestObject { }

    /// <summary>归还即拒绝的策略：迫使 Release 走 Destroy 路径（验证 Destroy 清空租约引用）。</summary>
    private sealed class RejectOnReleasePolicy : IHayateObjectPolicy<TestObject>
    {
        public TestObject Create() => new TestObject();
        public bool OnRelease(TestObject item) => false;
        public bool Validate(TestObject item) => true;
        public void OnAcquire(TestObject item) { }
        public void OnPassivate(TestObject item) { }
        public void OnDestroy(TestObject item) { }
    }

    [Fact]
    public async Task ConcurrentAcquires_ShouldOwnIsolatedLeaseContexts()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("m16-isolated")
            .WithMinSize(16)
            .WithMaxSize(32)
            .WithEnableLeakDetection(true)
            .WithLeakTraceCapture(HayateLeakTraceCaptureMode.EveryAcquire)
            .Build();

        // 并发借出：每个任务拿到自己的对象与自己的租约上下文。
        // 验证点：①租约 ID 全局唯一；②各上下文帧包含各自任务的标记方法名（流隔离）；
        // ③包装侧与流侧引用一致；④await 之后（ExecutionContext 流动）Current 仍随流携带。
        var done = await Task.WhenAll(Enumerable.Range(0, 8).Select(async id =>
        {
            var obj = BorrowMarked(pool, id);
            var ctx = HayateLeaseContext.Current;
            Assert.NotNull(ctx);
            Assert.Same(ctx, GetWrapped(pool, obj).LeaseContext);

            // 模拟租约期工作（含 await，ExecutionContext 流动后 Current 仍随流携带）
            await Task.Yield();
            Assert.Same(ctx, HayateLeaseContext.Current);

            pool.Release(obj);
            return (ctx, frames: ctx.Frames);
        }));

        var leaseIds = done.Select(d => d.ctx.LeaseId).ToHashSet();
        Assert.Equal(8, leaseIds.Count); // ① 租约 ID 唯一
        Assert.All(done, d => Assert.Contains("BorrowMarked", d.frames.Select(f => f.GetMethod()?.Name).ToList())); // ② 流隔离
        Assert.All(done, _ => Assert.Null(HayateLeaseContext.Current)); // ④ Release 后流上下文已清空
    }

    [Fact]
    public void SequentialReborrow_ShouldProduceDistinctLeaseIds()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("m16-reborrow")
            .WithMinSize(1)
            .WithMaxSize(2)
            .WithEnableLeakDetection(true)
            .WithLeakTraceCapture(HayateLeakTraceCaptureMode.EveryAcquire)
            .Build();

        // 同一包装对象被复借：每次借出产生新租约上下文（ID 递增），
        // 不存在 2.4 及之前 string 采集被复借覆盖的问题
        var seen = new HashSet<long>();
        for (var i = 0; i < 5; i++)
        {
            var obj = pool.Acquire();
            var ctx = HayateLeaseContext.Current;
            Assert.Same(ctx, GetWrapped(pool, obj).LeaseContext);
            Assert.True(seen.Add(ctx.LeaseId));
            pool.Release(obj);
            Assert.Null(HayateLeaseContext.Current);
        }
    }

    [Fact]
    public void Destroy_ShouldClearWrapperLeaseContextReference()
    {
        // 归还即拒绝 → Release 走 Destroy(w) → 包装侧 LeaseContext 引用清空
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("m16-destroy")
            .WithMinSize(1)
            .WithMaxSize(2)
            .WithPolicy(new RejectOnReleasePolicy())
            .WithEnableLeakDetection(true)
            .WithLeakTraceCapture(HayateLeakTraceCaptureMode.EveryAcquire)
            .Build();

        var obj = pool.Acquire();
        var w = GetWrapped(pool, obj);
        Assert.NotNull(w.LeaseContext);

        pool.Release(obj); // 策略拒绝 → Destroy

        Assert.Null(w.LeaseContext);
    }

    [Fact]
    public void CaptureOff_ShouldNotCreateLeaseContext()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("m16-off")
            .WithEnableLeakDetection(true)
            .Build();

        var obj = pool.Acquire();
        Assert.Null(HayateLeaseContext.Current);
        Assert.Null(GetWrapped(pool, obj).LeaseContext);
        pool.Release(obj);
    }

    /// <summary>带独立调用帧标记的借出（帧数组应含本方法名，证明流隔离）。</summary>
    private static TestObject BorrowMarked(IHayateObjectPool<TestObject> pool, int marker)
        => pool.Acquire();

    /// <summary>与 LeakDetectionTests 同款反射助手：经 _shards 逐分片探测登记表取包装对象。</summary>
    private static HayateObject<TestObject> GetWrapped(IHayateObjectPool<TestObject> pool, TestObject item)
    {
        var shardsField = pool.GetType().GetField("_shards", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.NotNull(shardsField);
        var shards = (Array)shardsField.GetValue(pool)!;

        foreach (var shard in shards)
        {
            var objectsField = shard.GetType().GetField("_objects", BindingFlags.Instance | BindingFlags.NonPublic);
            if (objectsField == null) continue;

            var map = objectsField.GetValue(shard);
            var tryGet = map.GetType().GetMethod("TryGetValue")!;
            var args = new object[] { item, null };
            tryGet.Invoke(map, args);
            if (args[1] != null) return (HayateObject<TestObject>)args[1];
        }

        return null!;
    }
}
