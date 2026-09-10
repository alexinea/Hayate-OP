using DotNetCore.HayateOP;
using DotNetCore.HayateOP.Prometheus;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DotNetCore.HayateOP.Tests.OpenTelemetry;

/// <summary>
/// Acceptance: Prometheus serializer and scrape endpoint.
/// <para>
/// Three layers of validation: 1) Prometheus text format (HELP/TYPE/sample lines + label escaping);
/// 2) Real pool end-to-end (registry + GetStats data fetch, counter increments after borrowing);
/// 3) DI registration and ASP.NET Core endpoint mapping (net8+).
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

            // Metric family header + sample lines with pool-name labels
            Assert.Contains("# TYPE hayateop_pool_size gauge", text);
            Assert.Contains("# HELP hayateop_pool_size ", text);
            Assert.Contains("hayateop_pool_size{pool=\"prom-pool\"} ", text);

            Assert.Contains("# TYPE hayateop_pool_created_total counter", text);
            Assert.Contains("hayateop_pool_acquired_total{pool=\"prom-pool\"} ", text);
            Assert.Contains("hayateop_pool_leak_suspected_total{pool=\"prom-pool\"} ", text);
            Assert.Contains("hayateop_pool_wait_average_milliseconds{pool=\"prom-pool\"} ", text);

            // A pool without allocation tracking does not expose the allocation metric family
            Assert.DoesNotContain("hayateop_pool_acquire_allocated_bytes_average", text);

            // End-to-end: acquired_total increments after a single borrow
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

            // Quotes and newlines are escaped per the protocol so the single-line sample format is preserved
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

            // Allocation tracking enabled -> the allocation metric family appears
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
