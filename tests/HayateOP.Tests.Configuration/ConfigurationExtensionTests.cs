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

        [Fact]
        public void AddHayatePool_ShouldApplyPoolValueEqualToBuiltInDefault()
        {
            // Regression (default-value misjudgment): when the pool config is set explicitly to the same value as the C# default (MinPoolSize=5,
            // i.e. DEFAULT_MIN_POOL_SIZE), the old implementation judged explicitness by "value != default",
            // which misjudged it as "not configured" and let the global value 30 take effect. Correct behavior: the pool config takes precedence, Min=5.
            var configData = new Dictionary<string, string>
            {
                {"HayatePool:Global:MinPoolSize", "30"},
                {"HayatePool:Pools:TestObject:MinPoolSize", "5"}
            };
            var config = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();

            var services = new ServiceCollection();
            services.AddHayatePoolSupport()
                .RegisterGlobalConfig(config)
                .RegisterHayatePool<TestObject>(config);

            var provider = services.BuildServiceProvider();

            var pool = provider.GetRequiredService<IHayateObjectPool<TestObject>>();
            var stats = pool.GetStats();

            Assert.Equal(5, stats.MinSize);
            Assert.Equal(5, stats.PooledCount);
        }
    }
}
