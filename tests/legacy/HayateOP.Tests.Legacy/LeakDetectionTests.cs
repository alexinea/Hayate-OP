using System.Reflection;

namespace DotNetCore.HayateOP.Tests;

public class LeakDetectionTests
{
    private class TestObject { }

    [Fact]
    public void EnableLeakDetection_ShouldAcquireAndReleaseNormally()
    {
        // PR-D L1：泄漏检测（阈值判定 + LeakCount）与栈取证解耦后，开启检测
        // 不再意味着每次借出抓栈（默认 Off）；本用例仅验证检测开启时借还路径正常。
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithEnableLeakDetection(true)
            .WithLeakDetectionThreshold(TimeSpan.FromSeconds(1))
            .Build();

        var obj = pool.Acquire();
        pool.Release(obj);
        Assert.NotNull(obj);
    }

    [Fact]
    public void DisableLeakDetection_ShouldNotRecordStackTrace()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithEnableLeakDetection(false)
            .Build();

        var snapshot = pool.TakeSnapshot();
        Assert.Empty(snapshot.LeakTraces);
    }

    [Fact]
    public void TakeSnapshot_ShouldDetectLeaks()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithEnableLeakDetection(true)
            .WithLeakDetectionThreshold(TimeSpan.FromMilliseconds(100))
            .Build();

        // 借出对象不归还
        var obj = pool.Acquire();

        // 等待超过泄漏阈值
        Thread.Sleep(200);

        var snapshot = pool.TakeSnapshot();

        // T13：原断言 LeakCount >= 0 为恒真（掩盖了 T04 后泄漏检测失效的缺陷）。
        // 修复后 TakeSnapshot 遍历分片登记表（含借出对象），借出超阈值未归还必须被检出。
        // PR-D L1：默认 LeakTraceCaptureMode=Off 不抓栈，LeakTraces 条目为占位文案
        //（"No stack trace available"），但泄漏发现与计数语义不变。
        Assert.True(snapshot.LeakCount >= 1,
            $"借出超阈值对象应被检出泄漏，实际 LeakCount={snapshot.LeakCount}");
        Assert.NotEmpty(snapshot.LeakTraces);
    }

    [Fact]
    public void DefaultOffMode_ShouldNotCaptureStackTrace()
    {
        // PR-D L1 行为变更：默认取证模式 Off——借出热路径不抓取调用栈（2.0 及之前
        // 每次借出抓全栈，37.5μs / 28.7KB 量级）。经分片登记表反射核验包装对象无栈。
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithEnableLeakDetection(true)
            .WithLeakDetectionThreshold(TimeSpan.FromMinutes(30))
            .Build();

        var a = pool.Acquire();
        var b = pool.Acquire();

        Assert.Null(GetWrapped(pool, a).AcquireTrace);
        Assert.Null(GetWrapped(pool, b).AcquireTrace);

        pool.Release(a);
        pool.Release(b);
    }

    [Fact]
    public void EveryAcquireMode_ShouldCaptureStackTrace()
    {
        // 显式 opt-in 旧行为（2.0 语义）：每次借出均抓取调用栈。
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithEnableLeakDetection(true)
            .WithLeakDetectionThreshold(TimeSpan.FromMinutes(30))
            .WithLeakTraceCapture(HayateLeakTraceCaptureMode.EveryAcquire)
            .Build();

        var a = pool.Acquire();
        var b = pool.Acquire();

        var traceA = GetWrapped(pool, a).AcquireTrace;
        var traceB = GetWrapped(pool, b).AcquireTrace;
        Assert.False(string.IsNullOrEmpty(traceA), "EveryAcquire 模式下首个借出应抓取调用栈");
        Assert.False(string.IsNullOrEmpty(traceB), "EveryAcquire 模式下每次借出都应抓取调用栈");

        pool.Release(a);
        pool.Release(b);
    }

    [Fact]
    public void SampledMode_ShouldCaptureEveryNthAcquire()
    {
        // 采样模式（1/N）：第 1 次必抓，之后每 N 次抓 1 次。N=2 借出 4 个 → 恰好 2 次抓栈（第 1、3 个）。
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithEnableLeakDetection(true)
            .WithLeakDetectionThreshold(TimeSpan.FromMinutes(30))
            .WithLeakTraceCapture(HayateLeakTraceCaptureMode.Sampled, 2)
            .Build();

        var items = new[] { pool.Acquire(), pool.Acquire(), pool.Acquire(), pool.Acquire() };

        var captured = items.Count(i => !string.IsNullOrEmpty(GetWrapped(pool, i).AcquireTrace));
        Assert.Equal(2, captured);

        foreach (var i in items) pool.Release(i);
    }

    private static HayateObject<TestObject> GetWrapped(IHayateObjectPool<TestObject> pool, TestObject item)
    {
        // T09：登记表已按分片拆分（池级 _objectMap → 各 Shard 私有字段 _objects），
        // 此处经 _shards 逐分片探测登记表取包装对象（与 ShardAtomicRemovalTests 同款反射助手）。
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
