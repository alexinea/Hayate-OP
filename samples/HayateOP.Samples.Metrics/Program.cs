// HayateOP sample: observability -- metrics, allocation tracking, capacity alarms
// Build & run:  dotnet run --project samples/HayateOP.Samples.Metrics
using System;
using System.Collections.Generic;
using DotNetCore.HayateOP;
using DotNetCore.HayateOP.Metrics;

using DotNetCore.HayateOP.Samples.Metrics;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var meter = new HayateMetricsMeter();

var pool = new HayatePoolBuilder<Resource>()
    .WithMinSize(4)
    .WithMaxSize(16)
    // CreateOnDemand so a borrow that finds no idle object is served immediately (instead of
    // blocking for the acquire timeout, which the default BlockTimeout policy does).
    .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand)
    .WithEnableMetrics(true)
    .WithMetrics(meter)
    .WithEnableAllocationTracking(true)
    // Capacity alarm: warn at 80% usage, critical at 95%.
    .WithCapacityAlarm(0.8, 0.95)
    .WithOnCapacityWarning(e => Console.WriteLine(
        $"   [WARN] {e.PoolName} usage={e.UsageRatio:P0} borrowed={e.BorrowedCount}/{e.MaxPoolSize}"))
    .WithOnCapacityCritical(e => Console.WriteLine(
        $"   [CRIT] {e.PoolName} usage={e.UsageRatio:P0} borrowed={e.BorrowedCount}/{e.MaxPoolSize}"))
    .Build();

Console.WriteLine("=== 1. Exercise the pool ===");
for (var i = 0; i < 50; i++)
{
    var obj = pool.Acquire();
    try { /* use the object */ }
    finally { pool.Release(obj); }
}

var stats = pool.GetStats();
Console.WriteLine("=== 2. Statistics ===");
Console.WriteLine($"   Created={stats.TotalCreated}, Acquired={stats.TotalAcquired}, " +
                  $"Released={stats.TotalReleased}, Pooled={stats.PooledCount}");

Console.WriteLine("=== 3. Capacity alarm wiring ===");
// Hold borrows to drive usage toward the ceiling; the alarm fires on acquire/return/maintenance.
var held = new List<Resource>();
for (var i = 0; i < 16; i++) held.Add(pool.Acquire()); // fills to MaxPoolSize
Console.WriteLine($"   holding {held.Count} objects; usage ratio should be high.");
foreach (var h in held) pool.Release(h);

var snapshot = pool.TakeSnapshot();
Console.WriteLine("=== 4. Snapshot ===");
Console.WriteLine($"   Borrowed={snapshot.BorrowedCount}, LeakCount={snapshot.LeakCount}");

pool.Dispose();
Console.WriteLine();
Console.WriteLine("Metrics sample completed.");
