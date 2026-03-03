using HayateOP.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace HayateOP.Tests;

public class HayateOpHealthCheckTests
{
    private IServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging(cfg => cfg.AddConsole());
        services.AddHayateObjectPool<TestPooledObject>(o =>
        {
            o.MaxConcurrent = 5;
            o.MaxPoolSize = 10;
            o.EnableMetrics = true;
        }).AddHealthChecks();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task HealthCheck_ReturnsHealthy()
    {
        var sp = BuildServiceProvider();
        var healthCheck = sp.GetRequiredService<HayateOpHealthCheck<TestPooledObject>>();
        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Healthy, result.Status);
    }
}