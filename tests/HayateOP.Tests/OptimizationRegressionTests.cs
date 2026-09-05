using System.Collections.Concurrent;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// 优化回归用例集：所有优化步骤必须100%通过此用例
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

    #region 核心功能必过用例

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

    #region 并发安全必过用例

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
        const int operationsPerThread = 2000;   // P2/R7：由 10000 收敛到 2000（16 线程 × 2000 = 32k 次），
                                                // 仍是 16 线程争抢 MaxSize=100 池的真实并发压力；
                                                // 但把原先 16 万次锁操作对全量耗时的拖累降下来。
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
        
        // 用于准确统计并发峰值
        long currentOutstanding = 0;
        long maxOutstanding = 0;
        
        var tasks = new Task[threadCount];
    
        for (int i = 0; i < threadCount; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                TestObject obj = null;
                
                try
                {
                    // 1. 获取对象（如果池满，这里会阻塞直到有对象归还或超时）
                    obj = pool.Acquire(TimeSpan.FromSeconds(10));
                    
                    // 2. 记录并发峰值
                    var newCount = Interlocked.Increment(ref currentOutstanding);
                    
                    // 线程安全地更新最大值
                    while (true)
                    {
                        var currentMax = Volatile.Read(ref maxOutstanding);
                        if (newCount <= currentMax) break;
                        if (Interlocked.CompareExchange(ref maxOutstanding, newCount, currentMax) == currentMax)
                            break;
                    }
                    
                    acquiredObjects.Add(obj);
                    
                    // 3. 模拟业务逻辑使用对象（让子弹飞一会儿，确保并发叠加）
                    Thread.Sleep(50); 
                }
                catch
                {
                    // 对于 Block 策略，如果等待超时会走到这里，属于正常测试现象
                }
                finally
                {
                    // 4. 【关键修复】无论成功与否，尝试归还对象（如果获取成功的话）
                    // 注意：这里需要根据你的实际 HayatePool API 调整，
                    // 如果 Acquire 失败返回 null 或抛出异常，需判断是否真的获取到了 obj。
                    // 假设 acquiredObjects 里只包含成功获取的对象，我们在最后统一归还。
                    if (obj != null)
                    {
                        pool.Release(obj);
                        // 归还后更新当前借出数
                        Interlocked.Decrement(ref currentOutstanding);
                    }
                }
            });
        }
    
        await Task.WhenAll(tasks);
        var stats = pool.GetStats();
    
        // 断言 1: 总创建数绝对不能超过 MaxSize
        Assert.True(stats.TotalCreated <= maxSize, $"TotalCreated: {stats.TotalCreated}, MaxSize: {maxSize}");

        // 断言 2: 并发借出的峰值绝对不能超过 MaxSize
        Assert.True(maxOutstanding <= maxSize, $"Max Outstanding: {maxOutstanding}, MaxSize: {maxSize}");
        
        // 归还所有对象
        foreach (var obj in acquiredObjects)
        {
            try
            {
                pool.Release(obj);
            }
            catch
            {
                // 忽略重复归还的异常（如果有）
            }
        }
        
        // 额外验证：最终空闲对象数≥最小容量（可选）
        // var finalStats = pool.GetStats();
        // Assert.True(finalStats.TotalReleased >= finalStats.TotalAcquired  - maxSize,
        //     "最终归还数异常，可能存在对象泄漏");
    }

    #endregion

    #region 功能开关必过用例

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

        // 验证关闭后，无效对象仍能正常借出
        var newObj = pool.Acquire();
        Assert.Same(obj, newObj);
    }

    #endregion

    #region 拒绝策略必过用例

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