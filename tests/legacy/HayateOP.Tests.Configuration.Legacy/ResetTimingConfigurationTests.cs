using DotNetCore.HayateOP;
using DotNetCore.HayateOP.Policies;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HayateOP.Tests.Configuration
{
    /// <summary>
    /// G-20 (3.1): the reset-timing switches set in configuration reach the pool. The configuration-driven
    /// registrations seed the library's own policy but do not pin it, so the pool derives a policy from its
    /// merged options — and the switches travel in those options. A policy the caller registered is pinned,
    /// and the switches never move its reset.
    /// </summary>
    public class ResetTimingConfigurationTests
    {
        /// <summary>The same observable the fluent-builder acceptance reads. <see cref="FromCallerPolicy"/>
        /// says which policy built the object, so a case can tell the caller's policy from the library's
        /// own.</summary>
        private sealed class ResettableBuffer : IHayateResettable
        {
            public int ResetCount;
            public int Payload;
            public bool FromCallerPolicy;

            public void Reset()
            {
                ResetCount++;
                Payload = 0;
            }
        }

        /// <summary>A caller's own policy, resetting in <c>OnRelease</c> only — so if the switches reached
        /// it the object would be reset on the borrow side instead, which is how the case detects a policy
        /// the fix wrongly replaced.</summary>
        private sealed class CallerPolicy : IHayateObjectPolicy<ResettableBuffer>
        {
            public ResettableBuffer Create() => new ResettableBuffer { FromCallerPolicy = true };

            public bool OnRelease(ResettableBuffer item)
            {
                item.Reset();
                return true;
            }

            public bool Validate(ResettableBuffer item) => true;
            public void OnAcquire(ResettableBuffer item) { }
            public void OnPassivate(ResettableBuffer item) { }
            public void OnDestroy(ResettableBuffer item) { }
        }

        /// <summary>
        /// The global section keys that make a small single-shard pool which creates on demand and never
        /// retires anything, so the object a case borrows is the object it gets back. One shard because a
        /// per-shard capacity of <c>MaxPoolSize / ShardCount</c> would floor to zero with the default four,
        /// no auto-scaling so the ceiling stays put, no eviction so nothing is destroyed between the two
        /// halves of a cycle, and <c>CreateOnDemand</c> so an empty pool hands out a new object instead of
        /// making the case wait out a timeout for a return that will never come.
        /// </summary>
        private static Dictionary<string, string?> DeterministicGlobal()
        {
            return new Dictionary<string, string?>
            {
                { "HayatePool:Global:ShardCount", "1" },
                { "HayatePool:Global:MinPoolSize", "0" },
                { "HayatePool:Global:MaxPoolSize", "4" },
                { "HayatePool:Global:EnableAutoScaling", "false" },
                { "HayatePool:Global:EnableEviction", "false" },
                { "HayatePool:Global:RejectPolicy", "CreateOnDemand" }
            };
        }

        [Fact]
        public void Configuration_ShouldReachThePoolsResetTiming()
        {
            var configData = DeterministicGlobal();
            configData["HayatePool:Pools:ResettableBuffer:ResetOnReturn"] = "false";
            configData["HayatePool:Pools:ResettableBuffer:ResetOnBorrow"] = "true";
            var config = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();

            var services = new ServiceCollection();
            services.AddHayatePoolSupport()
                .RegisterGlobalConfig(config)
                .RegisterHayatePool<ResettableBuffer>(config);

            var provider = services.BuildServiceProvider();
            var pool = provider.GetRequiredService<IHayateObjectPool<ResettableBuffer>>();

            // GetOptions() promised the effective value before this item, and now the pool runs it.
            Assert.False(pool.GetOptions().ResetOnReturn);
            Assert.True(pool.GetOptions().ResetOnBorrow);

            var first = pool.Acquire();

            // The acquire hook runs on a create-on-miss hand-out too, so a brand-new object is reset once
            // before its first borrower sees it.
            Assert.Equal(1, first.ResetCount);

            first.Payload = 7;
            pool.Release(first);

            // The discriminating assertion: the return side was turned off, so the object is still dirty.
            // A switch that was accepted and then ignored would show 0 here.
            Assert.Equal(1, first.ResetCount);
            Assert.Equal(7, first.Payload);

            var second = pool.Acquire();
            Assert.Same(first, second);
            Assert.Equal(0, second.Payload);
        }

        [Fact]
        public void PoolThatAsksForNothing_ShouldKeepTheHistoricalTiming()
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(DeterministicGlobal()).Build();

            var services = new ServiceCollection();
            services.AddHayatePoolSupport()
                .RegisterGlobalConfig(config)
                .RegisterHayatePool<ResettableBuffer>(config);

            var provider = services.BuildServiceProvider();
            var pool = provider.GetRequiredService<IHayateObjectPool<ResettableBuffer>>();

            Assert.True(pool.GetOptions().ResetOnReturn);

            var first = pool.Acquire();
            first.Payload = 7;
            pool.Release(first);

            Assert.Equal(1, first.ResetCount);
            Assert.Equal(0, first.Payload);
        }

        [Fact]
        public void NamedPools_ShouldEachTakeTheirTimingFromConfiguration()
        {
            var configData = DeterministicGlobal();
            configData["HayatePool:Pools:return-side:ResetOnReturn"] = "true";
            configData["HayatePool:Pools:return-side:ResetOnBorrow"] = "false";
            configData["HayatePool:Pools:borrow-side:ResetOnReturn"] = "false";
            configData["HayatePool:Pools:borrow-side:ResetOnBorrow"] = "true";
            var config = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();

            var services = new ServiceCollection();
            services.AddHayatePoolSupport()
                .RegisterGlobalConfig(config)
                .RegisterNamedHayatePool<ResettableBuffer>(config, "return-side")
                .RegisterNamedHayatePool<ResettableBuffer>(config, "borrow-side");

            var provider = services.BuildServiceProvider();
            var accessor = provider.GetRequiredService<IHayateNamedPoolAccessor>();

            var onReturn = accessor.GetPool<ResettableBuffer>("return-side");
            var onBorrow = accessor.GetPool<ResettableBuffer>("borrow-side");

            Assert.True(onReturn.GetOptions().ResetOnReturn);
            Assert.False(onReturn.GetOptions().ResetOnBorrow);
            Assert.False(onBorrow.GetOptions().ResetOnReturn);
            Assert.True(onBorrow.GetOptions().ResetOnBorrow);

            var a = onReturn.Acquire();
            a.Payload = 7;
            onReturn.Release(a);
            Assert.Equal(0, a.Payload); // reset on the way back

            var b = onBorrow.Acquire();
            b.Payload = 7;
            onBorrow.Release(b);
            Assert.Equal(7, b.Payload); // still dirty: this pool resets on the way out
        }

        [Fact]
        public void CallerPolicyInTheContainer_ShouldBePinnedAndKeepItsOwnReset()
        {
            // A caller's policy is pinned, so the switches do not reach it, and it is the policy that
            // runs — not the options-derived default.
            var configData = DeterministicGlobal();
            configData["HayatePool:Pools:ResettableBuffer:ResetOnReturn"] = "false";
            configData["HayatePool:Pools:ResettableBuffer:ResetOnBorrow"] = "true";
            var config = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();

            var services = new ServiceCollection();
            services.AddSingleton<IHayateObjectPolicy<ResettableBuffer>>(new CallerPolicy());
            services.AddHayatePoolSupport()
                .RegisterGlobalConfig(config)
                .RegisterHayatePool<ResettableBuffer>(config);

            var provider = services.BuildServiceProvider();
            var pool = provider.GetRequiredService<IHayateObjectPool<ResettableBuffer>>();

            var buffer = pool.Acquire();
            Assert.True(buffer.FromCallerPolicy);

            buffer.Payload = 7;
            pool.Release(buffer);

            // One reset, from the caller's own OnRelease; the borrow side did nothing.
            Assert.Equal(1, buffer.ResetCount);
            Assert.Equal(0, buffer.Payload);
        }
    }
}
