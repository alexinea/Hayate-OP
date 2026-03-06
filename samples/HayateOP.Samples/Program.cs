// See https://aka.ms/new-console-template for more information

using System.Text;
using DotNetCore.HayateOP;
using DotNetCore.HayateOP.Samples;
using Microsoft.Extensions.DependencyInjection;

Console.OutputEncoding = Encoding.UTF8;

var services = new ServiceCollection();

// var builder = new ConfigurationBuilder().AddJsonFile("appsettings.json");
// var config = builder.Build();
// services.Configure<ObjectPoolOptions>(config.GetSection("HayateOP"));

// services.AddObjectPool<MyPooledObject>(config =>
// {
//     config.MaxConcurrent = 15;
//     config.MaxPoolSize = 30;
// });

services.AddHayateObjectPool<MyPooledObject, CustomPolicy>(config =>
{
    config.MaxConcurrent = 15;
    config.MaxPoolSize = 30;
});

var provider = services.BuildServiceProvider();

var pool = provider.GetRequiredService<IHayateObjectPool<MyPooledObject>>();

var obj = pool.Get();

try
{
    // do something with ms
    obj.Id = 1;
    obj.Data = "Test";

    var stat1 = pool.GetStats();
    Console.WriteLine($"池中数量：{stat1.PooledCount}， 总创建：{stat1.TotalCreated}，总归还：{stat1.TotalReturned}，总未命中：{stat1.TotalMissed}");
}
finally
{
    pool.Return(obj);

    var stat2 = pool.GetStats();
    Console.WriteLine($"池中数量：{stat2.PooledCount}， 总创建：{stat2.TotalCreated}，总归还：{stat2.TotalReturned}，总未命中：{stat2.TotalMissed}");
}