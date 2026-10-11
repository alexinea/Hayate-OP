// HayateOP sample: soft capacity
// Build & run:  dotnet run --project samples/HayateOP.Samples.SoftCapacity
//
// Maps to the README feature "Soft Capacity": WithSoftCapacity(n) bounds how many returned objects the
// pool *keeps* on the return path. Returns past the soft ceiling are disposed instead of parked, while
// the pool can still lend out its whole hard ceiling (MaxPoolSize).
using System;
using System.Collections.Generic;
using DotNetCore.HayateOP;
using DotNetCore.HayateOP.Samples.SoftCapacity;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// NOTE: top-level statements => global-namespace Program. Both `using` directives must stay.

// Borrow `count` objects and return them all; returns `count` so the caller can release cleanly.
static IList<Resource> BorrowAndReturn(IHayateObjectPool<Resource> pool, int count)
{
    var borrowed = new List<Resource>(count);
    for (var i = 0; i < count; i++)
    {
        borrowed.Add(pool.Acquire());
    }

    foreach (var r in borrowed)
    {
        pool.Release(r);
    }

    return borrowed;
}

// --- Without a soft capacity: every returned object is retained up to MaxPoolSize. ---
Console.WriteLine("=== No soft capacity (MaxPoolSize = 8) ===");
var unbounded = new HayatePoolBuilder<Resource>()
    .WithMinSize(0)
    .WithMaxSize(8)
    .WithEnableSharding(false)
    .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand)
    .Build();
BorrowAndReturn(unbounded, 8);
Console.WriteLine($"   after returning 8 -> PooledCount = {unbounded.GetStats().PooledCount} (all 8 kept)");
unbounded.Dispose();

// --- With a soft capacity of 3: returns past the 3rd are dropped, not parked. ---
Console.WriteLine("=== Soft capacity = 3 (MaxPoolSize = 8) ===");
var softened = new HayatePoolBuilder<Resource>()
    .WithMinSize(0)
    .WithMaxSize(8)
    .WithEnableSharding(false)
    .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand)
    .WithSoftCapacity(3)              // keep at most 3 idle objects; drop the rest on return
    .Build();
BorrowAndReturn(softened, 8);
Console.WriteLine($"   after returning 8 -> PooledCount = {softened.GetStats().PooledCount} (only 3 kept, 5 dropped)");
softened.Dispose();

Console.WriteLine();
Console.WriteLine("Soft-capacity sample completed.");
