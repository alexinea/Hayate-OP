// HayateOP sample: pooled cancellation sources (CancellationTokenSourcePool)
// Build & run:  dotnet run --project samples/HayateOP.Samples.CancellationTokenSourcePool
//
// Maps to the README feature "Specialized pools": a CancellationTokenSource is one of the most
// frequently pooled objects in asynchronous code -- the per-operation timeout or cancellation scope
// that would otherwise be allocated on every request -- and one of the few that cannot be reused
// naively: a used source carries the timer and the callbacks its renter left behind. The pool owns the
// whole lifetime, and resets a source only after the rental has ended.
using System;
using System.Threading;
using DotNetCore.HayateOP.Specialized;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// NOTE: top-level statements => global-namespace Program. `DotNetCore.HayateOP.Specialized` brings in
// CancellationTokenSourcePool and PooledCancellationTokenSource.

// --- 0. Which mode is this runtime in? ---------------------------------------------------------
// TryReset() -- the one call that clears a used source -- does not exist on .NET Framework 4.8. Rather
// than fork its source per target framework, the pool looks the method up at run time: where it is
// missing, every returned source is destroyed instead of parked, so a rental is always correct, it is
// simply never reused. The pool states which mode it is in instead of leaving it to be inferred.
Console.WriteLine("=== Runtime reuse support ===");
Console.WriteLine($"   IsReuseSupported = {CancellationTokenSourcePool.IsReuseSupported}");
Console.WriteLine(CancellationTokenSourcePool.IsReuseSupported
    ? "   TryReset() exists: a returned source is reset and handed out again."
    : "   TryReset() is absent: a returned source is destroyed and the next rental creates a fresh one.");

using var pool = new CancellationTokenSourcePool(8);

// --- 1. The everyday shape: a per-operation scope that returns itself --------------------------
Console.WriteLine();
Console.WriteLine("=== Rent / return ===");
CancellationTokenSource first;
using (first = pool.Rent())
{
    first.CancelAfter(TimeSpan.FromSeconds(30));
    Console.WriteLine($"   rented; idle sources now {pool.GetStats().PooledCount}");
}

Console.WriteLine($"   after the using block, idle sources {pool.GetStats().PooledCount}");
using (var second = pool.Rent())
{
    Console.WriteLine($"   the next rental is the same instance: {ReferenceEquals(first, second)}"
        + (CancellationTokenSourcePool.IsReuseSupported
            ? "   (reset: its timer was disarmed)"
            : "   (fresh source: the returned one was destroyed)"));
    Console.WriteLine($"   nothing of the previous rental survived: {!second.IsCancellationRequested}");
}

// --- 2. A canceled source is never handed out again --------------------------------------------
// No reset can make a canceled source usable, so the pool destroys it on return rather than parking it.
Console.WriteLine();
Console.WriteLine("=== A canceled source is dropped, not reused ===");
var canceled = pool.Rent();
canceled.Cancel();
canceled.Dispose();
Console.WriteLine($"   canceled source returned; idle sources now {pool.GetStats().PooledCount}");

using (var replacement = pool.Rent())
{
    Console.WriteLine($"   the replacement is a different source: {!ReferenceEquals(canceled, replacement)}");
    Console.WriteLine($"   ...and it is not canceled: {!replacement.IsCancellationRequested}");
}

// --- 3. The previous renter's callbacks do not survive the return ------------------------------
Console.WriteLine();
Console.WriteLine("=== The previous renter's callbacks do not survive ===");
var callbackFired = 0;
var withCallback = pool.Rent();
_ = withCallback.Token.Register(() => callbackFired++);
withCallback.Dispose();

using (var afterCallback = pool.Rent())
{
    afterCallback.Cancel();
    Console.WriteLine($"   a callback left by the previous renter fired {callbackFired} time(s)");
}

// --- 4. RentLinked: a rental that follows an upstream token ------------------------------------
// The link is registered on the pooled source instead of creating one outside the pool, and it belongs
// to the rental: disposal drops it before the source can be handed to the next renter.
Console.WriteLine();
Console.WriteLine("=== RentLinked: follow an upstream token ===");
using var upstream = new CancellationTokenSource();
using (var linked = pool.RentLinked(upstream.Token))
{
    Console.WriteLine($"   canceled before the upstream cancels: {linked.IsCancellationRequested}");
    upstream.Cancel();
    Console.WriteLine($"   canceled after the upstream cancels:  {linked.IsCancellationRequested}");
}

using (var afterLink = pool.Rent())
{
    // The upstream is already canceled, but the finished rental's link is gone -- so it cannot reach
    // this rental.
    upstream.Cancel();
    Console.WriteLine($"   the finished rental's upstream cannot reach the next rental: {!afterLink.IsCancellationRequested}");
}

// --- 5. Clearing the pool ---------------------------------------------------------------------
// A parked source has already been reset -- no timer, no callbacks -- so clearing the pool drops the
// sources it holds and there is nothing left running to release.
Console.WriteLine();
Console.WriteLine("=== Clear ===");
Console.WriteLine($"   idle sources before Clear: {pool.GetStats().PooledCount}");
pool.Clear();
Console.WriteLine($"   idle sources after Clear:  {pool.GetStats().PooledCount}");

Console.WriteLine();
Console.WriteLine("Cancellation-source pool sample completed.");
