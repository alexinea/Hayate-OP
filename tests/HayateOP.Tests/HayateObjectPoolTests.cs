using Microsoft.Extensions.DependencyInjection;

namespace DotNetCore.HayateOP.Tests;

public class HayateObjectPoolTests
{
    [Fact]
    public void Get_WhenPoolEmpty_CreatesNew()
    {
        var services = new ServiceCollection();
        services.AddHayateObjectPool<TestPooledObject>(o =>
        {
            o.MaxConcurrent = 5;
            o.MaxPoolSize = 10;
        });
        var sp = services.BuildServiceProvider();
        var pool = sp.GetRequiredService<IHayateObjectPool<TestPooledObject>>();

        var obj = pool.Get();
        var stats = pool.GetStats();

        Assert.NotNull(obj);
        Assert.Equal(1, stats.TotalCreated);
        Assert.Equal(1, stats.TotalMissed);
    }

    [Fact]
    public void Return_Object_GoesToPool()
    {
        var services = new ServiceCollection();
        services.AddHayateObjectPool<TestPooledObject>();
        var sp = services.BuildServiceProvider();
        var pool = sp.GetRequiredService<IHayateObjectPool<TestPooledObject>>();

        var obj = pool.Get();
        pool.Return(obj);
        var stats = pool.GetStats();

        Assert.Equal(1, stats.PooledCount);
        Assert.Equal(1, stats.TotalReturned);
    }

    [Fact]
    public void Get_AfterReturn_ReturnsSameInstance()
    {
        var services = new ServiceCollection();
        services.AddHayateObjectPool<TestPooledObject>();
        var sp = services.BuildServiceProvider();
        var pool = sp.GetRequiredService<IHayateObjectPool<TestPooledObject>>();

        var obj1 = pool.Get();
        pool.Return(obj1);
        var obj2 = pool.Get();

        Assert.Same(obj1, obj2);
    }

    [Fact]
    public async Task GetAsync_Works()
    {
        var services = new ServiceCollection();
        services.AddHayateObjectPool<TestPooledObject>();
        var sp = services.BuildServiceProvider();
        var pool = sp.GetRequiredService<IHayateObjectPool<TestPooledObject>>();

        var obj = await pool.GetAsync();
        Assert.NotNull(obj);
    }

    [Fact]
    public void Clear_DisposesAll()
    {
        var services = new ServiceCollection();
        services.AddHayateObjectPool<TestPooledObject>();
        var sp = services.BuildServiceProvider();
        var pool = sp.GetRequiredService<IHayateObjectPool<TestPooledObject>>();

        var obj = pool.Get();
        pool.Return(obj);
        pool.Clear();

        Assert.True(obj.IsDisposed);
    }
}