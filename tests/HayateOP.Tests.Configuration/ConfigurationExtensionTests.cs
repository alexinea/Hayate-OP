using DotNetCore.HayateOP;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HayateOP.Tests.Configuration
{
    public class ConfigurationExtensionTests
    {
        private class TestObject { }

        [Fact]
        public void AddHayatePoolFromConfiguration_ShouldBindGlobalConfig()
        {
            var configData = new Dictionary<string, string>
            {
                {"HayatePool:Global:MinPoolSize", "10"},
                {"HayatePool:Global:MaxPoolSize", "100"}
            };
            var config = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();

            var services = new ServiceCollection();
            services.AddHayatePoolSupport()
                .RegisterGlobalConfig(config)
                .RegisterHayatePool<TestObject>(config);

            var provider = services.BuildServiceProvider();

            var pool = provider.GetRequiredService<IHayateObjectPool<TestObject>>();
            var stats = pool.GetStats();

            Assert.Equal(10, stats.MinSize);
            Assert.Equal(10, stats.PooledCount);
        }

        [Fact]
        public void AddHayatePool_ShouldBindPoolSpecificConfig()
        {
            var configData = new Dictionary<string, string>
            {
                {"HayatePool:Global:MinPoolSize", "5"},
                {"HayatePool:Pools:TestObject:MinPoolSize", "20"},
                {"HayatePool:Pools:TestObject:MaxPoolSize", "200"}
            };
            var config = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();

            var services = new ServiceCollection();
            services.AddHayatePoolSupport()
                .RegisterGlobalConfig(config)
                .RegisterHayatePool<TestObject>(config);

            var provider = services.BuildServiceProvider();

            var pool = provider.GetRequiredService<IHayateObjectPool<TestObject>>();
            var stats = pool.GetStats();

            Assert.Equal(20, stats.MinSize);
        }
    }
}
