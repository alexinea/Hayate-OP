using System;
using DotNetCore.HayateOP.Buffers;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// Acceptance for the bucketed buffer pool (O-H): the rent request routes — rounded up — to the
/// smallest declared bucket that covers it, a request above the largest declared size is rejected
/// rather than served by an undeclared bucket, and each bucket retains at most its declared capacity
/// of returned arrays. The constructor rejects the configurations that could never route cleanly:
/// duplicate bucket sizes, non-positive sizes or capacities.
/// </summary>
public class BufferPoolTests
{
    /// <summary>A reference-type element with a marker, so identity of a reused array is observable through its contents.</summary>
    private sealed class Marker
    {
        public Marker(string name) => Name = name;

        public string Name { get; }
    }

    private static HayateBufferPool<byte> ThreeBucketPool() => new(
        new HayateSegmentDefinition(100),
        new HayateSegmentDefinition(200),
        new HayateSegmentDefinition(400));

    [Fact]
    public void Rent_RoutesToTheSmallestBucketThatCoversTheRequest()
    {
        using var pool = ThreeBucketPool();

        Assert.Equal(100, pool.Rent(1).Length);
        Assert.Equal(100, pool.Rent(100).Length);
        Assert.Equal(200, pool.Rent(101).Length);
        Assert.Equal(200, pool.Rent(200).Length);
        Assert.Equal(400, pool.Rent(201).Length);
        Assert.Equal(400, pool.Rent(400).Length);
    }

    [Fact]
    public void Rent_AboveTheLargestDeclaredBucket_IsRejected()
    {
        using var pool = ThreeBucketPool();

        var exception = Assert.Throws<ArgumentException>(() => pool.Rent(401));
        Assert.Contains("401", exception.Message);
    }

    [Fact]
    public void Rent_NonPositiveSize_IsRejected()
    {
        using var pool = ThreeBucketPool();

        Assert.Throws<ArgumentOutOfRangeException>(() => pool.Rent(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.Rent(-5));
    }

    [Fact]
    public void Constructor_SortsBucketsAscending_AndExposesTheDeclaredLadder()
    {
        using var pool = new HayateBufferPool<byte>(
            new HayateSegmentDefinition(400),
            new HayateSegmentDefinition(100),
            new HayateSegmentDefinition(200));

        Assert.Equal(new long[] { 100, 200, 400 }, new[] { pool.Segments[0].ArraySize, pool.Segments[1].ArraySize, pool.Segments[2].ArraySize });
        Assert.Equal(400, pool.MaxArraySize);
    }

    [Fact]
    public void Constructor_RejectsDuplicates_AndInvalidDefinitions()
    {
        Assert.Throws<ArgumentNullException>(() => new HayateBufferPool<byte>(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HayateSegmentDefinition(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HayateSegmentDefinition(100, 0));
        // A duplicate size could never win a route and would split one size's retention across two
        // buckets, so it is rejected instead of accepted.
        Assert.Throws<ArgumentException>(() => new HayateBufferPool<byte>(
            new HayateSegmentDefinition(100),
            new HayateSegmentDefinition(200),
            new HayateSegmentDefinition(100)));
    }

    [Fact]
    public void Rent_Return_RentsTheSameInstanceAgain()
    {
        using var pool = new HayateBufferPool<byte>(new HayateSegmentDefinition(100, 2));

        var first = pool.Rent(1);
        first.Dispose();

        var second = pool.Rent(1);
        Assert.Same(first.Array, second.Array);
        second.Dispose();
    }

    [Fact]
    public void Bucket_RetainsAtMostItsCapacity_OfReturnedArrays()
    {
        // Three buffers returned into a bucket that keeps two: exactly the first two returned are
        // parked, the third is dropped, and a subsequent rent observes the difference through the
        // markers the caller left in the arrays (contents are not cleared, like ArrayPool).
        using var pool = new HayateBufferPool<Marker>(new HayateSegmentDefinition(2, 2));

        var a = pool.Rent(1);
        var b = pool.Rent(1);
        var c = pool.Rent(1);
        a.Array[0] = new Marker("A");
        b.Array[0] = new Marker("B");
        c.Array[0] = new Marker("C");

        a.Dispose();
        b.Dispose();
        c.Dispose(); // dropped: the bucket holds two

        var parkedOne = pool.Rent(1);
        var parkedTwo = pool.Rent(1);
        Assert.Equal(2, parkedOne.Length);
        var names = new[] { parkedOne.Array[0].Name, parkedTwo.Array[0].Name };
        Assert.Contains("A", names);
        Assert.Contains("B", names);
        Assert.DoesNotContain("C", names);

        var fresh = pool.Rent(1);
        Assert.Null(fresh.Array[0]);
        fresh.Dispose();
        parkedOne.Dispose();
        parkedTwo.Dispose();
    }

    [Fact]
    public void Dispose_RejectsFurtherRents_ButOutstandingBuffersStillReturn()
    {
        var pool = ThreeBucketPool();
        var outstanding = pool.Rent(1);

        pool.Dispose();
        Assert.Throws<ObjectDisposedException>(() => pool.Rent(1));

        outstanding.Dispose(); // dropped, not parked — and it must not throw
    }

    [Fact]
    public void Return_RejectsForeignBuffers_AndIgnoresNull()
    {
        using var pool = ThreeBucketPool();
        using var other = ThreeBucketPool();

        var foreign = pool.Rent(1);
        Assert.Throws<ArgumentException>(() => other.Return(foreign));
        other.Return(null); // no guard needed at the call site
        foreign.Dispose();

        // Double disposal (explicit Return plus using) is a no-op, not an error.
        var twice = pool.Rent(1);
        twice.Dispose();
        pool.Return(twice);
    }
}
