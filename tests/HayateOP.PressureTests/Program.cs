// See https://aka.ms/new-console-template for more information

using System.Diagnostics;
using DotNetCore.HayateOP;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

await PressureTester.RunAsync();

Console.WriteLine("Hello, World!");

public class TestItem : IHayateResettable, IDisposable
{
    public int Value { get; set; }
    public void Reset() => Value = 0;
    public void Dispose() { }
}

public class PressureTester
{
    public static async Task RunAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging(l => l.AddConsole().SetMinimumLevel(LogLevel.Warning));
        services.AddHayateObjectPool<TestItem>(opt =>
        {
            opt.MinPoolSize = 20;
            opt.MaxPoolSize = 200;
            opt.MaxConcurrent = 64;
            opt.ScalingIntervalMs = 1000;
            opt.EnableMetrics = false;
        });
        var sp = services.BuildServiceProvider();
        var pool = sp.GetRequiredService<IHayateObjectPool<TestItem>>();

        const int threadCount = 64;
        const int iterations = 50000;

        Console.WriteLine($"Threads: {threadCount}, Iterations/thread: {iterations}");
        var sw = Stopwatch.StartNew();

        var tasks = new Task[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                for (int i = 0; i < iterations; i++)
                {
                    var item = pool.Get();
                    item.Value = i;
                    pool.Return(item);
                }
            });
        }
        await Task.WhenAll(tasks);

        sw.Stop();
        var stats = pool.GetStats();

        Console.WriteLine($"Elapsed: {sw.ElapsedMilliseconds} ms");
        Console.WriteLine($"PooledCount: {stats.PooledCount}");
        Console.WriteLine($"TotalCreated: {stats.TotalCreated}");
        Console.WriteLine($"TotalMissed: {stats.TotalMissed}");
        Console.WriteLine($"CurrentSize(Max): {stats.CurrentSize}");
    }
}