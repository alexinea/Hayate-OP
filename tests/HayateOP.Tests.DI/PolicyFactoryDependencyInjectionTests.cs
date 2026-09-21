using DotNetCore.HayateOP;
using DotNetCore.HayateOP.Policies;
using Microsoft.Extensions.DependencyInjection;

namespace HayateOP.Tests.DI;

/// <summary>
/// B2 (2.9): the container path accepts an object-policy factory, so a pooled type without a public
/// parameterless constructor can be registered through DI at all, and a policy can be told what the
/// container knows — the connection string from configuration, which is the case this exists for.
/// <c>RegisterHayatePool</c> no longer appends its default policy unconditionally either, so a policy
/// registered directly in the container is honoured instead of being silently replaced.
/// </summary>
public class PolicyFactoryDependencyInjectionTests
{
    /// <summary>The pooled type: no public parameterless constructor, as a connection has none.</summary>
    public sealed class Connection
    {
        public Connection(string connectionString) => ConnectionString = connectionString;

        public string ConnectionString { get; }
    }

    /// <summary>Stands in for the configuration a policy factory reads the connection string from.</summary>
    public sealed class ConnectionStringSource
    {
        public ConnectionStringSource(string value) => Value = value;

        public string Value { get; }
    }

    private sealed class ConnectionPolicy : IHayateObjectPolicy<Connection>
    {
        private readonly string _connectionString;

        public ConnectionPolicy(string connectionString) => _connectionString = connectionString;

        public Connection Create() => new(_connectionString);

        public bool OnRelease(Connection item) => true;

        public bool Validate(Connection item) => true;

        public void OnAcquire(Connection item) { }

        public void OnPassivate(Connection item) { }

        public void OnDestroy(Connection item) { }
    }

    /// <summary>A pooled type that does have a parameterless constructor, so the library default can
    /// build it; <see cref="Origin"/> tells which policy actually ran.</summary>
    public sealed class Tagged
    {
        public Tagged() => Origin = "library default";

        public string Origin { get; set; }
    }

    private sealed class TaggedPolicy : IHayateObjectPolicy<Tagged>
    {
        private readonly string _origin;

        public TaggedPolicy(string origin) => _origin = origin;

        public Tagged Create() => new() { Origin = _origin };

        public bool OnRelease(Tagged item) => true;

        public bool Validate(Tagged item) => true;

        public void OnAcquire(Tagged item) { }

        public void OnPassivate(Tagged item) { }

        public void OnDestroy(Tagged item) { }
    }

    [Fact]
    public void RegisterHayatePool_WithPolicyFactory_CreatesObjectsThroughIt()
    {
        var services = new ServiceCollection();
        services.AddHayatePoolSupport().RegisterHayatePool<Connection>(sp => new ConnectionPolicy("Host=factory"));

        var provider = services.BuildServiceProvider();
        var pool = provider.GetRequiredService<IHayateObjectPool<Connection>>();

        var item = pool.Acquire();
        Assert.Equal("Host=factory", item.ConnectionString);

        pool.Release(item);
    }

    [Fact]
    public void RegisterHayatePool_WithPolicyFactory_TheFactoryReadsWhatTheContainerKnows()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ConnectionStringSource("Host=from-container"));
        services.AddHayatePoolSupport().RegisterHayatePool<Connection>(
            sp => new ConnectionPolicy(sp.GetRequiredService<ConnectionStringSource>().Value));

        var provider = services.BuildServiceProvider();
        var pool = provider.GetRequiredService<IHayateObjectPool<Connection>>();

        var item = pool.Acquire();
        Assert.Equal("Host=from-container", item.ConnectionString);

        pool.Release(item);
    }

    [Fact]
    public void RegisterHayatePool_WithPolicyFactory_RunsTheFactoryOnce()
    {
        var calls = 0;
        var services = new ServiceCollection();
        services.AddHayatePoolSupport().RegisterHayatePool<Connection>(sp =>
        {
            calls++;
            return new ConnectionPolicy("Host=factory");
        });

        var provider = services.BuildServiceProvider();
        var pool = provider.GetRequiredService<IHayateObjectPool<Connection>>();
        Assert.Equal(1, calls);

        // Resolving the policy once per container, not once per pool operation.
        var item = pool.Acquire();
        Assert.Equal(1, calls);

        pool.Release(item);
    }

    [Fact]
    public void RegisterHayatePool_WithPolicyFactory_StillAppliesTheConfigureCallback()
    {
        var services = new ServiceCollection();
        services.AddHayatePoolSupport().RegisterHayatePool<Connection>(
            sp => new ConnectionPolicy("Host=factory"),
            opt => opt.MaxPoolSize = 7);

        var provider = services.BuildServiceProvider();
        var pool = provider.GetRequiredService<IHayateObjectPool<Connection>>();

        Assert.Equal(7, pool.GetOptions().MaxPoolSize);
    }

    [Fact]
    public void RegisterHayatePool_WithoutFactory_APolicyRegisteredFirstWins()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHayateObjectPolicy<Tagged>>(sp => new TaggedPolicy("application"));
        services.AddHayatePoolSupport().RegisterHayatePool<Tagged>();

        var provider = services.BuildServiceProvider();
        var pool = provider.GetRequiredService<IHayateObjectPool<Tagged>>();

        var item = pool.Acquire();
        Assert.Equal("application", item.Origin);

        pool.Release(item);
    }

    [Fact]
    public void RegisterHayatePool_WithPolicyFactory_WinsOverAnEarlierRegistration()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHayateObjectPolicy<Tagged>>(sp => new TaggedPolicy("application"));
        services.AddHayatePoolSupport().RegisterHayatePool<Tagged>(sp => new TaggedPolicy("factory"));

        var provider = services.BuildServiceProvider();
        var pool = provider.GetRequiredService<IHayateObjectPool<Tagged>>();

        var item = pool.Acquire();
        Assert.Equal("factory", item.Origin);

        pool.Release(item);
    }

    [Fact]
    public void RegisterHayatePool_WithoutFactory_StillUsesTheLibraryDefault()
    {
        var services = new ServiceCollection();
        services.AddHayatePoolSupport().RegisterHayatePool<Tagged>();

        var provider = services.BuildServiceProvider();
        var pool = provider.GetRequiredService<IHayateObjectPool<Tagged>>();

        var item = pool.Acquire();
        Assert.Equal("library default", item.Origin);

        pool.Release(item);
    }

    [Fact]
    public void RegisterHayatePool_WithPolicyFactory_RejectsANullFactory()
    {
        var services = new ServiceCollection();
        var hayate = services.AddHayatePoolSupport();

        Assert.Throws<ArgumentNullException>(() =>
            hayate.RegisterHayatePool<Tagged>((Func<IServiceProvider, IHayateObjectPolicy<Tagged>>)null!));
    }
}
