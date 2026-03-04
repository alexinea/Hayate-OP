using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DotNetCore.HayateOP.Tests;

public class HayateOpScalingTests
{
    private IServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging(cfg => cfg.AddConsole());
        services.AddHayateObjectPool<TestPooledObject>(o =>
        {
            o.MinPoolSize = 5;
            o.MaxPoolSize = 20;
            o.MaxConcurrent = 10;
            o.ScalingIntervalMilliseconds = 500;
        }).AddHealthChecks();
        return services.BuildServiceProvider();
    }
    
    [Fact]
    public void Stats_IncludeScaleInfo()
    {
        var sp = BuildServiceProvider();
        var pool = sp.GetRequiredService<IHayateObjectPool<TestPooledObject>>();
        var stats = pool.GetStats();

        Assert.True(stats.MinSize > 0);
        Assert.True(stats.MaxSize >= stats.MinSize);
    }
}