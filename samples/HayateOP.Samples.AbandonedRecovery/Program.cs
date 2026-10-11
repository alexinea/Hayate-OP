// HayateOP sample: abandoned-object recovery
// Build & run:  dotnet run --project samples/HayateOP.Samples.AbandonedRecovery
//
// Maps to the README feature "Abandoned Recovery": with RemoveAbandonedOnBorrow / OnMaintenance enabled and a
// RemoveAbandonedTimeout set, the pool reclaims (destroys) objects that were borrowed and never returned past
// the timeout. This is destructive and OFF by default — opt in deliberately.
using System;
using System.Threading;
using DotNetCore.HayateOP;
using DotNetCore.HayateOP.Samples.AbandonedRecovery;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// NOTE: top-level statements => global-namespace Program. Both `using` directives must stay.

var pool = new HayatePoolBuilder<Resource>()
    .WithMinSize(0)
    .WithMaxSize(4)
    .WithEnableSharding(false)
    .WithRemoveAbandonedOnBorrow()                  // reclaim overdue borrows on the next borrow
    .WithRemoveAbandonedOnMaintenance()             // ...and on the background maintenance run
    .WithRemoveAbandonedTimeout(TimeSpan.FromMilliseconds(300))
    .WithRemoveAbandonedInterval(200)
    .Build();

Console.WriteLine("   borrowing an object and NOT returning it (simulating a lost/abandoned lease)...");
var abandoned = pool.Acquire();
Console.WriteLine($"   borrowed count before reclaim (peek): {pool.TakeSnapshot().BorrowedCount}");

Thread.Sleep(400);   // past RemoveAbandonedTimeout

// The next borrow triggers the on-borrow abandon-reclaim scan: the overdue object is reclaimed (destroyed).
var reclaimed = pool.Acquire();
var stats = pool.GetStats();
Console.WriteLine($"   subsequent borrow succeeded; AbandonedRemovedCount = {stats.AbandonedRemovedCount}");

// IMPORTANT: `abandoned` was reclaimed (destroyed) by the pool, so we must NOT Release it again — doing so
// would touch an object the pool no longer tracks. We only return the freshly acquired `reclaimed`.
pool.Release(reclaimed);
pool.Dispose();

Console.WriteLine();
Console.WriteLine("Abandoned-recovery sample completed.");
