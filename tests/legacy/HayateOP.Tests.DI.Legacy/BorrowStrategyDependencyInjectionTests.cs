using DotNetCore.HayateOP;
using DotNetCore.HayateOP.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace HayateOP.Tests.DI;

/// <summary>
/// G-21 (3.1): a container can ask for the Priority borrow order. The comparer cannot travel through
/// <see cref="HayatePoolOptions"/> — which is not generic, so it cannot name <see cref="IComparer{T}"/> —
/// nor through a configuration section, so the container supplies it as a service instead. A pool that
/// asks for the order without one is told when it is built rather than being handed a default order
/// nobody chose, and a pool that never asked for one never picks a comparer up.
/// </summary>
public class BorrowStrategyDependencyInjectionTests
{
    /// <summary>An item the comparer can order by. Mutable on purpose: a case sets the rank on the
    /// instance it borrowed, so the order is decided by the case and not by the policy that created it.
    /// </summary>
    private sealed class Item
    {
        public int Rank { get; set; }
    }

    /// <summary>
    /// The pool the container hands out: small, single-shard, creating on demand and never evicting, so
    /// the instances a case borrows are the whole content of its idle list and the order they are
    /// returned in is the order of that list.
    /// </summary>
    private static void Deterministic(HayatePoolOptions options)
    {
        options.ShardCount = 1;
        options.MinPoolSize = 0;
        options.MaxPoolSize = 3;
        options.EnableAutoScaling = false;
        options.EnableEviction = false;
        options.RejectPolicy = HayatePoolRejectPolicy.CreateOnDemand;
    }

    /// <summary>Orders by rank ascending, which is the direction Priority reads: the pool borrows the
    /// object the comparer ranks highest, so a positive result means "borrow this one first".</summary>
    private static IComparer<Item> HigherRankFirst() =>
        Comparer<Item>.Create((x, y) => x.Rank.CompareTo(y.Rank));

    private static IServiceCollection Container(HayateBorrowStrategy strategy, bool registerComparer)
    {
        var services = new ServiceCollection();
        if (registerComparer) services.AddSingleton(HigherRankFirst());

        services.AddHayatePoolSupport().RegisterHayatePool<Item>(o =>
        {
            Deterministic(o);
            o.BorrowStrategy = strategy;
        });

        return services;
    }

    [Fact]
    public void Priority_ShouldResolveTheComparerFromTheContainer()
    {
        var provider = Container(HayateBorrowStrategy.Priority, registerComparer: true).BuildServiceProvider();
        var pool = provider.GetRequiredService<IHayateObjectPool<Item>>();

        Assert.Equal(HayateBorrowStrategy.Priority, pool.GetOptions().BorrowStrategy);

        var low = pool.Acquire();
        low.Rank = 1;
        var high = pool.Acquire();
        high.Rank = 9;

        // Returned in creation order, so the list head is `low` — the object FIFO would have served. The
        // comparer asks for `high`, which is what makes this discriminate the container's comparer
        // actually reaching the pool from the pool falling back to its default order.
        pool.Release(low);
        pool.Release(high);

        Assert.Same(high, pool.Acquire());
    }

    [Fact]
    public void Priority_ShouldFailFastWithoutARegisteredComparer()
    {
        // The pool is built on first resolve, so this is where the missing comparer has to surface: a
        // container that named the order but never supplied the thing it needs is a configuration
        // mistake, not a reason to borrow in some default order.
        var provider = Container(HayateBorrowStrategy.Priority, registerComparer: false).BuildServiceProvider();

        var ex = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<IHayateObjectPool<Item>>());

        Assert.Contains("IComparer", ex.Message);
    }

    [Fact]
    public void AFifoPool_ShouldNotPickUpAComparerItNeverAskedFor()
    {
        // The lookup is conditional on the configured order, so a comparer registered for some unrelated
        // purpose does not quietly become this pool's borrow order.
        var provider = Container(HayateBorrowStrategy.Fifo, registerComparer: true).BuildServiceProvider();
        var pool = provider.GetRequiredService<IHayateObjectPool<Item>>();

        Assert.Equal(HayateBorrowStrategy.Fifo, pool.GetOptions().BorrowStrategy);

        var low = pool.Acquire();
        low.Rank = 1;
        var high = pool.Acquire();
        high.Rank = 9;

        pool.Release(low);
        pool.Release(high);

        // FIFO serves the object returned first, which is the one the comparer would have ranked last.
        Assert.Same(low, pool.Acquire());
    }
}
