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

            // The pool factory is resolved lazily - resolve it once first to make it register itself with IHayateObjectPoolRegistry,
            // so the management endpoints can find it by the logical pool name "TestObject" (replacing Type.GetType reflection-based addressing).
            var svc = host.Services.GetRequiredService<IHayateObjectPool<TestObject>>();
            Assert.NotNull(svc);

            var client = host.GetTestClient();

            // Act: pool name = typeof(TestObject).Name = "TestObject"
            var response = await client.GetAsync("/hayateop/TestObject/stats");

            // Assert: registry addressing should hit and return 200 + stats detail
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

            // Act: a pool name that was never registered
            var response = await client.GetAsync("/hayateop/DoesNotExist");

            // Assert: registry miss -> 404
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

            // The pool factory is resolved lazily - resolve it once first to make it register itself with IHayateObjectPoolRegistry
            var svc = host.Services.GetRequiredService<IHayateObjectPool<TestObject>>();
            Assert.NotNull(svc);

            // Act: request the pool-list endpoint
            var response = await host.GetTestClient().GetAsync("/hayateop/pools");

            // Assert: the full registry enumeration should include the registered pool's name and element type
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

            // Act: supports a registered-but-unresolved pool -> registry is empty
            var response = await client.GetAsync("/hayateop/pools");

            // Assert: empty list returns 200, not 404/500
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
