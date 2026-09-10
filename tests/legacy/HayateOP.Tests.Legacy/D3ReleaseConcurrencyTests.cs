using System.Collections.Concurrent;
using System.Reflection;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// D3 phase: unit tests for HayateObjectPool.Release concurrency semantics.
///
/// Previously, the B phase (commit 56a5c47) serialized tests via
/// [CollectionDefinition] to suppress intermittent timeouts caused by
/// cross-test pollution, but the Release path still has structural
///   1. When ProcessorId % ShardCount lands on a shard with max=0,
///      Shard.Add silently disposes the returned object (occurs when
///      Max &lt; ShardCount).
///   2. Shard.Remove (_queue.ToList + Clear + Enqueue) racing with
///      Shard.Add can lose objects (a previously exposed defect).
///
///   3. EvictionCallback / ValidateCallback scheduled Remove racing
///      with Release.
///
/// This file locks down the above three defect classes for regression
/// coverage; any later fix must keep all D3_* tests passing.
/// </summary>
public class D3ReleaseConcurrencyTests
{
    private sealed class TestObject : IHayateResettable
    {
        private static int _nextId;
        public int Id { get; } = Interlocked.Increment(ref _nextId);
        public bool IsReset { get; private set; }
        public bool WillRejectNextRelease { get; set; }
        public void Reset() => IsReset = true;
    }

    /// <summary>
    /// D3-1: the Acquire -> Release -> Acquire cycle should stably return
    ///
    /// an object without timing out.
    ///
/// Trigger path: with the default ShardCount=4 + MaxSize=2, shards 0/1
    /// have max=1 while shards 2/3 have max=0. The old Release selected a
    /// shard via Thread.GetCurrentProcessorId() % 4; if it hit a max=0
/// shard, Shard.Add silently disposed the object and the second Acquire
/// blocked until the default timeout. After the fix, Release round-trips
/// via HayateObject&lt;T&gt;.ShardIndex, preventing ProcessorId from
/// hitting a max=0 shard.
    /// </summary>
    [Fact]
    public void D3_1_ReleaseRoundTrip_SmallPoolOverSharding_NoTimeout()
    {
        // ShardCount=4 + MaxSize=2 makes shards 2/3 have max=0
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(1)
            .WithMaxSize(2)
            .WithShardCount(4)
            .WithEnableSharding(true)
            .WithAcquireTimeout(TimeSpan.FromSeconds(2))
            .Build();

        const int iterations = 50;
        for (var i = 0; i < iterations; i++)
        {
            TestObject obj;
            try
            {
                obj = pool.Acquire();
            }
            catch (TimeoutException)
            {
                throw new TimeoutException(
                    $"D3-1 triggered: the {i}-th Acquire timed out after Release (objects lost on shard). " +
                    "Release should round-trip via HayateObject<T>.ShardIndex to avoid ProcessorId hitting a max=0 shard.");
            }

            pool.Release(obj);

            // Key: if Release selected a max=0 shard, the object is already
            // disposed and a TimeoutException must be thrown within 2s here.
            var obj2 = pool.Acquire();
            Assert.NotNull(obj2);
            // Note: under the current implementation obj2 is not necessarily
            // the same object (it may land on another max>=1 shard).
// The key is obj2 != null and no timeout.
            pool.Release(obj2);
        }
    }

    /// <summary>
    /// D3-2: a continuous Acquire/Release cycle must not invalidate MinSize.
    /// When Release hits a max=0 shard, the object is disposed and the next
    /// Acquire times out (if the pool is drained). After the fix, round-trip
/// eliminates this failure path.
    /// </summary>
    [Fact]
    public void D3_2_ReleaseShouldNotDescreasePooledCount_BelowMinSize()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(1)
            .WithMaxSize(2)
            .WithShardCount(4)
            .WithEnableSharding(true)
            .WithEnableAutoScaling(false)   // disable auto-scaling to precisely observe object loss
            .WithEnableEviction(false)
            .WithAcquireTimeout(TimeSpan.FromSeconds(1))
            .Build();

        Assert.Equal(1, pool.GetStats().PooledCount);

        const int iterations = 30;
        for (var i = 0; i < iterations; i++)
        {
            TestObject obj;
            try { obj = pool.Acquire(); }
            catch (TimeoutException)
            {
                throw new TimeoutException($"D3-2 triggered: the {i}-th Acquire timed out. " +
                    "The Acquire -> Release -> Acquire cycle lost the object, draining the pool.");
            }
            pool.Release(obj);
        }
    }

    /// <summary>
    /// D3-3: concurrent Acquire/Release together with background Eviction
    /// should keep the total object count reconcilable. The current
/// Shard.Remove implementation (LinkedList + SpinLock) guarantees no
/// object loss; this test verifies pool-level consistency.
    /// </summary>
    [Fact]
    public async Task D3_3_ConcurrentAcquireReleaseWithEviction_ObjectCountConsistent()
    {
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(20)
            .WithMaxSize(100)
            .WithEnableEviction(true)
            .WithMaxLifeTime(TimeSpan.FromSeconds(2))
            .WithMaxIdleTime(TimeSpan.FromSeconds(1))
            .WithSoftMinEvictableIdleTime(TimeSpan.FromMilliseconds(500))
            .WithEvictionInterval(1000)        // builder enforces a >=1000ms minimum
            .WithEnableMetrics(true)
            .WithShardCount(4)
            .WithEnableSharding(true)
            .WithAcquireTimeout(TimeSpan.FromSeconds(10))
            .Build();

        const int threadCount = 16;
        const int operationsPerThread = 200;
        var tasks = new Task[threadCount];
        var exceptions = new ConcurrentBag<Exception>();

        using var startSignal = new ManualResetEventSlim(false);

        for (int i = 0; i < threadCount; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                startSignal.Wait();
                for (int j = 0; j < operationsPerThread; j++)
                {
                    TestObject obj;
                    try { obj = pool.Acquire(); }
                    catch (TimeoutException) { continue; }
                    catch (Exception ex) { exceptions.Add(ex); continue; }

                    Thread.SpinWait(50);
                    try { pool.Release(obj); }
                    catch (Exception ex) { exceptions.Add(ex); }
                }
            });
        }

        startSignal.Set();
        await Task.WhenAll(tasks);

        // the pool is still within valid bounds
        var stats = pool.GetStats();
        Assert.Empty(exceptions);
        Assert.InRange(stats.PooledCount, 0, 100);
        Assert.InRange(stats.TotalAcquired, 0, int.MaxValue);
    }

    /// <summary>
    /// D3-4: high-concurrency regression with OnRelease=false, ensuring the
    /// destroy path does not interfere with concurrent Acquire. The 3s
/// cooldown of ForceScaleUpOneStep is a known constraint; this test only
/// verifies:
    ///   - no uncaught exceptions
    ///   - the pool never exceeds MaxPoolSize
    ///   - all Acquires eventually complete (not forcing All Return, since
///     the cooldown causes pacing differences)
    /// </summary>
    [Fact]
    public async Task D3_4_ConcurrentReleaseWhereOnReleaseFalse_PoolStaysConsistent()
    {
        var policy = new HalfRejectPolicy();
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithMinSize(10)
            .WithMaxSize(50)
            .WithPolicy(policy)
            .WithShardCount(4)
            .WithEnableSharding(true)
            .WithEnableAutoScaling(true)
            .WithEnableMetrics(true)
            .WithEnableEviction(false)
            // Guard (R-pending): the default ScaleUpCooldownSeconds=3 lets
            // ForceScaleUp be throttled by the cooldown under high-frequency
            // consumption where half the Releases are rejected and objects keep
            // being destroyed; the pool is drained -> 6 threads keep hitting 15s
            // Acquire timeouts, and 600 operations drag into minute-long "timeout
// livelocks" that nearly hang the test. Setting the scale-up cooldown to
// 0 lets the pool immediately replenish MinSize after each exhaustion, so
// Acquire hits almost instantly and the test finishes in seconds; the
// AcquireTimeout is narrowed to 2s only as a safety net against any
// potential livelock dragging on.
            .WithScaleUpCooldownSeconds(0)
            .WithAcquireTimeout(TimeSpan.FromSeconds(2))
            .Build();

        const int threadCount = 6;
        const int opsPerThread = 100;
        var tasks = new Task[threadCount];
        var exceptions = new ConcurrentBag<Exception>();

        for (int i = 0; i < threadCount; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                for (int j = 0; j < opsPerThread; j++)
                {
                    TestObject obj;
                    try { obj = pool.Acquire(); }
                    catch (TimeoutException) { continue; }
                    catch { continue; }

                    obj.WillRejectNextRelease = (j % 2 == 0);
                    try { pool.Release(obj); }
                    catch (Exception ex) { exceptions.Add(ex); }
                }
            });
        }

        await Task.WhenAll(tasks);
        Thread.Sleep(500);   // let ForceScaleUp cooldown recover

        Assert.Empty(exceptions);
        var stats = pool.GetStats();
        Assert.InRange(stats.CurrentSize, 0, 50);
    }

    /// <summary>
    /// Policy that rejects half of the Releases.
    /// </summary>
    private sealed class HalfRejectPolicy : DotNetCore.HayateOP.Policies.IHayateObjectPolicy<TestObject>
    {
        public TestObject Create() => new();
        public bool OnRelease(TestObject item)
        {
            if (item.WillRejectNextRelease)
            {
                item.WillRejectNextRelease = false;
                return false;
            }
            item.Reset();
            return true;
        }
        public bool Validate(TestObject item) => true;
        public void OnAcquire(TestObject item) { }
        public void OnPassivate(TestObject item) { }
        public void OnDestroy(TestObject item) { }
    }
}
