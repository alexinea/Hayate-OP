using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// Scoped borrows and the one-call factory.
/// Acceptance: a scope returns its object exactly once — automatically at the end of a <c>using</c> block
/// and on the exception paths, and never twice however many times the lease is disposed; a failed borrow
/// produces no lease and therefore nothing to return; the asynchronous overload behaves the same; and
/// <c>HayatePool.Simple</c> produces an ordinary pool bounded by the requested size whose objects come from
/// the supplied factory, with the optional per-borrow callback invoked on every acquire.
/// </summary>
public class ScopedBorrowTests
{
    private class TestObject { }

    private static HayatePoolBuilder<TestObject> BaseBuilder() => new HayatePoolBuilder<TestObject>()
        .WithPoolName("s2-pool")
        .WithMinSize(1)
        .WithMaxSize(2)
        .WithEnableMetrics(true);

    /// <summary>Borrows through a scope, disposes it twice, and hands back the borrowed reference.</summary>
    private static TestObject BorrowAndDoubleDispose(IHayateObjectPool<TestObject> pool)
    {
        using (var scope = pool.AcquireScoped())
        {
            var item = scope.Value;
            scope.Dispose();
            scope.Dispose();
            return item;
        }
    }

    [Fact(Timeout = 30_000)]
    public void AcquireScoped_ShouldReturnTheObject_WhenTheScopeEnds()
    {
        using var pool = BaseBuilder().Build();
        Assert.Equal(1, pool.GetStats().PooledCount);

        TestObject borrowed;
        using (var scope = pool.AcquireScoped())
        {
            borrowed = scope.Value;
            Assert.NotNull(borrowed);
            Assert.True(scope.IsActive);
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
    public void AcquireScoped_ShouldReturnTheObject_WhenTheScopeBodyThrows()
    {
        using var pool = BaseBuilder().Build();

        // A named local keeps the lambda an Action: a body that only throws would also convert to
        // Func&lt;Task&gt;, which xUnit rejects as an un-awaited async assertion.
        void BorrowAndFail()
        {
            using var scope = pool.AcquireScoped();
            Assert.Equal(1, pool.TakeSnapshot().BorrowedCount);
            throw new InvalidOperationException("scope body failed");
        }

        Assert.Throws<InvalidOperationException>(BorrowAndFail);

        Assert.Equal(1, pool.GetStats().PooledCount);
        Assert.Equal(0, pool.TakeSnapshot().BorrowedCount);
    }

    [Fact(Timeout = 30_000)]
    public void Dispose_ShouldReturnTheObjectExactlyOnce()
    {
        // CreateOnDemand keeps every borrow immediate, so the assertions depend only on return semantics
        // and not on how long a wait takes.
        using var pool = BaseBuilder()
            .WithMinSize(0)
            .WithMaxSize(8)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand)
            .Build();

        // Returned twice on purpose: a duplicated return would put the same object in the idle list twice.
        BorrowAndDoubleDispose(pool);

        using (var afterFirst = pool.AcquireScoped())
        using (var afterSecond = pool.AcquireScoped())
        {
            Assert.NotSame(afterFirst.Value, afterSecond.Value);
        }

        // Both leases ended, so exactly the two objects are idle again — no duplicated entry survived.
        Assert.Equal(2, pool.TakeSnapshot().PooledCount);
        Assert.Equal(0, pool.TakeSnapshot().BorrowedCount);
    }

    [Fact(Timeout = 30_000)]
    public void Value_ShouldThrow_AfterTheScopeWasDisposed()
    {
        using var pool = BaseBuilder().Build();

        var scope = pool.AcquireScoped();
        var item = scope.Value;
        scope.Dispose();

        Assert.False(scope.IsActive);
        Assert.Throws<ObjectDisposedException>(() => scope.Value);

        // The object itself is back in the pool and still usable.
        Assert.Same(item, pool.Acquire());
    }

    [Fact(Timeout = 30_000)]
    public void AcquireScoped_WithTimeout_ShouldThrowWithoutBorrowing()
    {
        using var pool = BaseBuilder().WithMinSize(1).WithMaxSize(1).Build();

        using (var held = pool.AcquireScoped())
        {
            Assert.Throws<TimeoutException>(() => pool.AcquireScoped(TimeSpan.FromMilliseconds(50)));

            // Nothing was borrowed by the failed attempt.
            Assert.Equal(1, pool.TakeSnapshot().BorrowedCount);
            Assert.Equal(0, pool.GetStats().PooledCount);
        }

        Assert.Equal(1, pool.GetStats().PooledCount);
    }

    [Fact(Timeout = 30_000)]
    public async Task AcquireScopeAsync_ShouldReturnTheObject_WhenTheScopeEnds()
    {
        using var pool = BaseBuilder().Build();

        TestObject borrowed;
        using (var scope = await pool.AcquireScopeAsync())
        {
            borrowed = scope.Value;
            Assert.True(scope.IsActive);
            Assert.Equal(1, pool.TakeSnapshot().BorrowedCount);
        }

        Assert.Equal(1, pool.GetStats().PooledCount);
        Assert.Equal(0, pool.TakeSnapshot().BorrowedCount);
        Assert.NotNull(borrowed);
    }

    [Fact(Timeout = 30_000)]
    public async Task AcquireScopeAsync_WithTimeout_ShouldThrowWithoutBorrowing()
    {
        using var pool = BaseBuilder().WithMinSize(1).WithMaxSize(1).Build();

        using (var held = pool.AcquireScoped())
        {
            await Assert.ThrowsAsync<TimeoutException>(
                () => pool.AcquireScopeAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None));

            Assert.Equal(1, pool.TakeSnapshot().BorrowedCount);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task AcquireScopeAsync_ShouldThrowNothingWhenAlreadyCancelled()
    {
        using var pool = BaseBuilder().WithMinSize(1).WithMaxSize(1).Build();

        using (var held = pool.AcquireScoped())
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pool.AcquireScopeAsync(cts.Token));

            // A cancelled wait borrowed nothing, so the single object is still with its only borrower.
            Assert.Equal(1, pool.TakeSnapshot().BorrowedCount);
            Assert.Equal(0, pool.GetStats().PooledCount);
        }
    }

    [Fact(Timeout = 30_000)]
    public void Simple_ShouldBuildABoundedPool_ThatCreatesLazily()
    {
        var created = 0;
        using var pool = HayatePool.Simple<TestObject>(4, () =>
        {
            Interlocked.Increment(ref created);
            return new TestObject();
        });

        var options = pool.GetOptions();
        Assert.Equal(4, options.MaxPoolSize);
        Assert.Equal(0, options.MinPoolSize);
        Assert.Equal(HayatePoolRejectPolicy.CreateOnDemand, options.RejectPolicy);

        // Nothing exists until the first borrow.
        Assert.Equal(0, created);
        Assert.Equal(0, pool.GetStats().PooledCount);

        var item = pool.Acquire();
        Assert.NotNull(item);
        Assert.Equal(1, created);

        pool.Release(item);
        Assert.Equal(1, pool.GetStats().PooledCount);
    }

    // ── B6-E3 (3.0): the one-call factory grows on a miss ─────────────────────────────────────────────
    //
    // `Simple` used to leave the reject policy at the library default (BlockTimeout), which only shortcuts
    // creation while the pool tracks nothing at all. Measured before the change (Debug/net10.0,
    // Simple<TestObject>(4)): the first borrow cold-boots and the second waits out the acquire timeout and
    // throws, even though MaxPoolSize still leaves three places - a pool built as "4 objects" served
    // exactly one borrower. With the policy the other "N objects" entry points already set, the same four
    // borrows complete immediately instead.
    //
    // The ceiling changes too, which the second half pins: at the size the request no longer throws.
    // CreateOnDemand waits the timeout out and then creates - measured as a fifth borrow on
    // Simple<TestObject>(4) served after its full timeout, leaving the pool at five objects. That is the
    // pre-existing wait-then-create of the create policies, shared with the presets: the requested size is
    // where the wait starts, not where the borrow fails.
    [Fact(Timeout = 60_000)]
    public void Simple_ShouldServeUpToTheRequestedSize_InsteadOfTimingOutOnAMiss()
    {
        var created = 0;
        using var pool = HayatePool.Simple<TestObject>(4, () =>
        {
            Interlocked.Increment(ref created);
            return new TestObject();
        });

        // Four borrowers with nothing returned in between: a miss inside `poolSize` creates instead of
        // waiting for a return.
        var held = new TestObject[4];
        for (var i = 0; i < held.Length; i++)
        {
            held[i] = pool.Acquire(TimeSpan.FromMilliseconds(500));
            Assert.NotNull(held[i]);
        }

        Assert.Equal(4, created);
        Assert.Equal(4, pool.GetStats().CurrentSize);

        // At the size the next borrow waits for a return, and is then served by a fresh object rather than
        // by a TimeoutException. The short timeout keeps the test quick; the pool grows past the size.
        var extra = pool.Acquire(TimeSpan.FromMilliseconds(300));
        Assert.NotNull(extra);
        Assert.Equal(5, created);
        pool.Release(extra);

        foreach (var item in held) pool.Release(item);
    }

    [Fact(Timeout = 30_000)]
    public void Simple_ShouldUseTheFactory_AndRunTheAcquireCallback()
    {
        var acquired = new List<TestObject>();

        using var pool = HayatePool.Simple<TestObject>(2, () => new TestObject(), acquired.Add);

        var first = pool.Acquire();
        pool.Release(first);

        using (var second = pool.AcquireScoped())
        {
            // Reused objects run the callback again — the hook fires per borrow, not per creation.
            Assert.Equal(2, acquired.Count);
            Assert.Same(first, second.Value);
        }

        Assert.All(acquired, item => Assert.Same(first, item));
    }

    [Fact(Timeout = 30_000)]
    public void Simple_ShouldHonourThePoolName()
    {
        using var pool = HayatePool.Simple<TestObject>(2, () => new TestObject(), null, "s2-simple");

        using (var scope = pool.AcquireScoped())
        {
            var detail = Assert.Single(pool.TakeSnapshot().ObjectDetails);
            Assert.Equal("s2-simple", detail.OwnerPoolName);
        }
    }

    [Fact(Timeout = 30_000)]
    public void Simple_ShouldRejectAnInvalidSizeOrFactory()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HayatePool.Simple<TestObject>(0, () => new TestObject()));
        Assert.Throws<ArgumentOutOfRangeException>(() => HayatePool.Simple<TestObject>(-1, () => new TestObject()));
        Assert.Throws<ArgumentNullException>(() => HayatePool.Simple<TestObject>(2, null!));
    }
}
