using System;
using System.IO;
using System.Text;
using DotNetCore.HayateOP.Specialized;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// Z5: the Span output surface. <see cref="PooledStringBuilder.TryCopyTo"/> hands out the content as
/// chars without materializing a string (net6+ walks the chunk chain straight into the span; net48
/// degrades through one intermediate string, never worse than the ToString it replaces), and
/// <see cref="PooledStringBuilder.WriteTo"/> (net6+) streams the content as UTF-8 through a rented
/// scratch buffer. Neither ends the borrow; both snapshot the content as it stands when called.
/// </summary>
public class SpecializedSpanOutputTests
{
    [Fact]
    public void TryCopyTo_ShouldCopyTheContentWithoutEndingTheBorrow()
    {
        using var pool = new StringBuilderPool(4);

        var sb = pool.GetObject();
        sb.StringBuilder.Append("order: 42");

        var destination = new char[32];
        Assert.True(sb.TryCopyTo(destination, out var charsWritten));
        Assert.Equal(9, charsWritten);
        Assert.Equal("order: 42", new string(destination, 0, charsWritten));

        // The copy did not end the borrow: the builder keeps working.
        sb.StringBuilder.Append(" units");
        Assert.Equal("order: 42 units", sb.ToStringReturn());
    }

    [Fact]
    public void TryCopyTo_TooSmallSpan_ShouldFailWithoutWriting()
    {
        using var pool = new StringBuilderPool(4);

        var sb = pool.GetObject();
        sb.StringBuilder.Append("content");

        var destination = new char[3];
        Assert.False(sb.TryCopyTo(destination, out var charsWritten));
        Assert.Equal(0, charsWritten);

        sb.Dispose();
    }

    [Fact]
    public void TryCopyTo_LongContentBeyondOneChunk_ShouldCopyEverything()
    {
        using var pool = new StringBuilderPool(4);

        var sb = pool.GetObject();
        sb.StringBuilder.Append(new string('x', 5000));
        sb.StringBuilder.Append("tail");

        var destination = new char[6000];
        Assert.True(sb.TryCopyTo(destination, out var charsWritten));
        Assert.Equal(5004, charsWritten);
        Assert.Equal(new string('x', 5000) + "tail", new string(destination, 0, charsWritten));

        sb.Dispose();
    }

#if NET6_0_OR_GREATER
    [Fact]
    public void WriteTo_ShouldStreamTheContentAsUtf8()
    {
        using var pool = new StringBuilderPool(4);

        var sb = pool.GetObject();
        sb.StringBuilder.Append("order: 42 units");

        using var target = new MemoryStream();
        sb.WriteTo(target);

        Assert.Equal("order: 42 units", Encoding.UTF8.GetString(target.ToArray()));

        // The write did not end the borrow.
        Assert.Equal("order: 42 units", sb.ToStringReturn());
    }

    [Fact]
    public void WriteTo_LongContentBeyondOneChunk_ShouldStreamEverything()
    {
        using var pool = new StringBuilderPool(4);

        var sb = pool.GetObject();
        var payload = new string('x', 5000) + "é中\U0001F600tail";
        sb.StringBuilder.Append(payload);

        using var target = new MemoryStream();
        sb.WriteTo(target);

        // The payload includes a BMP astral mix and a chunk boundary; the persistent encoder keeps a
        // surrogate pair split across chunks intact.
        Assert.Equal(payload, Encoding.UTF8.GetString(target.ToArray()));

        sb.Dispose();
    }
#endif
}
