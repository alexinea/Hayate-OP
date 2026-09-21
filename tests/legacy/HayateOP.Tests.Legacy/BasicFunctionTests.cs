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

        // After release, borrow again to verify the cumulative count keeps increasing
        pool.Release(obj1);
        var obj4 = pool.Acquire();
        stats = pool.GetStats();
        Assert.Equal(4, stats.TotalAcquired);
    }
    
    [Fact]
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

        // Assert: total borrow count = thread count x borrow count per thread
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
        Assert.Equal(5, pool.GetStats().PooledCount); // pool size is unchanged
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