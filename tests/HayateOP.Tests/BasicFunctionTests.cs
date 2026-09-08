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

        // 归还后再次借出，验证累计次数继续累加
        pool.Release(obj1);
        var obj4 = pool.Acquire();
        stats = pool.GetStats();
        Assert.Equal(4, stats.TotalAcquired);
    }
    
    [Fact]
    public void FifoOrder_AcquireReturnsObjectsInReleaseOrder()
    {
        // 关闭分片以在单分片内确定性验证 FIFO（多分片下 FIFO 仅保证分片内顺序）
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithEnableSharding(false)
            .WithMinSize(5)
            .WithMaxSize(5)
            .Build();

        // 借出全部预暖对象并保留引用顺序
        var acquired = new TestObject[5];
        for (var i = 0; i < acquired.Length; i++)
            acquired[i] = pool.Acquire();

        // 按借出顺序归还，形成 FIFO 空闲队列（分片空闲链表 Add 尾插）
        foreach (var o in acquired)
            pool.Release(o);

        // 再次借出应保持归还顺序（TryTake 头取，先入先出）
        for (var i = 0; i < acquired.Length; i++)
        {
            var o = pool.Acquire();
            Assert.Same(acquired[i], o);
        }
    }

    [Fact]
    public void TrackedRegistry_ConservesObjectsAcrossBorrowReleaseCycles()
    {
        // T09 回归守卫：登记表按分片拆分后不得产生孤儿登记项。
        // 纯借还循环（驱逐/校验/自动扩缩容全关）后应满足：
        //   1) CurrentSize（各分片登记表 TrackedCount 之和）== PooledCount（各分片空闲数之和）
        //      —— 即「登记总数 == 空闲数」，无借出残留、无孤儿登记；
        //   2) TotalCreated 不增长 —— 纯借还绝不新建对象；
        //   3) 归还的对象能再次按原样借出（Release 反查逐分片探测路径正确）。
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

        // 末轮抽验：任取一个已归还对象仍可正常借出（登记项 round-trip 有效）
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

        // Assert：累计借出次数 = 线程数 × 每个线程借出次数
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
        Assert.True(obj.IsReset); // 验证Reset钩子被调用
    }

    [Fact]
    public void Release_NullObject_ShouldNotThrow()
    {
        // Arrange
        using var pool = new HayatePoolBuilder<TestObject>().Build();

        // Act & Assert
        pool.Release(null);
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
        Assert.Equal(5, pool.GetStats().PooledCount); // 池大小不变
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
                .WithMaxSize(50) // Min > Max，无效配置
                .Build();
        });
    }
}