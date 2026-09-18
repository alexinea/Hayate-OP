using System.Collections.Generic;
using DotNetCore.HayateOP;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HayateOP.Tests.Configuration
{
    public class NamedPoolConfigurationTests
    {
        private class TestObject { }

        [Fact]
        public void RegisterNamedHayatePool_ShouldMergeGlobalAndPoolSections()
        {
            var configData = new Dictionary<string, string>
            {
                {"HayatePool:Global:MinPoolSize", "5"},
                {"HayatePool:Pools:replica:MinPoolSize", "20"},
                {"HayatePool:Pools:replica:MaxPoolSize", "200"}
            };
            var config = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();

            var services = new ServiceCollection();
            services.AddHayatePoolSupport()
                .RegisterGlobalConfig(config)
                .RegisterNamedHayatePool<TestObject>(config, "replica");

            var provider = services.BuildServiceProvider();
            var accessor = provider.GetRequiredService<IHayateNamedPoolAccessor>();
            var pool = accessor.GetPool<TestObject>("replica");

            var stats = pool.GetStats();
            Assert.Equal(20, stats.MinSize);
            Assert.Equal(20, stats.PooledCount);
            Assert.Equal(200, pool.GetOptions().MaxPoolSize);
        }

        [Fact]
        public void RegisterNamedHayatePool_ShouldNotOccupyTheUnnamedPoolSlot()
        {
            var configData = new Dictionary<string, string>
            {
                {"HayatePool:Pools:replica:MaxPoolSize", "16"}
            };
            var config = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();

            var services = new ServiceCollection();
            services.AddHayatePoolSupport()
                .RegisterNamedHayatePool<TestObject>(config, "replica");

            var provider = services.BuildServiceProvider();
            var accessor = provider.GetRequiredService<IHayateNamedPoolAccessor>();
            var registry = provider.GetRequiredService<IHayateObjectPoolRegistry>();

            var pool = accessor.GetPool<TestObject>("replica");

            Assert.Equal(16, pool.GetOptions().MaxPoolSize);
            Assert.Null(provider.GetService<IHayateObjectPool<TestObject>>());
            Assert.True(registry.TryGet("TestObject:replica", out var registered));
            Assert.Same(pool, registered);
        }
    }
}
