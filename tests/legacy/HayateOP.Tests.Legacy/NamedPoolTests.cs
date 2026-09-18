using System;
using System.Threading.Tasks;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// Named pools (N4): the service key, the run-time pool factory and the typed registry lookups that
/// let several independently configured pools of one element type coexist.
/// </summary>
public class NamedPoolTests
{
    private sealed class TestObject { }

    private sealed class OtherObject { }

    private sealed class NoDefaultCtorObject
    {
        public NoDefaultCtorObject(int _)
        {
        }
    }

    [Fact]
    public void ServiceKey_ShouldBuildTheCanonicalRegistryName()
    {
        var key = HayateServiceKey.Create<TestObject>("primary");

        Assert.Equal("primary", key.Name);
        Assert.Equal(typeof(TestObject), key.ElementType);
        Assert.Equal("TestObject:primary", key.RegistryName);
        Assert.Equal("TestObject:primary", key.ToString());
    }

    [Fact]
    public void ServiceKey_ShouldRejectNamesThatCannotBeAddressed()
    {
        // The separator is what keeps the registry key unambiguous, so a name containing it would
        // make two different keys resolve to the same registry entry.
        Assert.Throws<ArgumentException>(() => HayateServiceKey.Create<TestObject>("a:b"));
        Assert.Throws<ArgumentException>(() => HayateServiceKey.Create<TestObject>("  "));
        Assert.Throws<ArgumentNullException>(() => HayateServiceKey.Create<TestObject>(null!));
        Assert.Throws<ArgumentException>(() => HayateServiceKey.Create(typeof(int), "counter"));
    }

    [Fact]
    public void ServiceKey_Equality_ShouldCoverElementTypeAndName()
    {
        var a = HayateServiceKey.Create<TestObject>("primary");
        var sameAgain = HayateServiceKey.Create<TestObject>("primary");
        var otherName = HayateServiceKey.Create<TestObject>("replica");
        var otherType = HayateServiceKey.Create<OtherObject>("primary");

        Assert.Equal(a, sameAgain);
        Assert.Equal(a.GetHashCode(), sameAgain.GetHashCode());
        Assert.NotEqual(a, otherName);
        Assert.NotEqual(a, otherType);
        Assert.True(a.Equals((object)sameAgain));
        Assert.False(a.Equals(null));
    }

    [Fact]
    public void ServiceKey_TryCreate_ShouldReportUnusableNamesInsteadOfThrowing()
    {
        Assert.True(HayateServiceKey.TryCreate<TestObject>("primary", out var key));
        Assert.NotNull(key);
        Assert.Equal("TestObject:primary", key!.RegistryName);

        Assert.False(HayateServiceKey.TryCreate<TestObject>("", out var empty));
        Assert.Null(empty);
        Assert.False(HayateServiceKey.TryCreate<TestObject>("a:b", out var separated));
        Assert.Null(separated);
    }

    [Fact]
    public void Factory_Create_ShouldBuildAnIndependentlyConfiguredPool()
    {
        var registry = new HayateObjectPoolRegistry();
        var factory = new HayatePoolFactory(registry);

        using var primary = factory.CreatePool<TestObject>("primary", o => o.MaxPoolSize = 8);
        using var replica = factory.CreatePool<TestObject>("replica", o => o.MaxPoolSize = 32);

        Assert.NotSame(primary, replica);
        Assert.Equal(8, primary.GetOptions().MaxPoolSize);
        Assert.Equal(32, replica.GetOptions().MaxPoolSize);

        // Both pools are registered under their canonical name, and neither disturbs the unnamed pool.
        Assert.Equal(2, registry.Count);
        Assert.True(registry.TryGet("TestObject:primary", out _));
        Assert.True(registry.TryGet("TestObject:replica", out _));
        Assert.False(registry.TryGet("TestObject", out _));
    }

    [Fact]
    public void Factory_GetOrCreate_ShouldReuseAnAlreadyRegisteredPool()
    {
        var registry = new HayateObjectPoolRegistry();
        var factory = new HayatePoolFactory(registry);

        var first = factory.GetOrCreatePool<TestObject>("primary");
        var second = factory.GetOrCreatePool<TestObject>("primary", o => o.MaxPoolSize = 7);

        Assert.Same(first, second);
        // The configure callback is ignored on the second call: the existing pool is returned as is.
        Assert.NotEqual(7, first.GetOptions().MaxPoolSize);

        first.Dispose();
    }

    [Fact]
    public void Factory_Create_ShouldReplaceTheRegisteredPool()
    {
        var registry = new HayateObjectPoolRegistry();
        var factory = new HayatePoolFactory(registry);

        var first = factory.CreatePool<TestObject>("primary");
        var second = factory.CreatePool<TestObject>("primary");

        Assert.NotSame(first, second);
        Assert.Same(second, registry.GetPool<TestObject>("primary"));

        first.Dispose();
        second.Dispose();
    }

    [Fact]
    public void Factory_WithoutRegistry_ShouldNotDeduplicate()
    {
        // No registry means no place to remember a pool, so GetOrCreate degrades to Create rather
        // than silently pretending to deduplicate.
        var factory = new HayatePoolFactory();

        var first = factory.GetOrCreatePool<TestObject>("primary");
        var second = factory.GetOrCreatePool<TestObject>("primary");

        Assert.NotSame(first, second);

        first.Dispose();
        second.Dispose();
    }

    [Fact]
    public void Factory_ShouldRejectElementTypesItCannotCreate()
    {
        var factory = new HayatePoolFactory();
        var noDefaultCtor = HayateServiceKey.Create(typeof(NoDefaultCtorObject), "probe");

        // The key itself is only an identity, so the rejection happens when the factory tries to build.
        Assert.Throws<ArgumentException>(() => factory.Create(noDefaultCtor));
        Assert.Throws<ArgumentException>(() => factory.GetOrCreate(noDefaultCtor));
    }

    [Fact]
    public void Factory_ShouldSurfaceBuildFailuresUnwrapped()
    {
        var factory = new HayatePoolFactory();

        // A rejected configuration must reach the caller as the builder's own InvalidOperationException,
        // not as a reflection wrapper.
        Assert.Throws<InvalidOperationException>(
            () => factory.CreatePool<TestObject>("invalid", o => o.MinPoolSize = int.MaxValue));
    }

    [Fact]
    public void Registry_TypedLookup_ShouldVerifyTheElementType()
    {
        var registry = new HayateObjectPoolRegistry();
        var factory = new HayatePoolFactory(registry);

        using var pool = factory.CreatePool<TestObject>("primary");

        Assert.True(registry.TryGetPool<TestObject>("primary", out var typed));
        Assert.Same(pool, typed);

        // The name is registered, but not for this element type: a cast that silently succeeded here
        // would hand back a pool that cannot lend the requested type.
        Assert.False(registry.TryGetPool<OtherObject>("primary", out _));
        Assert.Null(registry.GetPool<OtherObject>("primary"));
        Assert.False(registry.TryGetPool<TestObject>("absent", out _));

        Assert.Same(pool, registry.GetRequiredPool<TestObject>("primary"));
        var ex = Assert.Throws<InvalidOperationException>(() => registry.GetRequiredPool<TestObject>("absent"));
        Assert.Contains("absent", ex.Message);
    }

    [Fact]
    public async Task NamedPools_ShouldServeBorrowsIndependently()
    {
        var registry = new HayateObjectPoolRegistry();
        var factory = new HayatePoolFactory(registry);

        // Metrics are off by default, and the release counter is gated by them: switch them on so the
        // per-pool counters below are meaningful.
        using var primary = factory.CreatePool<TestObject>("primary", o =>
        {
            o.MinPoolSize = 0;
            o.MaxPoolSize = 8;
            o.EnableMetrics = true;
        });
        using var replica = factory.CreatePool<TestObject>("replica", o =>
        {
            o.MinPoolSize = 0;
            o.MaxPoolSize = 8;
            o.EnableMetrics = true;
        });

        var fromPrimary = primary.Acquire();
        var fromReplica = replica.Acquire();
        Assert.NotSame(fromPrimary, fromReplica);

        // Each pool counts its own borrows, so one pool's lease is invisible to the other.
        Assert.Equal(1, primary.GetStats().TotalAcquired);
        Assert.Equal(1, replica.GetStats().TotalAcquired);

        primary.Release(fromPrimary);
        replica.Release(fromReplica);

        var asyncLease = await replica.AcquireAsync();
        replica.Release(asyncLease);

        Assert.Equal(1, primary.GetStats().TotalAcquired);
        Assert.Equal(2, replica.GetStats().TotalAcquired);
        Assert.Equal(1, primary.GetStats().TotalReleased);
        Assert.Equal(2, replica.GetStats().TotalReleased);
    }
}
