// HayateOP sample: unbounded pool for transient / bursty workloads (never blocks)
// Build & run:  dotnet run --project samples/HayateOP.Samples.Unbounded
using System;
using DotNetCore.HayateOP;

using DotNetCore.HayateOP.Samples.Unbounded;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// An unbounded pool creates on demand and never makes a borrower wait; it only caps how many
// *idle* objects it retains. Ideal for short-lived, array-like buffers.
var pool = new HayateUnboundedPool<ReusableBuffer>(maxIdle: 64);

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

Console.WriteLine($"=== 2. Idle retention cap (MaxIdle={pool.MaxIdle}) ===");
for (var i = 0; i < 200; i++)
{
    var buf = pool.Acquire();
    pool.Release(buf); // park 200 buffers; only MaxIdle (64) are kept
}
Console.WriteLine($"   parked objects after returning 200 -> {pool.PooledCount} (capped at {pool.MaxIdle})");

Console.WriteLine("=== 3. With a custom factory ===");
var pooled = new HayateUnboundedPool<ReusableBuffer>(maxIdle: 8,
    factory: () => new ReusableBuffer { Tag = "factory-made" });
var b = pooled.Acquire();
try { Console.WriteLine($"   {b}"); }
finally { pooled.Release(b); }

pool.Dispose();
Console.WriteLine();
Console.WriteLine("Unbounded sample completed.");
