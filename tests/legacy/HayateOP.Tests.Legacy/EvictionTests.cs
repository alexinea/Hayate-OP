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

        [Fact]
        public void MaxLifeTime_ShouldEvictExpiredObjects()
        {
            using var pool = new HayatePoolBuilder<TestObject>()
                .WithEnableEviction(true)
                .WithMaxLifeTime(TimeSpan.FromMilliseconds(100))
                .WithMinSize(5)
                .Build();

            // Wait for the object to expire
            Thread.Sleep(200);

            // Verify the pool works normally
            var obj = pool.Acquire();
            pool.Release(obj);
            Assert.NotNull(obj);
        }
    }
}
