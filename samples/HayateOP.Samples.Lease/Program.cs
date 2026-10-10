// HayateOP sample: zero-allocation value-type leasing (HayatePoolLease<T>)
// Build & run:  dotnet run --project samples/HayateOP.Samples.Lease
using System;
using System.Threading;
using System.Threading.Tasks;
using DotNetCore.HayateOP;

using DotNetCore.HayateOP.Samples.Lease;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// A bounded, sharded pool. Every borrow below uses the value-type lease, so no heap object is
// allocated for the borrow itself -- only for the pooled Worker (which the pool owns anyway).
var pool = new HayatePoolBuilder<Worker>()
    .WithMinSize(4)
    .WithMaxSize(64)
    .WithEnableSharding(true)
    .WithRejectPolicy(HayatePoolRejectPolicy.BlockTimeout)
    .WithAcquireTimeout(TimeSpan.FromSeconds(3))
    .Build();

// 1) Synchronous lease: a `using` block replaces the manual try/finally + Release.
Console.WriteLine("=== 1. Synchronous lease (using) ===");
using (var lease = pool.AcquireLeased())
{
    lease.Value.DoWork("sync");
}
Console.WriteLine($"   after lease -> pooled={pool.GetStats().PooledCount}");

// 2) Lease with an explicit timeout argument (overrides the pool's acquire timeout).
Console.WriteLine("=== 2. Lease with explicit timeout ===");
using (var lease = pool.AcquireLeased(TimeSpan.FromSeconds(2)))
{
    lease.Value.DoWork("with-timeout");
}

// 3) Asynchronous lease: dispose with `await using` so an async return path can complete.
Console.WriteLine("=== 3. Async lease (await using) ===");
await using (var asyncLease = await pool.AcquireLeaseAsync(CancellationToken.None))
{
    asyncLease.Value.DoWork("async");
}

// 4) Hot-path loop: the value-type lease lives on the stack -- no per-iteration allocation.
Console.WriteLine("=== 4. Hot-path loop (no per-borrow allocation) ===");
for (var i = 0; i < 1000; i++)
{
    using var l = pool.AcquireLeased();
    l.Value.DoWork($"item-{i}");
}

// 5) Idempotent return + Value guard: after disposal, Value throws ObjectDisposedException.
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

// 6) Contrast: the class-based HayatePoolScope<T> allocates one object per borrow.
//    Prefer it when the lease must be stored, passed across frames, or held as a reference.
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
