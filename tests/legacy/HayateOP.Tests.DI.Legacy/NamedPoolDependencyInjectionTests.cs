using DotNetCore.HayateOP;
using Microsoft.Extensions.DependencyInjection;

namespace HayateOP.Tests.DI;

/// <summary>
/// Named pools and typed clients (N4): several independently configured pools of one element type,
/// resolved by name instead of by "the pool of T".
/// </summary>
public class NamedPoolDependencyInjectionTests
{
    public sealed class TestObject { }

    public sealed class PrimaryClient
    {
        public IHayateObjectPool<TestObject> Pool { get; }

        public PrimaryClient(IHayateObjectPool<TestObject> pool) => Pool = pool;
    }

    public sealed class ReplicaClient
    {
        public IHayateObjectPool<TestObject> Pool { get; }

        public ReplicaClient(IHayateObjectPool<TestObject> pool) => Pool = pool;
    }

    public sealed class DefaultClient
    {
        public IHayateObjectPool<TestObject> Pool { get; }

        public DefaultClient(IHayateObjectPool<TestObject> pool) => Pool = pool;
    }

    [Fact]
    public void AddNamedPool_ShouldRegisterIndependentlyConfiguredPools()
    {
        var services = new ServiceCollection();
        services.AddHayatePoolSupport()
            .AddNamedPool<TestObject>("primary", o => o.MaxPoolSize = 8)
            .AddNamedPool<TestObject>("replica", o => o.MaxPoolSize = 32);

        var provider = services.BuildServiceProvider();
        var accessor = provider.GetRequiredService<IHayateNamedPoolAccessor>();

        var primary = accessor.GetPool<TestObject>("primary");
        var replica = accessor.GetPool<TestObject>("replica");

        Assert.NotSame(primary, replica);
        Assert.Equal(8, primary.GetOptions().MaxPoolSize);
        Assert.Equal(32, replica.GetOptions().MaxPoolSize);
    }

    [Fact]
    public void AddNamedPool_ShouldResolveOneSingletonPerName()
    {
        var services = new ServiceCollection();
        services.AddHayatePoolSupport()
            .AddNamedPool<TestObject>("primary");

        var provider = services.BuildServiceProvider();
        var accessor = provider.GetRequiredService<IHayateNamedPoolAccessor>();

        Assert.Same(accessor.GetPool<TestObject>("primary"), accessor.GetPool<TestObject>("primary"));
        Assert.Equal(new[] { "primary" }, accessor.GetPoolNames<TestObject>());
    }

    [Fact]
    public void AddNamedPool_ShouldNotOccupyTheUnnamedPoolSlot()
    {
        var services = new ServiceCollection();
        services.AddHayatePoolSupport()
            .AddNamedPool<TestObject>("primary");

        var provider = services.BuildServiceProvider();

        // The unnamed registration stays reserved for the unnamed pool, so "the pool of T" never
        // silently resolves to one of several named pools.
        Assert.Null(provider.GetService<IHayateObjectPool<TestObject>>());
    }

    [Fact]
    public void AddNamedPool_ShouldRegisterUnderTheCanonicalRegistryName()
    {
        var services = new ServiceCollection();
        services.AddHayatePoolSupport()
            .AddNamedPool<TestObject>("primary");

        var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IHayateObjectPoolRegistry>();
        var accessor = provider.GetRequiredService<IHayateNamedPoolAccessor>();

        // Nothing is built until something asks for it.
        Assert.Equal(0, registry.Count);

        var pool = accessor.GetPool<TestObject>("primary");

        Assert.Equal(1, registry.Count);
        Assert.True(registry.TryGetPool<TestObject>("primary", out var registered));
        Assert.Same(pool, registered);
        Assert.True(registry.TryGet("TestObject:primary", out _));
    }

    [Fact]
    public void Accessor_GetPool_ShouldThrowForAnUnregisteredName()
    {
        var services = new ServiceCollection();
        services.AddHayatePoolSupport()
            .AddNamedPool<TestObject>("primary");

        var provider = services.BuildServiceProvider();
        var accessor = provider.GetRequiredService<IHayateNamedPoolAccessor>();

        Assert.False(accessor.TryGetPool<TestObject>("absent", out var pool));
        Assert.Null(pool);

        // Resolution never creates a pool on demand: a misspelled name must fail loudly rather than
        // silently start an unconfigured pool.
        var ex = Assert.Throws<InvalidOperationException>(() => accessor.GetPool<TestObject>("absent"));
        Assert.Contains("absent", ex.Message);
    }

    [Fact]
    public void AddPool_TypedClient_ShouldReceiveTheBoundNamedPool()
    {
        var services = new ServiceCollection();
        services.AddHayatePoolSupport()
            .AddPool<TestObject, PrimaryClient>("primary")
            .AddPool<TestObject, ReplicaClient>("replica");

        var provider = services.BuildServiceProvider();
        var accessor = provider.GetRequiredService<IHayateNamedPoolAccessor>();

        var primary = provider.GetRequiredService<PrimaryClient>();
        var replica = provider.GetRequiredService<ReplicaClient>();

        Assert.Same(accessor.GetPool<TestObject>("primary"), primary.Pool);
        Assert.Same(accessor.GetPool<TestObject>("replica"), replica.Pool);
        Assert.NotSame(primary.Pool, replica.Pool);
    }

    [Fact]
    public void AddPool_TypedClient_WithoutName_ShouldReceiveTheUnnamedPool()
    {
        var services = new ServiceCollection();
        services.AddHayatePoolSupport()
            .AddPool<TestObject, DefaultClient>(o => o.MaxPoolSize = 16);

        var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService<DefaultClient>();

        Assert.NotNull(client.Pool);
        Assert.Equal(16, client.Pool.GetOptions().MaxPoolSize);
        Assert.Same(provider.GetRequiredService<IHayateObjectPool<TestObject>>(), client.Pool);
    }

    [Fact]
    public void Accessor_GetPool_WithoutName_ShouldResolveTheUnnamedPool()
    {
        var services = new ServiceCollection();
        services.AddHayatePoolSupport()
            .RegisterHayatePool<TestObject>()
            .AddNamedPool<TestObject>("primary");

        var provider = services.BuildServiceProvider();
        var accessor = provider.GetRequiredService<IHayateNamedPoolAccessor>();

        var unnamed = accessor.GetPool<TestObject>();
        var named = accessor.GetPool<TestObject>("primary");

        Assert.Same(provider.GetRequiredService<IHayateObjectPool<TestObject>>(), unnamed);
        Assert.NotSame(unnamed, named);
    }
}
