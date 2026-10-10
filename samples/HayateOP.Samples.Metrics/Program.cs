// HayateOP sample: observability -- metrics, allocation tracking, capacity alarms
// Build & run:  dotnet run --project samples/HayateOP.Samples.Metrics
using System;
using System.Collections.Generic;
using DotNetCore.HayateOP;
using DotNetCore.HayateOP.Metrics;

using DotNetCore.HayateOP.Samples.Metrics;

// Ensure the [WARN]/[CRIT] capacity-alarm lines print correctly on consoles whose default code page
// would mangle characters.
Console.OutputEncoding = System.Text.Encoding.UTF8;

// A metrics sink built on System.Diagnostics.Metrics (consumed by dotnet-counters / OpenTelemetry).
// We attach it to the pool below so acquire/release/miss/scaling are published as instruments.
var meter = new HayateMetricsMeter();

var pool = new HayatePoolBuilder<Resource>()
    .WithMinSize(4)
    .WithMaxSize(16)
    // CreateOnDemand so a borrow that finds no idle object is served immediately (instead of
    // blocking for the acquire timeout, which the default BlockTimeout policy does).
    // GOTCHA: the default reject policy is BlockTimeout. Under it, when the pool is full (all
    // MaxPoolSize objects lent out) a borrow blocks until the acquire timeout and then throws
    // TimeoutException. CreateOnDemand avoids that by creating a new object while capacity remains.
    .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand)
    .WithEnableMetrics(true)
    .WithMetrics(meter)
    .WithEnableAllocationTracking(true)
    // Capacity alarm: warn at 80% usage, critical at 95%. Usage ratio = borrowed / MaxPoolSize.
    .WithCapacityAlarm(0.8, 0.95)
    // The alarm callbacks fire once per state transition (re-armed when usage drops), so holding
    // borrows past the threshold drives the [WARN]/[CRIT] lines below.
    .WithOnCapacityWarning(e => Console.WriteLine(
        $"   [WARN] {e.PoolName} usage={e.UsageRatio:P0} borrowed={e.BorrowedCount}/{e.MaxPoolSize}"))
    .WithOnCapacityCritical(e => Console.WriteLine(
        $"   [CRIT] {e.PoolName} usage={e.UsageRatio:P0} borrowed={e.BorrowedCount}/{e.MaxPoolSize}"))
    .Build();

// Section 1: push the pool through many borrow/return cycles. Because each Acquire is paired with a
// Release inside the loop, the pool never stays full, so CreateOnDemand serves misses by creating
// objects immediately instead of blocking. The empty try/finally is the canonical "use then return"
// shape every borrow should follow so a thrown exception still releases the object.
Console.WriteLine("=== 1. Exercise the pool ===");
for (var i = 0; i < 50; i++)
{
    var obj = pool.Acquire();
    try { /* use the object */ }
    finally { pool.Release(obj); }
}

// Section 2: read cumulative point-in-time statistics. GetStats() returns counters such as how many
// objects were created, acquired and released, plus how many are currently parked (PooledCount).
var stats = pool.GetStats();
Console.WriteLine("=== 2. Statistics ===");
Console.WriteLine($"   Created={stats.TotalCreated}, Acquired={stats.TotalAcquired}, " +
                  $"Released={stats.TotalReleased}, Pooled={stats.PooledCount}");

// Section 3: demonstrate the capacity alarm. We keep every object borrowed at once so usage climbs
// toward MaxPoolSize; the alarm callbacks fire on the acquire that crosses the warn/critical ratio.
Console.WriteLine("=== 3. Capacity alarm wiring ===");
// Hold borrows to drive usage toward the ceiling; the alarm fires on acquire/return/maintenance.
var held = new List<Resource>();
for (var i = 0; i < 16; i++) held.Add(pool.Acquire()); // fills to MaxPoolSize
Console.WriteLine($"   holding {held.Count} objects; usage ratio should be high.");
foreach (var h in held) pool.Release(h);

// Section 4: TakeSnapshot() is a heavier, object-by-object survey (every pooled object's state,
// plus the cumulative leak count). Useful for diagnostics/forensics, distinct from the cheap GetStats().
var snapshot = pool.TakeSnapshot();
Console.WriteLine("=== 4. Snapshot ===");
Console.WriteLine($"   Borrowed={snapshot.BorrowedCount}, LeakCount={snapshot.LeakCount}");

pool.Dispose();
Console.WriteLine();
Console.WriteLine("Metrics sample completed.");
