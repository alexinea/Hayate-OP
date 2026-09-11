using DotNetCore.HayateOP;
using DotNetCore.HayateOP.ObjectPoolCompat;
using Microsoft.Extensions.ObjectPool;
using System;
using System.Diagnostics;
using Xunit;

namespace DotNetCore.HayateOP.Tests.Compat;

/// <summary>
/// Acceptance: existing Microsoft.Extensions.ObjectPool callers switch to HayateOP with zero code changes.
/// <para>
/// The same consumer code runs against DefaultObjectPoolProvider (the MEOP baseline) and
/// HayateObjectPoolCompatProvider (the HayateOP backend), producing identical results:
/// lazy creation / reuse of the same instance / discard-and-recreate on Return=false.
/// </para>
/// </summary>
public class ObjectPoolCompatTests
{
    private sealed class CompatResource
    {
        public bool Broken { get; set; }
        public int Used { get; set; }
    }

    /// <summary>Counting policy: Return=false means the object is discarded (aligned with MEOP semantics).</summary>
    private sealed class CountingPolicy : PooledObjectPolicy<CompatResource>
    {
        public int Created;
        public int Returned;
        public override CompatResource Create()
        {
            Interlocked.Increment(ref Created);
            return new CompatResource();
        }
        public override bool Return(CompatResource obj)
        {
            Interlocked.Increment(ref Returned);
            return !obj.Broken;
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 1. MEOP baseline: the golden behavior of the same consumer code
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void DefaultObjectPoolProvider_ParityBaseline()
        => RunParityScenario(new DefaultObjectPoolProvider());

    // ─────────────────────────────────────────────────────────────
    // 2. HayateOP backend runs the same consumer code with identical behavior (zero code changes to switch)
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void HayateCompatProvider_MatchesMeopParity()
        => RunParityScenario(new HayateObjectPoolCompatProvider(new HayateCompatOptions
        {
            AcquireTimeout = TimeSpan.FromMilliseconds(50)   // this bound only applies to at-capacity requests; the scenario stays below capacity
        }));

    /// <summary>
    /// Shared consumer code: lazy creation -> return-and-reuse -> discard-and-rebuild on a broken object.
    /// Both the MEOP and HayateOP backends must yield the same result.
    /// </summary>
    private static void RunParityScenario(ObjectPoolProvider provider)
    {
        var policy = new CountingPolicy();
        ObjectPool<CompatResource> pool = provider.Create(policy);

        // Lazy creation: the first Get triggers a single Create
        var first = pool.Get();
        Assert.NotNull(first);
        Assert.Equal(1, policy.Created);

        // Return accepted -> the next Get reuses the same instance without creating a new one
        pool.Return(first);
        Assert.Equal(1, policy.Returned);
        var second = pool.Get();
        Assert.Same(first, second);
        Assert.Equal(1, policy.Created);

        // Broken object: the Return policy returns false -> discard; the next Get must create a new one
        second.Broken = true;
        pool.Return(second);
        Assert.Equal(2, policy.Returned);
        var third = pool.Get();
        Assert.NotSame(second, third);
        Assert.Equal(2, policy.Created);
        Assert.False(third.Broken);
    }

    // ─────────────────────────────────────────────────────────────
    // 3. Cold start is synchronous: a miss below capacity creates immediately instead of waiting out
    //    AcquireTimeout, matching MEOP's "create on a miss, never block" contract.
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void HayateCompatProvider_ColdStartCreatesSynchronously()
    {
        var provider = new HayateObjectPoolCompatProvider(new HayateCompatOptions
        {
            // Deliberately far above any plausible creation cost: if the first Get waited this timeout out,
            // the elapsed check below could not pass.
            AcquireTimeout = TimeSpan.FromSeconds(2)
        });
        var pool = provider.Create(new DefaultPooledObjectPolicy<CompatResource>());

        var sw = Stopwatch.StartNew();
        var obj = pool.Get();
        sw.Stop();

        Assert.NotNull(obj);
        Assert.True(sw.ElapsedMilliseconds < 500,
            $"cold Get took {sw.ElapsedMilliseconds}ms — an empty pool must create synchronously, not wait out AcquireTimeout(2000ms)");

        pool.Return(obj);
        pool.Get(); // Immediately reusable after return; no cold-start path involved
    }

    // ─────────────────────────────────────────────────────────────
    // 3b. Concurrent misses below capacity all succeed: each request creates instead of blocking on a
    //     return (the case a pure block-until-timeout policy cannot serve).
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void HayateCompatProvider_ConcurrentMissesBelowCapacity_AllServed()
    {
        const int requests = 4;

        var provider = new HayateObjectPoolCompatProvider(new HayateCompatOptions
        {
            MinSize = 0,
            MaxSize = 8,                                   // comfortably above the request count
            AcquireTimeout = TimeSpan.FromMilliseconds(100)
        });
        var policy = new CountingPolicy();
        var pool = provider.Create(policy);

        var results = new CompatResource[requests];
        using var barrier = new Barrier(requests);
        var sw = Stopwatch.StartNew();

        var tasks = new Task[requests];
        for (var i = 0; i < requests; i++)
        {
            var index = i;
            tasks[index] = Task.Run(() =>
            {
                barrier.SignalAndWait();
                results[index] = pool.Get();
            });
        }

        Task.WaitAll(tasks);
        sw.Stop();

        Assert.All(results, Assert.NotNull);
        Assert.Equal(requests, results.Distinct().Count());     // one object per request, no double lending
        Assert.Equal(requests, policy.Created);                 // every miss created its own object
        Assert.True(sw.ElapsedMilliseconds < 500,
            $"concurrent cold Gets took {sw.ElapsedMilliseconds}ms — below capacity each miss must create without waiting");
    }

    // ─────────────────────────────────────────────────────────────
    // 4. The adapter wraps an existing HayateOP pool directly (without going through the provider build path)
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void Adapter_WrapsExistingHayatePool()
    {
        using var hayate = new HayatePoolBuilder<CompatResource>()
            .WithPoolName("compat-direct")
            .WithMinSize(2)
            .WithMaxSize(5)
            .WithEnableAutoScaling(false)
            .WithEnableValidation(false)
            .WithEnableEviction(false)
            .WithEnableLeakDetection(false)
            .WithEnableMetrics(false)
            .Build();

        ObjectPool<CompatResource> pool = new HayateObjectPoolAdapter<CompatResource>(hayate);

        var a = pool.Get();
        pool.Return(a);
        var b = pool.Get();
        Assert.Same(a, b);   // Min=2 warm-up + no eviction -> same instance retrieved immediately after return

        // Underlying pool capabilities are passed through (GetStats is still available)
        Assert.True(hayate.GetStats().TotalAcquired >= 2);
    }

    // ─────────────────────────────────────────────────────────────
    // 5. MEOP default policy path: Create<T>() (the new() constraint -> DefaultPooledObjectPolicy)
    // ─────────────────────────────────────────────────────────────

    private sealed class NewableResource
    {
        public int Value { get; set; }
    }

    [Fact]
    public void HayateCompatProvider_DefaultPolicy_CreateT()
    {
        var provider = new HayateObjectPoolCompatProvider(new HayateCompatOptions
        {
            AcquireTimeout = TimeSpan.FromMilliseconds(50)
        });

        ObjectPool<NewableResource> pool = provider.Create<NewableResource>();

        var a = pool.Get();
        Assert.NotNull(a);
        a.Value = 42;
        pool.Return(a);
        var b = pool.Get();
        Assert.Same(a, b);
        Assert.Equal(42, b.Value);   // reused on return, state preserved (MEOP DefaultPooledObjectPolicy does not reset custom fields)
    }
}
