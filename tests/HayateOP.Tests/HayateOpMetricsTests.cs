using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DotNetCore.HayateOP.Tests;

public class HayateOpMetricsTests
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
        });
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Get_LogAndMetrics()
    {
        var sp = BuildServiceProvider();
        var pool = sp.GetRequiredService<IHayateObjectPool<TestPooledObject>>();
        var obj = pool.Get();
        pool.Return(obj);
        Assert.NotNull(obj);
    }

    [Fact]
    public void Clear_DisposesAll()
    {
        var sp = BuildServiceProvider();
        var pool = sp.GetRequiredService<IHayateObjectPool<TestPooledObject>>();
        var obj = pool.Get();
        pool.Return(obj);
        pool.Clear();
        Assert.True(obj.IsDisposed);
    }
}