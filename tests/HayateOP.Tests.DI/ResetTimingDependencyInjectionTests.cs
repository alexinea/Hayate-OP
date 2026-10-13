using System;
using DotNetCore.HayateOP;
using DotNetCore.HayateOP.DependencyInjection;
using DotNetCore.HayateOP.Policies;
using Microsoft.Extensions.DependencyInjection;

namespace HayateOP.Tests.DI;

/// <summary>
/// G-20 (3.1): the reset-timing switches reach a pool the <b>container</b> builds, not only one the
/// fluent builder builds. The library's own object policy is not pinned onto a pool that has no policy
/// of its own, so the pool derives one from its options — and the switches travel in those options. A
/// policy the caller supplied is pinned, and the switches never move its reset.
/// </summary>
/// <remarks>
/// The three things that make the claim checkable: a pool asking for the borrow-side reset really runs it
/// (the discriminating case — a switch accepted and ignored would show a clean object), a policy the
/// caller registered is still the one that runs whether it was registered before or after the pool, and
/// the registration path B8 documented is unchanged.
/// </remarks>
public class ResetTimingDependencyInjectionTests
{
    /// <summary>The same observable the fluent-builder acceptance reads: how often the pool reset this
    /// object and what its borrower left in it. <see cref="FromCallerPolicy"/> says which policy built
    /// it, so a case can tell the caller's policy from the library's own.</summary>
    private sealed class ResettableBuffer : IHayateResettable
    {
        public int ResetCount;
        public int Payload;
        public bool FromCallerPolicy;

        public void Reset()
        {
            ResetCount++;
            Payload = 0;
        }
    }

    /// <summary>A caller's own policy. It resets in <c>OnRelease</c> and never on acquire, so if the
    /// switches reached it the object would be reset on the borrow side instead — which is how the case
    /// below detects a policy the fix wrongly replaced.</summary>
    private sealed class CallerPolicy : IHayateObjectPolicy<ResettableBuffer>
    {
        public ResettableBuffer Create() => new ResettableBuffer { FromCallerPolicy = true };

        public bool OnRelease(ResettableBuffer item)
        {
            item.Reset();
            return true;
        }

        public bool Validate(ResettableBuffer item) => true;
        public void OnAcquire(ResettableBuffer item) { }
        public void OnPassivate(ResettableBuffer item) { }
        public void OnDestroy(ResettableBuffer item) { }
    }

    /// <summary>The B8 factory form, so the case can assert it still wins over the options-derived
    /// default.</summary>
    private sealed class CallerPolicyFactory : IHayateObjectPolicyFactory<ResettableBuffer>
    {
        public IHayateObjectPolicy<ResettableBuffer> Create(string poolName) => new CallerPolicy();
    }

    /// <summary>
    /// A small single-shard pool that creates on demand and never retires anything, so the object a case
    /// borrows is the object it gets back. One shard because a per-shard capacity of
    /// <c>MaxPoolSize / ShardCount</c> would floor to zero with the default four, no auto-scaling so the
    /// ceiling stays put, no eviction so nothing is destroyed between the two halves of a cycle, and
    /// <c>CreateOnDemand</c> so an empty pool hands out a new object instead of making the case wait out a
    /// timeout for a return that will never come.
    /// </summary>
    private static void Deterministic(HayatePoolOptions options)
    {
        options.ShardCount = 1;
        options.MinPoolSize = 0;
        options.MaxPoolSize = 4;
        options.EnableAutoScaling = false;
        options.EnableEviction = false;
        options.RejectPolicy = HayatePoolRejectPolicy.CreateOnDemand;
    }

    /// <summary>The same pool with the reset moved to the borrow side — the configuration this item is
    /// about.</summary>
    private static void ResetOnBorrowSide(HayatePoolOptions options)
    {
        Deterministic(options);
        options.ResetOnReturn = false;
        options.ResetOnBorrow = true;
    }

    [Fact(Timeout = 30_000)]
    public void NamedPool_ShouldTakeTheBorrowSideResetFromItsOptions()
    {
        var services = new ServiceCollection();
        services.AddHayatePoolSupport().AddNamedPool<ResettableBuffer>("primary", ResetOnBorrowSide);

        var provider = services.BuildServiceProvider();
        var pool = provider.GetRequiredService<IHayateNamedPoolAccessor>().GetPool<ResettableBuffer>("primary");

        // GetOptions() promised the effective value before this item, and now the pool actually runs it.
        Assert.False(pool.GetOptions().ResetOnReturn);
        Assert.True(pool.GetOptions().ResetOnBorrow);

        var first = pool.Acquire();

        // The acquire hook runs on a create-on-miss hand-out too, so a brand-new object is reset once
        // before its first borrower sees it.
        Assert.Equal(1, first.ResetCount);

        first.Payload = 7;
        pool.Release(first);

        // The discriminating assertion: the return side was turned off, so the object is still dirty.
        // A switch that was accepted and then ignored would show 0 here.
        Assert.Equal(1, first.ResetCount);
        Assert.Equal(7, first.Payload);

        var second = pool.Acquire();
        Assert.Same(first, second);
        Assert.Equal(2, second.ResetCount);
        Assert.Equal(0, second.Payload);
    }

    [Fact(Timeout = 30_000)]
    public void UnnamedPool_ShouldTakeTheBorrowSideResetFromItsOptions()
    {
        var services = new ServiceCollection();
        services.AddHayatePoolSupport().RegisterHayatePool<ResettableBuffer>(ResetOnBorrowSide);

        var provider = services.BuildServiceProvider();
        var pool = provider.GetRequiredService<IHayateObjectPool<ResettableBuffer>>();

        Assert.True(pool.GetOptions().ResetOnBorrow);

        var first = pool.Acquire();
        first.Payload = 7;
        pool.Release(first);

        Assert.Equal(7, first.Payload);

        var second = pool.Acquire();
        Assert.Same(first, second);
        Assert.Equal(0, second.Payload);
    }

    [Fact(Timeout = 30_000)]
    public void PoolThatAsksForNothing_ShouldKeepTheHistoricalTiming()
    {
        // The other half of the fix: a pool that never moves the switch must not be moved by it either,
        // which is what makes "the container honours the switches" a change and not a behaviour swap.
        var services = new ServiceCollection();
        services.AddHayatePoolSupport().AddNamedPool<ResettableBuffer>("primary", Deterministic);

        var provider = services.BuildServiceProvider();
        var pool = provider.GetRequiredService<IHayateNamedPoolAccessor>().GetPool<ResettableBuffer>("primary");

        Assert.True(pool.GetOptions().ResetOnReturn);
        Assert.False(pool.GetOptions().ResetOnBorrow);

        var first = pool.Acquire();
        first.Payload = 7;
        pool.Release(first);

        Assert.Equal(1, first.ResetCount);
        Assert.Equal(0, first.Payload);
    }

    [Fact(Timeout = 30_000)]
    public void TwoNamesOfOneType_ShouldRunTheirOwnTimings()
    {
        // Each name has its own options, so each name derives its own default policy. Before this item
        // both names shared one policy instance and therefore one timing.
        var services = new ServiceCollection();
        services.AddHayatePoolSupport()
            .AddNamedPool<ResettableBuffer>("return-side", Deterministic)
            .AddNamedPool<ResettableBuffer>("borrow-side", ResetOnBorrowSide);

        var provider = services.BuildServiceProvider();
        var accessor = provider.GetRequiredService<IHayateNamedPoolAccessor>();

        var onReturn = accessor.GetPool<ResettableBuffer>("return-side");
        var onBorrow = accessor.GetPool<ResettableBuffer>("borrow-side");

        Assert.True(onReturn.GetOptions().ResetOnReturn);
        Assert.False(onReturn.GetOptions().ResetOnBorrow);
        Assert.False(onBorrow.GetOptions().ResetOnReturn);
        Assert.True(onBorrow.GetOptions().ResetOnBorrow);

        var a = onReturn.Acquire();
        a.Payload = 7;
        onReturn.Release(a);
        Assert.Equal(0, a.Payload); // reset on the way back

        var b = onBorrow.Acquire();
        b.Payload = 7;
        onBorrow.Release(b);
        Assert.Equal(7, b.Payload); // still dirty: this pool resets on the way out
    }

    [Fact(Timeout = 30_000)]
    public void CallerPolicyRegisteredBeforeThePool_ShouldBeUsedAndKeepItsOwnReset()
    {
        // Half of the boundary: the switches govern the library's own policy, so a caller's policy keeps
        // its own reset — and the caller's policy is the one that runs, not the library's.
        var services = new ServiceCollection();
        services.AddSingleton<IHayateObjectPolicy<ResettableBuffer>>(new CallerPolicy());
        services.AddHayatePoolSupport().AddNamedPool<ResettableBuffer>("primary", ResetOnBorrowSide);

        var provider = services.BuildServiceProvider();
        var pool = provider.GetRequiredService<IHayateNamedPoolAccessor>().GetPool<ResettableBuffer>("primary");

        var buffer = pool.Acquire();
        Assert.True(buffer.FromCallerPolicy);

        buffer.Payload = 7;
        pool.Release(buffer);

        // One reset, from the caller's own OnRelease. The borrow side did nothing: it belongs to the
        // library default, which this pool is not using.
        Assert.Equal(1, buffer.ResetCount);
        Assert.Equal(0, buffer.Payload);

        var again = pool.Acquire();
        Assert.Same(buffer, again);
        Assert.Equal(1, again.ResetCount);
    }

    [Fact(Timeout = 30_000)]
    public void CallerPolicyRegisteredAfterThePool_ShouldStillBeUsedAndKeepItsOwnReset()
    {
        // The other half, and the case a "was the fallback added?" flag gets wrong: the caller registered
        // their policy *after* the pool, so a second descriptor exists and GetRequiredService hands it
        // out. The fallback is therefore not what runs, and it must not be pinned away — dropping the
        // caller's policy here would be worse than the gap this item closes.
        var services = new ServiceCollection();
        services.AddHayatePoolSupport().AddNamedPool<ResettableBuffer>("primary", ResetOnBorrowSide);
        services.AddSingleton<IHayateObjectPolicy<ResettableBuffer>>(new CallerPolicy());

        var provider = services.BuildServiceProvider();
        var pool = provider.GetRequiredService<IHayateNamedPoolAccessor>().GetPool<ResettableBuffer>("primary");

        var buffer = pool.Acquire();
        Assert.True(buffer.FromCallerPolicy);

        buffer.Payload = 7;
        pool.Release(buffer);

        Assert.Equal(1, buffer.ResetCount);
        Assert.Equal(0, buffer.Payload);
    }

    [Fact(Timeout = 30_000)]
    public void PolicyFactory_ShouldStillWinOverTheOptionsDerivedDefault()
    {
        // B8's factory is an explicit, name-aware instruction, so it stays first — and its policy is
        // pinned, so the switches do not move it.
        var services = new ServiceCollection();
        services.AddHayatePoolSupport()
            .AddNamedPool<ResettableBuffer>("primary", ResetOnBorrowSide)
            .AddHayatePolicyFactory<ResettableBuffer>(new CallerPolicyFactory());

        var provider = services.BuildServiceProvider();
        var pool = provider.GetRequiredService<IHayateNamedPoolAccessor>().GetPool<ResettableBuffer>("primary");

        var buffer = pool.Acquire();
        Assert.True(buffer.FromCallerPolicy);

        buffer.Payload = 7;
        pool.Release(buffer);

        Assert.Equal(1, buffer.ResetCount);
        Assert.Equal(0, buffer.Payload);
    }

    [Fact]
    public void SeededDefaultPolicy_ShouldStillBeResolvableAndASingleton()
    {
        // B8 §5 pins this: the element-type registration and its resolution are unchanged. The seeded
        // fallback is what a container without a policy of its own resolves — this item only stops a
        // pool from being *pinned* to it.
        var services = new ServiceCollection();
        services.AddHayatePoolSupport().AddNamedPool<ResettableBuffer>("primary", Deterministic);

        var provider = services.BuildServiceProvider();

        var policy = provider.GetRequiredService<IHayateObjectPolicy<ResettableBuffer>>();
        Assert.IsType<DefaultHayateObjectPolicy<ResettableBuffer>>(policy);
        Assert.Same(policy, provider.GetRequiredService<IHayateObjectPolicy<ResettableBuffer>>());
    }
}
