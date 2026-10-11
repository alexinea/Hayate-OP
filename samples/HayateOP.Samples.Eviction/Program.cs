// HayateOP sample: custom eviction policy (IHayateEvictionPolicy<T>)
// Build & run:  dotnet run --project samples/HayateOP.Samples.Eviction
//
// Maps to the README feature "Eviction Policy": you can replace the built-in idle-eviction rule with your
// own IHayateEvictionPolicy<T>, installed via WithEvictionPolicy. The background maintenance run (started
// by WithEvictionInterval) is what consults it; a manual Evict(HayateEvictReason) uses the built-in rules.
using System;
using System.Threading;
using DotNetCore.HayateOP;
using DotNetCore.HayateOP.Samples.Eviction;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// NOTE: top-level statements => global-namespace Program. Both `using` directives must stay.

// Bump the lease count of every connection by borrowing and returning it several times.
static void WearOut(IHayateObjectPool<Connection> pool, int connections, int rounds)
{
    for (var r = 0; r < rounds; r++)
    {
        for (var i = 0; i < connections; i++)
        {
            var c = pool.Acquire();
            pool.Release(c);
        }
    }
}

// --- Default eviction policy (no custom rule): Evict(HayateEvictReason.Idle) uses the built-in idle rule. ---
Console.WriteLine("=== Default eviction policy ===");
var defaultPool = new HayatePoolBuilder<Connection>()
    .WithMinSize(0)
    .WithMaxSize(8)
    .WithEnableSharding(false)
    .WithEnableEviction(true)
    .WithMaxIdleTime(TimeSpan.FromMilliseconds(50))
    .Build();
WearOut(defaultPool, 4, 1);
var beforeDefault = defaultPool.GetStats().PooledCount;
Thread.Sleep(80);   // let idle objects exceed MaxIdleTime
var evictedDefault = defaultPool.Evict(HayateEvictReason.Idle);   // reason-based, built-in rule
Console.WriteLine($"   idle objects before manual evict: {beforeDefault}; evicted by Idle rule: {evictedDefault}");
defaultPool.Dispose();

// --- Custom eviction policy: retire connections worn past a lease-count threshold. ---
Console.WriteLine("=== Custom eviction policy (retire after N leases) ===");
var customPool = new HayatePoolBuilder<Connection>()
    .WithMinSize(0)
    .WithMaxSize(8)
    .WithEnableSharding(false)
    .WithEnableEviction(true)
    .WithEvictionInterval(1000)                     // background scan period (1000 ms is the minimum)
    .WithEvictionPolicy(new RetireHeavilyUsedConnections(maxLeaseCount: 3))
    .Build();
WearOut(customPool, 4, 5);   // 5 rounds => each connection leased >= 6 times
var idleBefore = customPool.GetStats().PooledCount;
Console.WriteLine($"   idle objects before background run: {idleBefore} (all worn past the lease threshold)");
Thread.Sleep(1300);          // let the background run (every 1 s) consult the custom policy at least once
var idleAfter = customPool.GetStats().PooledCount;
Console.WriteLine($"   idle objects after background run: {idleAfter} (worn-out connections retired by the custom policy)");
customPool.Dispose();

Console.WriteLine();
Console.WriteLine("Eviction-policy sample completed.");
