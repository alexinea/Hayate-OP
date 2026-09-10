using System.Reflection;

namespace DotNetCore.HayateOP.Tests;

public class LeakDetectionTests
{
    private class TestObject { }

    [Fact]
    public void EnableLeakDetection_ShouldAcquireAndReleaseNormally()
    {
        // After leak detection (threshold check + LeakCount) is decoupled from stack capture, enabling detection
        // no longer means capturing a stack on every borrow (default Off); this case only verifies that the borrow/return path works normally when detection is on.
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

        // Borrow an object and do not return it
        var obj = pool.Acquire();

        // Wait beyond the leak threshold
        Thread.Sleep(200);

        var snapshot = pool.TakeSnapshot();

        // The original assertion LeakCount >= 0 was always true (it masked the earlier leak-detection regression).
        // After the fix, TakeSnapshot walks the per-shard registry (including borrowed objects), so a borrowed object past its threshold with no return must be detected.
        // Default LeakTraceCaptureMode=Off does not capture a stack; LeakTraces entries are placeholder text
        // ("No stack trace available"), but the leak-finding and counting semantics are unchanged.
        Assert.True(snapshot.LeakCount >= 1,
            $"Borrowed object past threshold should be detected as a leak, actual LeakCount={snapshot.LeakCount}");
        Assert.NotEmpty(snapshot.LeakTraces);
    }

    [Fact]
    public void DefaultOffMode_ShouldNotCaptureStackTrace()
    {
        // Behavior change: the default capture mode is Off -- the borrow hot path does not capture a call stack (in 2.0 and earlier
        // every borrow captured the full stack, on the order of 37.5us / 28.7KB). Verified via reflection on the per-shard registry that the wrapped object has no stack.
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithEnableLeakDetection(true)
            .WithLeakDetectionThreshold(TimeSpan.FromMinutes(30))
            .Build();

        var a = pool.Acquire();
        var b = pool.Acquire();

        Assert.Null(GetWrapped(pool, a).LeaseContext);
        Assert.Null(GetWrapped(pool, b).LeaseContext);

        pool.Release(a);
        pool.Release(b);
    }

    [Fact]
    public void EveryAcquireMode_ShouldCaptureStackTrace()
    {
        // Explicitly opt in to the old behavior (2.0 semantics): capture a call stack on every borrow.
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithEnableLeakDetection(true)
            .WithLeakDetectionThreshold(TimeSpan.FromMinutes(30))
            .WithLeakTraceCapture(HayateLeakTraceCaptureMode.EveryAcquire)
            .Build();

        var a = pool.Acquire();
        var b = pool.Acquire();

        var ctxA = GetWrapped(pool, a).LeaseContext;
        var ctxB = GetWrapped(pool, b).LeaseContext;
        Assert.NotNull(ctxA);
        Assert.NotEmpty(ctxA.Frames);
        Assert.NotNull(ctxB);
        Assert.NotEmpty(ctxB.Frames);

        pool.Release(a);
        pool.Release(b);
    }

    [Fact]
    public void SampledMode_ShouldCaptureEveryNthAcquire()
    {
        // Sampled mode (1/N): the 1st is always captured, then 1 in every N. N=2 with 4 borrows -> exactly 2 captures (the 1st and 3rd).
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithEnableLeakDetection(true)
            .WithLeakDetectionThreshold(TimeSpan.FromMinutes(30))
            .WithLeakTraceCapture(HayateLeakTraceCaptureMode.Sampled, 2)
            .Build();

        var items = new[] { pool.Acquire(), pool.Acquire(), pool.Acquire(), pool.Acquire() };

        var captured = items.Count(i => GetWrapped(pool, i).LeaseContext is not null);
        Assert.Equal(2, captured);

        foreach (var i in items) pool.Release(i);
    }

    private static HayateObject<TestObject> GetWrapped(IHayateObjectPool<TestObject> pool, TestObject item)
    {
        // The registry is already split per shard (pool-level _objectMap -> each Shard's private field _objects),
        // here we probe the registry per shard via _shards to get the wrapped object (same reflection helper as ShardAtomicRemovalTests).
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
