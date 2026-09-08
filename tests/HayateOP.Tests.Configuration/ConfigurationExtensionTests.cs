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
            // T13 回归（默认值误判）：池配置显式设置为与 C# 默认值相同（MinPoolSize=5，
            // 即 DEFAULT_MIN_POOL_SIZE）时，旧实现以「值 != 默认值」判断显式性，
            // 会误判为"未配置"而让全局值 30 生效。正确行为：池配置优先，Min=5。
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
