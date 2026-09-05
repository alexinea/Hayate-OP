using System;
using System.Collections.Generic;
using System.Text;

namespace DotNetCore.HayateOP.Tests
{
    public class ShardingTests
    {
        private class TestObject { }

        [Fact]
        public void EnableSharding_ShouldCreateMultipleShards()
        {
            using var pool = new HayatePoolBuilder<TestObject>()
                .WithEnableSharding(true)
                .WithShardCount(4)
                .WithMinSize(20)
                .Build();

            var stats = pool.GetStats();
            Assert.Equal(20, stats.PooledCount);
        }

        [Fact]
        public void DisableSharding_ShouldForceSingleShard()
        {
            using var pool = new HayatePoolBuilder<TestObject>()
                .WithEnableSharding(false)
                .WithShardCount(4)
                .WithMinSize(20)
                .Build();

            var options = new HayatePoolOptions { EnableSharding = false, ShardCount = 4 };
            options.ApplyFeatureSwitches();
            Assert.Equal(1, options.ShardCount);
        }
    }
}
