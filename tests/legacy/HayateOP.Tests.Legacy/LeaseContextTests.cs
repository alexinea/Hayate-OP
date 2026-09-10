using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DotNetCore.HayateOP.Policies;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// AsyncLocal lease context -- HayateLeaseContext.
/// Core semantics: concurrent borrow/return each hold an independent lease context (AsyncLocal flow isolation), and no longer overwrite each other;
/// Release ends the lease (DetachFromFlow) and Destroy clears the wrapper-side reference.
/// </summary>
public class LeaseContextTests
{
    private sealed class TestObject { }

    /// <summary>Policy that rejects on release: forces Release down the Destroy path (verifies Destroy clears the lease reference).</summary>
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

        // Concurrent borrows: each task obtains its own object and its own lease context.
        // Verification points: (1) lease IDs are globally unique; (2) each context's frames contain the marker method name of its own task (flow isolation);
        // (3) the wrapper-side and flow-side references agree; (4) after await (ExecutionContext flows), Current still travels with the flow.
        var done = await Task.WhenAll(Enumerable.Range(0, 8).Select(async id =>
        {
            var obj = BorrowMarked(pool, id);
            var ctx = HayateLeaseContext.Current;
            Assert.NotNull(ctx);
            Assert.Same(ctx, GetWrapped(pool, obj).LeaseContext);

            // Simulate in-lease work (including await; after ExecutionContext flows, Current still travels with the flow)
            await Task.Yield();
            Assert.Same(ctx, HayateLeaseContext.Current);

            pool.Release(obj);
            return (ctx, frames: ctx.Frames);
        }));

        var leaseIds = done.Select(d => d.ctx.LeaseId).ToHashSet();
        Assert.Equal(8, leaseIds.Count); // (1) lease IDs are unique
        Assert.All(done, d => Assert.Contains("BorrowMarked", d.frames.Select(f => f.GetMethod()?.Name).ToList())); // (2) flow isolation
        Assert.All(done, _ => Assert.Null(HayateLeaseContext.Current)); // (4) flow context cleared after Release
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

        // The same wrapped object is re-borrowed: each borrow produces a new lease context (ID increments),
        // avoiding the pre-2.4 problem where string collection was overwritten on re-borrow
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
        // Reject on release -> Release goes to Destroy(w) -> wrapper-side LeaseContext reference is cleared
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

        pool.Release(obj); // policy rejects -> Destroy

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

    /// <summary>Borrow tagged with an independent call frame (the frame array should contain this method's name, proving flow isolation).</summary>
    /// <remarks>
    /// Must be <see cref="MethodImplOptions.NoInlining"/>: this method is only a single forwarding call,
    /// net48's JIT would inline it into the caller (the async state machine <c>MoveNext</c>), causing
    /// the method name to disappear from the stack frame, making the "frame contains the marker method" assertion drift with JIT behavior and report false negatives.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static TestObject BorrowMarked(IHayateObjectPool<TestObject> pool, int marker)
        => pool.Acquire();

    /// <summary>Same reflection helper as in LeaseDetectionTests: probes the registry table shard by shard via _shards to fetch the wrapped object.</summary>
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
