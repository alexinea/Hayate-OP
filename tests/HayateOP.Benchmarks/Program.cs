// See https://aka.ms/new-console-template for more information

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using DotNetCore.HayateOP;

BenchmarkRunner.Run<HayateOpBenchmark>();

Console.WriteLine("Hello, World!");

public class TestItem : IHayateResettable, IDisposable
{
    public int Id { get; set; }
    public int Value { get; set; }

    public void Reset()
    {
        Id = 0;
        Value = 0;
    }

    public void Dispose()
    {
    }
}

[MemoryDiagnoser]
public class HayateOpBenchmark
{
    private IHayateObjectPool<TestItem> _pool;

    private Microsoft.Extensions.ObjectPool.ObjectPool<TestItem> _msPool;
    private Microsoft.Extensions.ObjectPool.ObjectPoolProvider _provider = new Microsoft.Extensions.ObjectPool.DefaultObjectPoolProvider();

    [GlobalSetup]
    public void Setup()
    {
        var options = new HayatePoolOptions
        {
            MinPoolSize = 32,
            MaxPoolSize = 256,
            MaxConcurrent = 128,
        };
        _pool = new HayateObjectPoolFactory().GetPool<TestItem>(options);

        // MSOP
        _msPool = _provider.Create<TestItem>(new MsReusableObjectPolicy());
    }

    [Benchmark(Description = "HayateOP.GetAndReturn")]
    public TestItem GetReturn()
    {
        var item = _pool.Get();

        try
        {
            // 模拟使用...
            return item;
        }
        finally
        {
            _pool.Return(item); // 必须归还
        }
    }

    [Benchmark(Description = "MSOP.GetAndReturn")]
    public TestItem MsGetReturn()
    {
        var obj = _msPool.Get(); // 从池获取
        try
        {
            // 模拟使用...
            return obj;
        }
        finally
        {
            _msPool.Return(obj); // 必须归还
        }
    }

    [Benchmark(Baseline = true)]
    public TestItem NewEachTime()
    {
        var item = new TestItem();
        return item;
    }
}

#region MS OP

// 自定义策略：告诉池如何创建和重置对象
public class MsReusableObjectPolicy : Microsoft.Extensions.ObjectPool.PooledObjectPolicy<TestItem>
{
    public override TestItem Create() => new TestItem();

    // Return 时会调用这个方法来清空/重置对象状态
    public override bool Return(TestItem obj)
    {
        // 重置逻辑：将对象恢复到干净状态
        obj.Id = 0;
        obj.Value = 0;
        return true; // 返回 true 表示可以放回池中，false 表示丢弃
    }
}

#endregion