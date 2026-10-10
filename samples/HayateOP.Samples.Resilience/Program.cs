// HayateOP sample: availability & backpressure -- circuit breaker and reject policies
// Build & run:  dotnet run --project samples/HayateOP.Samples.Resilience
using System;
using System.Threading;
using System.Threading.Tasks;
using DotNetCore.HayateOP;

using DotNetCore.HayateOP.Samples.Resilience;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// Section 1: the pool-level circuit breaker. When enabled, CheckAvailable() reports false while the
// breaker is open, and SetUnavailable()/SetAvailable() let the application take the pool out of (and
// back into) service as the dependency behind it fails / recovers. The callbacks announce transitions.
Console.WriteLine("=== 1. Circuit breaker ===");
var pool = new HayatePoolBuilder<Resource>()
    .WithMinSize(2)
    .WithMaxSize(8)
    .WithEnableCircuitBreaker(true)
    .WithOnUnavailable(_ => Console.WriteLine("   [event] pool became UNAVAILABLE"))
    .WithOnAvailable(_ => Console.WriteLine("   [event] pool became AVAILABLE"))
    .Build();

Console.WriteLine($"   available at start? {pool.CheckAvailable()}");
pool.SetUnavailable("dependency down");
Console.WriteLine($"   available after SetUnavailable? {pool.CheckAvailable()}");
pool.SetAvailable();
Console.WriteLine($"   available after SetAvailable? {pool.CheckAvailable()}");

// Section 2: the Abort reject policy. With a pool of exactly one slot, holding the only object and
// then borrowing again fails immediately with an exception instead of blocking or waiting.
Console.WriteLine("=== 2. Reject policy: Abort throws immediately when at capacity ===");
var failFast = new HayatePoolBuilder<Resource>()
    .WithMinSize(1)
    .WithMaxSize(1)
    .WithRejectPolicy(HayatePoolRejectPolicy.Abort)
    .Build();
var heldAbort = failFast.Acquire(); // occupies the only slot
try
{
    try { failFast.Acquire(); Console.WriteLine("   ERROR: second acquire did not throw"); }
    catch (Exception ex) { Console.WriteLine($"   Abort threw: {ex.GetType().Name}"); }
}
finally { failFast.Release(heldAbort); }

// Section 3: the BlockTimeout reject policy (the default). With one slot held, the second acquire
// blocks until the acquire timeout elapses, then throws TimeoutException. The short 200ms timeout
// keeps the demo fast; in production this is the backpressure knob that bounds how long a caller waits.
Console.WriteLine("=== 3. Reject policy: BlockTimeout waits, then times out ===");
var blocking = new HayatePoolBuilder<Resource>()
    .WithMinSize(1)
    .WithMaxSize(1)
    .WithRejectPolicy(HayatePoolRejectPolicy.BlockTimeout)
    .WithAcquireTimeout(TimeSpan.FromMilliseconds(200))
    .Build();
var heldBlock = blocking.Acquire();
try
{
    try { blocking.Acquire(); Console.WriteLine("   ERROR: blocking acquire did not time out"); }
    catch (TimeoutException) { Console.WriteLine("   BlockTimeout threw TimeoutException after 200ms"); }
}
finally { blocking.Release(heldBlock); }

pool.Dispose();
failFast.Dispose();
blocking.Dispose();
Console.WriteLine();
Console.WriteLine("Resilience sample completed.");
