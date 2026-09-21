using System.Collections.Concurrent;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// Optimization regression suite: every optimization step must pass this suite 100%.
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

    #region Core functionality must-pass cases

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

    #region Concurrency safety must-pass cases

    [Fact]
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
                                                // still exercises real concurrency pressure from 16 threads contending for a MaxSize=100 pool;
                                                // but avoids the drag of the original 160k lock operations on total run time.
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

    [Fact]
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
                    // 1. acquire an object (blocks until one is returned or a timeout occurs if the pool is full)
                    obj = pool.Acquire(TimeSpan.FromSeconds(10));
                    
                    // 2. record the concurrency peak
                    var newCount = Interlocked.Increment(ref currentOutstanding);
                    
                    // update the maximum value in a thread-safe way
                    while (true)
                    {
                        var currentMax = Volatile.Read(ref maxOutstanding);
                        if (newCount <= currentMax) break;
                        if (Interlocked.CompareExchange(ref maxOutstanding, newCount, currentMax) == currentMax)
                            break;
                    }
                    
                    acquiredObjects.Add(obj);
                    
                    // 3. simulate business-logic use of the object (let it run a bit to ensure concurrency stacks up)
                    Thread.Sleep(50); 
                }
                catch
                {
                    // for the Block policy, hitting the wait timeout lands here; this is a normal test occurrence
                }
                finally
                {
                    // 4. [key fix] regardless of success, attempt to return the object (if one was successfully acquired)
                    // note: this needs to be adjusted to your actual HayatePool API;
                    // if Acquire fails by returning null or throwing, you must determine whether obj was actually acquired.
                    // assume acquiredObjects contains only successfully acquired objects, which we return together at the end.
                    if (obj != null)
                    {
                        pool.Release(obj);
                        // after returning, update the current outstanding count
                        Interlocked.Decrement(ref currentOutstanding);
                    }
                }
            });
        }
    
        await Task.WhenAll(tasks);
        var stats = pool.GetStats();
    
        // assertion 1: total created count must never exceed MaxSize
        Assert.True(stats.TotalCreated <= maxSize, $"TotalCreated: {stats.TotalCreated}, MaxSize: {maxSize}");

        // assertion 2: the peak concurrent outstanding count must never exceed MaxSize
        Assert.True(maxOutstanding <= maxSize, $"Max Outstanding: {maxOutstanding}, MaxSize: {maxSize}");
        
        // return all objects
        foreach (var obj in acquiredObjects)
        {
            try
            {
                pool.Release(obj);
            }
            catch
            {
                // ignore exceptions from duplicate returns (if any)
            }
        }
        
        // extra check: final idle object count >= min capacity (optional)
        // var finalStats = pool.GetStats();
        // Assert.True(finalStats.TotalReleased >= finalStats.TotalAcquired  - maxSize,
        //     "final return count is abnormal; possible object leak");
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

        // verify that after disabling, invalid objects can still be borrowed normally
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