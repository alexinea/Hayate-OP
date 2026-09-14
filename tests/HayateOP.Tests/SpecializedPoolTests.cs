using DotNetCore.HayateOP.Specialized;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// Specialized pools (O-B): MemoryStream / StringBuilder pooling as a ready-to-use extension package.
/// Acceptance: the default capacity tiers match the P89OP values (4KB/512KB bytes for streams, 4096/524288
/// characters for builders); a borrowed stream or builder comes back when disposed, so <c>using</c> replaces
/// the manual Release and the return cannot be forgotten — including when the body throws; returned
/// objects are reset (empty stream, cleared builder) and reused; a returned object that grew past the
/// pool's maximum capacity is destroyed instead of parked; disposal routes exactly one return, so a
/// double dispose never double-parks an object.
/// </summary>
public class SpecializedPoolTests
{
    private const int MinimumCapacity = MemoryStreamPool.DefaultMinimumMemoryStreamCapacity;
    private const int MaximumCapacity = MemoryStreamPool.DefaultMaximumMemoryStreamCapacity;

    [Fact(Timeout = 30_000)]
    public void DefaultCapacityTiers_ShouldMatchP89OpDefaults()
    {
        // P89OP: DefaultMinimumMemoryStreamCapacity = 4KB, DefaultMaximumMemoryStreamCapacity = 512KB.
        Assert.Equal(4 * 1024, MemoryStreamPool.DefaultMinimumMemoryStreamCapacity);
        Assert.Equal(512 * 1024, MemoryStreamPool.DefaultMaximumMemoryStreamCapacity);

        // P89OP: DefaultMinimumStringBuilderCapacity = 4096 chars, DefaultMaximumStringBuilderCapacity = 512KB chars.
        Assert.Equal(4 * 1024, StringBuilderPool.DefaultMinimumStringBuilderCapacity);
        Assert.Equal(512 * 1024, StringBuilderPool.DefaultMaximumStringBuilderCapacity);

        // P89OP: ObjectPool.DefaultPoolMaximumSize = 16, shared by both specialized pools.
        Assert.Equal(16, MemoryStreamPool.DefaultPoolMaximumSize);
        Assert.Equal(16, StringBuilderPool.DefaultPoolMaximumSize);
    }

    [Fact(Timeout = 30_000)]
    public void StreamPool_ShouldCreateStreamsAtMinimumCapacity()
    {
        using var pool = new MemoryStreamPool(4);

        using var stream = pool.GetObject();
        Assert.NotNull(stream);
        Assert.True(stream.Capacity >= MinimumCapacity,
            $"A fresh stream should reserve at least the minimum capacity ({stream.Capacity} < {MinimumCapacity}).");
    }

    [Fact(Timeout = 30_000)]
    public void StreamPool_UsingShouldReturnAndResetTheStream()
    {
        using var pool = new MemoryStreamPool(4);

        PooledMemoryStream borrowed;
        using (borrowed = pool.GetObject())
        {
            borrowed.Write(new byte[] { 1, 2, 3, 4, 5 }, 0, 5);
        }

        // Disposing the using lease returned the stream, and the reset on return emptied it.
        Assert.Equal(0, borrowed.Position);
        Assert.Equal(0, borrowed.Length);

        // The returned stream is reused for the next borrow.
        Assert.Same(borrowed, pool.GetObject());
    }

    [Fact(Timeout = 30_000)]
    public void StreamPool_DisposeShouldReturnExactlyOnce()
    {
        using var pool = new MemoryStreamPool(4);

        var stream = pool.GetObject();
        stream.Dispose();

        // A second dispose of the same borrow is a no-op: double-returning would park the stream twice
        // and hand the same stream to two borrowers later.
        stream.Dispose();

        var a = pool.GetObject();
        var b = pool.GetObject();
        Assert.NotSame(a, b);
    }

    [Fact(Timeout = 30_000)]
    public void StreamPool_ShouldReuseReturnedStream()
    {
        using var pool = new MemoryStreamPool(4);

        var first = pool.GetObject();
        first.Dispose();

        Assert.Same(first, pool.GetObject());
    }

    [Fact(Timeout = 30_000)]
    public void StreamPool_OversizedStreamShouldBeDestroyedOnReturn()
    {
        using var pool = new MemoryStreamPool(4);
        // A small window makes an oversized stream cheap to produce.
        pool.MinimumMemoryStreamCapacity = 64;
        pool.MaximumMemoryStreamCapacity = 128;

        var oversized = pool.GetObject();
        Assert.True(oversized.Capacity >= 64);
        oversized.Write(new byte[200], 0, 200);
        oversized.Dispose();

        // The oversized stream was destroyed on return, not parked; the next two borrows are served by
        // fresh streams, never by the oversized one.
        var a = pool.GetObject();
        var b = pool.GetObject();
        Assert.NotSame(oversized, a);
        Assert.NotSame(oversized, b);

        // Destroying closed the buffer, like any closed stream.
        Assert.Throws<ObjectDisposedException>(() => oversized.WriteByte(1));
    }

    [Fact(Timeout = 30_000)]
    public void StreamPool_TighteningMaximumShouldClearThePool()
    {
        using var pool = new MemoryStreamPool(4);

        var stream = pool.GetObject();
        stream.Dispose();

        // Lowering the maximum clears the pool (P89OP setter contract), destroying the parked stream.
        pool.MaximumMemoryStreamCapacity = MinimumCapacity;
        var next = pool.GetObject();

        Assert.NotSame(stream, next);
    }

    [Fact(Timeout = 30_000)]
    public void StreamPool_StandaloneStreamShouldDisposeLikeAMemoryStream()
    {
        // A stream created directly owns nobody: disposing it is a real disposal, not a routed return.
        var standalone = new PooledMemoryStream(16);
        standalone.Dispose();
        Assert.Throws<ObjectDisposedException>(() => standalone.WriteByte(1));
    }

    [Fact(Timeout = 30_000)]
    public void StreamPool_SharedInstanceShouldServeConcurrentBorrows()
    {
        // The shared singleton behaves like any pool: two concurrent borrows yield two distinct streams.
        var a = MemoryStreamPool.Instance.GetObject();
        var b = MemoryStreamPool.Instance.GetObject();

        Assert.NotSame(a, b);

        a.Dispose();
        b.Dispose();
    }

    [Fact(Timeout = 30_000)]
    public void StringBuilderPool_UsingShouldReturnAndClearTheBuilder()
    {
        using var pool = new StringBuilderPool(4);

        PooledStringBuilder borrowed;
        using (borrowed = pool.GetObject())
        {
            borrowed.StringBuilder.Append("hello");
            Assert.Equal("hello", borrowed.ToString());
        }

        // The clear happens on return, so the next borrower starts from an empty builder.
        Assert.Equal(0, borrowed.StringBuilder.Length);

        // The returned builder is reused for the next borrow.
        Assert.Same(borrowed, pool.GetObject());
    }

    [Fact(Timeout = 30_000)]
    public void StringBuilderPool_GetObjectWithStringShouldPrefill()
    {
        using var pool = new StringBuilderPool(4);

        using var sb = pool.GetObject("order: 42");
        Assert.Equal("order: 42", sb.ToString());
    }

    [Fact(Timeout = 30_000)]
    public void StringBuilderPool_GetObjectWithNullShouldGiveAnEmptyBuilder()
    {
        using var pool = new StringBuilderPool(4);

        using var sb = pool.GetObject(null);
        Assert.Equal(string.Empty, sb.ToString());
    }

    [Fact(Timeout = 30_000)]
    public void StringBuilderPool_DisposeShouldReturnExactlyOnce()
    {
        using var pool = new StringBuilderPool(4);

        var sb = pool.GetObject();
        sb.Dispose();

        // A second dispose of the same borrow is a no-op: double-returning would park the builder twice
        // and hand the same builder to two borrowers later.
        sb.Dispose();

        var a = pool.GetObject();
        var b = pool.GetObject();
        Assert.NotSame(a, b);
    }

    [Fact(Timeout = 30_000)]
    public void StringBuilderPool_ShouldReuseReturnedBuilder()
    {
        using var pool = new StringBuilderPool(4);

        var first = pool.GetObject();
        first.Dispose();

        Assert.Same(first, pool.GetObject());
    }

    [Fact(Timeout = 30_000)]
    public void StringBuilderPool_OversizedBuilderShouldBeDestroyedOnReturn()
    {
        using var pool = new StringBuilderPool(4);
        // A small window makes an oversized builder cheap to produce.
        pool.MinimumStringBuilderCapacity = 64;
        pool.MaximumStringBuilderCapacity = 128;

        var oversized = pool.GetObject();
        Assert.True(oversized.StringBuilder.Capacity >= 64);
        oversized.StringBuilder.Append(new string('x', 200));
        oversized.Dispose();

        // The oversized builder was destroyed on return, not parked; the next two borrows are served by
        // fresh builders, never by the oversized one.
        var a = pool.GetObject();
        var b = pool.GetObject();
        Assert.NotSame(oversized, a);
        Assert.NotSame(oversized, b);
    }

    [Fact(Timeout = 30_000)]
    public void StringBuilderPool_TighteningMaximumShouldClearThePool()
    {
        using var pool = new StringBuilderPool(4);

        var sb = pool.GetObject();
        sb.Dispose();

        // Lowering the maximum clears the pool (P89OP setter contract), destroying the parked builder.
        pool.MaximumStringBuilderCapacity = pool.MinimumStringBuilderCapacity;
        var next = pool.GetObject();

        Assert.NotSame(sb, next);
    }

    [Fact(Timeout = 30_000)]
    public void StringBuilderPool_StandaloneInstanceShouldNotRouteAReturn()
    {
        // An instance created directly owns nobody: disposing it does nothing.
        var standalone = new PooledStringBuilder(16);
        standalone.StringBuilder.Append("keep me");
        standalone.Dispose();
        Assert.Equal("keep me", standalone.ToString());
    }

    [Fact(Timeout = 30_000)]
    public void SharedStringBuilder_ShouldSerializeAccessAndClearOnRelease()
    {
        string content;
        using (var scope = SharedStringBuilder.Acquire())
        {
            scope.StringBuilder.Append("shared");
            content = scope.ToString();
        }

        Assert.Equal("shared", content);

        // The shared builder is cleared on release, so the next acquirer starts from an empty builder.
        using (var next = SharedStringBuilder.Acquire())
        {
            Assert.Equal(0, next.StringBuilder.Length);
        }
    }

    [Fact(Timeout = 30_000)]
    public void SharedStringBuilder_ScopeDisposeShouldBeOnceOnly()
    {
        var scope = SharedStringBuilder.Acquire();
        scope.StringBuilder.Append("once");
        scope.Dispose();

        // A second dispose of the same scope is a no-op: the lock was already released.
        scope.Dispose();
    }

    [Fact(Timeout = 30_000)]
    public void SharedStringBuilder_ShouldBlockUntilThePreviousHolderReleases()
    {
        var secondDone = new ManualResetEventSlim(false);

        var first = SharedStringBuilder.Acquire();
        first.StringBuilder.Append("held");

        var second = Task.Run(() =>
        {
            // The shared builder is exclusively held: this acquires only after the first scope released.
            using var scope = SharedStringBuilder.Acquire();
            secondDone.Set();
        });
        first.Dispose();

        Assert.True(second.Wait(TimeSpan.FromSeconds(20)), "The second acquirer should get in after the first released.");
        second.Dispose();
        secondDone.Dispose();
    }

    [Fact(Timeout = 30_000)]
    public void SharedStringBuilder_BuildShouldReturnTheContentAndLeaveAnEmptyBuilder()
    {
        var content = SharedStringBuilder.Build(sb => sb.Append("order: ").Append(42));
        Assert.Equal("order: 42", content);

        using var scope = SharedStringBuilder.Acquire();
        Assert.Equal(0, scope.StringBuilder.Length);
    }

    [Fact(Timeout = 30_000)]
    public void SharedStringBuilder_BuildShouldReleaseTheLockWhenTheCallbackThrows()
    {
        Assert.Throws<InvalidOperationException>(() =>
            SharedStringBuilder.Build(_ => throw new InvalidOperationException("boom")));

        // The lock is released on the failure path too, so the next acquirer gets in.
        using var scope = SharedStringBuilder.Acquire();
        Assert.Equal(0, scope.StringBuilder.Length);
    }

    [Fact(Timeout = 30_000)]
    public void SharedStringBuilder_DisposedScopeShouldThrow()
    {
        var scope = SharedStringBuilder.Acquire();
        scope.Dispose();

        Assert.Throws<ObjectDisposedException>(() => _ = scope.StringBuilder);
        Assert.Throws<ObjectDisposedException>(() => scope.ToString());
    }
}
