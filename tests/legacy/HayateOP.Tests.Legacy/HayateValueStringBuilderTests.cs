using System;
using System.Buffers;
using System.Text;
using DotNetCore.HayateOP.Specialized;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// HayateValueStringBuilder (Z1): the zero-allocation ref-struct builder over an ArrayPool buffer.
/// Acceptance: appends touch no heap memory, ToString is the build's single allocation and ends the
/// builder (dispose-by-ToString), Dispose is idempotent, TryCopyTo copies without ending the borrow,
/// Clear keeps the buffer, growth preserves content, and every member after the builder has ended
/// throws ObjectDisposedException rather than reading a buffer that belongs to the pool.
/// Ref-struct locals cannot be captured by lambdas, so the post-dispose assertions use inline
/// try/catch instead of Assert.Throws.
/// </summary>
public class HayateValueStringBuilderTests
{
    private sealed class TrackedPool : ArrayPool<char>
    {
        private readonly ArrayPool<char> _inner = ArrayPool<char>.Create();

        public int Rents { get; private set; }

        public int Returns { get; private set; }

        public override char[] Rent(int minimumLength)
        {
            Rents++;
            return _inner.Rent(minimumLength);
        }

        public override void Return(char[] array, bool clearArray = false)
        {
            Returns++;
            _inner.Return(array, clearArray);
        }
    }

    [Fact]
    public void Append_BuildsTheExpectedContent()
    {
        using var sb = new HayateValueStringBuilder();

        // Sequential calls, ZString-style: the builder is a mutable ref struct, so chained calls
        // would run on struct copies and lose their position state.
        sb.Append("order: ");
        sb.Append('A');
        sb.Append("12");
        sb.AppendLine();
        sb.AppendLine("tail");

        Assert.Equal("order: A12" + Environment.NewLine + "tail" + Environment.NewLine, sb.ToString());
    }

    [Fact]
    public void Append_NullStringAppendsNothing()
    {
        using var sb = new HayateValueStringBuilder(64);
        sb.Append("a");
        sb.Append(null);
        sb.Append((string)null!);
        Assert.Equal("a", sb.ToString());
    }

    [Fact]
    public void ToString_MaterializesTheContentAndEndsTheBuilder()
    {
        var sb = new HayateValueStringBuilder(64);
        sb.Append("done");

        Assert.Equal("done", sb.ToString());

        // Dispose-by-ToString: the borrow ended at the call, and the buffer now belongs to the pool.
        var appendThrew = false;
        try { sb.Append("x"); }
        catch (ObjectDisposedException) { appendThrew = true; }
        Assert.True(appendThrew, "Append after ToString must throw ObjectDisposedException.");

        var toStringThrew = false;
        try { sb.ToString(); }
        catch (ObjectDisposedException) { toStringThrew = true; }
        Assert.True(toStringThrew, "A second ToString must throw ObjectDisposedException.");

        var lengthThrew = false;
        try { _ = sb.Length; }
        catch (ObjectDisposedException) { lengthThrew = true; }
        Assert.True(lengthThrew, "Length after the builder has ended must throw ObjectDisposedException.");

        // The trailing dispose of a using block is a no-op after ToString.
        sb.Dispose();
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var sb = new HayateValueStringBuilder(64);
        sb.Append("once");
        sb.Dispose();
        sb.Dispose();
    }

    [Fact]
    public void UsingPattern_EndsTheBuilderExactlyOnce()
    {
        var pool = new TrackedPool();

        using (var sb = new HayateValueStringBuilder(pool, 64))
        {
            sb.Append("scoped");
        }

        // One rent, one return: the using disposal ended the borrow exactly once.
        Assert.Equal(1, pool.Rents);
        Assert.Equal(1, pool.Returns);
    }

    [Fact]
    public void TryCopyTo_CopiesWithoutEndingTheBuilder()
    {
        using var sb = new HayateValueStringBuilder(64);
        sb.Append("hello");

        var destination = new char[16];
        Assert.True(sb.TryCopyTo(destination, out var charsWritten));
        Assert.Equal(5, charsWritten);
        Assert.Equal("hello", new string(destination, 0, charsWritten));

        // The copy did not end the borrow: the builder keeps working.
        sb.Append(" world");
        Assert.Equal("hello world", sb.ToString());
    }

    [Fact]
    public void TryCopyTo_TooSmallSpanReportsFailure()
    {
        using var sb = new HayateValueStringBuilder(64);
        sb.Append("content");

        Assert.False(sb.TryCopyTo(new char[3], out var charsWritten));
        Assert.Equal(0, charsWritten);
    }

    [Fact]
    public void AsSpan_ViewReflectsTheContent()
    {
        using var sb = new HayateValueStringBuilder(64);
        sb.Append("view");

        Assert.Equal("view", sb.AsSpan().ToString());

        // Clear keeps the buffer; the view follows the builder.
        sb.Clear();
        Assert.Equal(0, sb.AsSpan().Length);
        sb.Append("again");
        Assert.Equal("again", sb.AsSpan().ToString());
    }

    [Fact]
    public void Grow_PreservesTheContent()
    {
        var pool = new TrackedPool();
        using var sb = new HayateValueStringBuilder(pool, 16);

        var expected = new string('x', 5000);
        sb.Append(expected);

        Assert.True(sb.Capacity >= 5000, $"Capacity should cover the grown content (got {sb.Capacity}).");
        Assert.Equal(expected, sb.ToString());
        // Every grow hands the old buffer back to the pool, on top of the final return.
        Assert.True(pool.Returns >= 2, "Growth should return the old buffers to the pool.");
    }

    [Fact]
    public void Clear_ResetsLengthAndKeepsTheBuilderUsable()
    {
        using var sb = new HayateValueStringBuilder(64);
        var capacity = sb.Capacity;
        sb.Append("first");
        sb.Clear();

        Assert.Equal(0, sb.Length);
        Assert.Equal(capacity, sb.Capacity);

        sb.Append("second");
        Assert.Equal("second", sb.ToString());
    }

    [Fact]
    public void Indexer_ReadsTheContentAndValidatesBounds()
    {
        using var sb = new HayateValueStringBuilder(64);
        sb.Append("abc");

        Assert.Equal('a', sb[0]);
        Assert.Equal('c', sb[2]);

        var outOfRange = false;
        try { _ = sb[3]; }
        catch (ArgumentOutOfRangeException) { outOfRange = true; }
        Assert.True(outOfRange, "An index at Length must throw ArgumentOutOfRangeException.");

        var negative = false;
        try { _ = sb[-1]; }
        catch (ArgumentOutOfRangeException) { negative = true; }
        Assert.True(negative, "A negative index must throw ArgumentOutOfRangeException.");
    }

    [Fact]
    public void NegativeInitialCapacity_ShouldThrow()
    {
        var negativeCapacity = false;
        try { _ = new HayateValueStringBuilder(-1); }
        catch (ArgumentOutOfRangeException) { negativeCapacity = true; }
        Assert.True(negativeCapacity);

        var negativeCapacityWithPool = false;
        try { _ = new HayateValueStringBuilder(null, -1); }
        catch (ArgumentOutOfRangeException) { negativeCapacityWithPool = true; }
        Assert.True(negativeCapacityWithPool);
    }

#if !NET48
    // GC.GetAllocatedBytesForCurrentThread does not exist on .NET Framework 4.8, so the zero-
    // allocation acceptance runs on the modern targets only; net48 runs the functional suite above.
    [Fact]
    public void Build_ShouldAllocateNothingBeyondTheFinalString()
    {
        // Warm the bucket and the jitted paths first. There is deliberately no GC between the warmup
        // and the measurement: a collection trims the shared pool's per-thread caches and would force
        // the measured rent to allocate a fresh buffer. The explicit capacity keeps the measured build
        // off the grow path, so its rent can hit the warmed bucket.
        const int capacity = 4096;
        var warm = new HayateValueStringBuilder(capacity);
        warm.Append(new string('w', capacity - 1));
        warm.Dispose();

        var before = GC.GetAllocatedBytesForCurrentThread();
        long allocatedDuringBuild;
        string text;
        using (var sb = new HayateValueStringBuilder(capacity))
        {
            for (var i = 0; i < 50; i++)
            {
                sb.Append("order-");
                sb.Append('x');
                sb.Append(' ');
            }

            allocatedDuringBuild = GC.GetAllocatedBytesForCurrentThread() - before;
            text = sb.ToString();
        }

        var total = GC.GetAllocatedBytesForCurrentThread() - before;

        // The whole build (rent, appends) must touch no heap memory; only ToString allocates.
        Assert.Equal(0, allocatedDuringBuild);
        Assert.Equal(400, text.Length);
        Assert.Equal("order-x order-x order-x", text[..23]);
        Assert.True(total > 0, "ToString should be the build's single allocation.");
    }
#endif
}
