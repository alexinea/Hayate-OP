// See https://aka.ms/new-console-template for more information

using DotNetCore.Extensions.ObjectPool;

Console.WriteLine("Hello, World!");

var pool = new ObjectPool<PooledObj>(
    factory: () => new(),
    maxConcurrent: 10,
    maxSize: 20);

var obj = pool.Get();
var obj2 = pool.Get();
var obj3 = pool.Get();

try
{
    // do something with ms
    obj.Data = 100;

}
finally
{
    pool.Return(obj);
}

var stat = pool.GetStats();
Console.WriteLine($"池中数量：{stat.PooledCount}， 总创建：{stat.TotalCreated}，总归还：{stat.TotalReturned}，总未命中：{stat.TotalMissed}");

public class PooledObj : IResettable, IDisposable
{
    public int Data { get; set; }

    public void Reset()
    {
        Data = 0; // 归还时清空状态
    }

    public void Dispose()
    {
        // 释放非托管资源
    }
}