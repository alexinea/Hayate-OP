// See https://aka.ms/new-console-template for more information
//
// HayateOP sample tour — demonstrates the CURRENT public API surface.
// Build & run:  dotnet run --project samples/HayateOP.Samples

using System.Text;
using DotNetCore.HayateOP;
using DotNetCore.HayateOP.Policies;
using DotNetCore.HayateOP.Samples;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

Console.OutputEncoding = Encoding.UTF8;

// ---------------------------------------------------------------------------
// 1. Build a pool with the fluent API
//    Every option exposed by HayatePoolOptions has a matching With* method.
// ---------------------------------------------------------------------------
var pool = new HayatePoolBuilder<MyPooledObject>()
    .WithMinSize(10)
    .WithMaxSize(100)
    .WithEnableSharding(true)
    .WithShardCount(4)
    .WithEnableAutoScaling(true)
    .WithScalingInterval(5000)
    .WithScaleUpThreshold(0.8)
    .WithScaleDownThreshold(0.2)
    .WithScaleUpStep(10)
    .WithScaleDownStep(5)
    .WithScaleUpCooldownSeconds(3)
    .WithScaleDownCooldownSeconds(15)
    .WithEnableValidation(true)
    .WithValidateInterval(30000)
    .WithEnableEviction(true)
    .WithMaxLifeTime(TimeSpan.FromMinutes(10))
    .WithMaxIdleTime(TimeSpan.FromMinutes(5))
    .WithSoftMinEvictableIdleTime(TimeSpan.FromMinutes(2))
    .WithEvictionInterval(30000)
    .WithNumTestsPerEvictionRun(10)
    .WithEnableLeakDetection(true)
    .WithLeakDetectionThreshold(TimeSpan.FromMinutes(30))
    .WithRejectPolicy(HayatePoolRejectPolicy.BlockTimeout)
    .WithAcquireTimeout(TimeSpan.FromSeconds(5))
    .WithPolicy(new CustomPolicy())
    .Build();

// ---------------------------------------------------------------------------
// 2. Acquire / use / Release (synchronous)
//    ALWAYS wrap the borrowed object in try/finally and Release in the finally.
// ---------------------------------------------------------------------------
Console.WriteLine("=== 2. Acquire / use / Release (sync) ===");
var obj = pool.Acquire();
try
{
    obj.Id = 1;
    obj.Data = "test";
    Console.WriteLine($"   borrowed object -> Id={obj.Id}, Data={obj.Data}");
}
finally
{
    pool.Release(obj);
}

// ---------------------------------------------------------------------------
// 3. Acquire with an explicit timeout (overrides DefaultAcquireTimeout)
// ---------------------------------------------------------------------------
Console.WriteLine("=== 3. Acquire with an explicit timeout ===");
var obj2 = pool.Acquire(TimeSpan.FromSeconds(3));
try
{
    Console.WriteLine($"   borrowed with timeout -> Id={obj2.Id}");
}
finally
{
    pool.Release(obj2);
}

// ---------------------------------------------------------------------------
// 4. Acquire asynchronously
// ---------------------------------------------------------------------------
Console.WriteLine("=== 4. Acquire asynchronously ===");
var obj3 = await pool.AcquireAsync();
try
{
    Console.WriteLine($"   borrowed async -> Id={obj3.Id}");
}
finally
{
    pool.Release(obj3);
}

// ---------------------------------------------------------------------------
// 5. Inspect runtime statistics
// ---------------------------------------------------------------------------
Console.WriteLine("=== 5. Statistics ===");
var stats = pool.GetStats();
Console.WriteLine($"   Pooled={stats.PooledCount}, Created={stats.TotalCreated}, " +
                  $"Acquired={stats.TotalAcquired}, Released={stats.TotalReleased}, " +
                  $"Missed={stats.TotalMissed}, Leaks={stats.LeakDetectedCount}");

// ---------------------------------------------------------------------------
// 6. Take a diagnostic snapshot (borrowed objects + leak stack traces)
// ---------------------------------------------------------------------------
Console.WriteLine("=== 6. Diagnostic snapshot ===");
var snapshot = pool.TakeSnapshot();
Console.WriteLine($"   Borrowed={snapshot.BorrowedCount}, LeakCount={snapshot.LeakCount}, " +
                  $"Traces={snapshot.LeakTraces.Count}");

// ---------------------------------------------------------------------------
// 7. Reload configuration at runtime (no pool rebuild required)
// ---------------------------------------------------------------------------
Console.WriteLine("=== 7. Reload configuration at runtime ===");
pool.ReloadConfig(o =>
{
    o.MaxPoolSize = 200;
    o.EnableAutoScaling = true;
});
Console.WriteLine($"   MaxPoolSize is now {pool.GetOptions().MaxPoolSize}");

// ---------------------------------------------------------------------------
// 8. Clear every pooled object
// ---------------------------------------------------------------------------
Console.WriteLine("=== 8. Clear the pool ===");
pool.Clear();
Console.WriteLine($"   Pooled after clear = {pool.GetStats().PooledCount}");

// Stop background timers (scaling / eviction / validation / leak detection).
(pool as IDisposable)?.Dispose();

// ---------------------------------------------------------------------------
// 9. Dependency Injection integration
// ---------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("=== 9. Dependency Injection ===");
var services = new ServiceCollection();
services.AddHayatePoolSupport()
        .RegisterHayatePool<MyPooledObject>(options =>
        {
            options.MinPoolSize = 10;
            options.MaxPoolSize = 100;
            options.EnableMetrics = true;
        });

using var provider = services.BuildServiceProvider();
var diPool = provider.GetRequiredService<IHayateObjectPool<MyPooledObject>>();
var diObj = diPool.Acquire();
try
{
    diObj.Data = "via DI";
    Console.WriteLine($"   DI pool -> {diPool.GetStats()}");
}
finally
{
    diPool.Release(diObj);
}

// ---------------------------------------------------------------------------
// 10. Configuration binding from appsettings.json
//     Global defaults live under "HayatePool:Global"; per-pool overrides live
//     under "HayatePool:Pools:{TypeName}".
// ---------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("=== 10. Configuration binding (appsettings.json) ===");
var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .Build();

var cfgServices = new ServiceCollection();
cfgServices.AddHayatePoolSupport()
          .RegisterGlobalConfig(configuration)
          .RegisterHayatePool<MyPooledObject>(configuration)
          .RegisterDiagnostics<MyPooledObject>();

using var cfgProvider = cfgServices.BuildServiceProvider();
var cfgPool = cfgProvider.GetRequiredService<IHayateObjectPool<MyPooledObject>>();
var cfgOptions = cfgPool.GetOptions();
Console.WriteLine($"   Configured pool -> Min={cfgOptions.MinPoolSize}, " +
                  $"Max={cfgOptions.MaxPoolSize}, Shards={cfgOptions.ShardCount}");

Console.WriteLine();
Console.WriteLine("All samples completed.");
