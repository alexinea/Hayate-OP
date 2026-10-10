// HayateOP sample: connection readiness on every borrow via IHayatePreparationStrategy<T>
// Build & run:  dotnet run --project samples/HayateOP.Samples.Preparation
using System;
using System.Threading;
using System.Threading.Tasks;
using DotNetCore.HayateOP;

using DotNetCore.HayateOP.Samples.Preparation;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// NOTE: top-level statements put this `Program` in the global namespace, so the `using` lines above
// (`DotNetCore.HayateOP` for the pool API, `...Samples.Preparation` for DbConnection/ReconnectStrategy)
// are required.

// A bounded pool of DB connections. Validation is deliberately OFF on the inner pool because the
// readiness check is handled by the preparation strategy instead (see below) -- the two would overlap.
var inner = new HayatePoolBuilder<DbConnection>()
    .WithMinSize(2)          // keep 2 connections warm
    .WithMaxSize(8)          // hard ceiling of 8 idle connections
    .WithEnableValidation(false)
    .Build();

// WithPreparation wraps the inner pool; the returned HayatePreparationPool<T> runs the
// ready-check / prepare chain on every borrow. `onDiscard` disposes a connection whose prepare
// failed, and `maxPrepareAttempts` is how many objects one borrow may run through before giving up.
var pool = inner.WithPreparation(new ReconnectStrategy(), onDiscard: c => c.Dispose(), maxPrepareAttempts: 3);

// SECTION 1 — Healthy path. A connection that reports ready is delivered exactly as taken. The only
// extra cost over a plain borrow is one asynchronous ready-check round-trip.
Console.WriteLine("=== 1. Healthy connections are delivered as-is ===");
var a = await pool.AcquireAsync();
try { Console.WriteLine($"   {a}"); }
finally { pool.Release(a); }

// SECTION 2 — Repair on next borrow. We `Drop()` a connection to simulate a dead one and return it.
// The preparation pool does NOT repair on return; instead, the next borrow runs `IsReadyAsync` (false),
// then `PrepareAsync` (reconnect), so the caller is handed a usable connection, never a broken one.
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
