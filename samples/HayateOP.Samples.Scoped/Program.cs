// HayateOP sample: class-based scoped borrows (HayatePoolScope<T>) for cross-frame lifetimes
// Build & run:  dotnet run --project samples/HayateOP.Samples.Scoped
using System;
using System.Threading;
using System.Threading.Tasks;
using DotNetCore.HayateOP;

using DotNetCore.HayateOP.Samples.Scoped;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var pool = new HayatePoolBuilder<Resource>()
    .WithMinSize(2)
    .WithMaxSize(16)
    .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand)
    .Build();

// 1) Basic scoped borrow: `using` returns the object even if the body throws.
Console.WriteLine("=== 1. Scoped borrow (using) ===");
using (var scope = pool.AcquireScoped())
{
    scope.Value.DoWork("basic");
}

// 2) Pass the scope to a method that outlives the calling frame. Because the scope is a reference type,
//    it travels anywhere a normal object can -- unlike the value-type HayatePoolLease<T>, which lives on
//    the stack and cannot be handed across frames.
Console.WriteLine("=== 2. Scope handed to another method ===");
using (var scope = pool.AcquireScoped())
{
    ProcessAcrossFrames(scope);
}

// 3) Store the scope in a field and use it while it is still alive. The lease owns the object until disposed.
Console.WriteLine("=== 3. Scope held in a field ===");
HayatePoolScope<Resource>? field = null;
using (var scope = pool.AcquireScoped())
{
    field = scope;
    field.Value.DoWork("via-field");
}
// field is disposed now; touching field.Value would throw ObjectDisposedException.

// 4) Async scope spanning an await: dispose with `await using` so the return can await an async hook.
Console.WriteLine("=== 4. Async scope (await using) ===");
await using (var asyncScope = await pool.AcquireScopeAsync(CancellationToken.None))
{
    await Task.Delay(1);
    asyncScope.Value.DoWork("async");
}

pool.Dispose();
Console.WriteLine();
Console.WriteLine("Scoped sample completed.");

static void ProcessAcrossFrames(HayatePoolScope<Resource> scope)
{
    // The scope is a reference type, so it crosses method boundaries naturally.
    scope.Value.DoWork("cross-frame");
}
