// HayateOP sample: deep observability -- options, snapshot, availability, warm-up, reload, eviction
// Build & run:  dotnet run --project samples/HayateOP.Samples.Diagnostics
using System;
using System.Threading;
using DotNetCore.HayateOP;

using DotNetCore.HayateOP.Samples.Diagnostics;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var pool = new HayatePoolBuilder<Resource>()
    .WithPoolName("diagnostics-demo")
    .WithMinSize(0)
    .WithMaxSize(16)
    .WithEnableDiagnostics(true)
    .WithEnableMetrics(true)
    .WithEnableLeakDetection(true)
    .WithEnableCircuitBreaker(true)
    .WithCircuitBreaker(new HayateCircuitBreakerOptions { FailureThreshold = 1 })
    .WithOnUnavailable(e => Console.WriteLine($"   [UNAVAILABLE] {e.PoolName}: {e.Reason}"))
    .WithOnAvailable(e => Console.WriteLine($"   [AVAILABLE] {e.PoolName}"))
    .WithAcquireTimeout(TimeSpan.FromSeconds(5))
    .Build();

// 1) Live options: read back the configuration the pool is actually running with.
Console.WriteLine("=== 1. Live options (GetOptions) ===");
var opts = pool.GetOptions();
Console.WriteLine($"   MinPoolSize={opts.MinPoolSize}, MaxPoolSize={opts.MaxPoolSize}, " +
                  $"MaxActiveObjects={opts.MaxActiveObjects}, EnableDiagnostics={opts.EnableDiagnostics}, " +
                  $"EnableMetrics={opts.EnableMetrics}");

// 2) Warm-up: pay the creation cost up front so later borrows find an idle object ready to go.
Console.WriteLine("=== 2. Pre-warm ===");
var warmed = pool.PreWarm(8);
Console.WriteLine($"   created {warmed} idle objects; pooled={pool.GetStats().PooledCount}");

// 3) Runtime reconfiguration: raise MinSize without rebuilding the pool.
Console.WriteLine("=== 3. ReloadConfig at runtime ===");
pool.ReloadConfig(o => o.MinPoolSize = 16);
Console.WriteLine($"   MinPoolSize is now {pool.GetOptions().MinPoolSize}");

// 4) Snapshot: a point-in-time view of every live object (idle plus borrowed).
Console.WriteLine("=== 4. Snapshot (TakeSnapshot) ===");
var snap = pool.TakeSnapshot();
Console.WriteLine($"   Timestamp={snap.Timestamp:HH:mm:ss}, PooledCount={snap.PooledCount}, " +
                  $"BorrowedCount={snap.BorrowedCount}, ObjectDetails={snap.ObjectDetails.Count}, " +
                  $"LeakTraces={snap.LeakTraces.Count}");

// 5) Availability / circuit breaker: with FailureThreshold=1 a single failure report trips the breaker,
//    taking the pool out of service and raising OnUnavailable; SetAvailable recovers it.
Console.WriteLine("=== 5. Availability / circuit breaker ===");
Console.WriteLine($"   available before = {pool.CheckAvailable()}");
pool.SetUnavailable("dependency down");
Console.WriteLine($"   available after one SetUnavailable = {pool.CheckAvailable()}");
pool.SetAvailable();
Console.WriteLine($"   available after SetAvailable = {pool.CheckAvailable()}");

// 6) On-demand eviction: reclaim idle objects now instead of waiting for the background pass.
//    `Touched` targets idle objects that have been borrowed before (LeaseCount > 0); touch a few
//    first so the eviction actually has something to reclaim.
Console.WriteLine("=== 6. On-demand eviction (Touched) ===");
for (var i = 0; i < 4; i++) { var o = pool.Acquire(); pool.Release(o); }
var evicted = pool.Evict(HayateEvictReason.Touched);
Console.WriteLine($"   evicted touched idle objects = {evicted}; pooled now {pool.GetStats().PooledCount}");

pool.Dispose();
Console.WriteLine();
Console.WriteLine("Diagnostics sample completed.");
