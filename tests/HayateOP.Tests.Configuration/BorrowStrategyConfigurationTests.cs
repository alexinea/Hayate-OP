using System.Diagnostics;
using DotNetCore.HayateOP;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HayateOP.Tests.Configuration
{
    /// <summary>
    /// G-21 (3.1): the borrow order is settable from configuration. Lru needs nothing but the section —
    /// the order is a plain enum value — while Priority is an order the pool cannot perform without a
    /// comparer, which no configuration section can express, so the container supplies it as a service.
    /// A configuration that names Priority without one is told rather than built with a default order
    /// nobody chose.
    /// </summary>
    public class BorrowStrategyConfigurationTests
    {
        /// <summary>An item the comparer can order by. Mutable on purpose: a case sets the rank on the
        /// instance it borrowed, so the order is decided by the case and not by the policy that created
        /// it.</summary>
        private sealed class Item
        {
            public int Rank { get; set; }
        }

        /// <summary>
        /// The global section keys that make a small single-shard pool which creates on demand and never
        /// retires anything, so the instances a case borrows are the whole content of its idle list and
        /// the order they are returned in is the order of that list.
        /// </summary>
        private static Dictionary<string, string> DeterministicGlobal()
        {
            return new Dictionary<string, string>
            {
                { "HayatePool:Global:ShardCount", "1" },
                { "HayatePool:Global:MinPoolSize", "0" },
                { "HayatePool:Global:MaxPoolSize", "3" },
                { "HayatePool:Global:EnableAutoScaling", "false" },
                { "HayatePool:Global:EnableEviction", "false" },
                { "HayatePool:Global:RejectPolicy", "CreateOnDemand" }
            };
        }

        /// <summary>Orders by rank ascending, which is the direction Priority reads: the pool borrows the
        /// object the comparer ranks highest, so a positive result means "borrow this one first".</summary>
        private static IComparer<Item> HigherRankFirst() =>
            Comparer<Item>.Create((x, y) => x.Rank.CompareTo(y.Rank));

        private static IServiceProvider Container(HayateBorrowStrategy strategy, bool registerComparer)
        {
            var configData = DeterministicGlobal();
            configData["HayatePool:Pools:Item:BorrowStrategy"] = strategy.ToString();
            var config = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();

            var services = new ServiceCollection();
            if (registerComparer) services.AddSingleton(HigherRankFirst());

            services.AddHayatePoolSupport()
                .RegisterGlobalConfig(config)
                .RegisterHayatePool<Item>(config);

            return services.BuildServiceProvider();
        }

        /// <summary>
        /// Spins until <see cref="Stopwatch.GetTimestamp"/> has moved on, so two borrows taken back to
        /// back carry different <c>LastBorrowedAt</c> values — the stamp Lru orders by. Without this the
        /// two borrows could share a tick and the case would be pinning which of two equally-old objects
        /// the walk happens to keep rather than the order itself.
        /// </summary>
        private static void WaitForDistinctTimestamp()
        {
            var start = Stopwatch.GetTimestamp();
            var spins = 0;
            while (Stopwatch.GetTimestamp() == start)
            {
                if (++spins > 1_000_000)
                {
                    Thread.Sleep(1);
                    return;
                }
            }
        }

        [Fact(Timeout = 30_000)]
        public void Configuration_ShouldCarryTheLruOrder()
        {
            var provider = Container(HayateBorrowStrategy.Lru, registerComparer: false);
            var pool = provider.GetRequiredService<IHayateObjectPool<Item>>();

            // The value has to survive the merge and the normalization, and then the pool has to run it —
            // GetOptions alone would also report a value that had been accepted and then ignored.
            Assert.Equal(HayateBorrowStrategy.Lru, pool.GetOptions().BorrowStrategy);

            var borrowedFirst = pool.Acquire();
            WaitForDistinctTimestamp();
            var borrowedLast = pool.Acquire();

            // Returned in the opposite order to the borrows, so the list head is the object borrowed last
            // while the smallest stamp belongs to the object borrowed first. FIFO would serve the head.
            pool.Release(borrowedLast);
            pool.Release(borrowedFirst);

            Assert.Same(borrowedFirst, pool.Acquire());
        }

        [Fact(Timeout = 30_000)]
        public void Priority_ShouldResolveTheComparerFromTheContainer()
        {
            var provider = Container(HayateBorrowStrategy.Priority, registerComparer: true);
            var pool = provider.GetRequiredService<IHayateObjectPool<Item>>();

            Assert.Equal(HayateBorrowStrategy.Priority, pool.GetOptions().BorrowStrategy);

            var low = pool.Acquire();
            low.Rank = 1;
            var high = pool.Acquire();
            high.Rank = 9;

            pool.Release(low);
            pool.Release(high);

            Assert.Same(high, pool.Acquire());
        }

        [Fact]
        public void Priority_ShouldFailFastWithoutARegisteredComparer()
        {
            var provider = Container(HayateBorrowStrategy.Priority, registerComparer: false);

            var ex = Assert.Throws<InvalidOperationException>(
                () => provider.GetRequiredService<IHayateObjectPool<Item>>());

            Assert.Contains("IComparer", ex.Message);
        }
    }
}
