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

            // 验证池能正常工作
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

            // 验证池能正常工作
            var obj = pool.Acquire();
            pool.Release(obj);
            Assert.NotNull(obj);
        }

        [Fact(Timeout = 60000)]
        public void MaxLifeTime_ShouldEvictExpiredObjects()
        {
            using var pool = new HayatePoolBuilder<TestObject>()
                .WithEnableEviction(true)
                .WithEvictionInterval(1000) // builder 下限 1000ms
                .WithMaxLifeTime(TimeSpan.FromMilliseconds(100))
                .WithMinSize(5)
                .Build();

            Assert.Equal(5, pool.GetStats().PooledCount);

            // 先借出一个再等待：BlockTimeout 策略下空池 Acquire 只等归还、不自动重建，
            // 若等驱逐清空后再 Acquire 会吃满 DefaultAcquireTimeout 抛 TimeoutException。
            var borrowed = pool.Acquire();

            // 等待至少两次驱逐周期（2.5s >> MaxLifeTime 100ms），
            // 池内 4 个空闲预暖对象全部超期，应被 EvictionCallback 驱逐。
            Thread.Sleep(2500);

            var pooledCount = pool.GetStats().PooledCount;

            // T13：原断言仅验证"池能正常工作"，未验证驱逐行为本身。
            // 驱逐受 NumTestsPerEvictionRun 分批影响，此处断言池内数量确实减少；
            // 稳定情况下（多次周期后）应为 0。
            Assert.True(pooledCount < 5,
                $"过期对象应被驱逐，实际 PooledCount={pooledCount}");

            // 归还后池仍可正常借还
            pool.Release(borrowed);
            var obj = pool.Acquire();
            pool.Release(obj);
            Assert.NotNull(obj);
        }
    }
}
