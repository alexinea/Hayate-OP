using System;
using System.Linq;
using System.Threading.Tasks;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// Full registry -- GetAll enumeration (with metadata) / Remove deregistration / Count.
/// Register / TryGet original-semantics regression coverage is also provided.
/// </summary>
public class RegistryTests
{
    private sealed class TestObject { }

    [Fact]
    public void Register_ShouldPopulateMetadata()
    {
        var registry = new HayateObjectPoolRegistry();
        using var pool = new HayatePoolBuilder<TestObject>()
            .WithPoolName("probe-meta")
            .WithMinSize(1)
            .WithMaxSize(2)
            .Build();

        registry.Register("probe-meta", pool);

        Assert.Equal(1, registry.Count);
        var entry = Assert.Single(registry.GetAll());
        Assert.Equal("probe-meta", entry.PoolName);
        Assert.Equal(typeof(TestObject), entry.ElementType);
        Assert.Same(pool, entry.Pool);
        Assert.Equal(pool.GetType(), entry.PoolType);
        // The registration (approx. construction) time should fall within the last minute
        Assert.True(entry.RegisteredAt <= DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.True(entry.RegisteredAt >= DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public void GetAll_ShouldEnumerateAllRegistrations()
    {
        var registry = new HayateObjectPoolRegistry();
        using var poolA = BuildPool("pool-a");
        using var poolB = BuildPool("pool-b");
        using var poolC = BuildPool("pool-c");

        registry.Register("pool-a", poolA);
        registry.Register("pool-b", poolB);
        registry.Register("pool-c", poolC);

        Assert.Equal(3, registry.Count);
        var names = registry.GetAll().Select(m => m.PoolName).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "pool-a", "pool-b", "pool-c" }, names);

        // The Names view is consistent with GetAll
        var namesLegacy = registry.Names.OrderBy(n => n).ToArray();
        Assert.Equal(names, namesLegacy);
    }

    [Fact]
    public void Remove_ShouldUnregisterOnlyTargetName()
    {
        var registry = new HayateObjectPoolRegistry();
        using var poolA = BuildPool("pool-a");
        using var poolB = BuildPool("pool-b");
        registry.Register("pool-a", poolA);
        registry.Register("pool-b", poolB);

        Assert.True(registry.Remove("pool-a"));
        Assert.Equal(1, registry.Count);
        Assert.False(registry.TryGet("pool-a", out _));
        Assert.True(registry.TryGet("pool-b", out var remaining));
        Assert.Same(poolB, remaining);

        // Repeated removal / unknown name / blank name all return false
        Assert.False(registry.Remove("pool-a"));
        Assert.False(registry.Remove("never-registered"));
        Assert.False(registry.Remove(null!));
        Assert.False(registry.Remove("  "));
    }

    [Fact]
    public void Register_ShouldReplaceExistingEntryWithNewMetadata()
    {
        var registry = new HayateObjectPoolRegistry();
        using var poolV1 = BuildPool("pool-x");
        registry.Register("pool-x", poolV1);
        var first = Assert.Single(registry.GetAll());

        using var poolV2 = BuildPool("pool-x");
        registry.Register("pool-x", poolV2);

        Assert.Equal(1, registry.Count);
        var second = Assert.Single(registry.GetAll());
        Assert.Same(poolV2, second.Pool);
        Assert.True(second.RegisteredAt >= first.RegisteredAt);
    }

    [Fact]
    public async Task GetAll_ConcurrentRegisterRemove_ShouldYieldConsistentSnapshot()
    {
        var registry = new HayateObjectPoolRegistry();
        using var pool = BuildPool("pool-concurrent");
        registry.Register("pool-concurrent", pool);

        // Concurrent register/remove of temporary names; GetAll returns a self-consistent snapshot each time (no exception, consistent count)
        var workers = Enumerable.Range(0, 4).Select(async id =>
        {
            for (var i = 0; i < 200; i++)
            {
                var name = $"temp-{id}-{i}";
                registry.Register(name, pool);
                var snapshot = registry.GetAll();
                Assert.True(snapshot.Count >= 1);
                Assert.True(registry.Remove(name) || snapshot.Any(m => m.PoolName == name));
            }
        });

        await Task.WhenAll(workers);

        // After all concurrent workers finish, only the initial registration remains
        Assert.Equal(1, registry.Count);
        Assert.Equal("pool-concurrent", Assert.Single(registry.GetAll()).PoolName);
    }

    private static IHayateObjectPool<TestObject> BuildPool(string name)
        => new HayatePoolBuilder<TestObject>()
            .WithPoolName(name)
            .WithMinSize(1)
            .WithMaxSize(2)
            .Build();
}
