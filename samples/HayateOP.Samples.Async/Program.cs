// HayateOP sample: asynchronous borrows -- fan-out, cancellation and timeouts
// Build & run:  dotnet run --project samples/HayateOP.Samples.Async
using System;
using System.Threading;
using System.Threading.Tasks;
using DotNetCore.HayateOP;

using DotNetCore.HayateOP.Samples.Async;

// A top-level `Program` lives in the global namespace, so the two `using` directives above pull in the pool
// API (DotNetCore.HayateOP) and this sample's helper type (DbConnection) without qualification.
Console.OutputEncoding = System.Text.Encoding.UTF8;

// Build a pool whose size grows from a floor of 2 up to 16, creating brand-new connections on demand when
// every idle object is already borrowed (CreateOnDemand) rather than blocking or failing. Metrics are turned
// on so the fan-out section can later report PeakActiveObjects.
var pool = new HayatePoolBuilder<DbConnection>()
    .WithMinSize(2)
    .WithMaxSize(16)
    .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand)
    .WithEnableMetrics(true)
    .Build();

// 1) A single async borrow/return with `await using`: the lease returns the object on the async path,
//    so an async policy hook (if any) can finish before the method returns.
// WHY: shows the simplest correct shape for an async resource -- the `await using` block guarantees the
//      connection is returned even if OpenAsync/QueryAsync/CloseAsync throws, with no hand-written try/finally.
Console.WriteLine("=== 1. Single async borrow (await using) ===");
await using (var lease = await pool.AcquireLeaseAsync(CancellationToken.None))
{
    await lease.Value.OpenAsync();
    var result = await lease.Value.QueryAsync("SELECT 1");
    Console.WriteLine($"   {result}");
    await lease.Value.CloseAsync();
}

// 2) Fan-out: many concurrent borrows sharing a small pool. The value-type lease keeps each borrow
//    allocation-free, and Task.WhenAll waits for every query to finish.
// WHY: proves the pool safely multiplexes far more concurrent work than it has idle objects -- with
//      CreateOnDemand it simply spins up extra connections instead of making callers wait or fail.
Console.WriteLine("=== 2. Concurrent fan-out (Task.WhenAll) ===");
var tasks = new Task<string>[20];
for (var i = 0; i < tasks.Length; i++)
{
    // Capture the loop index into a local: closures would otherwise all see the final value of `i`.
    var idx = i;
    tasks[i] = Task.Run(async () =>
    {
        // AcquireLeaseAsync returns a HayatePoolLease<T> (a struct) -- no heap allocation per borrow.
        await using var l = await pool.AcquireLeaseAsync(CancellationToken.None);
        await l.Value.OpenAsync();
        var r = await l.Value.QueryAsync($"query-{idx}");
        await l.Value.CloseAsync();
        return r;
    });
}
// Task.WhenAll awaits the whole batch; each await-using lease disposes as its task finishes, returning
// connections to the pool immediately rather than all at the very end.
var results = await Task.WhenAll(tasks);
Console.WriteLine($"   completed {results.Length} queries; peak active objects = {pool.GetStats().PeakActiveObjects}");

// 3) Cancellation: build a tight pool whose only object is held, then borrow with an already-cancelled
//    token. The wait is interrupted and nothing is taken.
// WHY: an external cancellation must surface promptly as OperationCanceledException and must NOT consume an
//      object -- the waiting caller should leave the pool exactly as it found it.
Console.WriteLine("=== 3. Cancellation (tight pool, only slot held) ===");
var tight = new HayatePoolBuilder<DbConnection>()
    .WithMinSize(0)
    .WithMaxSize(1)                       // only a single connection can ever exist
    .WithRejectPolicy(HayatePoolRejectPolicy.BlockTimeout)  // wait rather than create beyond MaxSize
    .WithAcquireTimeout(TimeSpan.FromMilliseconds(500))
    .Build();
var held = tight.Acquire(); // occupy the single object
try
{
    using var cts = new CancellationTokenSource();
    cts.Cancel(); // already cancelled before the call
    try
    {
        // A pre-cancelled token makes AcquireLeaseAsync throw before it ever claims the held object.
        await using var _ = await tight.AcquireLeaseAsync(cts.Token);
        Console.WriteLine("   ERROR: borrow succeeded despite cancellation");
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("   borrow was cancelled (OperationCanceledException); nothing was taken.");
    }
}
finally
{
    tight.Release(held);
}

// 4) Timeout: a borrow that cannot be satisfied within the bound throws TimeoutException, while an
//    external cancellation would have surfaced as OperationCanceledException instead.
// WHY: shows the two distinct failure modes of a blocked async borrow -- a timeout budget vs. a cancel signal.
Console.WriteLine("=== 4. Async acquire with timeout ===");
held = tight.Acquire(); // occupy the single object again
try
{
    try
    {
        // The (TimeSpan, CancellationToken) overload bounds the wait at 100 ms; with the lone object held it
        // must time out and throw, never succeeding.
        await using var _ = await tight.AcquireLeaseAsync(TimeSpan.FromMilliseconds(100), CancellationToken.None);
        Console.WriteLine("   ERROR: borrow succeeded despite a full pool");
    }
    catch (TimeoutException)
    {
        Console.WriteLine("   timed out waiting for the only object (TimeoutException).");
    }
}
finally
{
    tight.Release(held);
    tight.Dispose();
}

pool.Dispose();
Console.WriteLine();
Console.WriteLine("Async sample completed.");
