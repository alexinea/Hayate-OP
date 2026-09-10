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
            AcquireTimeout = TimeSpan.FromMilliseconds(50)   // the bounded cold-start latency keeps this test short
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
    // 3. Cold-start latency is bounded (CreateNew creates only after AcquireTimeout elapses; this guards that behavior)
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void HayateCompatProvider_ColdStartLatencyBounded()
    {
        var provider = new HayateObjectPoolCompatProvider(new HayateCompatOptions
        {
            AcquireTimeout = TimeSpan.FromMilliseconds(50)
        });
        var pool = provider.Create(new DefaultPooledObjectPolicy<CompatResource>());

        var sw = Stopwatch.StartNew();
        var obj = pool.Get();
        sw.Stop();

        Assert.NotNull(obj);
        Assert.True(sw.ElapsedMilliseconds < 2000,
            $"cold Get took {sw.ElapsedMilliseconds}ms — CreateNew should create right after AcquireTimeout(50ms)");

        pool.Return(obj);
        pool.Get(); // Immediately reusable after return; no longer triggers the cold-start path
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
