using DotNetCore.HayateOP;
using DotNetCore.HayateOP.DependencyInjection;
using DotNetCore.HayateOP.Policies;
using Microsoft.Extensions.DependencyInjection;

namespace HayateOP.Tests.DI;

/// <summary>
/// B8 (3.0): the container resolves a policy per <b>pool name</b>, not per element type, when an
/// <see cref="IHayateObjectPolicyFactory{T}"/> is registered — so "primary uses connection string A,
/// replica uses connection string B" is expressible without inventing a second element type. The
/// factory is optional: with none registered, the element-type lookup is the only lookup and behaves
/// as it always has. The form decision behind this shape is recorded in
/// <c>docs/named-pool-policies.md</c>.
/// </summary>
public class NamedPoolPolicyFactoryTests
{
    /// <summary>The pooled type. The library default can build it, so a policy is only ever chosen
    /// deliberately; <see cref="Origin"/> says which policy actually ran.</summary>
    public sealed class Tagged
    {
        public Tagged() => Origin = "library default";

        public string Origin { get; set; }
    }

    /// <summary>Creates objects whose <see cref="Tagged.Origin"/> is the string it was built with, so
    /// a test can tell which policy — and therefore which pool name — produced an object.</summary>
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

    /// <summary>Records what it was asked for and what it handed back, so the tests can assert both
    /// the names the container passes and the instances it ends up using.</summary>
    private sealed class RecordingPolicyFactory : IHayateObjectPolicyFactory<Tagged>
    {
        private readonly List<string> _requestedNames = new();
        private readonly List<IHayateObjectPolicy<Tagged>> _created = new();

        public IReadOnlyList<string> RequestedNames => _requestedNames;

        public IReadOnlyList<IHayateObjectPolicy<Tagged>> Created => _created;

        public int CreateCount => _created.Count;

        public IHayateObjectPolicy<Tagged> Create(string poolName)
        {
            var policy = new TaggedPolicy(poolName);
            _requestedNames.Add(poolName);
            _created.Add(policy);
            return policy;
        }
    }

    /// <summary>A factory with a dependency of its own, for the form the container builds.</summary>
    public sealed class ConfigurablePolicyFactory : IHayateObjectPolicyFactory<Tagged>
    {
        private readonly PolicyOriginSource _source;

        public ConfigurablePolicyFactory(PolicyOriginSource source) => _source = source;

        public IHayateObjectPolicy<Tagged> Create(string poolName) =>
            new TaggedPolicy(string.Concat(_source.Value, ":", poolName));
    }

    /// <summary>Stands in for the configuration a factory reads from.</summary>
    public sealed class PolicyOriginSource
    {
        public PolicyOriginSource(string value) => Value = value;

        public string Value { get; }
    }

    /// <summary>Hands back <c>null</c>, to pin what the container does with a factory that breaks the
    /// contract the interface states.</summary>
    private sealed class NullReturningPolicyFactory : IHayateObjectPolicyFactory<Tagged>
    {
        public IHayateObjectPolicy<Tagged> Create(string poolName) => null;
    }

    [Fact]
    public void AddHayatePolicyFactory_ShouldGiveEachNamedPoolItsOwnPolicy()
    {
        var factory = new RecordingPolicyFactory();
        var services = new ServiceCollection();
        services.AddHayatePoolSupport()
            .AddNamedPool<Tagged>("primary")
            .AddNamedPool<Tagged>("replica")
            .AddHayatePolicyFactory<Tagged>(factory);

        var provider = services.BuildServiceProvider();
        var accessor = provider.GetRequiredService<IHayateNamedPoolAccessor>();

        var primary = accessor.GetPool<Tagged>("primary");
        var replica = accessor.GetPool<Tagged>("replica");

        // Two names, two policies — the whole point of the item.
        Assert.NotSame(primary, replica);
        Assert.Equal(2, factory.CreateCount);
        Assert.NotSame(factory.Created[0], factory.Created[1]);

        // And each pool is built with the policy the factory returned for *its* name. The origin
        // reports that name back, so the tie is behavioural, not just structural.
        var fromPrimary = primary.Acquire();
        var fromReplica = replica.Acquire();

        Assert.Equal("Tagged:primary", fromPrimary.Origin);
        Assert.Equal("Tagged:replica", fromReplica.Origin);

        // The instance the factory returned for "primary" is the one that built the primary pool.
        Assert.Equal("Tagged:primary", factory.Created[0].Create().Origin);
        Assert.Equal("Tagged:replica", factory.Created[1].Create().Origin);

        primary.Release(fromPrimary);
        replica.Release(fromReplica);
    }

    [Fact]
    public void AddHayatePolicyFactory_ShouldReceiveTheCanonicalRegistryName()
    {
        var factory = new RecordingPolicyFactory();
        var services = new ServiceCollection();
        services.AddHayatePoolSupport()
            .AddNamedPool<Tagged>("primary")
            .AddNamedPool<Tagged>("replica")
            .AddHayatePolicyFactory<Tagged>(factory);

        var provider = services.BuildServiceProvider();
        var accessor = provider.GetRequiredService<IHayateNamedPoolAccessor>();

        accessor.GetPool<Tagged>("primary");
        accessor.GetPool<Tagged>("replica");

        // Not the bare logical name: the factory sees HayateServiceKey.RegistryName, the same single
        // string that addresses the pool in the registry and in the named options.
        Assert.Equal(new[] { "Tagged:primary", "Tagged:replica" }, factory.RequestedNames);
    }

    [Fact]
    public void AddHayatePolicyFactory_ShouldAlsoDecideTheUnnamedPool()
    {
        var factory = new RecordingPolicyFactory();
        var services = new ServiceCollection();
        services.AddHayatePoolSupport()
            .RegisterHayatePool<Tagged>()
            .AddHayatePolicyFactory<Tagged>(factory);

        var provider = services.BuildServiceProvider();
        var pool = provider.GetRequiredService<IHayateObjectPool<Tagged>>();

        var item = pool.Acquire();

        // The unnamed pool has no logical name, so it is addressed by the bare element type name —
        // the same string the registry keeps it under. The factory is not a named-pool-only feature.
        Assert.Equal(new[] { "Tagged" }, factory.RequestedNames);
        Assert.Equal("Tagged", item.Origin);

        pool.Release(item);
    }

    [Fact]
    public void AddHayatePolicyFactory_ShouldSeparateTheUnnamedPoolFromANamedOne()
    {
        var factory = new RecordingPolicyFactory();
        var services = new ServiceCollection();
        services.AddHayatePoolSupport()
            .RegisterHayatePool<Tagged>()
            .AddNamedPool<Tagged>("primary")
            .AddHayatePolicyFactory<Tagged>(factory);

        var provider = services.BuildServiceProvider();
        var accessor = provider.GetRequiredService<IHayateNamedPoolAccessor>();

        var unnamed = provider.GetRequiredService<IHayateObjectPool<Tagged>>();
        var named = accessor.GetPool<Tagged>("primary");

        var fromUnnamed = unnamed.Acquire();
        var fromNamed = named.Acquire();

        Assert.Equal("Tagged", fromUnnamed.Origin);
        Assert.Equal("Tagged:primary", fromNamed.Origin);

        unnamed.Release(fromUnnamed);
        named.Release(fromNamed);
    }

    [Fact]
    public void AddHayatePolicyFactory_ShouldWinOverAPolicyRegisteredInTheContainer()
    {
        var factory = new RecordingPolicyFactory();
        var services = new ServiceCollection();
        services.AddSingleton<IHayateObjectPolicy<Tagged>>(sp => new TaggedPolicy("application"));
        services.AddHayatePoolSupport()
            .AddNamedPool<Tagged>("primary")
            .AddHayatePolicyFactory<Tagged>(factory);

        var provider = services.BuildServiceProvider();
        var accessor = provider.GetRequiredService<IHayateNamedPoolAccessor>();

        // A factory is an explicit, name-aware instruction, so it is consulted first...
        var item = accessor.GetPool<Tagged>("primary").Acquire();
        Assert.Equal("Tagged:primary", item.Origin);

        // ...and it replaces nothing. The element-type registration is still there, still a
        // singleton, and still what every pool uses when no factory is registered.
        Assert.Equal("application", provider.GetRequiredService<IHayateObjectPolicy<Tagged>>().Create().Origin);
        Assert.Same(provider.GetRequiredService<IHayateObjectPolicy<Tagged>>(),
            provider.GetRequiredService<IHayateObjectPolicy<Tagged>>());
    }

    [Fact]
    public void WithoutAFactory_EveryNamedPoolStillSharesTheElementTypePolicy()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHayateObjectPolicy<Tagged>>(sp => new TaggedPolicy("application"));
        services.AddHayatePoolSupport()
            .AddNamedPool<Tagged>("primary")
            .AddNamedPool<Tagged>("replica");

        var provider = services.BuildServiceProvider();
        var accessor = provider.GetRequiredService<IHayateNamedPoolAccessor>();

        var fromPrimary = accessor.GetPool<Tagged>("primary").Acquire();
        var fromReplica = accessor.GetPool<Tagged>("replica").Acquire();

        // Unchanged: one policy per element type, shared by every name. Declaring a second name does
        // not give it a second policy — that is the pre-3.0 contract, and it is what a container
        // without a factory still gets.
        Assert.Equal("application", fromPrimary.Origin);
        Assert.Equal("application", fromReplica.Origin);
    }

    [Fact]
    public void WithoutAFactory_TheLibraryDefaultIsStillTheOnlyPolicy()
    {
        var services = new ServiceCollection();
        services.AddHayatePoolSupport()
            .AddNamedPool<Tagged>("primary")
            .AddNamedPool<Tagged>("replica");

        var provider = services.BuildServiceProvider();
        var accessor = provider.GetRequiredService<IHayateNamedPoolAccessor>();

        // The library default is reached by the element-type lookup and by nothing else, so both
        // names producing it is direct evidence that the factory branch stayed out of the way.
        Assert.Equal("library default", accessor.GetPool<Tagged>("primary").Acquire().Origin);
        Assert.Equal("library default", accessor.GetPool<Tagged>("replica").Acquire().Origin);
    }

    [Fact]
    public void AddHayatePolicyFactory_TypeForm_ShouldBuildTheFactoryFromTheContainer()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new PolicyOriginSource("configured"));
        services.AddHayatePoolSupport()
            .AddNamedPool<Tagged>("primary")
            .AddNamedPool<Tagged>("replica")
            .AddHayatePolicyFactory<Tagged, ConfigurablePolicyFactory>();

        var provider = services.BuildServiceProvider();
        var accessor = provider.GetRequiredService<IHayateNamedPoolAccessor>();

        // The factory is constructed by the container, so it may read what the container knows and
        // still dispatch on the name it is handed.
        Assert.Equal("configured:Tagged:primary", accessor.GetPool<Tagged>("primary").Acquire().Origin);
        Assert.Equal("configured:Tagged:replica", accessor.GetPool<Tagged>("replica").Acquire().Origin);
    }

    [Fact]
    public void AddHayatePolicyFactory_ShouldBeConsultedOncePerPoolBuild()
    {
        var factory = new RecordingPolicyFactory();
        var services = new ServiceCollection();
        services.AddHayatePoolSupport()
            .AddNamedPool<Tagged>("primary")
            .AddHayatePolicyFactory<Tagged>(factory);

        var provider = services.BuildServiceProvider();
        var accessor = provider.GetRequiredService<IHayateNamedPoolAccessor>();

        // Nothing is built at registration time, so nothing is asked of the factory either.
        Assert.Equal(0, factory.CreateCount);

        var pool = accessor.GetPool<Tagged>("primary");
        Assert.Equal(1, factory.CreateCount);

        // The factory sits next to the options and scaling-strategy lookups on the cold path, not on
        // the borrow path: acquiring and releasing does not consult it again.
        var item = pool.Acquire();
        pool.Release(item);
        Assert.Equal(1, factory.CreateCount);

        // Nor does resolving the same pool again.
        Assert.Same(pool, accessor.GetPool<Tagged>("primary"));
        Assert.Equal(1, factory.CreateCount);
    }

    [Fact]
    public void AddHayatePolicyFactory_WithASingleNamedPool_ShouldAskForThatNameOnly()
    {
        var factory = new RecordingPolicyFactory();
        var services = new ServiceCollection();
        services.AddHayatePoolSupport()
            .AddNamedPool<Tagged>("primary")
            .AddHayatePolicyFactory<Tagged>(factory);

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IHayateNamedPoolAccessor>().GetPool<Tagged>("primary");

        Assert.Equal(new[] { "Tagged:primary" }, factory.RequestedNames);
    }

    [Fact]
    public void AddHayatePolicyFactory_ShouldRejectANullFactory()
    {
        var services = new ServiceCollection();
        var hayate = services.AddHayatePoolSupport();

        Assert.Throws<ArgumentNullException>(() =>
            hayate.AddHayatePolicyFactory<Tagged>((IHayateObjectPolicyFactory<Tagged>)null!));
    }

    [Fact]
    public void AddHayatePolicyFactory_ReturningNull_ShouldFailLoudlyRatherThanSilently()
    {
        var services = new ServiceCollection();
        services.AddHayatePoolSupport()
            .AddNamedPool<Tagged>("primary")
            .AddHayatePolicyFactory<Tagged>(new NullReturningPolicyFactory());

        var provider = services.BuildServiceProvider();
        var accessor = provider.GetRequiredService<IHayateNamedPoolAccessor>();

        // The interface states the policy must not be null; a factory that breaks that is rejected
        // when the pool is built, rather than quietly producing a pool with no policy at all.
        Assert.Throws<ArgumentNullException>(() => accessor.GetPool<Tagged>("primary"));
    }
}
