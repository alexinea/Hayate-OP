using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// M10+M20（2.5）：观测字段结构化。
/// M10：借出取证 string 全文 → StackFrame[]（低开销采集，快照按需格式化）。
/// M20：HayateObject 增 CreatedAtTick / LeaseCount / OwnerPoolName，经快照 ObjectDetails 输出。
/// </summary>
public class ObservabilityFieldsTests
{
    private sealed class TestObject { }

    // ---------- M10 ----------

    [Fact]
    public void EveryAcquireMode_ShouldCaptureLeaseContext()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("m10-every")
            .WithEnableLeakDetection(true)
            .WithLeakDetectionThreshold(TimeSpan.FromMinutes(30))
            .WithLeakTraceCapture(HayateLeakTraceCaptureMode.EveryAcquire)
            .Build();

        var a = pool.Acquire();
        var w = GetWrapped(pool, a);
        Assert.NotNull(w.LeaseContext);
        var ctx = w.LeaseContext;

        // 结构化帧：包含借出调用链上的方法帧（Acquire 或测试辅助方法）
        var frames = ctx.Frames;
        Assert.NotEmpty(frames);
        var methodNames = frames.Select(f => f.GetMethod()?.Name).ToList();
        // 借出调用链须留有方法帧。M3 后 Acquire()/Acquire(TimeSpan) 均为极薄转发
        // （`return AcquireCore(...)`），高版本 JIT 的分层编译/PGO 可能将其内联，
        // 栈帧中仅剩 AcquireCore —— 断言本意为「含借出链方法帧」，二者皆满足。
        Assert.Contains(methodNames, n => n == "Acquire" || n == "AcquireCore");
        Assert.Contains("EveryAcquireMode_ShouldCaptureLeaseContext", methodNames);
        // 低开销口径：不解析源文件行号
        Assert.All(frames, f => Assert.Null(f.GetFileName()));

        // M16：租约 ID 单调递增；AsyncLocal 流上下文在租约期内与包装侧一致
        Assert.True(ctx.LeaseId > 0);
        Assert.Same(ctx, HayateLeaseContext.Current);
        Assert.True(ctx.BorrowedAt > 0); // 借出时刻（Stopwatch ticks）

        pool.Release(a);
        // Release 结束租约：流上下文清空
        Assert.Null(HayateLeaseContext.Current);
    }

    [Fact]
    public void OffMode_ShouldNotCaptureLeaseContext()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("m10-off")
            .WithEnableLeakDetection(true)
            .WithLeakDetectionThreshold(TimeSpan.FromMinutes(30))
            .Build();

        var a = pool.Acquire();
        Assert.Null(GetWrapped(pool, a).LeaseContext);
        Assert.Null(HayateLeaseContext.Current);
        pool.Release(a);
    }

    [Fact]
    public void Snapshot_LeakTraces_ShouldBeFormattedFromFrames()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("m10-format")
            .WithEnableLeakDetection(true)
            .WithLeakDetectionThreshold(TimeSpan.FromMilliseconds(150))
            .WithLeakTraceCapture(HayateLeakTraceCaptureMode.EveryAcquire)
            .Build();

        var obj = pool.Acquire();
        Thread.Sleep(300);

        var snapshot = pool.TakeSnapshot();
        Assert.NotEmpty(snapshot.LeakTraces);
        var trace = snapshot.LeakTraces[0];
        // M16 格式化形态：[Lease {id}] 前缀 + Type.Method+0x偏移，帧间 " <- " 连接
        Assert.StartsWith("[Lease ", trace);
        Assert.Contains("ObservabilityFieldsTests", trace);
        Assert.Contains("+0x", trace);
        Assert.Contains(" <- ", trace);

        pool.Release(obj);
    }

    // ---------- M20 ----------

    [Fact]
    public void LeaseCount_ShouldIncrementPerAcquire()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("m20-lease")
            .WithMinSize(1)
            .WithMaxSize(2)
            .Build();

        var obj = pool.Acquire();
        var w = GetWrapped(pool, obj);
        Assert.Equal(1, w.LeaseCount);
        pool.Release(obj);

        // 同一包装对象再次借出，计数应累计为 2
        var again = pool.Acquire();
        Assert.Same(obj, again);
        Assert.Equal(2, GetWrapped(pool, again).LeaseCount);
        pool.Release(again);
    }

    [Fact]
    public void OwnerPoolName_And_CreatedAtTick_ShouldBeStampedAtCreation()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("m20-owner")
            .WithMinSize(1)
            .WithMaxSize(2)
            .Build();

        var obj = pool.Acquire();
        var w = GetWrapped(pool, obj);

        Assert.Equal("m20-owner", w.OwnerPoolName);
        // 挂钟创建时间应落在最近一分钟内
        var createdAt = new DateTimeOffset(w.CreatedAtTick, TimeSpan.Zero);
        Assert.True(createdAt <= DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.True(createdAt >= DateTimeOffset.UtcNow.AddMinutes(-1));

        pool.Release(obj);
    }

    [Fact]
    public void Snapshot_ObjectDetails_ShouldCarryLifecycleFields()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("m20-detail")
            .WithMinSize(2)
            .WithMaxSize(4)
            .Build();

        var borrowed = pool.Acquire(); // Min=2 预热 2 个，借出 1 → 1 借出 + 1 空闲
        var snapshot = pool.TakeSnapshot();

        Assert.Equal(2, snapshot.ObjectDetails.Count);
        Assert.Equal(1, snapshot.ObjectDetails.Count(d => d.IsBorrowed));

        var detail = snapshot.ObjectDetails.First(d => d.IsBorrowed);
        Assert.Equal("m20-detail", detail.OwnerPoolName);
        Assert.Equal(1, detail.LeaseCount);
        var createdAt = new DateTimeOffset(detail.CreatedAtTick, TimeSpan.Zero);
        Assert.True(createdAt <= DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.True(createdAt >= DateTimeOffset.UtcNow.AddMinutes(-1));

        pool.Release(borrowed);

        // 归还后借出态明细归零
        var after = pool.TakeSnapshot();
        Assert.All(after.ObjectDetails, d => Assert.False(d.IsBorrowed));
    }

    /// <summary>与 LeakDetectionTests 同款反射助手：经 _shards 逐分片探测登记表取包装对象。</summary>
    private static HayateObject<TestObject> GetWrapped(IHayateObjectPool<TestObject> pool, TestObject item)
    {
        var shardsField = pool.GetType().GetField("_shards", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Assert.NotNull(shardsField);
        var shards = (Array)shardsField.GetValue(pool)!;

        foreach (var shard in shards)
        {
            var objectsField = shard.GetType().GetField("_objects", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
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
