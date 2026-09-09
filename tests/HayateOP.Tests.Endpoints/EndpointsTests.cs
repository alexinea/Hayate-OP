using DotNetCore.HayateOP;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace HayateOP.Tests.Endpoints
{
    public class EndpointsTests
    {
        private class TestObject { }

        [Fact]
        public async Task MapHayatePoolEndpoints_ShouldMapOverviewEndpoint()
        {
            // Arrange
            var hostBuilder = Host.CreateDefaultBuilder()
                .ConfigureWebHost(webHost =>
                {
                    webHost.UseTestServer();
                    webHost.Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapHayatePoolEndpoints();
                        });
                    });
                    webHost.ConfigureServices(services =>
                    {
                        services.AddRouting();
                        services.AddHayatePoolSupport().RegisterHayatePool<TestObject>();
                    });
                });

            using var host = await hostBuilder.StartAsync();
            var client = host.GetTestClient();

            // Act
            var response = await client.GetAsync("/hayateop");

            // Assert
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync();
            Assert.Contains("Hayate Object Pool", content);
        }

        [Fact]
        public async Task MapHayatePoolEndpoints_ShouldMapStatsEndpoint()
        {
            // Arrange
            var hostBuilder = Host.CreateDefaultBuilder()
                .ConfigureWebHost(webHost =>
                {
                    webHost.UseTestServer();
                    webHost.Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapHayatePoolEndpoints();
                        });
                    });
                    webHost.ConfigureServices(services =>
                    {
                        services.AddRouting();
                        services.AddHayatePoolSupport().RegisterHayatePool<TestObject>();
                    });
                });

            using var host = await hostBuilder.StartAsync();

            // T05：池工厂是懒解析的——先解析一次以触发其把自身注册进 IHayateObjectPoolRegistry，
            // 这样管理端点才能按逻辑池名 "TestObject" 找到它（替代 Type.GetType 反射寻址）。
            var svc = host.Services.GetRequiredService<IHayateObjectPool<TestObject>>();
            Assert.NotNull(svc);

            var client = host.GetTestClient();

            // Act：池名 = typeof(TestObject).Name = "TestObject"
            var response = await client.GetAsync("/hayateop/TestObject/stats");

            // Assert：注册表寻址应命中并返回 200 + stats 明细
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync();
            Assert.Contains("stats", content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("pooledCount", content, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task MapHayatePoolEndpoints_Detail_Returns404_WhenPoolNotRegistered()
        {
            // Arrange
            var hostBuilder = Host.CreateDefaultBuilder()
                .ConfigureWebHost(webHost =>
                {
                    webHost.UseTestServer();
                    webHost.Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapHayatePoolEndpoints();
                        });
                    });
                    webHost.ConfigureServices(services =>
                    {
                        services.AddRouting();
                        services.AddHayatePoolSupport();
                    });
                });

            using var host = await hostBuilder.StartAsync();
            var client = host.GetTestClient();

            // Act：一个从未注册过的池名
            var response = await client.GetAsync("/hayateop/DoesNotExist");

            // Assert：注册表未命中 → 404
            Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
        }

        [Fact]
        public async Task MapHayatePoolEndpoints_Pools_ReturnsRegistryEnumeration()
        {
            // Arrange
            var hostBuilder = Host.CreateDefaultBuilder()
                .ConfigureWebHost(webHost =>
                {
                    webHost.UseTestServer();
                    webHost.Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapHayatePoolEndpoints();
                        });
                    });
                    webHost.ConfigureServices(services =>
                    {
                        services.AddRouting();
                        services.AddHayatePoolSupport().RegisterHayatePool<TestObject>();
                    });
                });

            using var host = await hostBuilder.StartAsync();

            // 池工厂是懒解析的——先解析一次以触发其把自身注册进 IHayateObjectPoolRegistry
            var svc = host.Services.GetRequiredService<IHayateObjectPool<TestObject>>();
            Assert.NotNull(svc);

            // Act：请求池列表端点
            var response = await host.GetTestClient().GetAsync("/hayateop/pools");

            // Assert：M11+ 注册表完整版枚举——应包含已注册池的名称与元素类型
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync();
            Assert.Contains("TestObject", content);
            Assert.Contains("registeredAt", content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("pooledCount", content, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task MapHayatePoolEndpoints_Pools_ReturnsEmpty_WhenNoPoolResolved()
        {
            // Arrange
            var hostBuilder = Host.CreateDefaultBuilder()
                .ConfigureWebHost(webHost =>
                {
                    webHost.UseTestServer();
                    webHost.Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapHayatePoolEndpoints();
                        });
                    });
                    webHost.ConfigureServices(services =>
                    {
                        services.AddRouting();
                        services.AddHayatePoolSupport();
                    });
                });

            using var host = await hostBuilder.StartAsync();
            var client = host.GetTestClient();

            // Act：支持已注册但未解析任何池 → 注册表为空
            var response = await client.GetAsync("/hayateop/pools");

            // Assert：空列表 200，而非 404/500
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync();
            Assert.Contains("[]", content);
        }

        [Fact]
        public void AddHayatePoolCore_WithAspNetCore_ShouldWork()
        {
            // Arrange
            var services = new ServiceCollection();
            services.AddHayatePoolSupport().RegisterHayatePool<TestObject>();

            // Act
            var provider = services.BuildServiceProvider();
            var pool = provider.GetService<IHayateObjectPool<TestObject>>();

            // Assert
            Assert.NotNull(pool);
        }
    }
}
