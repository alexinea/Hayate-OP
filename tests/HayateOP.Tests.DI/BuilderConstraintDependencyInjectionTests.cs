using DotNetCore.HayateOP;
using DotNetCore.HayateOP.Policies;
using Microsoft.Extensions.DependencyInjection;

namespace HayateOP.Tests.DI;

/// <summary>
/// B1 (2.9) on the container path: a pooled type without a public parameterless constructor.
/// <c>AddNamedPool</c> registers the default policy with <c>TryAdd</c>, so a policy the application
/// registered first wins and the pool resolves; <c>RegisterHayatePool</c> owns its policy registration, so
/// until it gains the factory overload of B2 the missing constructor is reported when the pool is built,
/// naming the type.
/// </summary>
public class BuilderConstraintDependencyInjectionTests
{
    public sealed class ConnectionLike
    {
        public ConnectionLike(string connectionString) => ConnectionString = connectionString;

        public string ConnectionString { get; }
    }

    private sealed class ConnectionPolicy : IHayateObjectPolicy<ConnectionLike>
    {
        public ConnectionLike Create() => new("Host=registered");

        public bool OnRelease(ConnectionLike item) => true;

        public bool Validate(ConnectionLike item) => true;

        public void OnAcquire(ConnectionLike item) { }

        public void OnPassivate(ConnectionLike item) { }

        public void OnDestroy(ConnectionLike item) { }
    }

    [Fact]
    public void AddNamedPool_WithoutParameterlessConstructor_ApplicationPolicyWins()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHayateObjectPolicy<ConnectionLike>, ConnectionPolicy>();
        services.AddHayatePoolSupport().AddNamedPool<ConnectionLike>("primary");

        var provider = services.BuildServiceProvider();
        var accessor = provider.GetRequiredService<IHayateNamedPoolAccessor>();
        var pool = accessor.GetPool<ConnectionLike>("primary");

        var item = pool.Acquire();
        Assert.Equal("Host=registered", item.ConnectionString);

        pool.Release(item);
    }

    [Fact]
    public void RegisterHayatePool_WithoutParameterlessConstructor_WithoutPolicy_ReportsTheMissingConstructor()
    {
        var services = new ServiceCollection();
        services.AddHayatePoolSupport().RegisterHayatePool<ConnectionLike>();

        var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<IHayateObjectPool<ConnectionLike>>());

        Assert.Contains(typeof(ConnectionLike).FullName!, ex.Message);
    }
}
