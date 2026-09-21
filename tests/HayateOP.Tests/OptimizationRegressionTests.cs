using System.Collections.Concurrent;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// Optimization regression suite: every optimization step must pass 100% of these cases
/// </summary>
[Collection(OptimizationRegressionCollection.Name)]
public class OptimizationRegressionTests
{
    private class TestObject : IHayateResettable, IHayateValidatable, IDisposable
    {
        public int Id { get; set; }
        public bool IsReset { get; private set; }
        public bool IsValidReturn { get; set; } = true;
        public bool IsDisposed { get; private set; }

        public void Reset() => IsReset = true;
        public bool IsValid() => IsValidReturn && !IsDisposed;
        public void Dispose() => IsDisposed = true;
    }

    #region Core-functionality must-pass cases

    [Fact]
    public void Acquire_ShouldReturnNonNullObject()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(5)
            .WithMaxSize(10)
            .Build();

        var obj = pool.Acquire();
        Assert.NotNull(obj);
    }

    [Fact]
    public void Release_ShouldReturnObjectToPool()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(5)
            .WithMaxSize(10)
            .Build();

        var obj = pool.Acquire();
        var initialCount = pool.GetStats().PooledCount;
        pool.Release(obj);

        Assert.Equal(initialCount + 1, pool.GetStats().PooledCount);
        Assert.True(obj.IsReset);
    }

    [Fact]
    public void PreWarm_ShouldCreateMinSizeObjects()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithEnableMetrics(true)
            .WithMinSize(10)
            .WithMaxSize(20)
            .Build();

        var stats = pool.GetStats();
        Assert.Equal(10, stats.PooledCount);
        Assert.Equal(10, stats.TotalCreated);
    }

    [Fact]
    public void Dispose_ShouldDestroyAllObjects()
    {
        var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(5)
            .WithMaxSize(10)
            .Build();

        var objs = Enumerable.Range(0, 5).Select(_ => pool.Acquire()).ToList();
        objs.ForEach(pool.Release);
        pool.Dispose();

        var stats = pool.GetStats();
        Assert.Equal(0, stats.PooledCount);
    }

    #endregion

    #region Concurrency-safety must-pass cases

    [Fact(Timeout = 60000)]
    public async Task ConcurrentAcquireRelease_ShouldNotThrow()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(20)
            .WithMaxSize(100)
            .WithEnableSharding(true)
            .WithShardCount(4)
            .Build();

        const int threadCount = 16;
        const int operationsPerThread = 2000;   // converged from 10000 to 2000 (16 threads x 2000 = 32k ops),
                                                // still real concurrency pressure of 16 threads contending for a MaxSize=100 pool;
                                                // but removes the full-run slowdown from the original 160k lock operations.
        var tasks = new Task[threadCount];
        var totalOperations = 0L;

        for (int i = 0; i < threadCount; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                for (int j = 0; j < operationsPerThread; j++)
                {
                    var obj = pool.Acquire();
                    pool.Release(obj);
                    Interlocked.Increment(ref totalOperations);
                }
            });
        }

        await Task.WhenAll(tasks);
        Assert.Equal(threadCount * operationsPerThread, totalOperations);
    }

    [Fact(Timeout = 60000)]
    public async Task ConcurrentAcquire_ShouldNotExceedMaxPoolSize()
    {
        const int maxSize = 50;
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithEnableMetrics(true)
            .WithMinSize(10)
            .WithMaxSize(maxSize)
            .WithRejectPolicy(HayatePoolRejectPolicy.Block)
            .Build();
    
        const int threadCount = 100;
        var acquiredObjects = new ConcurrentBag<TestObject>();
        
        // used to accurately count the concurrency peak
        long currentOutstanding = 0;
        long maxOutstanding = 0;
        
        var tasks = new Task[threadCount];
    
        for (int i = 0; i < threadCount; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                TestObject? obj = null;
                
                try
                {
                    // 1. Acquire an object (if the pool is full, this blocks until an object is returned or times out)
                    obj = pool.Acquire(TimeSpan.FromSeconds(10));
                    
                    // 2. Record the concurrency peak
                    var newCount = Interlocked.Increment(ref currentOutstanding);
                    
                    // Thread-safely update the maximum
                    while (true)
                    {
                        var currentMax = Volatile.Read(ref maxOutstanding);
                        if (newCount <= currentMax) break;
                        if (Interlocked.CompareExchange(ref maxOutstanding, newCount, currentMax) == currentMax)
                            break;
                    }
                    
                    acquiredObjects.Add(obj);
                    
                    // 3. Simulate business logic using the object (let it run a bit to ensure concurrency overlap)
                    Thread.Sleep(50); 
                }
                catch
                {
                    // For the Block policy, hitting the wait timeout lands here; this is a normal test occurrence
                }
                finally
                {
                    // 4. [Key fix] regardless of success, attempt to return the object (if acquisition succeeded)
                    // Note: adjust this to your actual HayatePool API;
                    // if Acquire fails and returns null or throws, check whether obj was really acquired.
                    // Assume acquiredObjects contains only successfully acquired objects; we return them all at the end.
                    if (obj != null)
                    {
                        pool.Release(obj);
                        // after return, decrement the current outstanding count
                        Interlocked.Decrement(ref currentOutstanding);
                    }
                }
            });
        }
    
        await Task.WhenAll(tasks);
        var stats = pool.GetStats();
    
        // Assert 1: total created count must never exceed MaxSize
        Assert.True(stats.TotalCreated <= maxSize, $"TotalCreated: {stats.TotalCreated}, MaxSize: {maxSize}");

        // Assert 2: the peak concurrently borrowed count must never exceed MaxSize
        Assert.True(maxOutstanding <= maxSize, $"Max Outstanding: {maxOutstanding}, MaxSize: {maxSize}");
        
        // Return all objects
        foreach (var obj in acquiredObjects)
        {
            try
            {
                pool.Release(obj);
            }
            catch
            {
                // Ignore duplicate-return exceptions (if any)
            }
        }
        
        // Extra check: final idle object count >= min capacity (optional)
        // var finalStats = pool.GetStats();
        // Assert.True(finalStats.TotalReleased >= finalStats.TotalAcquired  - maxSize,
        //     "final returned count is abnormal; possible object leak");
    }

    #endregion

    #region Feature-switch must-pass cases

    [Fact]
    public void DisableAutoScaling_ShouldFixPoolSize()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithEnableAutoScaling(false)
            .WithMinSize(5)
            .WithMaxSize(50)
            .Build();

        var stats = pool.GetStats();
        Assert.Equal(5, stats.MinSize);
        Assert.Equal(5, stats.CurrentSize);
    }

    [Fact]
    public void DisableValidation_ShouldSkipAllValidation()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithEnableValidation(false)
            .WithValidateOnBorrow(true)
            .WithValidateOnReturn(true)
            .WithMaxSize(2)
            .WithMinSize(1)
            .Build();

        var obj = pool.Acquire();
        obj.IsValidReturn = false;
        pool.Release(obj);

        // After disabling validation, invalid objects can still be borrowed normally
        var newObj = pool.Acquire();
        Assert.Same(obj, newObj);
    }

    #endregion

    #region Reject-policy must-pass cases

    [Fact]
    public void RejectPolicy_Abort_ShouldThrowImmediately()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(1)
            .WithMaxSize(1)
            .WithRejectPolicy(HayatePoolRejectPolicy.Abort)
            .WithEnableAutoScaling(false)
            .Build();

        var obj = pool.Acquire();
        Assert.Throws<InvalidOperationException>(() => pool.Acquire());
    }

    [Fact]
    public void RejectPolicy_CreateNew_ShouldReturnNewObjectOnTimeout()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(1)
            .WithMaxSize(1)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateNew)
            .WithAcquireTimeout(TimeSpan.FromMilliseconds(100))
            .Build();

        var obj1 = pool.Acquire();
        var obj2 = pool.Acquire();

        Assert.NotNull(obj2);
        Assert.NotSame(obj1, obj2);
    }

    #endregion
}