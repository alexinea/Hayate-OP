// HayateOP sample: asynchronous borrows -- fan-out, cancellation and timeouts
// Build & run:  dotnet run --project samples/HayateOP.Samples.Async
using System;
using System.Threading;
using System.Threading.Tasks;
using DotNetCore.HayateOP;

using DotNetCore.HayateOP.Samples.Async;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var pool = new HayatePoolBuilder<DbConnection>()
    .WithMinSize(2)
    .WithMaxSize(16)
    .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand)
    .WithEnableMetrics(true)
    .Build();

// 1) A single async borrow/return with `await using`: the lease returns the object on the async path,
//    so an async policy hook (if any) can finish before the method returns.
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
Console.WriteLine("=== 2. Concurrent fan-out (Task.WhenAll) ===");
var tasks = new Task<string>[20];
for (var i = 0; i < tasks.Length; i++)
{
    var idx = i;
    tasks[i] = Task.Run(async () =>
    {
        await using var l = await pool.AcquireLeaseAsync(CancellationToken.None);
        await l.Value.OpenAsync();
        var r = await l.Value.QueryAsync($"query-{idx}");
        await l.Value.CloseAsync();
        return r;
    });
}
var results = await Task.WhenAll(tasks);
Console.WriteLine($"   completed {results.Length} queries; peak active objects = {pool.GetStats().PeakActiveObjects}");

// 3) Cancellation: build a tight pool whose only object is held, then borrow with an already-cancelled
//    token. The wait is interrupted and nothing is taken.
Console.WriteLine("=== 3. Cancellation (tight pool, only slot held) ===");
var tight = new HayatePoolBuilder<DbConnection>()
    .WithMinSize(0)
    .WithMaxSize(1)
    .WithRejectPolicy(HayatePoolRejectPolicy.BlockTimeout)
    .WithAcquireTimeout(TimeSpan.FromMilliseconds(500))
    .Build();
var held = tight.Acquire(); // occupy the single object
try
{
    using var cts = new CancellationTokenSource();
    cts.Cancel(); // already cancelled before the call
    try
    {
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
Console.WriteLine("=== 4. Async acquire with timeout ===");
held = tight.Acquire(); // occupy the single object again
try
{
    try
    {
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
