// HayateOP sample: bounding concurrent borrows with the active-object ceiling (MaxActiveObjects, G-5)
// Build & run:  dotnet run --project samples/HayateOP.Samples.ActiveCeiling
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DotNetCore.HayateOP;

using DotNetCore.HayateOP.Samples.ActiveCeiling;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// Cap the number of objects that may be borrowed at the same time to 2, while still allowing the pool
// to hold up to 16. Extra borrows wait for an active slot to free instead of failing or creating more.
var pool = new HayatePoolBuilder<Resource>()
    .WithMinSize(2)
    .WithMaxSize(16)
    .WithMaxActiveObjects(2)
    .WithRejectPolicy(HayatePoolRejectPolicy.BlockTimeout)
    .WithAcquireTimeout(TimeSpan.FromSeconds(5))
    .WithEnableMetrics(true)
    .Build();

Console.WriteLine("=== 1. Fan-in under a tight active ceiling ===");
Console.WriteLine("   launching 20 concurrent borrows with a ceiling of 2...");
var tasks = new List<Task>();
var concurrent = 0;
var maxConcurrent = 0;
var gate = new object();
for (var i = 0; i < 20; i++)
{
    tasks.Add(Task.Run(async () =>
    {
        var obj = pool.Acquire(); // blocks until an active slot is free
        try
        {
            lock (gate) { concurrent++; maxConcurrent = Math.Max(maxConcurrent, concurrent); }
            await Task.Delay(15);
        }
        finally
        {
            lock (gate) { concurrent--; }
            pool.Release(obj);
        }
    }));
}
await Task.WhenAll(tasks);

var stats = pool.GetStats();
Console.WriteLine($"   max concurrent active observed = {maxConcurrent} (ceiling = 2)");
Console.WriteLine($"   ActiveLimitThrottledCount (borrows held back by the ceiling) = {stats.ActiveLimitThrottledCount}");
Console.WriteLine($"   TotalAcquired={stats.TotalAcquired}");

pool.Dispose();
Console.WriteLine();
Console.WriteLine("ActiveCeiling sample completed.");
