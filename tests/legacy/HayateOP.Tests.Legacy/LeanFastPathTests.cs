using System.Collections.Concurrent;
using System.Diagnostics;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// Covers the lean (wrapper-free) fast path: configuration normalization, storage semantics,
/// capacity enforcement, reject-policy behaviour, the async path and the diagnostics surface.
/// </summary>
public class LeanFastPathTests
{
    private class TestObject
    {
        public int Data { get; set; }
    }

    private static HayatePoolBuilder<TestObject> Lean(int min, int max)
    {
        return new HayatePoolBuilder<TestObject>()
            .WithLean()
            .WithMinSize(min)
            .WithMaxSize(max);
    }

    // ── Configuration ────────────────────────────────────────

    [Fact]
    public void Lean_ApplyFeatureSwitches_ShouldDisableEveryConflictingFeature()
    {
        // Arrange: an explicitly contradictory configuration — lean wins, the knobs are normalized away
        var options = new HayatePoolOptions
        {
            EnableLean = true,
            EnableSharding = true,
            ShardCount = 8,
            EnableAutoScaling = true,
            EnableValidation = true,
            ValidateOnBorrow = true,
            ValidateOnReturn = true,
            ValidateWhileIdle = true,
            EnableEviction = true,
            EnableGenerationOptimization = true,
            EnableLeakDetection = true,
            EnableMetrics = true,
            EnableAllocationTracking = true,
            WarnAtRatio = 0.8,
            CriticalAtRatio = 0.95,
            ShardAffinityMode = HayateShardAffinityMode.Thread
        };

        // Act
        options.ApplyFeatureSwitches();

        // Assert
        Assert.False(options.EnableSharding);
        Assert.Equal(1, options.ShardCount);
        Assert.False(options.EnableAutoScaling);
        Assert.False(options.EnableValidation);
        Assert.False(options.ValidateOnBorrow);
        Assert.False(options.ValidateOnReturn);
        Assert.False(options.ValidateWhileIdle);
        Assert.False(options.EnableEviction);
        Assert.False(options.EnableGenerationOptimization);
        Assert.False(options.EnableLeakDetection);
        Assert.False(options.EnableMetrics);
        Assert.False(options.EnableAllocationTracking);
        Assert.Equal(0, options.WarnAtRatio);
        Assert.Equal(0, options.CriticalAtRatio);
        Assert.Equal(HayateShardAffinityMode.None, options.ShardAffinityMode);
        Assert.True(options.IsValid());
    }

    [Fact]
    public void Lean_BuilderCallOrder_ShouldNotMatter()
    {
        // WithLean() first, conflicting toggles afterwards: the mode must still win, because
        // normalization runs at build time rather than at each setter.
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithLean()
            .WithMinSize(2)
            .WithMaxSize(4)
            .WithEnableMetrics(true)
            .WithEnableValidation(true)
            .WithEnableEviction(true)
            .Build();

        var options = pool.GetOptions();
        Assert.True(options.EnableLean);
        Assert.False(options.EnableMetrics);
        Assert.False(options.EnableValidation);
        Assert.False(options.EnableEviction);
        Assert.Equal(1, options.ShardCount);
    }

    [Fact]
    public void Lean_ShouldBeOptIn()
    {
        var options = new HayatePoolOptions();
        Assert.False(options.EnableLean);

        using var pool = new HayatePoolBuilder<TestObject>().Build();
        Assert.False(pool.GetOptions().EnableLean);
    }

    // ── Storage semantics ────────────────────────────────────

    [Fact]
    public void Lean_AcquireRelease_ShouldReuseTheSameInstance()
    {
        using var pool = Lean(1, 1).Build();

        var first = pool.Acquire();
        pool.Release(first);
        var second = pool.Acquire();

        Assert.NotNull(second);
        Assert.Same(first, second);
    }

    [Fact]
    public void Lean_MinPoolSize_ShouldPrewarmTheBuffer()
    {
        using var pool = Lean(5, 10).Build();

        Assert.Equal(5, pool.GetStats().PooledCount);
        Assert.Equal(5, pool.TakeSnapshot().PooledCount);
    }

    [Fact]
    public void Lean_MaxPoolSize_ShouldBeAHardCeiling()
    {
        using var pool = Lean(0, 3)
            .WithRejectPolicy(HayatePoolRejectPolicy.Abort)
            .Build();

        var a = pool.Acquire();
        var b = pool.Acquire();
        var c = pool.Acquire();

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.NotNull(c);
        Assert.NotSame(a, b);
        Assert.NotSame(b, c);

        // Three objects are outstanding and the ceiling is three: nothing left to create or hand out.
        Assert.Throws<InvalidOperationException>(() => pool.Acquire());
    }

    [Fact]
    public void Lean_EmptyPool_ShouldCreateOnDemandWithoutWaiting()
    {
        // An empty lean pool grows on demand instead of parking until a timeout elapses, which is
        // what makes the fast path non-blocking for MinPoolSize = 0 configurations.
        using var pool = Lean(0, 4)
            .WithRejectPolicy(HayatePoolRejectPolicy.BlockTimeout)
            .WithAcquireTimeout(TimeSpan.FromSeconds(5))
            .Build();

        var sw = Stopwatch.StartNew();
        var obj = pool.Acquire();
        sw.Stop();

        Assert.NotNull(obj);
        Assert.True(sw.ElapsedMilliseconds < 1000,
            $"Acquire on an empty lean pool should not wait, but took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void Lean_ReleaseOfUnknownInstance_ShouldNotCorruptThePool()
    {
        // Lean mode keeps no registry, so it cannot reject a foreign instance — the return is
        // accepted, exactly as the reference zero-wrapper pool does. What matters is that the pool
        // stays usable and consistent afterwards.
        using var pool = Lean(1, 4).Build();

        pool.Release(new TestObject());

        var obj = pool.Acquire();
        Assert.NotNull(obj);
        pool.Release(obj);

        var stats = pool.GetStats();
        Assert.True(stats.CurrentSize >= 1);
        Assert.True(stats.PooledCount >= 1);
    }

    [Fact]
    public void Lean_ReleaseNull_ShouldBeIgnored()
    {
        using var pool = Lean(1, 2).Build();

        pool.Release(null);

        Assert.Equal(1, pool.GetStats().PooledCount);
    }

    // ── Reject policies ──────────────────────────────────────

    [Fact]
    public void Lean_BlockPolicy_ShouldWaitForAReturn()
    {
        using var pool = Lean(1, 1)
            .WithRejectPolicy(HayatePoolRejectPolicy.Block)
            .Build();

        var first = pool.Acquire();

        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            pool.Release(first);
        });

        var sw = Stopwatch.StartNew();
        var second = pool.Acquire();
        sw.Stop();

        Assert.Same(first, second);
        Assert.True(sw.ElapsedMilliseconds >= 50,
            $"Block policy returned after {sw.ElapsedMilliseconds}ms, before the object was released");
    }

    [Fact]
    public void Lean_BlockTimeoutPolicy_ShouldThrowAfterTheTimeout()
    {
        using var pool = Lean(1, 1)
            .WithRejectPolicy(HayatePoolRejectPolicy.BlockTimeout)
            .WithAcquireTimeout(TimeSpan.FromMilliseconds(150))
            .Build();

        var held = pool.Acquire();

        var sw = Stopwatch.StartNew();
        Assert.Throws<TimeoutException>(() => pool.Acquire());
        sw.Stop();

        Assert.NotNull(held);
        Assert.True(sw.ElapsedMilliseconds >= 100,
            $"BlockTimeout threw after only {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void Lean_CreateNewPolicy_ShouldDegradeToTheTimeoutBehaviour()
    {
        // CreateNew promises a fresh object once the timeout elapses, but at MaxPoolSize the pool is
        // at its hard ceiling and cannot deliver one; the policy therefore degrades to BlockTimeout.
        using var pool = Lean(1, 1)
            .WithRejectPolicy(HayatePoolRejectPolicy.CreateNew)
            .WithAcquireTimeout(TimeSpan.FromMilliseconds(150))
            .Build();

        var held = pool.Acquire();

        Assert.Throws<TimeoutException>(() => pool.Acquire());
        Assert.NotNull(held);
    }

    // ── Async path ───────────────────────────────────────────

    [Fact]
    public async Task Lean_AcquireAsync_ShouldReuseTheSameInstance()
    {
        using var pool = Lean(1, 1).Build();

        var first = await pool.AcquireAsync();
        pool.Release(first);
        var second = await pool.AcquireAsync();

        Assert.NotNull(second);
        Assert.Same(first, second);
    }

    [Fact]
    public async Task Lean_AcquireAsync_ShouldRespectCancellation()
    {
        using var pool = Lean(1, 1)
            .WithRejectPolicy(HayatePoolRejectPolicy.Block)
            .Build();

        var held = pool.Acquire();

        using var cts = new CancellationTokenSource(150);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pool.AcquireAsync(cts.Token));

        Assert.NotNull(held);
    }

    [Fact]
    public async Task Lean_AcquireAsync_ShouldShareStorageWithTheSynchronousPath()
    {
        using var pool = Lean(2, 4).Build();

        var first = pool.Acquire();
        pool.Release(first);

        var second = await pool.AcquireAsync();
        Assert.Same(first, second);

        pool.Release(second);
        Assert.Equal(2, pool.GetStats().PooledCount);
    }

    // ── Lifecycle and diagnostics ────────────────────────────

    [Fact]
    public void Lean_Clear_ShouldDestroyIdleObjectsOnly()
    {
        using var pool = Lean(4, 8).Build();

        var borrowed = pool.Acquire();
        Assert.Equal(3, pool.GetStats().PooledCount);

        pool.Clear();
        Assert.Equal(0, pool.GetStats().PooledCount);

        // The borrowed instance is still accounted for and returns to the emptied buffer.
        pool.Release(borrowed);
        Assert.Equal(1, pool.GetStats().PooledCount);
        Assert.Same(borrowed, pool.Acquire());
    }

    [Fact]
    public void Lean_GetStats_ShouldReportInstantaneousStateWithoutCumulativeCounters()
    {
        using var pool = Lean(3, 6).Build();

        var obj = pool.Acquire();
        var stats = pool.GetStats();

        Assert.Equal(2, stats.PooledCount);
        Assert.Equal(2, stats.AvailableSlots);
        Assert.Equal(3, stats.CurrentSize);
        Assert.Equal(3, stats.MinSize);
        Assert.False(stats.AllocationTrackingEnabled);
        Assert.Equal(0, stats.TotalAcquired);
        Assert.Equal(0, stats.TotalCreated);
        Assert.Equal(0, stats.TotalReleased);
        Assert.Equal(0, stats.TotalMissed);

        pool.Release(obj);
    }

    [Fact]
    public void Lean_TakeSnapshot_ShouldDeriveBorrowedCountAndOmitObjectDetails()
    {
        using var pool = Lean(3, 6).Build();

        var obj = pool.Acquire();
        var snapshot = pool.TakeSnapshot();

        Assert.Equal(2, snapshot.PooledCount);
        Assert.Equal(1, snapshot.BorrowedCount);
        Assert.Empty(snapshot.ObjectDetails);
        Assert.Empty(snapshot.LeakTraces);

        pool.Release(obj);
    }

    [Fact]
    public void Lean_Evict_ShouldThrowInsteadOfSilentlyDoingNothing()
    {
        using var pool = Lean(2, 4).Build();

        Assert.Throws<InvalidOperationException>(() => pool.Evict(HayateEvictReason.Idle));
        Assert.Throws<InvalidOperationException>(() => pool.Evict(HayateEvictReason.Touched));
        Assert.Throws<InvalidOperationException>(() => pool.Evict(HayateEvictReason.Expired));
    }

    [Fact]
    public void Lean_ReloadConfig_ShouldRejectAResizeButAcceptRuntimeSettings()
    {
        using var pool = Lean(2, 4).Build();

        Assert.Throws<InvalidOperationException>(() => pool.ReloadConfig(o => o.MaxPoolSize = 8));

        // The rejected change is rolled back, so the pool keeps working against its real ceiling.
        Assert.Equal(4, pool.GetOptions().MaxPoolSize);
        var obj = pool.Acquire();
        pool.Release(obj);

        pool.ReloadConfig(o => o.DefaultAcquireTimeout = TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(2), pool.GetOptions().DefaultAcquireTimeout);
    }

    [Fact]
    public void Lean_WaitForWarmup_ShouldStillPrewarmBeforeServingBorrows()
    {
        using var pool = Lean(4, 8)
            .WithWaitForWarmup(true)
            .Build();

        var obj = pool.Acquire();
        Assert.NotNull(obj);

        // Three pre-warmed objects remain besides the one just borrowed.
        Assert.Equal(3, pool.GetStats().PooledCount);
    }

    // ── Concurrency ──────────────────────────────────────────

    [Fact]
    public void Lean_ConcurrentBorrowReturn_ShouldNeverHandOutTheSameInstanceTwice()
    {
        const int threads = 8;
        const int iterations = 2000;

        using var pool = Lean(4, 8)
            .WithRejectPolicy(HayatePoolRejectPolicy.Block)
            .Build();

        // Reference-based (TestObject does not override Equals): a successful TryAdd proves the
        // instance was idle when it was handed out, so a failure means two threads hold it at once.
        var inUse = new ConcurrentDictionary<TestObject, int>();
        var doubleBorrows = 0;

        Parallel.For(0, threads, _ =>
        {
            for (var i = 0; i < iterations; i++)
            {
                var obj = pool.Acquire();
                if (!inUse.TryAdd(obj, 1)) Interlocked.Increment(ref doubleBorrows);
                inUse.TryRemove(obj, out _);
                pool.Release(obj);
            }
        });

        Assert.Equal(0, doubleBorrows);

        var stats = pool.GetStats();
        Assert.True(stats.PooledCount >= 1, $"Expected the buffer to hold objects after the run, but it holds {stats.PooledCount}");
        Assert.True(stats.CurrentSize <= 8, $"The pool grew past MaxPoolSize: {stats.CurrentSize}");
    }

    [Fact]
    public void Lean_ConcurrentGrowBeyondCapacity_ShouldWaitForReturns()
    {
        // 8 threads against a ceiling of 2: most borrows must wait for a return rather than create.
        using var pool = Lean(0, 2)
            .WithRejectPolicy(HayatePoolRejectPolicy.Block)
            .Build();

        var inUse = new ConcurrentDictionary<TestObject, int>();
        var doubleBorrows = 0;

        Parallel.For(0, 8, _ =>
        {
            for (var i = 0; i < 200; i++)
            {
                var obj = pool.Acquire();
                if (!inUse.TryAdd(obj, 1)) Interlocked.Increment(ref doubleBorrows);
                inUse.TryRemove(obj, out _);
                pool.Release(obj);
            }
        });

        Assert.Equal(0, doubleBorrows);
        Assert.True(pool.GetStats().CurrentSize <= 2);
    }
}
