using System;
using System.Threading.Tasks;

namespace DotNetCore.HayateOP.Tests
{
    /// <summary>
    /// Keyed pooling (O-A): one sub-pool per key.
    /// Acceptance: a key borrows only from its own sub-pool and never receives another key's object; capacity
    /// is per key, so one exhausted key leaves the others serving; returning goes back to the key it was
    /// borrowed from; keys are counted and enumerable and can be retired; every sub-pool is registered under a
    /// derived name so the registry can address it, and disposal deregisters and disposes them all.
    /// </summary>
    public class ParameterizedPoolTests
    {
        private class TestObject { }

        private static ParameterizedHayatePool<string, TestObject> CreateKeyedPool(
            int maxSizePerKey = 1,
            IHayateObjectPoolRegistry? registry = null,
            Action<string, HayatePoolOptions>? configure = null)
        {
            return new ParameterizedHayatePool<string, TestObject>(
                key => new TestObject(),
                maxSizePerKey,
                configure: configure,
                registry: registry,
                name: "keyed");
        }

        [Fact]
        public void GetObject_ShouldGiveEachKeyItsOwnObject()
        {
            using (var pools = CreateKeyedPool(maxSizePerKey: 2))
            {
                var a = pools.GetObject("a");
                var b = pools.GetObject("b");

                Assert.NotNull(a);
                Assert.NotNull(b);
                // The whole point of keying: one key's sub-pool never hands out another key's object.
                Assert.NotSame(a, b);

                pools.ReturnObject("a", a);
                pools.ReturnObject("b", b);

                // Each returned object waits in its own key's sub-pool, so each key gets its own back.
                Assert.Same(a, pools.GetObject("a"));
                Assert.Same(b, pools.GetObject("b"));
            }
        }

        [Fact]
        public void Capacity_ShouldBePerKeyRatherThanShared()
        {
            using (var pools = CreateKeyedPool(maxSizePerKey: 1))
            {
                // Two keys, one object each: a shared pool of one could not serve both, and each sub-pool
                // reports the full per-key size rather than a slice of it.
                var a = pools.GetObject("a");
                var b = pools.GetObject("b");

                Assert.NotSame(a, b);
                Assert.Equal(1, pools.GetPool("a").GetOptions().MaxPoolSize);
                Assert.Equal(1, pools.GetPool("b").GetOptions().MaxPoolSize);

                pools.ReturnObject("a", a);
                pools.ReturnObject("b", b);
            }
        }

        [Fact]
        public void PerKeySize_ShouldSetTheOptionsItPromises()
        {
            using (var pools = CreateKeyedPool(maxSizePerKey: 2))
            {
                var options = pools.GetPool("a").GetOptions();
                Assert.Equal(2, options.MaxPoolSize);
                // Nothing is pre-created and nothing is held in reserve, so an unused key costs nothing.
                Assert.Equal(0, options.MinPoolSize);
                // Not the default four: sharding divides capacity across shards, and a size of two split four
                // ways would leave shards that can hold nothing at all.
                Assert.Equal(2, options.ShardCount);
                // A sub-pool starts empty, so a miss inside the size has to create rather than wait -- with the
                // default wait-then-timeout policy every borrow past the first would stall.
                Assert.Equal(HayatePoolRejectPolicy.CreateOnDemand, options.RejectPolicy);
            }

            // A size at or above the default shard count keeps the default sharding ...
            using (var wide = new ParameterizedHayatePool<string, TestObject>(key => new TestObject(), 8, name: "wide"))
            {
                Assert.Equal(new HayatePoolOptions().ShardCount, wide.GetPool("a").GetOptions().ShardCount);
            }

            // ... and a size of one is a single shard rather than one shard plus three empty ones.
            using (var single = new ParameterizedHayatePool<string, TestObject>(key => new TestObject(), 1, name: "single"))
            {
                Assert.Equal(1, single.GetPool("a").GetOptions().ShardCount);
            }
        }

        [Fact]
        public void ReturnObject_ShouldGiveTheObjectBackToItsOwnKey()
        {
            using (var pools = CreateKeyedPool(maxSizePerKey: 1))
            {
                var item = pools.GetObject("a");
                pools.ReturnObject("a", item);

                // A returned object waits in its own key's sub-pool, ready for the next borrow of that key.
                Assert.Same(item, pools.GetObject("a"));
            }
        }

        [Fact]
        public void ReturnObject_ShouldRejectAKeyThatHasNoSubPool()
        {
            using (var pools = CreateKeyedPool())
            {
                Assert.Throws<InvalidOperationException>(() => pools.ReturnObject("never-used", new TestObject()));
                Assert.Throws<ArgumentNullException>(() => pools.ReturnObject("a", null!));
            }
        }

        [Fact]
        public void Keys_ShouldCountAndExposeOnlyTheKeysInUse()
        {
            using (var pools = CreateKeyedPool(maxSizePerKey: 2))
            {
                Assert.Equal(0, pools.KeysInPoolCount);

                pools.GetObject("a");
                Assert.Equal(1, pools.KeysInPoolCount);

                pools.GetObject("b");
                pools.GetObject("a");       // a second borrow of a known key adds no key
                Assert.Equal(2, pools.KeysInPoolCount);

                Assert.Equal(2, pools.Keys.Count);
                Assert.Contains("a", pools.Keys);
                Assert.Contains("b", pools.Keys);
            }
        }

        [Fact]
        public void GetPool_ShouldAddressTheSameSubPoolForAKey()
        {
            using (var pools = CreateKeyedPool(maxSizePerKey: 2))
            {
                var item = pools.GetObject("a");
                var poolA = pools.GetPool("a");

                Assert.Same(poolA, pools.GetPool("a"));
                Assert.NotSame(poolA, pools.GetPool("b"));

                // The sub-pool is an ordinary pool, so a key's own statistics and lifecycle are reachable.
                Assert.Equal(1, poolA.GetStats().TotalAcquired);
                poolA.Clear();
                Assert.Equal(0, poolA.GetStats().PooledCount);

                pools.ReturnObject("a", item);
            }
        }

        [Fact]
        public void SubPools_ShouldHaveIndependentLifecycles()
        {
            using (var pools = CreateKeyedPool(maxSizePerKey: 2))
            {
                var a = pools.GetObject("a");
                var b = pools.GetObject("b");
                pools.ReturnObject("a", a);
                pools.ReturnObject("b", b);

                Assert.Equal(1, pools.GetPool("a").GetStats().PooledCount);
                Assert.Equal(1, pools.GetPool("b").GetStats().PooledCount);

                // Draining one key leaves the other's object in place.
                pools.GetPool("a").Clear();
                Assert.Equal(0, pools.GetPool("a").GetStats().PooledCount);
                Assert.Equal(1, pools.GetPool("b").GetStats().PooledCount);
            }
        }

        [Fact]
        public void Clear_ShouldDrainEverySubPoolButKeepTheKeys()
        {
            using (var pools = CreateKeyedPool(maxSizePerKey: 2))
            {
                pools.ReturnObject("a", pools.GetObject("a"));
                pools.ReturnObject("b", pools.GetObject("b"));

                pools.Clear();

                Assert.Equal(0, pools.GetPool("a").GetStats().PooledCount);
                Assert.Equal(0, pools.GetPool("b").GetStats().PooledCount);
                Assert.Equal(2, pools.KeysInPoolCount);
            }
        }

        [Fact]
        public async Task GetObjectAsync_ShouldBorrowFromTheKeySubPool()
        {
            using (var pools = CreateKeyedPool(maxSizePerKey: 2))
            {
                var a = await pools.GetObjectAsync("a");
                var b = await pools.GetObjectAsync("b");

                Assert.NotSame(a, b);
                Assert.Equal(2, pools.KeysInPoolCount);

                pools.ReturnObject("a", a);
                pools.ReturnObject("b", b);
            }
        }

        [Fact]
        public void SubPools_ShouldBeRegisteredUnderADerivedName()
        {
            var registry = new HayateObjectPoolRegistry();

            using (var pools = CreateKeyedPool(maxSizePerKey: 1, registry: registry))
            {
                pools.GetObject("a");
                pools.GetObject("b");

                Assert.Equal(2, registry.Count);

                // The sub-pool is registered, so management endpoints and metrics can address a single key.
                Assert.True(registry.TryGet("keyed[a]", out var resolved));
                Assert.Same(pools.GetPool("a"), resolved);
            }

            // Disposal leaves nothing behind in the registry.
            Assert.Equal(0, registry.Count);
        }

        [Fact]
        public void TryRemove_ShouldRetireTheKeyAndItsSubPool()
        {
            var registry = new HayateObjectPoolRegistry();
            using (var pools = CreateKeyedPool(maxSizePerKey: 1, registry: registry))
            {
                var item = pools.GetObject("a");
                pools.GetObject("b");

                Assert.True(pools.TryRemove("a"));
                Assert.False(pools.TryRemove("a"));     // already gone
                Assert.Equal(1, pools.KeysInPoolCount);
                Assert.False(pools.TryGetPool("a", out _));
                Assert.False(registry.TryGet("keyed[a]", out _));

                // An object borrowed from a retired key has nowhere to go.
                Assert.Throws<InvalidOperationException>(() => pools.ReturnObject("a", item));

                // A retired key is not banned: using it again builds a fresh sub-pool.
                Assert.Same(pools.GetPool("a"), pools.GetPool("a"));
                Assert.Equal(2, pools.KeysInPoolCount);
            }
        }

        [Fact]
        public void Dispose_ShouldDisposeEverySubPoolAndTakeItOutOfService()
        {
            var pools = CreateKeyedPool(maxSizePerKey: 1);
            pools.GetObject("a");

            pools.Dispose();
            pools.Dispose();   // idempotent

            Assert.Equal(0, pools.KeysInPoolCount);
            Assert.Throws<ObjectDisposedException>(() => pools.GetObject("a"));
            // A new key would otherwise create a sub-pool that nothing will ever dispose.
            Assert.Throws<ObjectDisposedException>(() => pools.GetObject("fresh"));
        }

        [Fact]
        public void Constructor_ShouldRejectArgumentsItCannotUse()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new ParameterizedHayatePool<string, TestObject>(
                    (Func<string, IHayateObjectPool<TestObject>>)null!));
            Assert.Throws<ArgumentNullException>(() =>
                new ParameterizedHayatePool<string, TestObject>((Func<string, TestObject>)null!, 1));

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new ParameterizedHayatePool<string, TestObject>(key => new TestObject(), 0));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new ParameterizedHayatePool<string, TestObject>(key => new TestObject(), -1));

            using (var pools = CreateKeyedPool())
            {
                Assert.Throws<ArgumentNullException>(() => pools.GetObject(null!));
            }
        }

        [Fact]
        public void FactoryReturningNull_ShouldFailAtTheCreationBoundary()
        {
            using (var pools = new ParameterizedHayatePool<string, TestObject>(key => null!, 1))
            {
                Assert.Throws<InvalidOperationException>(() => pools.GetObject("a"));
            }
        }
    }
}
