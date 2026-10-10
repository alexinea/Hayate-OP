// HayateOP sample: zero-allocation value-type leasing (HayatePoolLease<T>)
// Build & run:  dotnet run --project samples/HayateOP.Samples.Lease
using System;
using System.Threading;
using System.Threading.Tasks;
using DotNetCore.HayateOP;

using DotNetCore.HayateOP.Samples.Lease;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// NOTE: this file uses top-level statements, so the implicit `Program` lives in the GLOBAL namespace.
// That is why both `using` directives above must stay: `DotNetCore.HayateOP` brings in the pool types
// (HayatePoolBuilder<T>, HayatePoolLease<T>, HayatePoolScope<T>, HayatePoolRejectPolicy), and
// `DotNetCore.HayateOP.Samples.Lease` brings in the `Worker` pooled type defined in this project.
// Without them the unqualified names below would not resolve.

// A bounded, sharded pool. Every borrow below uses the value-type lease, so no heap object is
// allocated for the borrow itself -- only for the pooled Worker (which the pool owns anyway).
var pool = new HayatePoolBuilder<Worker>()
    .WithMinSize(4)                       // keep 4 objects warm so the first borrows are served from stock
    .WithMaxSize(64)                      // hard ceiling: the pool never holds more than 64 idle objects
    .WithEnableSharding(true)             // spread objects across shards to reduce lock contention on hot paths
    .WithRejectPolicy(HayatePoolRejectPolicy.BlockTimeout) // wait for a free object, throw on timeout
    .WithAcquireTimeout(TimeSpan.FromSeconds(3))           // the wait bound used by BlockTimeout
    .Build();

// SECTION 1 — Synchronous lease via `using`. `AcquireLeased()` returns a value-type (struct) lease;
// the `using` block disposes it, which returns the Worker to the pool and replaces the manual
// try/finally + Release pattern. No heap object is allocated for the borrow itself.
Console.WriteLine("=== 1. Synchronous lease (using) ===");
using (var lease = pool.AcquireLeased())
{
    lease.Value.DoWork("sync");
}
Console.WriteLine($"   after lease -> pooled={pool.GetStats().PooledCount}");

// SECTION 2 — Per-borrow timeout. `AcquireLeased(timeout)` overrides the pool's configured acquire
// timeout for THIS borrow only, so a single caller can wait longer (or shorter) than the default.
Console.WriteLine("=== 2. Lease with explicit timeout ===");
using (var lease = pool.AcquireLeased(TimeSpan.FromSeconds(2)))
{
    lease.Value.DoWork("with-timeout");
}

// SECTION 3 — Asynchronous lease. `AcquireLeaseAsync` borrows without blocking a thread; dispose it
// with `await using` so the asynchronous return path (policy hooks, async Release) can complete.
Console.WriteLine("=== 3. Async lease (await using) ===");
await using (var asyncLease = await pool.AcquireLeaseAsync(CancellationToken.None))
{
    asyncLease.Value.DoWork("async");
}

// SECTION 4 — Hot-path loop. `using var l = ...` declares the value-type lease on the stack for each
// iteration; disposing at the end of the iteration returns the Worker. Because the lease is a struct,
// the 1000 borrows allocate nothing for the borrow bookkeeping itself.
Console.WriteLine("=== 4. Hot-path loop (no per-borrow allocation) ===");
for (var i = 0; i < 1000; i++)
{
    using var l = pool.AcquireLeased();
    l.Value.DoWork($"item-{i}");
}

// SECTION 5 — Idempotent return + Value guard. A lease returns its object exactly once: a second
// Dispose() is a no-op, so you can never double-return and hand the same object to two borrowers.
// After disposal, reading `.Value` throws ObjectDisposedException, which is the safety net that keeps
// a disposed lease from being used by mistake.
Console.WriteLine("=== 5. Return is idempotent; Value guards post-disposal ===");
var guardLease = pool.AcquireLeased();
guardLease.Value.DoWork("guard");
guardLease.Dispose();
guardLease.Dispose(); // second dispose is a no-op, never a double-return
try
{
    var _ = guardLease.Value; // throws
    Console.WriteLine("   ERROR: Value did not throw after disposal");
}
catch (ObjectDisposedException)
{
    Console.WriteLine("   Value threw ObjectDisposedException after disposal (correct).");
}

// SECTION 6 — Contrast with the class-based scope. `HayatePoolScope<T>` is the reference-type twin of
// the lease: same lifetime/return rules, but it allocates one object per borrow. Prefer it when the
// borrow must be stored in a field, passed to another method, or held across an await as a reference.
Console.WriteLine("=== 6. Class-based scope (allocates, but reference-friendly) ===");
using (var scope = pool.AcquireScoped())
{
    scope.Value.DoWork("scoped");
}
await using (var scopeAsync = await pool.AcquireScopeAsync(CancellationToken.None))
{
    scopeAsync.Value.DoWork("scoped-async");
}

pool.Dispose();
Console.WriteLine();
Console.WriteLine("Lease sample completed.");
