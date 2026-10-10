// HayateOP sample: class-based scoped borrows (HayatePoolScope<T>) for cross-frame lifetimes
// Build & run:  dotnet run --project samples/HayateOP.Samples.Scoped
using System;
using System.Threading;
using System.Threading.Tasks;
using DotNetCore.HayateOP;

using DotNetCore.HayateOP.Samples.Scoped;

// A top-level `Program` lives in the global namespace, so the two `using` directives above pull in the pool
// API (DotNetCore.HayateOP) and this sample's helper type (Resource) without qualification.
Console.OutputEncoding = System.Text.Encoding.UTF8;

// A conventionally-sized pool that grows from a floor of 2 to a ceiling of 16 and creates new objects on
// demand when every idle one is already borrowed. Metrics are left off because this sample is about the
// lease's lifetime shape, not counters.
var pool = new HayatePoolBuilder<Resource>()
    .WithMinSize(2)
    .WithMaxSize(16)
    .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand)
    .Build();

// 1) Basic scoped borrow: `using` returns the object even if the body throws.
// WHY: the class-based HayatePoolScope<T> behaves like the value-type lease for a simple in-frame block, but
//      (shown next) can also be stored or passed by reference because it is a heap object, not a struct.
Console.WriteLine("=== 1. Scoped borrow (using) ===");
using (var scope = pool.AcquireScoped())
{
    scope.Value.DoWork("basic");
}

// 2) Pass the scope to a method that outlives the calling frame. Because the scope is a reference type,
//    it travels anywhere a normal object can -- unlike the value-type HayatePoolLease<T>, which lives on
//    the stack and cannot be handed across frames.
// WHY: this is the core reason to pick HayatePoolScope<T> over HayatePoolLease<T> -- a struct can't be
//      stored in a field or passed to another method the way an object can.
Console.WriteLine("=== 2. Scope handed to another method ===");
using (var scope = pool.AcquireScoped())
{
    ProcessAcrossFrames(scope);
}

// 3) Store the scope in a field and use it while it is still alive. The lease owns the object until disposed.
// WHY: demonstrates the same reference-type freedom in a field -- the resource stays valid for exactly as
//      long as the scope has not been disposed.
Console.WriteLine("=== 3. Scope held in a field ===");
HayatePoolScope<Resource>? field = null;
using (var scope = pool.AcquireScoped())
{
    field = scope;
    field.Value.DoWork("via-field");
}
// field is disposed now; touching field.Value would throw ObjectDisposedException.

// 4) Async scope spanning an await: dispose with `await using` so the return can await an async hook.
// WHY: when a scope is used across an await, `await using` lets the return complete on the async path,
//      mirroring the value-type lease's async disposal.
Console.WriteLine("=== 4. Async scope (await using) ===");
await using (var asyncScope = await pool.AcquireScopeAsync(CancellationToken.None))
{
    await Task.Delay(1);
    asyncScope.Value.DoWork("async");
}

pool.Dispose();
Console.WriteLine();
Console.WriteLine("Scoped sample completed.");

// A scope received as a normal argument: only possible because HayatePoolScope<T> is a reference type that
// can cross method boundaries; a value-type HayatePoolLease<T> could not be passed this way.
static void ProcessAcrossFrames(HayatePoolScope<Resource> scope)
{
    // The scope is a reference type, so it crosses method boundaries naturally.
    scope.Value.DoWork("cross-frame");
}
