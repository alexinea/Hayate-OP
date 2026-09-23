using DotNetCore.HayateOP;
using DotNetCore.HayateOP.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace HayateOP.Tests.DI;

/// <summary>
/// A container with no logging provider (L3): the pool used to go silent there, which is indistinguishable
/// from a pool that has nothing to say. It now falls back to the built-in logger — the one a bare
/// <see cref="HayatePoolBuilder{T}"/> resolves — so the two paths agree.
/// </summary>
public class LoggerFactoryFallbackTests
{
    public sealed class TestObject { }

    [Fact]
    public void NoMELFactory_ShouldFallBackToTheBuiltInLogger()
    {
        // Compared by type, not by identity: DefaultHayateLogger is internal and is created fresh per
        // call, so "is it the built-in one?" can only be answered by asking what type came back.
        var expected = DotNetCore.HayateOP.Logging.DefaultHayateLoggerFactory.Instance
            .CreateLogger("pool").GetType();
        var actual = new HayateMicrosoftLoggerFactory(null).CreateLogger("pool").GetType();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void NoMELFactory_ShouldNotBeTheSilentAdapter()
    {
        // The regression this guards: the factory used to wrap a null ILogger in the adapter, which
        // discards everything. Asserting the adapter's type is the direct negation of the case above.
        Assert.NotEqual(typeof(HayateMicrosoftLoggerAdapter),
            new HayateMicrosoftLoggerFactory(null).CreateLogger("pool").GetType());
    }

    [Fact]
    public void BareContainer_ShouldStillResolveAPool()
    {
        // A container the host never called AddLogging on. Before L3 this resolved a pool wired to a
        // silent logger; it still resolves either way, so this is the wiring smoke test, while the two
        // cases above are the ones that pin the fallback.
        var services = new ServiceCollection();
        services.AddHayatePoolSupport().RegisterHayatePool<TestObject>();

        var provider = services.BuildServiceProvider();
        var pool = provider.GetRequiredService<IHayateObjectPool<TestObject>>();

        Assert.NotNull(pool);
        Assert.NotNull(pool.GetStats());
    }
}
