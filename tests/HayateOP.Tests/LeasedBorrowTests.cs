using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// The allocation-free lease (G-6).
/// <para>
/// Acceptance: a lease returns its object exactly once — automatically at the end of a <c>using</c> block
/// and on the exception paths, and never twice however many times the lease is disposed; a failed borrow
/// produces no lease and therefore nothing to return; the asynchronous overloads behave the same; the value
/// accessors match the existing class scope (<see cref="HayatePoolScope{T}"/>) exactly, so the two forms are
/// interchangeable; and a lease survives an <c>await</c> boundary, which is what forced the type to be a
/// mutable struct rather than a <c>ref struct</c>.
/// </para>
/// <para>
/// V7 (nested / reentrant borrowing) is verified here too: several leases live on the same thread at the same
/// time and may be released in any order.
/// </para>
/// </summary>
public class LeasedBorrowTests
{
    private class TestObject { }

    private static HayatePoolBuilder<TestObject> BaseBuilder() => new HayatePoolBuilder<TestObject>()
        .WithPoolName("g6-pool")
        .WithMinSize(1)
        .WithMaxSize(2)
        .WithEnableMetrics(true);

    /// <summary>
    /// A builder whose every borrow is served immediately. Needed wherever a case holds two objects at once:
    /// with the default wait-and-block policy, the second borrow against a pool holding one object waits out
    /// the acquire timeout instead of creating, so the case would measure the timeout rather than the lease.
    /// </summary>
    private static HayatePoolBuilder<TestObject> CreateOnDemandBuilder(string poolName) =>
        new HayatePoolBuilder<TestObject>()
            .WithPoolName(poolName)
            .WithMinSize(0)
            .WithMaxSize(8)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand)
            .WithEnableMetrics(true);

    /// <summary>Borrows through a lease, disposes it twice, and hands back the borrowed reference.</summary>
    private static TestObject BorrowAndDoubleDispose(IHayateObjectPool<TestObject> pool)
    {
        using (var lease = pool.AcquireLeased())
        {
            var item = lease.Value;
            lease.Dispose();
            lease.Dispose();
            return item;
        }
    }

    [Fact(Timeout = 30_000)]
    public void AcquireLeased_ShouldReturnTheObject_WhenTheLeaseEnds()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var pool = BaseBuilder().Build();
        Assert.Equal(1, pool.GetStats().PooledCount);

        TestObject borrowed;
        using (var lease = pool.AcquireLeased())
        {
            borrowed = lease.Value;
            Assert.NotNull(borrowed);
            Assert.True(lease.IsActive);
            Assert.Equal(0, pool.GetStats().PooledCount);
            Assert.Equal(1, pool.TakeSnapshot().BorrowedCount);
        }

        Assert.Equal(1, pool.GetStats().PooledCount);
        Assert.Equal(0, pool.TakeSnapshot().BorrowedCount);

        // The returned object is reusable, not destroyed and recreated.
        Assert.Same(borrowed, pool.Acquire());
        Assert.Equal(1, pool.TakeSnapshot().BorrowedCount);
    }

    [Fact(Timeout = 30_000)]
    public void AcquireLeased_ShouldReturnTheObject_WhenTheLeaseBodyThrows()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var pool = BaseBuilder().Build();

        // A named local keeps the lambda an Action: a body that only throws would also convert to
        // Func&lt;Task&gt;, which xUnit rejects as an un-awaited async assertion.
        void BorrowAndFail()
        {
            using var lease = pool.AcquireLeased();
            Assert.Equal(1, pool.TakeSnapshot().BorrowedCount);
            throw new InvalidOperationException("lease body failed");
        }

        Assert.Throws<InvalidOperationException>(BorrowAndFail);

        Assert.Equal(1, pool.GetStats().PooledCount);
        Assert.Equal(0, pool.TakeSnapshot().BorrowedCount);
    }

    [Fact(Timeout = 30_000)]
    public void Dispose_ShouldReturnTheObjectExactlyOnce()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        // CreateOnDemand keeps every borrow immediate, so the assertions depend only on return semantics
        // and not on how long a wait takes.
        using var pool = CreateOnDemandBuilder("g6-once").Build();

        // Returned twice on purpose: a duplicated return would put the same object in the idle list twice.
        BorrowAndDoubleDispose(pool);

        using (var afterFirst = pool.AcquireLeased())
        using (var afterSecond = pool.AcquireLeased())
        {
            Assert.NotSame(afterFirst.Value, afterSecond.Value);
        }

        // Both leases ended, so exactly the two objects are idle again — no duplicated entry survived.
        Assert.Equal(2, pool.TakeSnapshot().PooledCount);
        Assert.Equal(0, pool.TakeSnapshot().BorrowedCount);
    }

    [Fact(Timeout = 30_000)]
    public void Value_ShouldThrow_AfterTheLeaseWasDisposed()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var pool = BaseBuilder().Build();

        var lease = pool.AcquireLeased();
        var item = lease.Value;
        lease.Dispose();

        Assert.False(lease.IsActive);
        Assert.Throws<ObjectDisposedException>(() => lease.Value);

        // The object itself is back in the pool and still usable.
        Assert.Same(item, pool.Acquire());
    }

    [Fact(Timeout = 30_000)]
    public void AcquireLeased_WithTimeout_ShouldThrowWithoutBorrowing()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var pool = BaseBuilder().WithMinSize(1).WithMaxSize(1).Build();

        using (var held = pool.AcquireLeased())
        {
            Assert.Throws<TimeoutException>(() => pool.AcquireLeased(TimeSpan.FromMilliseconds(50)));

            // Nothing was borrowed by the failed attempt.
            Assert.Equal(1, pool.TakeSnapshot().BorrowedCount);
            Assert.Equal(0, pool.GetStats().PooledCount);
        }

        Assert.Equal(1, pool.GetStats().PooledCount);
    }

    [Fact(Timeout = 30_000)]
    public void AcquireLeased_ShouldRejectANullPool()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        IHayateObjectPool<TestObject>? nullPool = null;

        Assert.Throws<ArgumentNullException>(() => nullPool!.AcquireLeased());
        Assert.Throws<ArgumentNullException>(() => nullPool!.AcquireLeased(TimeSpan.FromSeconds(1)));
    }

    // ── G-6: the shape constraints that decided the type ────────────────────────────────────────────────
    //
    // A `ref struct` lease was the original intent, and the probe (`_g6_probe/`, 2026-10-08) showed it
    // cannot work: a ref struct local may not live across an await boundary (CS4007), so its asynchronous
    // disposal would be unreachable and the new lease would be weaker than the class scope it was meant to
    // complement. The type is therefore a mutable struct. These two tests pin the capabilities that choice
    // bought, so a future change back to `ref struct` fails here rather than silently dropping async release.
    [Fact(Timeout = 30_000)]
    public async Task TheLease_ShouldSurviveAnAwaitBoundary()
    {
        using var pool = BaseBuilder().Build();

        using (var lease = pool.AcquireLeased())
        {
            var before = lease.Value;

            // The lease is live across a real suspension point: only a non-ref struct gets this far.
            await Task.Yield();
            await Task.Delay(10, TestContext.Current.CancellationToken);

            Assert.True(lease.IsActive);
            Assert.Same(before, lease.Value);
            Assert.Equal(1, pool.TakeSnapshot().BorrowedCount);
        }

        Assert.Equal(1, pool.GetStats().PooledCount);
        Assert.Equal(0, pool.TakeSnapshot().BorrowedCount);
    }

    [Fact(Timeout = 30_000)]
    public void NestedLeases_ShouldReturnIndependently_InAnyOrder()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        // V7: three leases live at once on the same thread. Released out of acquisition order on purpose —
        // ownership is per lease, not per pool.
        using var pool = CreateOnDemandBuilder("g6-v7").Build();

        var first = pool.AcquireLeased();
        var second = pool.AcquireLeased();
        var third = pool.AcquireLeased();

        var firstItem = first.Value;
        var secondItem = second.Value;
        var thirdItem = third.Value;

        Assert.Equal(3, pool.TakeSnapshot().BorrowedCount);

        // Interleaved release: middle, then last, then first.
        second.Dispose();
        third.Dispose();
        first.Dispose();

        Assert.Equal(0, pool.TakeSnapshot().BorrowedCount);
        Assert.Equal(3, pool.TakeSnapshot().PooledCount);

        // Each lease returned its own object: a crossed return would show up as a duplicate idle entry.
        Assert.Equal(3, new HashSet<TestObject> { firstItem, secondItem, thirdItem }.Count);
    }

    [Fact(Timeout = 30_000)]
    public void NestedLeases_ShouldStillSeeTheirOwnObject_AfterAnInnerLeaseEnds()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var pool = CreateOnDemandBuilder("g6-nested").Build();

        using var outer = pool.AcquireLeased();
        var outerItem = outer.Value;

        using (var inner = pool.AcquireLeased())
        {
            Assert.NotSame(outerItem, inner.Value);
        }

        // Ending the inner lease must not disturb the outer one.
        Assert.True(outer.IsActive);
        Assert.Same(outerItem, outer.Value);
    }

    [Fact(Timeout = 30_000)]
    public void TheLease_ShouldMatchTheClassScope_OnTheSamePool()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        // The two forms must be interchangeable, so the same script run through each leaves the same
        // observable state.
        //
        // CreateOnDemand is required, not incidental: the script holds two objects at once, and with the
        // default BlockTimeout policy a second borrow against MinSize=1 would wait out the acquire timeout
        // for a return that is not coming — the pool would time out rather than create. Both pools get it,
        // so the comparison stays like-for-like.
        using var leasedPool = CreateOnDemandBuilder("g6-equiv-leased").Build();
        using var scopedPool = CreateOnDemandBuilder("g6-equiv-scoped").Build();

        RunScopeScript(leasedPool, useLease: true);
        RunScopeScript(scopedPool, useLease: false);

        Assert.Equal(scopedPool.GetStats().PooledCount, leasedPool.GetStats().PooledCount);
        Assert.Equal(scopedPool.TakeSnapshot().BorrowedCount, leasedPool.TakeSnapshot().BorrowedCount);
        Assert.Equal(scopedPool.GetStats().TotalCreated, leasedPool.GetStats().TotalCreated);
        Assert.Equal(scopedPool.GetStats().TotalReleased, leasedPool.GetStats().TotalReleased);
    }

    private static void RunScopeScript(IHayateObjectPool<TestObject> pool, bool useLease)
    {
        if (useLease)
        {
            using (var a = pool.AcquireLeased())
            using (var b = pool.AcquireLeased())
            {
                Assert.NotSame(a.Value, b.Value);
            }

            var lease = pool.AcquireLeased();
            var item = lease.Value;
            lease.Dispose();
            lease.Dispose();
            Assert.Throws<ObjectDisposedException>(() => lease.Value);

            pool.Release(item);
        }
        else
        {
            using (var a = pool.AcquireScoped())
            using (var b = pool.AcquireScoped())
            {
                Assert.NotSame(a.Value, b.Value);
            }

            var scope = pool.AcquireScoped();
            var item = scope.Value;
            scope.Dispose();
            scope.Dispose();
            Assert.Throws<ObjectDisposedException>(() => scope.Value);

            pool.Release(item);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task AcquireLeaseAsync_ShouldReturnTheObject_WhenTheLeaseEnds()
    {
        using var pool = BaseBuilder().Build();

        TestObject borrowed;
        using (var lease = await pool.AcquireLeaseAsync(TestContext.Current.CancellationToken))
        {
            borrowed = lease.Value;
            Assert.True(lease.IsActive);
            Assert.Equal(1, pool.TakeSnapshot().BorrowedCount);
        }

        Assert.Equal(1, pool.GetStats().PooledCount);
        Assert.Equal(0, pool.TakeSnapshot().BorrowedCount);
        Assert.NotNull(borrowed);
    }

    [Fact(Timeout = 30_000)]
    public async Task AcquireLeaseAsync_WithTimeout_ShouldThrowWithoutBorrowing()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var pool = BaseBuilder().WithMinSize(1).WithMaxSize(1).Build();

        using (var held = pool.AcquireLeased())
        {
            await Assert.ThrowsAsync<TimeoutException>(
                () => pool.AcquireLeaseAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None));

            Assert.Equal(1, pool.TakeSnapshot().BorrowedCount);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task AcquireLeaseAsync_ShouldThrowNothingWhenAlreadyCancelled()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var pool = BaseBuilder().WithMinSize(1).WithMaxSize(1).Build();

        using (var held = pool.AcquireLeased())
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pool.AcquireLeaseAsync(cts.Token));

            // A cancelled wait borrowed nothing, so the single object is still with its only borrower.
            Assert.Equal(1, pool.TakeSnapshot().BorrowedCount);
            Assert.Equal(0, pool.GetStats().PooledCount);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task AcquireLeaseAsync_ShouldRejectANullPool()
    {
        IHayateObjectPool<TestObject>? nullPool = null;

        await Assert.ThrowsAsync<ArgumentNullException>(() => nullPool!.AcquireLeaseAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(() => nullPool!.AcquireLeaseAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
    }

#if NET6_0_OR_GREATER
    [Fact(Timeout = 30_000)]
    public async Task DisposeAsync_ShouldReturnTheObjectExactlyOnce()
    {
        using var pool = CreateOnDemandBuilder("g6-dispose-async").Build();

        var lease = await pool.AcquireLeaseAsync(TestContext.Current.CancellationToken);
        var item = lease.Value;

        await lease.DisposeAsync();
        await lease.DisposeAsync();

        Assert.False(lease.IsActive);
        Assert.Throws<ObjectDisposedException>(() => lease.Value);
        Assert.Equal(1, pool.TakeSnapshot().PooledCount);

        // Same identity as the synchronous path: exactly one return happened.
        Assert.Same(item, pool.Acquire());
    }

    [Fact(Timeout = 30_000)]
    public async Task AwaitUsing_ShouldReturnTheObject_WhenTheBlockEnds()
    {
        using var pool = BaseBuilder().Build();

        TestObject borrowed;
        await using (var lease = await pool.AcquireLeaseAsync(TestContext.Current.CancellationToken))
        {
            borrowed = lease.Value;
            await Task.Yield();
            Assert.Equal(1, pool.TakeSnapshot().BorrowedCount);
        }

        Assert.Equal(1, pool.GetStats().PooledCount);
        Assert.Equal(0, pool.TakeSnapshot().BorrowedCount);
        Assert.NotNull(borrowed);
    }

    [Fact(Timeout = 30_000)]
    public async Task AwaitUsing_ShouldReturnTheObject_WhenTheBlockThrows()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var pool = BaseBuilder().Build();

        async Task BorrowAndFail()
        {
            await using var lease = await pool.AcquireLeaseAsync();
            Assert.Equal(1, pool.TakeSnapshot().BorrowedCount);
            throw new InvalidOperationException("await using body failed");
        }

        await Assert.ThrowsAsync<InvalidOperationException>(BorrowAndFail);

        Assert.Equal(1, pool.GetStats().PooledCount);
        Assert.Equal(0, pool.TakeSnapshot().BorrowedCount);
    }
#endif

    // ── The point of the type: acquiring through it must not allocate ──────────────────────────────────
    //
    // The class scope costs one allocation per borrow (the deliberate trade for never leaking a borrow).
    // This is the case that trade was buying, so it is measured rather than asserted by inspection.
    //
    // The measurement is *relative*, and that matters. The pool's own acquire/return path allocates a little
    // on every borrow (per-borrow bookkeeping — metrics are enabled on these pools), so an absolute
    // "zero allocations" assertion would be asserting a property of the acquire path rather than of the lease,
    // and would fail for reasons that have nothing to do with G-6. The baseline below measures that acquire
    // path with a plain Acquire/Release pair; the claim under test is the *difference*:
    //
    //   leased <= manual   the lease adds no allocation of its own
    //   scoped >  manual   the scope adds one object per borrow (the trade being made here)
    //
    // All three run against equivalent pools with the same options, so the comparison is like-for-like.
    [Fact(Timeout = 120_000)]
    public void AcquireLeased_ShouldNotAllocate_UnlikeTheClassScope()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var manualPool = BaseBuilder().WithPoolName("g6-alloc-manual").WithMinSize(1).WithMaxSize(1).Build();
        using var leasedPool = BaseBuilder().WithPoolName("g6-alloc-leased").WithMinSize(1).WithMaxSize(1).Build();
        using var scopedPool = BaseBuilder().WithPoolName("g6-alloc-scoped").WithMinSize(1).WithMaxSize(1).Build();

        // Warm all three paths so one-time costs (statics, lazy init, first-touch) are not measured.
        for (var i = 0; i < 100; i++)
        {
            manualPool.Release(manualPool.Acquire());
            using (leasedPool.AcquireLeased()) { }
            using (scopedPool.AcquireScoped()) { }
        }

        const int Iterations = 2_000;

        var manualBytes = Measure(() =>
        {
            var item = manualPool.Acquire();
            manualPool.Release(item);
        }, Iterations);

        var leasedBytes = Measure(() =>
        {
            using var lease = leasedPool.AcquireLeased();
            GC.KeepAlive(lease.Value);
        }, Iterations);

        var scopedBytes = Measure(() =>
        {
            using var scope = scopedPool.AcquireScoped();
            GC.KeepAlive(scope.Value);
        }, Iterations);

        // The lease carries the value in the frame, so it adds nothing on top of the acquire path.
        Assert.True(leasedBytes <= manualBytes,
            $"lease must not exceed the manual baseline: leased={leasedBytes} manual={manualBytes}");

        // The scope still allocates one object per borrow — the trade being made here. This also guards the
        // comparison from becoming void: if the scope ever measured zero, the assertion above would pass
        // trivially.
        Assert.True(scopedBytes > manualBytes,
            $"expected the class scope to allocate more per borrow than a manual pair: scoped={scopedBytes} manual={manualBytes}");
    }

    private static long Measure(Action body, int iterations)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++) body();
        var after = GC.GetAllocatedBytesForCurrentThread();

        return after - before;
    }
}
