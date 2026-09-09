using System;
using System.Linq;
using System.Threading.Tasks;

namespace DotNetCore.HayateOP.Tests;

/// <summary>
/// M11+（2.5）：注册表完整版——GetAll 枚举（含元数据）/ Remove 反注册 / Count。
/// Register / TryGet 原语义回归一并提供。
/// </summary>
public class RegistryTests
{
    private sealed class TestObject { }

    [Fact(Timeout = 30_000)]
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
        // 注册（≈构建）时间应落在最近一分钟内
        Assert.True(entry.RegisteredAt <= DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.True(entry.RegisteredAt >= DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact(Timeout = 30_000)]
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

        // Names 视图与 GetAll 一致
        var namesLegacy = registry.Names.OrderBy(n => n).ToArray();
        Assert.Equal(names, namesLegacy);
    }

    [Fact(Timeout = 30_000)]
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

        // 重复移除 / 未知名称 / 空白名称均返回 false
        Assert.False(registry.Remove("pool-a"));
        Assert.False(registry.Remove("never-registered"));
        Assert.False(registry.Remove(null));
        Assert.False(registry.Remove("  "));
    }

    [Fact(Timeout = 30_000)]
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

    [Fact(Timeout = 30_000)]
    public async Task GetAll_ConcurrentRegisterRemove_ShouldYieldConsistentSnapshot()
    {
        var registry = new HayateObjectPoolRegistry();
        using var pool = BuildPool("pool-concurrent");
        registry.Register("pool-concurrent", pool);

        // 并发注册/移除临时名称，GetAll 每次返回自洽快照（无异常、计数自洽）
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

        // 并发Worker全部结束后，仅初始注册项留存
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
