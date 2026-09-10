using DotNetCore.HayateOP;
using DotNetCore.HayateOP.Prometheus;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DotNetCore.HayateOP.Tests.OpenTelemetry;

/// <summary>
/// M22 验收：Prometheus 序列化器与抓取端点。
/// <para>
/// 三层验证：①Prometheus 文本格式（HELP/TYPE/样本行 + 标签转义）；
/// ②真实池端到端（registry + GetStats 取数，借出后计数递增）；
/// ③DI 注册与 ASP.NET Core 端点映射（net8+）。
/// </para>
/// </summary>
public class PrometheusExporterTests
{
    private sealed class PooledResource
    {
        public int Id { get; set; }
    }

    private static (HayateObjectPoolRegistry Registry, IHayateObjectPool<PooledResource> Pool) BuildPool(string name, int min = 3, int max = 10)
    {
        var registry = new HayateObjectPoolRegistry();
        var pool = new HayatePoolBuilder<PooledResource>()
            .WithPoolName(name)
            .WithMinSize(min)
            .WithMaxSize(max)
            .WithEnableAutoScaling(false)
            .WithEnableEviction(false)
            .Build();

        registry.Register(name, pool);
        return (registry, pool);
    }

    [Fact]
    public void Scrape_ShouldEmitPrometheusTextFormat()
    {
        var (registry, pool) = BuildPool("prom-pool");
        try
        {
            var text = new HayatePrometheusExporter(registry).Scrape();

            // 指标族头 + 带池名标签的样本行
            Assert.Contains("# TYPE hayateop_pool_size gauge", text);
            Assert.Contains("# HELP hayateop_pool_size ", text);
            Assert.Contains("hayateop_pool_size{pool=\"prom-pool\"} ", text);

            Assert.Contains("# TYPE hayateop_pool_created_total counter", text);
            Assert.Contains("hayateop_pool_acquired_total{pool=\"prom-pool\"} ", text);
            Assert.Contains("hayateop_pool_leak_suspected_total{pool=\"prom-pool\"} ", text);
            Assert.Contains("hayateop_pool_wait_average_milliseconds{pool=\"prom-pool\"} ", text);

            // 未启用 M3 分配追踪的池不出现分配指标族
            Assert.DoesNotContain("hayateop_pool_acquire_allocated_bytes_average", text);

            // 端到端：借出一次后 acquired_total 递增
            var item = pool.Acquire();
            var after = new HayatePrometheusExporter(registry).Scrape();
            Assert.Contains("hayateop_pool_acquired_total{pool=\"prom-pool\"} 1", after);

            pool.Release(item);
        }
        finally
        {
            pool.Dispose();
        }
    }

    [Fact]
    public void Scrape_ShouldEscapeLabelValues()
    {
        var (registry, pool) = BuildPool("we\"ird\nname");
        try
        {
            var text = new HayatePrometheusExporter(registry).Scrape();

            // 引号与换行按协议转义，保证单行样本格式不被破坏
            Assert.Contains("pool=\"we\\\"ird\\nname\"", text);
        }
        finally
        {
            pool.Dispose();
        }
    }

    [Fact]
    public void Scrape_ShouldHonourNamespacePrefixAndAllocationTracking()
    {
        var registry = new HayateObjectPoolRegistry();
        var pool = new HayatePoolBuilder<PooledResource>()
            .WithPoolName("alloc-pool")
            .WithMinSize(2)
            .WithMaxSize(6)
            .WithEnableAutoScaling(false)
            .WithEnableEviction(false)
            .WithEnableAllocationTracking(true)
            .Build();
        registry.Register("alloc-pool", pool);

        try
        {
            var item = pool.Acquire();
            pool.Release(item);

            var text = new HayatePrometheusExporter(registry, new HayatePrometheusOptions { Namespace = "hayate" }).Scrape();
            Assert.Contains("hayate_pool_size{pool=\"alloc-pool\"} ", text);
            Assert.DoesNotContain("hayateop_pool_size", text);

            // M3 追踪开启 → 分配指标族出现
            Assert.Contains("# TYPE hayate_pool_acquire_allocated_bytes_average gauge", text);
            Assert.Contains("hayate_pool_release_allocated_bytes_average{pool=\"alloc-pool\"} ", text);
        }
        finally
        {
            pool.Dispose();
        }
    }

    [Fact]
    public void Scrape_WithoutRegistry_ShouldReturnEmptyText()
    {
        var text = new HayatePrometheusExporter().Scrape();
        Assert.Equal(string.Empty, text);
    }

    [Fact]
    public void AddHayatePrometheusExporter_ShouldRegisterResolvableSingleton()
    {
        var (registry, pool) = BuildPool("di-pool");
        try
        {
            var services = new ServiceCollection();
            services.AddSingleton<IHayateObjectPoolRegistry>(registry);
            services.AddHayatePrometheusExporter();

            using var provider = services.BuildServiceProvider();
            var exporter = provider.GetRequiredService<HayatePrometheusExporter>();

            Assert.Contains("hayateop_pool_size{pool=\"di-pool\"} ", exporter.Scrape());
        }
        finally
        {
            pool.Dispose();
        }
    }

    [Fact]
    public void UseHayatePrometheusExporter_ShouldMapScrapeEndpoint()
    {
        var (registry, pool) = BuildPool("endpoint-pool");
        try
        {
            var app = WebApplication.CreateBuilder().Build();
            app.UseHayatePrometheusExporter("/custom/metrics");

            var routes = ((IEndpointRouteBuilder)app).DataSources
                .SelectMany(ds => ds.Endpoints)
                .OfType<RouteEndpoint>()
                .Select(e => e.RoutePattern.RawText)
                .ToList();

            Assert.Contains("/custom/metrics", routes);
        }
        finally
        {
            pool.Dispose();
        }
    }
}
