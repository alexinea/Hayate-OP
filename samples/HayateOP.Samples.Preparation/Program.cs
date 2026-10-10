// HayateOP sample: connection readiness on every borrow via IHayatePreparationStrategy<T>
// Build & run:  dotnet run --project samples/HayateOP.Samples.Preparation
using System;
using System.Threading;
using System.Threading.Tasks;
using DotNetCore.HayateOP;

using DotNetCore.HayateOP.Samples.Preparation;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// A bounded pool of DB connections, wrapped so every borrow checks readiness and reconnects if needed.
var inner = new HayatePoolBuilder<DbConnection>()
    .WithMinSize(2)
    .WithMaxSize(8)
    .WithEnableValidation(false)
    .Build();

// WithPreparation wraps the inner pool; the returned HayatePreparationPool<T> runs the
// ready-check / prepare chain on every borrow.
var pool = inner.WithPreparation(new ReconnectStrategy(), onDiscard: c => c.Dispose(), maxPrepareAttempts: 3);

Console.WriteLine("=== 1. Healthy connections are delivered as-is ===");
var a = await pool.AcquireAsync();
try { Console.WriteLine($"   {a}"); }
finally { pool.Release(a); }

Console.WriteLine("=== 2. A dropped connection is repaired before the next delivery ===");
var b = await pool.AcquireAsync();
try
{
    b.Drop(); // simulate a dead connection
    Console.WriteLine($"   acquired id={b.Id} (will be checked on next borrow)");
}
finally { pool.Release(b); }

var c = await pool.AcquireAsync();
try { Console.WriteLine($"   re-acquired -> {c} (repaired if it had dropped)"); }
finally { pool.Release(c); }

inner.Dispose();
Console.WriteLine();
Console.WriteLine("Preparation sample completed.");
