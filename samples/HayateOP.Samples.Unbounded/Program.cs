// HayateOP sample: unbounded pool for transient / bursty workloads (never blocks)
// Build & run:  dotnet run --project samples/HayateOP.Samples.Unbounded
using System;
using DotNetCore.HayateOP;

using DotNetCore.HayateOP.Samples.Unbounded;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// An unbounded pool creates on demand and never makes a borrower wait; it only caps how many
// *idle* objects it retains. Ideal for short-lived, array-like buffers.
// NOTE: top-level statements put this `Program` in the global namespace, so the two `using` lines
// above (`DotNetCore.HayateOP` for the pool, `...Samples.Unbounded` for `ReusableBuffer`) are required.
var pool = new HayateUnboundedPool<ReusableBuffer>(maxIdle: 64);

// SECTION 1 — Burst borrows. When the idle queue is empty the pool simply creates a new buffer, so a
// burst never blocks or rejects. Returning via `Release` is OPTIONAL: an object that is never returned
// is reclaimed by the GC (the pool keeps no per-object bookkeeping).
Console.WriteLine("=== 1. Borrow under a burst: created on demand, never blocks ===");
for (var i = 0; i < 5; i++)
{
    var buf = pool.Acquire();
    try
    {
        buf.Reset();
        buf.Append($"message-{i}");
        Console.WriteLine($"   buffer -> \"{buf}\"");
    }
    finally
    {
        pool.Release(buf); // optional: not returning is fine, the GC handles it
    }
}

// SECTION 2 — Idle retention cap. `MaxIdle` bounds only the RESIDENT set, not concurrent demand: a
// return that arrives when MaxIdle objects are already idle is destroyed instead of parked, so memory
// tracks the peak-idle, not the peak-burst. After returning 200 we never hold more than MaxIdle.
Console.WriteLine($"=== 2. Idle retention cap (MaxIdle={pool.MaxIdle}) ===");
for (var i = 0; i < 200; i++)
{
    var buf = pool.Acquire();
    pool.Release(buf); // park 200 buffers; only MaxIdle (64) are kept
}
Console.WriteLine($"   parked objects after returning 200 -> {pool.PooledCount} (capped at {pool.MaxIdle})");

// SECTION 3 — Custom factory. The constructor also accepts a `Func<T>` used to create objects whenever
// the idle queue is empty, so you can inject state (here a `Tag`) without a parameterless constructor.
Console.WriteLine("=== 3. With a custom factory ===");
var pooled = new HayateUnboundedPool<ReusableBuffer>(maxIdle: 8,
    factory: () => new ReusableBuffer { Tag = "factory-made" });
var b = pooled.Acquire();
try { Console.WriteLine($"   {b}"); }
finally { pooled.Release(b); }

pool.Dispose();
Console.WriteLine();
Console.WriteLine("Unbounded sample completed.");
