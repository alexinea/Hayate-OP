using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DotNetCore.HayateOP.Specialized;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// Specialized pool convenience APIs (Z-C-A): declared-capacity tiering, seed prefill with the robust
/// clear-first order, convert-and-return, and the generic Format/Concat/Join helpers.
/// Acceptance: a borrow through <see cref="StringBuilderPool.GetObject(int)"/> (or the stream pool
/// equivalent) returns a builder/stream that already fits the declared capacity, tier builders are
/// parked and reused even above the pool's global maximum (the mixed-size churn the tiers exist to
/// remove), only growth beyond the declared envelope destroys a builder, a seeded builder holds exactly
/// the seed however previous borrows ended, <c>ToStringReturn</c>/<c>ToByteArrayReturn</c> convert and
/// return in one exactly-once step, the format helpers match <c>string.Format</c>/<c>string.Concat</c>/<c>string.Join</c>
/// without a params array, and the pool's stats cover the tier engines.
/// </summary>
public class SpecializedPoolConvenienceApiTests
{
    // ── StringBuilderPool: declared-capacity tiers ──────────────────────

    [Fact(Timeout = 30_000)]
    public void GetObject_WithCapacity_ShouldReturnBuilderWithAtLeastTheRequestedCapacity()
    {
        using var pool = new StringBuilderPool(4);

        using var sb = pool.GetObject(60_000);

        // No append happened: the capacity was reserved by the borrow itself, so the grow ladder is
        // skipped entirely.
        Assert.True(sb.StringBuilder.Capacity >= 60_000,
            $"A declared-capacity borrow should reserve the requested capacity (got {sb.StringBuilder.Capacity}).");
        Assert.Equal(0, sb.StringBuilder.Length);
    }

    [Fact(Timeout = 30_000)]
    public void GetObject_WithCapacity_ShouldReuseTierBuildersAcrossBorrows()
    {
        using var pool = new StringBuilderPool(4);

        var first = pool.GetObject(60_000);
        first.Dispose();

        // The tier parks its builders, exactly like the base pool does.
        Assert.Same(first, pool.GetObject(60_000));
    }

    [Fact(Timeout = 30_000)]
    public void GetObject_WithCapacity_AboveMaximum_ShouldParkInsteadOfDestroy()
    {
        using var pool = new StringBuilderPool(4);
        pool.MaximumStringBuilderCapacity = 256 * 1024;

        // 600K chars is far above the global maximum: undeclared growth of this size would be destroyed
        // on every return, but the borrow declared the need, so the tier parks the buffer and the next
        // declared borrow reuses it — the mixed-size churn the tiers exist to remove.
        var first = pool.GetObject(600_000);
        first.StringBuilder.Append(new string('x', 1000));
        first.Dispose();

        var second = pool.GetObject(600_000);
        Assert.Same(first, second);
        Assert.True(second.StringBuilder.Capacity >= 600_000);
        second.Dispose();
    }

    [Fact(Timeout = 30_000)]
    public void GetObject_WithCapacity_GrowthBeyondTheDeclaredEnvelope_ShouldStillBeDestroyed()
    {
        using var pool = new StringBuilderPool(4);

        // The tier covers its declared bucket; appending far past it (and past the global maximum)
        // leaves nothing declared about the buffer, so the builder is destroyed on return.
        var oversized = pool.GetObject(60_000);
        oversized.StringBuilder.Append(new string('x', 600_000));
        oversized.Dispose();

        var a = pool.GetObject(60_000);
        var b = pool.GetObject(60_000);
        Assert.NotSame(oversized, a);
        Assert.NotSame(oversized, b);
    }

    [Fact(Timeout = 30_000)]
    public void GetObject_WithCapacity_ShouldNotDisturbTheBaseTier()
    {
        using var pool = new StringBuilderPool(1);

        var baseBuilder = pool.GetObject();

        // A one-slot base pool is full: the declared borrow must come from its own tier engine, not
        // from the base pool's single builder.
        var tierBuilder = pool.GetObject(60_000);
        Assert.NotSame(baseBuilder, tierBuilder);

        baseBuilder.Dispose();
        tierBuilder.Dispose();

        Assert.Same(baseBuilder, pool.GetObject());
    }

    [Fact(Timeout = 30_000)]
    public void GetObject_WithCapacity_BelowMinimum_ShouldBeServedByTheBaseTier()
    {
        using var pool = new StringBuilderPool(4);

        var baseBuilder = pool.GetObject();
        baseBuilder.Dispose();

        // A request within the creation capacity needs no tier: the base pool serves it like any
        // plain borrow.
        Assert.Same(baseBuilder, pool.GetObject(16));
    }

    [Fact(Timeout = 30_000)]
    public void GetObject_WithCapacity_ShouldThrowForNonPositiveCapacity()
    {
        using var pool = new StringBuilderPool(4);

        Assert.Throws<ArgumentOutOfRangeException>(() => pool.GetObject(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.GetObject(-4096));
    }

    [Fact(Timeout = 30_000)]
    public void Clear_ShouldAlsoDropTheCapacityTierBuilders()
    {
        using var pool = new StringBuilderPool(4);

        var tierBuilder = pool.GetObject(60_000);
        tierBuilder.Dispose();

        pool.Clear();

        Assert.NotSame(tierBuilder, pool.GetObject(60_000));
    }

    [Fact(Timeout = 30_000)]
    public void RaisingMinimumCapacity_ShouldRetireTheExistingTiers()
    {
        using var pool = new StringBuilderPool(4);

        var tierBuilder = pool.GetObject(60_000);
        tierBuilder.Dispose();

        // The tier ladder is anchored at the minimum, so raising it retires the tiers along with the
        // parked builders (P89OP parity for the base pool, extended to the tiers).
        pool.MinimumStringBuilderCapacity = 100_000;

        Assert.NotSame(tierBuilder, pool.GetObject(60_000));
    }

    [Fact(Timeout = 30_000)]
    public void GetStats_ShouldIncludeTheCapacityTierBuilders()
    {
        using var pool = new StringBuilderPool(4);

        var tierBuilder = pool.GetObject(60_000);
        tierBuilder.Dispose();

        // The parked tier builder is part of the pool: stats and snapshots aggregate the tier engines.
        Assert.True(pool.GetStats().PooledCount >= 1,
            "GetStats should cover builders parked in capacity tiers.");
        Assert.True(pool.TakeSnapshot().PooledCount >= 1,
            "TakeSnapshot should cover builders parked in capacity tiers.");

        // G-1: the aggregation sums the counters and merges the operational members, so it must not
        // invent a measurement none of the parts took. These builders run with the metrics switch off,
        // and the aggregate has to say so rather than report a fabricated peak or age.
        var stats = pool.GetStats();
        Assert.False(stats.MetricsEnabled);
        Assert.Equal(0, stats.PeakActiveObjects);
        Assert.Equal(default(DateTimeOffset), stats.StartedAt);
        Assert.Null(stats.LastActivityTime);
        Assert.Equal(0, stats.ReuseEfficiency);
    }

    // ── StringBuilderPool: seeded borrows ───────────────────────────────

    [Fact(Timeout = 30_000)]
    public void GetObject_WithSeed_ShouldHoldExactlyTheSeed()
    {
        using var pool = new StringBuilderPool(4);

        var seed = new string('s', 5000);
        using var sb = pool.GetObject(seed);

        Assert.Equal(seed, sb.ToString());

        // A seed longer than the creation capacity borrows through a tier, so the builder already fits
        // the seed instead of growing to it.
        Assert.True(sb.StringBuilder.Capacity >= seed.Length);
    }

    [Fact(Timeout = 30_000)]
    public void GetObject_WithSeed_ShouldNotKeepResidueFromThePreviousBorrow()
    {
        using var pool = new StringBuilderPool(4);

        var previous = pool.GetObject();
        previous.StringBuilder.Append("JUNK");
        previous.Dispose();

        // The fill clears first and appends second: the content is exactly the seed however the
        // previous borrow ended.
        using var sb = pool.GetObject("order: 42");
        Assert.Equal("order: 42", sb.ToString());
    }

    // ── PooledStringBuilder: convert and return ─────────────────────────

    [Fact(Timeout = 30_000)]
    public void ToStringReturn_ShouldReturnTheContentAndReleaseTheBuilder()
    {
        using var pool = new StringBuilderPool(4);

        var sb = pool.GetObject();
        sb.StringBuilder.Append("order: 42");

        Assert.Equal("order: 42", sb.ToStringReturn());

        // The convert-and-return ended the borrow: the builder is parked again, exactly once.
        Assert.Same(sb, pool.GetObject());
    }

    [Fact(Timeout = 30_000)]
    public void ToStringReturn_ShouldReturnExactlyOnce()
    {
        using var pool = new StringBuilderPool(4);

        var sb = pool.GetObject();
        sb.StringBuilder.Append("once");
        Assert.Equal("once", sb.ToStringReturn());

        // A second call on the same borrow must not route a second return: the pool would park the
        // builder twice and later hand it to two borrowers. The parked builder is empty, and the pool
        // still serves two distinct builders.
        Assert.Equal(string.Empty, sb.ToStringReturn());
        var a = pool.GetObject();
        var b = pool.GetObject();
        Assert.NotSame(a, b);
    }

    [Fact(Timeout = 30_000)]
    public void ToStringReturn_OnAStandaloneBuilder_ShouldJustReturnTheContent()
    {
        var standalone = new PooledStringBuilder(16);
        standalone.StringBuilder.Append("keep me");

        Assert.Equal("keep me", standalone.ToStringReturn());
    }

    // ── PooledMemoryStream: convert and return ──────────────────────────

    [Fact(Timeout = 30_000)]
    public void ToByteArrayReturn_ShouldReturnTheBytesAndReleaseTheStream()
    {
        using var pool = new MemoryStreamPool(4);

        var stream = pool.GetObject();
        stream.Write(new byte[] { 1, 2, 3, 4, 5 }, 0, 5);

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, stream.ToByteArrayReturn());

        Assert.Same(stream, pool.GetObject());
    }

    [Fact(Timeout = 30_000)]
    public void ToByteArrayReturn_ShouldReturnExactlyOnce()
    {
        using var pool = new MemoryStreamPool(4);

        var stream = pool.GetObject();
        stream.Write(new byte[] { 9, 8, 7 }, 0, 3);
        Assert.Equal(new byte[] { 9, 8, 7 }, stream.ToByteArrayReturn());

        // The second call on the same borrow must not route a second return; the parked stream is
        // empty, and the pool still serves two distinct streams.
        Assert.Empty(stream.ToByteArrayReturn());
        var a = pool.GetObject();
        var b = pool.GetObject();
        Assert.NotSame(a, b);
    }

    [Fact(Timeout = 30_000)]
    public void ToByteArrayReturn_OnAStandaloneStream_ShouldJustReturnTheBytes()
    {
        var standalone = new PooledMemoryStream(16);
        standalone.Write(new byte[] { 5, 6, 7 }, 0, 3);

        Assert.Equal(new byte[] { 5, 6, 7 }, standalone.ToByteArrayReturn());
    }

    // ── MemoryStreamPool: declared-capacity tiers ────────────────────────

    [Fact(Timeout = 30_000)]
    public void StreamPool_GetObject_WithCapacity_ShouldReturnStreamWithAtLeastTheRequestedCapacity()
    {
        using var pool = new MemoryStreamPool(4);

        using var stream = pool.GetObject(60_000);

        Assert.True(stream.Capacity >= 60_000,
            $"A declared-capacity borrow should reserve the requested capacity (got {stream.Capacity}).");
        Assert.Equal(0, stream.Length);
    }

    [Fact(Timeout = 30_000)]
    public void StreamPool_GetObject_WithCapacity_ShouldReuseTierStreamsAcrossBorrows()
    {
        using var pool = new MemoryStreamPool(4);

        var first = pool.GetObject(60_000);
        first.Dispose();

        Assert.Same(first, pool.GetObject(60_000));
    }

    [Fact(Timeout = 30_000)]
    public void StreamPool_GetObject_WithCapacity_AboveMaximum_ShouldParkInsteadOfDestroy()
    {
        using var pool = new MemoryStreamPool(4);
        pool.MaximumMemoryStreamCapacity = 256 * 1024;

        var first = pool.GetObject(600_000);
        first.Write(new byte[] { 1 }, 0, 1);
        first.Dispose();

        var second = pool.GetObject(600_000);
        Assert.Same(first, second);
        second.Dispose();
    }

    [Fact(Timeout = 30_000)]
    public void StreamPool_GetObject_WithCapacity_ShouldNotDisturbTheBaseTier()
    {
        using var pool = new MemoryStreamPool(1);

        var baseStream = pool.GetObject();
        var tierStream = pool.GetObject(60_000);
        Assert.NotSame(baseStream, tierStream);

        baseStream.Dispose();
        tierStream.Dispose();

        Assert.Same(baseStream, pool.GetObject());
    }

    [Fact(Timeout = 30_000)]
    public void StreamPool_GetObject_WithCapacity_ShouldThrowForNonPositiveCapacity()
    {
        using var pool = new MemoryStreamPool(4);

        Assert.Throws<ArgumentOutOfRangeException>(() => pool.GetObject(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.GetObject(-4096));
    }

    // ── Generic formatting helpers ──────────────────────────────────────

    [Fact(Timeout = 30_000)]
    public void Format_ShouldMatchStringFormat()
    {
        // Baseline against string.Format at every TFM's current culture, so the comparison is
        // culture-safe by construction.
        Assert.Equal(
            string.Format("{0}-{1}", "order", 42),
            StringBuilderPool.Format("{0}-{1}", "order", 42));

        // Repeated holes.
        Assert.Equal(
            string.Format("{0}{0}", "x"),
            StringBuilderPool.Format("{0}{0}", "x"));

        // Brace escapes.
        Assert.Equal(
            string.Format("{{{0}}}", 7),
            StringBuilderPool.Format("{{{0}}}", 7));

        // Format specifiers, right and left alignment.
        Assert.Equal(
            string.Format("{0:N2}", 1234.5678),
            StringBuilderPool.Format("{0:N2}", 1234.5678));
        Assert.Equal(
            string.Format("[{0,6}]", 42),
            StringBuilderPool.Format("[{0,6}]", 42));
        Assert.Equal(
            string.Format("[{0,-6}]", 42),
            StringBuilderPool.Format("[{0,-6}]", 42));

        // Null holes render as empty, like string.Format.
        Assert.Equal(
            string.Format("{0}|{1}", "a", (string?)null),
            StringBuilderPool.Format("{0}|{1}", "a", (string?)null));
    }

    [Fact(Timeout = 30_000)]
    public void Format_ShouldSupportUpToEightArguments()
    {
        Assert.Equal(
            string.Format("{7}{6}{5}{4}{3}{2}{1}{0}", 1, 2, 3, 4, 5, 6, 7, 8),
            StringBuilderPool.Format("{7}{6}{5}{4}{3}{2}{1}{0}", 1, 2, 3, 4, 5, 6, 7, 8));
    }

    [Fact(Timeout = 30_000)]
    public void Format_ShouldThrowWhenTheIndexIsOutOfRange()
    {
        Assert.Throws<FormatException>(() => StringBuilderPool.Format("{1}", "only-one"));
    }

    [Fact(Timeout = 30_000)]
    public void Format_ShouldThrowOnMalformedHoles()
    {
        Assert.Throws<FormatException>(() => StringBuilderPool.Format("{}", "a"));
        Assert.Throws<FormatException>(() => StringBuilderPool.Format("{0", "a"));
        Assert.Throws<FormatException>(() => StringBuilderPool.Format("}", "a"));
        Assert.Throws<FormatException>(() => StringBuilderPool.Format("{0,}", "a"));
    }

    [Fact(Timeout = 30_000)]
    public void Concat_ShouldMatchStringConcat()
    {
        Assert.Equal(string.Concat("order", "-", 42), StringBuilderPool.Concat("order", "-", 42));
        Assert.Equal(string.Concat(1, 2, 3), StringBuilderPool.Concat(1, 2, 3));
        Assert.Equal("x", StringBuilderPool.Concat((string?)null, "x"));
        Assert.Equal(
            string.Concat("a", "b", "c", "d", "e", "f", "g", "h"),
            StringBuilderPool.Concat("a", "b", "c", "d", "e", "f", "g", "h"));
    }

    [Fact(Timeout = 30_000)]
    public void Join_ShouldMatchStringJoin()
    {
        // string.Join has no char-separator overload on net48, so the baseline uses the string one —
        // same rendering, culture-consistent on both sides of the comparison.
        Assert.Equal(string.Join("-", 1, 2, 3), StringBuilderPool.Join('-', new[] { 1, 2, 3 }));
        Assert.Equal(
            string.Join(", ", new[] { "a", "b", "c" }),
            StringBuilderPool.Join(", ", new[] { "a", "b", "c" }));

        // Null segments render empty, empty input yields an empty string, and a null separator joins
        // with nothing — all matching string.Join.
        Assert.Equal(string.Join(", ", new string?[] { "a", null, "c" }), StringBuilderPool.Join(", ", new string?[] { "a", null, "c" }));
        Assert.Equal(string.Empty, StringBuilderPool.Join('-', Array.Empty<int>()));
        Assert.Equal(string.Join(null, new[] { "a", "b" }), StringBuilderPool.Join(null, new[] { "a", "b" }));
    }

    [Fact(Timeout = 30_000)]
    public void Join_ShouldThrowWhenTheValuesAreNull()
    {
        Assert.Throws<ArgumentNullException>(() => StringBuilderPool.Join('-', (IEnumerable<int>)null!));
        Assert.Throws<ArgumentNullException>(() => StringBuilderPool.Join(", ", (IEnumerable<string>)null!));
    }
}
