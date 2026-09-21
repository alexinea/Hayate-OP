using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// Structured observability fields.
/// Behavior change: borrow evidence changes from a full string to a StackFrame[] (low-overhead capture, formatted on demand in the snapshot).
/// Behavior change: HayateObject gains CreatedAtTick / LeaseCount / OwnerPoolName, surfaced through the snapshot's ObjectDetails.
/// </summary>
public class ObservabilityFieldsTests
{
    private sealed class TestObject { }

    // ---------- lease-context capture ----------

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

        // Structured frames: contain the method frames on the borrow call chain (Acquire or a test helper method)
        var frames = ctx.Frames;
        Assert.NotEmpty(frames);
        var methodNames = frames.Select(f => f.GetMethod()?.Name).ToList();
        // The borrow call chain must leave a method frame. After this change, Acquire()/Acquire(TimeSpan) are extremely thin forwarders
        // (`return AcquireCore(...)`), and higher-version JIT tiered compilation / PGO may inline them,
        // leaving only AcquireCore in the stack frame -- the assertion's intent is "contains a borrow-chain method frame", and both satisfy it.
        Assert.Contains(methodNames, n => n == "Acquire" || n == "AcquireCore");
        Assert.Contains("EveryAcquireMode_ShouldCaptureLeaseContext", methodNames);
        // Low-overhead approach: source file line numbers are not resolved
        Assert.All(frames, f => Assert.Null(f.GetFileName()));

        // Lease IDs increase monotonically; the AsyncLocal flow context agrees with the wrapper side for the lease duration
        Assert.True(ctx.LeaseId > 0);
        Assert.Same(ctx, HayateLeaseContext.Current);
        Assert.True(ctx.BorrowedAt > 0); // Borrow time (Stopwatch ticks)

        pool.Release(a);
        // Release ends the lease: the flow context is cleared
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
        // Format: a "[Lease {id}]" prefix + Type.Method+0x offset, frames joined by " <- " 
        Assert.StartsWith("[Lease ", trace);
        Assert.Contains("ObservabilityFieldsTests", trace);
        Assert.Contains("+0x", trace);
        Assert.Contains(" <- ", trace);

        pool.Release(obj);
    }

    // ---------- lease lifecycle fields ----------

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

        // The same wrapped object is borrowed again, so the count should accumulate to 2
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
        // The wall-clock creation time should fall within the last minute
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

        var borrowed = pool.Acquire(); // Min=2 warms up 2 objects, 1 is borrowed -> 1 borrowed + 1 idle
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

        // After release, the borrowed-state details reset to zero
        var after = pool.TakeSnapshot();
        Assert.All(after.ObjectDetails, d => Assert.False(d.IsBorrowed));
    }

    /// <summary>Same reflection helper as in LeaseDetectionTests: probes the registry table shard by shard via _shards to fetch the wrapped object.</summary>
    private static HayateObject<TestObject> GetWrapped(IHayateObjectPool<TestObject> pool, TestObject item)
    {
        var shardsField = pool.GetType().GetField("_shards", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Assert.NotNull(shardsField);
        var shards = (Array)shardsField.GetValue(pool)!;

        foreach (var shard in shards)
        {
            var objectsField = shard.GetType().GetField("_objects", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (objectsField == null) continue;

            var map = objectsField.GetValue(shard)!;
            var tryGet = map.GetType().GetMethod("TryGetValue")!;
            var args = new object?[] { item, null };
            tryGet.Invoke(map, args);
            if (args[1] != null) return (HayateObject<TestObject>)args[1]!;
        }

        return null!;
    }
}
