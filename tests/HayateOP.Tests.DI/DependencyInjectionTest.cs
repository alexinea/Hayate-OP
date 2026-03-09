using DotNetCore.HayateOP;
using DotNetCore.HayateOP.Metrics;
using DotNetCore.HayateOP.Scaling;
using Microsoft.Extensions.DependencyInjection;

namespace HayateOP.Tests.DI
{
    public class DependencyInjectionTest
    {
        private class TestObject { }

        [Fact]
        public void AddHayatePoolCore_ShouldRegisterCoreServices()
        {
            var services = new ServiceCollection();
            services.AddHayatePoolSupport();
            var provider = services.BuildServiceProvider();

            Assert.NotNull(provider.GetService<IHayateScalingStrategy>());
            Assert.NotNull(provider.GetService<IHayateMetrics>());
        }

        [Fact]
        public void AddHayatePool_ShouldRegisterPoolAsSingleton()
        {
            var services = new ServiceCollection();
            services
                .AddHayatePoolSupport()
                .RegisterHayatePool<TestObject>();
            var provider = services.BuildServiceProvider();

            var pool1 = provider.GetRequiredService<IHayateObjectPool<TestObject>>();
            var pool2 = provider.GetRequiredService<IHayateObjectPool<TestObject>>();

            Assert.Same(pool1, pool2);
        }

        [Fact]
        public void AddHayatePool_WithConfigure_ShouldApplyOptions()
        {
            var services = new ServiceCollection();
            services
                .AddHayatePoolSupport()
                .RegisterHayatePool<TestObject>(opt =>
            {
                opt.MinPoolSize = 20;
                opt.MaxPoolSize = 200;
            });
            var provider = services.BuildServiceProvider();

            var pool = provider.GetRequiredService<IHayateObjectPool<TestObject>>();
            var stats = pool.GetStats();

            Assert.Equal(20, stats.MinSize);
            Assert.Equal(20, stats.PooledCount);
        }
    }
}
