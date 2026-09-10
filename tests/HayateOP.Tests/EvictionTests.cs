using System;
using System.Collections.Generic;
using System.Text;

namespace DotNetCore.HayateOP.Tests
{
    public class EvictionTests
    {
        private class TestObject { }

        [Fact]
        public void EnableEviction_ShouldInitializeEvictionTimer()
        {
            using var pool = new HayatePoolBuilder<TestObject>()
                .WithEnableEviction(true)
                .WithEvictionInterval(1000)
                .Build();

            // Verify the pool works normally
            var obj = pool.Acquire();
            pool.Release(obj);
            Assert.NotNull(obj);
        }

        [Fact]
        public void DisableEviction_ShouldNotInitializeEvictionTimer()
        {
            using var pool = new HayatePoolBuilder<TestObject>()
                .WithEnableEviction(false)
                .Build();

            // Verify the pool works normally
            var obj = pool.Acquire();
            pool.Release(obj);
            Assert.NotNull(obj);
        }

        [Fact(Timeout = 60000)]
        public void MaxLifeTime_ShouldEvictExpiredObjects()
        {
            using var pool = new HayatePoolBuilder<TestObject>()
                .WithEnableEviction(true)
                .WithEvictionInterval(1000) // builder lower bound 1000ms
                .WithMaxLifeTime(TimeSpan.FromMilliseconds(100))
                .WithMinSize(5)
                .Build();

            Assert.Equal(5, pool.GetStats().PooledCount);

            // First borrow one object and then wait: under the BlockTimeout policy an empty-pool Acquire only waits for a return and does not auto-rebuild,
            // if we wait for eviction to clear everything and then Acquire, it will exhaust DefaultAcquireTimeout and throw TimeoutException.
            var borrowed = pool.Acquire();

            // Wait at least two eviction cycles (2.5s >> MaxLifeTime 100ms),
            // so all 4 idle pre-warmed objects in the pool become expired and should be evicted by EvictionCallback.
            Thread.Sleep(2500);

            var pooledCount = pool.GetStats().PooledCount;

            // The original assertion only verified "the pool works normally" and did not verify eviction behavior itself.
            // Eviction is batched by NumTestsPerEvictionRun; here we assert that the pool count actually decreases;
            // under stable conditions (after multiple cycles) it should be 0.
            Assert.True(pooledCount < 5,
                $"Expired objects should have been evicted, actual PooledCount={pooledCount}");

            // After return the pool can still borrow/return normally
            pool.Release(borrowed);
            var obj = pool.Acquire();
            pool.Release(obj);
            Assert.NotNull(obj);
        }
    }
}
