namespace DotNetCore.HayateOP.Tests;

public class BasicFunctionTests
{
    private class TestObject : IHayateResettable, IHayateValidatable
    {
        public int Id { get; set; }
        public bool IsReset { get; private set; }
        public bool IsValidReturn { get; set; } = true;

        public void Reset() => IsReset = true;
        public bool IsValid() => IsValidReturn;
    }

    [Fact]
    public void Acquire_ShouldReturnNonNullObject()
    {
        using var pool = new HayatePoolBuilder<TestObject>().Build();
        var obj = pool.Acquire();
        Assert.NotNull(obj);
    }

    [Fact]
    public void Acquire_ShouldReturnObjectFromPool()
    {
        // Arrange
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(5)
            .WithMaxSize(10)
            .Build();

        // Act
        var obj = pool.Acquire();

        // Assert
        Assert.NotNull(obj);
        Assert.Equal(4, pool.GetStats().PooledCount);
    }

    [Fact]
    public void Acquire_ShouldIncrementTotalAcquired()
    {
        // Arrange
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(5)
            .WithMaxSize(10)
            .Build();

        // Act
        var obj1 = pool.Acquire();
        var obj2 = pool.Acquire();
        var obj3 = pool.Acquire();
        var stats = pool.GetStats();

        // Assert
        Assert.Equal(3, stats.TotalAcquired);

        // After returning, borrow again and verify the cumulative count keeps increasing
        pool.Release(obj1);
        var obj4 = pool.Acquire();
        stats = pool.GetStats();
        Assert.Equal(4, stats.TotalAcquired);
    }
    
    [Fact]
    public void FifoOrder_AcquireReturnsObjectsInReleaseOrder()
    {
        // Disable sharding to deterministically verify FIFO within a single shard (under multiple shards FIFO only guarantees in-shard order)
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithEnableSharding(false)
            .WithMinSize(5)
            .WithMaxSize(5)
            .Build();

        // Borrow all pre-warmed objects and keep the reference order
        var acquired = new TestObject[5];
        for (var i = 0; i < acquired.Length; i++)
            acquired[i] = pool.Acquire();

        // Return in borrow order to form the FIFO idle queue (shard idle linked list appends at tail)
        foreach (var o in acquired)
            pool.Release(o);

        // Borrowing again should preserve the return order (TryTake takes from head, first-in-first-out)
        for (var i = 0; i < acquired.Length; i++)
        {
            var o = pool.Acquire();
            Assert.Same(acquired[i], o);
        }
    }

    [Fact]
    public void TrackedRegistry_ConservesObjectsAcrossBorrowReleaseCycles()
    {
        // Regression guard: after the registry is split per shard, no orphaned registration entries may appear.
        // After a pure borrow/return loop (eviction/validation/auto-scaling all off), the following should hold:
        //   1) CurrentSize (sum of per-shard registry TrackedCount) == PooledCount (sum of per-shard idle counts)
        //      -- i.e. "total registered == idle count", with no outstanding borrow residue and no orphaned registrations;
        //   2) TotalCreated does not grow -- a pure borrow/return never creates new objects;
        //   3) returned objects can be borrowed again as-is (Release's per-shard probe path is correct).
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithEnableSharding(true)
            .WithShardCount(4)
            .WithMinSize(8)
            .WithMaxSize(8)
            .WithEnableEviction(false)
            .WithEnableValidation(false)
            .WithEnableAutoScaling(false)
            .WithEnableLeakDetection(false)
            .WithEnableGenerationOptimization(false)
            .Build();

        var createdBaseline = pool.GetStats().TotalCreated;

        for (var round = 0; round < 100; round++)
        {
            var borrowed = new TestObject[8];
            for (var i = 0; i < borrowed.Length; i++)
                borrowed[i] = pool.Acquire();

            foreach (var o in borrowed)
                pool.Release(o);
        }

        var stats = pool.GetStats();
        Assert.Equal(8, stats.PooledCount);
        Assert.Equal(stats.PooledCount, stats.CurrentSize);
        Assert.Equal(createdBaseline, stats.TotalCreated);

        // Final-round spot check: any returned object can still be borrowed normally (registration round-trip is valid)
        var reAcquired = pool.Acquire();
        Assert.NotNull(reAcquired);
        pool.Release(reAcquired);
    }

    [Fact(Timeout = 60000)]
    public async Task ConcurrentAcquire_ShouldCorrectlyIncrementTotalAcquired()
    {
        // Arrange
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(10)
            .WithMaxSize(100)
            .Build();

        const int threadCount = 10;
        const int acquirePerThread = 100;
        var tasks = new Task[threadCount];

        // Act
        for (int i = 0; i < threadCount; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                for (int j = 0; j < acquirePerThread; j++)
                {
                    var obj = pool.Acquire();
                    pool.Release(obj);
                }
            });
        }
        await Task.WhenAll(tasks);

        var stats = pool.GetStats();

        // Assert: cumulative borrow count = thread count x borrows per thread
        Assert.Equal(threadCount * acquirePerThread, stats.TotalAcquired);
    }
    
    [Fact]
    public void Release_ShouldReturnObjectToPool()
    {
        // Arrange
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(5)
            .WithMaxSize(10)
            .Build();
        var obj = pool.Acquire();

        // Act
        pool.Release(obj);

        // Assert
        Assert.Equal(5, pool.GetStats().PooledCount);
        Assert.True(obj.IsReset); // verify the Reset hook was called
    }

    [Fact]
    public void Release_NullObject_ShouldNotThrow()
    {
        // Arrange
        using var pool = new HayatePoolBuilder<TestObject>().Build();

        // Act & Assert
        pool.Release(null!);
    }

    [Fact]
    public void Release_ExternalObject_ShouldNotAddToPool()
    {
        // Arrange
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(5)
            .Build();
        var externalObj = new TestObject();

        // Act
        pool.Release(externalObj);

        // Assert
        Assert.Equal(5, pool.GetStats().PooledCount); // pool size unchanged
    }

    [Fact]
    public void PreWarm_ShouldCreateMinSizeObjects()
    {
        using var pool = new HayatePoolBuilder<TestObject>().WithMinSize(20).Build();
        Assert.Equal(20, pool.GetStats().PooledCount);
    }

    [Fact]
    public void InvalidConfig_ShouldThrowOnBuild()
    {
        // Arrange & Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
        {
            new HayatePoolBuilder<TestObject>()
                .WithMinSize(100)
                .WithMaxSize(50) // Min > Max, invalid configuration
                .Build();
        });
    }
}