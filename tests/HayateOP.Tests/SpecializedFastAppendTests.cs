using System;
using System.Globalization;
using DotNetCore.HayateOP.Specialized;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// Z4a: the generic fast-append surface. <see cref="PooledStringBuilder.Append{T}"/> (fluent) and
/// <see cref="HayateValueStringBuilder.Append{T}"/> write values through their concrete type:
/// net6+ formats ISpanFormattable values straight into the destination (no intermediate string), and
/// net48 degrades to IFormattable — the same cost the builders' own primitive appends have there.
/// Custom IFormattable-only types keep their format specifier everywhere.
/// </summary>
public class SpecializedFastAppendTests
{
    private sealed class Wallet : IFormattable
    {
        public decimal Amount { get; init; }

        public string ToString(string? format, IFormatProvider? formatProvider)
            => "W:" + Amount.ToString(format, formatProvider ?? CultureInfo.CurrentCulture);
    }

    [Fact(Timeout = 30_000)]
    public void PooledStringBuilder_Append_Generic_ShouldBuildTheContent()
    {
        using var pool = new StringBuilderPool(4);

        var sb = pool.GetObject();
        sb.Append("n=").Append(42).Append('!');

        Assert.Equal("n=42!", sb.ToStringReturn());
    }

    [Fact(Timeout = 30_000)]
    public void PooledStringBuilder_Append_WithFormat_ShouldMatchStringFormat()
    {
        using var pool = new StringBuilderPool(4);

        var sb = pool.GetObject();
        sb.Append(1234.5678, "F2").Append('|').Append(1234.5678, "N1");

        Assert.Equal(
            string.Format("{0:F2}|{1:N1}", 1234.5678, 1234.5678),
            sb.ToStringReturn());
    }

    [Fact(Timeout = 30_000)]
    public void PooledStringBuilder_Append_Null_ShouldAppendNothing()
    {
        using var pool = new StringBuilderPool(4);

        var sb = pool.GetObject();
        sb.Append("a").Append<string?>(null).Append("b");

        Assert.Equal("ab", sb.ToStringReturn());
    }

    [Fact(Timeout = 30_000)]
    public void PooledStringBuilder_Append_CustomIFormattable_ShouldHonorTheFormat()
    {
        using var pool = new StringBuilderPool(4);

        var sb = pool.GetObject();
        sb.Append(new Wallet { Amount = 12.5m }, "F1");

        // The IFormattable-only branch (custom types are not ISpanFormattable) still carries the
        // format specifier.
        Assert.Equal(
            "W:" + 12.5m.ToString("F1", CultureInfo.CurrentCulture),
            sb.ToStringReturn());
    }

    [Fact(Timeout = 30_000)]
    public void PooledStringBuilder_Append_ValueAndReferenceTypes_ShouldUseToString()
    {
        using var pool = new StringBuilderPool(4);

        var sb = pool.GetObject();
        sb.Append(new Wallet { Amount = 1m }).Append('|').Append(DateTime.MinValue, "yyyy");

        Assert.Equal(
            "W:1|" + DateTime.MinValue.ToString("yyyy", CultureInfo.CurrentCulture),
            sb.ToStringReturn());
    }

    [Fact(Timeout = 30_000)]
    public void ValueBuilder_Append_Generic_ShouldBuildTheContent()
    {
        using var sb = new HayateValueStringBuilder(64);
        sb.Append("n=");
        sb.Append(42);
        sb.Append('!');

        Assert.Equal("n=42!", sb.ToString());
    }

    [Fact(Timeout = 30_000)]
    public void ValueBuilder_Append_WithFormat_ShouldMatchStringFormat()
    {
        using var sb = new HayateValueStringBuilder(64);
        sb.Append(1234.5678, "F2");
        sb.Append('|');
        sb.Append(1234.5678, "N1");

        Assert.Equal(
            string.Format("{0:F2}|{1:N1}", 1234.5678, 1234.5678),
            sb.ToString());
    }

    [Fact(Timeout = 30_000)]
    public void ValueBuilder_Append_CustomIFormattable_ShouldHonorTheFormat()
    {
        using var sb = new HayateValueStringBuilder(64);
        sb.Append(new Wallet { Amount = 12.5m }, "F1");

        Assert.Equal(
            "W:" + 12.5m.ToString("F1", CultureInfo.CurrentCulture),
            sb.ToString());
    }

    [Fact(Timeout = 30_000)]
    public void ValueBuilder_Append_GrowingForTheFormattedValue_ShouldPreserveContent()
    {
        // The TryFormat path grows the buffer when the remaining space does not fit the formatted
        // value; the content written so far must survive the growth.
        using var sb = new HayateValueStringBuilder(16);
        sb.Append(new string('x', 10));
        sb.Append(1234567890);
        sb.Append(new string('y', 10));

        Assert.Equal("xxxxxxxxxx1234567890yyyyyyyyyy", sb.ToString());
    }

    [Fact(Timeout = 30_000)]
    public void ValueBuilder_Append_Generic_AfterToString_ShouldThrow()
    {
        var sb = new HayateValueStringBuilder(64);
        sb.Append(42);
        Assert.Equal("42", sb.ToString());

        var threw = false;
        try { sb.Append(42); }
        catch (ObjectDisposedException) { threw = true; }
        Assert.True(threw, "Append after the builder has ended must throw ObjectDisposedException.");
    }

#if !NET48
    // The direct-write acceptance: on net6+ an ISpanFormattable append must not materialize any
    // intermediate string. GC.GetAllocatedBytesForCurrentThread does not exist on net48, so this
    // assertion is future-only; net48's degradation path is covered by the content tests above.
    [Fact(Timeout = 30_000)]
    public void ValueBuilder_Append_Generic_ShouldAllocateNothing()
    {
        // Collect BEFORE warming: a collection after the warmup trims the shared pool's per-thread
        // caches and would make the measured rent allocate a fresh buffer.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        // Warm every measured path (int, double and its format) with a real loop: tiered compilation
        // promotes the generic instantiations to tier1 only after several calls, and the tier0 code
        // still box-frames the interface dispatch — a single warmup call would leak exactly that box
        // into the first measured iteration.
        for (var i = 0; i < 100; i++)
        {
            var warm = new HayateValueStringBuilder(64);
            warm.Append(1234567890);
            warm.Append(98.75, "F1");
            warm.Dispose();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        using var sb = new HayateValueStringBuilder(64);
        sb.Append(1234567890);
        sb.Append(98.75, "F1");
        long during = GC.GetAllocatedBytesForCurrentThread() - before;
        string text = sb.ToString();

        Assert.Equal(0, during);
        Assert.Equal("1234567890" + string.Format("{0:F1}", 98.75), text);
    }

    [Fact(Timeout = 30_000)]
    public void PooledStringBuilder_Append_Generic_ShouldAddNothingBeyondTheEngineFloor()
    {
        using var pool = new StringBuilderPool(4);

        // Loop the warmup for the same tiering reason as the value-builder test: the tier0 code of
        // the generic append box-frames its interface dispatch, and only repeated calls promote it.
        for (var i = 0; i < 100; i++)
        {
            var warm = pool.GetObject();
            warm.Append(1234567890);
            warm.Append(98.75, "F1");
            warm.Dispose();
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // One discarded measured iteration absorbs the first-call environment cost a test host can
        // introduce (xunit's runner re-JITs under its own instrumentation); the acceptance is about
        // the steady-state append path.
        var discarded = pool.GetObject();
        discarded.Append(1234567890);
        discarded.Append(98.75, "F1");
        discarded.Dispose();

        // Iteration average instead of one sample: a single borrow can pick up the engine's own
        // allocation bookkeeping, which varies by runtime; the steady-state per-append cost is what
        // this acceptance is about.
        long total = 0;
        const int iterations = 100;
        string text = string.Empty;
        for (var i = 0; i < iterations; i++)
        {
            var sb = pool.GetObject();
            long before = GC.GetAllocatedBytesForCurrentThread();
            sb.Append(1234567890);
            sb.Append(98.75, "F1");
            total += GC.GetAllocatedBytesForCurrentThread() - before;
            text = sb.ToStringReturn();
        }

        Assert.Equal(0, total);
        Assert.Equal("1234567890" + string.Format("{0:F1}", 98.75), text);
    }
#endif
}
