// ---------------------------------------------------------------------------
// HayateOP.Extensions.ObjectPoolCompat sample
// Demonstrates how Microsoft.Extensions.ObjectPool (MEOP) callers can switch to the HayateOP backend with a single line of code,
// and how to tune HayateCompatOptions, integrate via DI, and the reverse adapter (HayateObjectPoolAdapter).
// Build & run:  dotnet run --project samples/HayateOP.Samples.ObjectPoolCompat
// ---------------------------------------------------------------------------
using System.Text;
using DotNetCore.HayateOP;
using DotNetCore.HayateOP.ObjectPoolCompat;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.ObjectPool;

Console.OutputEncoding = Encoding.UTF8;

// ---------------------------------------------------------------------------
// 0. Strategy: reuse the built-in MEOP StringBuilderPooledObjectPolicy (the same one from the official docs:
//    Create sets the initial capacity; Return clears and resets the object, and oversized objects are rejected from the pool).
//    Custom IPooledObjectPolicy<T> implementations also work unchanged — the compat pool
//    uses HayateCompatPooledObjectPolicy<T> to map the Create/Return hooks to HayateOP's
//    Create/OnRelease semantics.
// ---------------------------------------------------------------------------
Console.WriteLine("=== 0. MEOP-style policy (IPooledObjectPolicy<T>) ===");
var sbPolicy = new StringBuilderPooledObjectPolicy();
Console.WriteLine($"   Policy: StringBuilderPooledObjectPolicy (InitialCapacity={sbPolicy.InitialCapacity}, MaximumRetainedCapacity={sbPolicy.MaximumRetainedCapacity})");

// ---------------------------------------------------------------------------
// 1. Baseline before switching: DefaultObjectPoolProvider (native MEOP behavior)
// ---------------------------------------------------------------------------
Console.WriteLine("=== 1. Before switching: DefaultObjectPoolProvider (MEOP baseline) ===");
DemoPool("MEOP", new DefaultObjectPoolProvider().Create(sbPolicy));

// ---------------------------------------------------------------------------
// 2. One-line switch: new DefaultObjectPoolProvider() -> new HayateObjectPoolCompatProvider()
//    The Get/Return caller code is unchanged; the pool backend becomes HayateOP (sharding, statistics, observability, etc. ship with the package).
// ---------------------------------------------------------------------------
Console.WriteLine("=== 2. One-line switch: HayateObjectPoolCompatProvider ===");
DemoPool("HayateOP", new HayateObjectPoolCompatProvider().Create(sbPolicy));
Console.WriteLine("   - On a cold pool, the first borrow can wait up to AcquireTimeout (default 1s); HayateOP's CreateNew semantics are: " +
                  "create-after-timeout (unlike MEOP, which creates immediately on an empty pool; see the HayateCompatOptions comments). " +
                  "Use the MinSize warm-up from section 3, or shorten AcquireTimeout, to eliminate this wait.");

// ---------------------------------------------------------------------------
// 3. Tuning parameters: HayateCompatOptions
//    - MinSize warm-up: avoids the AcquireTimeout wait on the cold pool's first borrow (HayateOP's
//      CreateNew semantics are create-after-timeout, which differs from MEOP's create-immediately-on-empty-pool);
//    - Shorten AcquireTimeout: lowers the cold-start latency cap;
//    - PoolNamePrefix: a prefix for pool names, making them easier to identify for observability and operations.
// ---------------------------------------------------------------------------
Console.WriteLine("=== 3. HayateCompatOptions tuning (MinSize=2 warm-up + AcquireTimeout=100ms) ===");
var tunedProvider = new HayateObjectPoolCompatProvider(new HayateCompatOptions
{
    PoolNamePrefix = "MyBiz.HayateCompat.",
    MinSize = 2,                                   // Warm up 2 objects to eliminate cold-start latency
    AcquireTimeout = TimeSpan.FromMilliseconds(100)
});
DemoPool("Tuned", tunedProvider.Create(sbPolicy));

// ---------------------------------------------------------------------------
// 4. DI integration: register ObjectPoolProvider as a singleton, inject ObjectPool<T> into consumers.
//    The registration in business code is identical to MEOP; only the provider instance is swapped here.
// ---------------------------------------------------------------------------
Console.WriteLine("=== 4. DI integration (ServiceCollection) ===");
var services = new ServiceCollection();
services.AddSingleton<ObjectPoolProvider>(new HayateObjectPoolCompatProvider(new HayateCompatOptions
{
    MinSize = 1,
    AcquireTimeout = TimeSpan.FromMilliseconds(100)
}));
services.AddSingleton(sp => sp.GetRequiredService<ObjectPoolProvider>().Create(sbPolicy));

using var sp = services.BuildServiceProvider();
var diPool = sp.GetRequiredService<ObjectPool<StringBuilder>>();
var diSb = diPool.Get();
try
{
    diSb.Append("from DI");
    Console.WriteLine($"   DI-injected pool borrow -> \"{diSb}\"");
}
finally
{
    diPool.Return(diSb);   // Reset and return to the pool
}
Console.WriteLine("   Consumers depend only on ObjectPool<StringBuilder>; the backend can be switched at any time in the DI registration.");

// ---------------------------------------------------------------------------
// 5. Reverse adapter: HayateObjectPoolAdapter<T>
//    When you already have a native HayateOP pool (with full features like sharding, auto scaling, leak detection, etc.),
//    you can wrap it as MEOP's ObjectPool<T> for use by MEOP-style consumers.
// ---------------------------------------------------------------------------
Console.WriteLine("=== 5. Reverse adapter: native HayateOP pool -> ObjectPool<T> ===");
using var nativePool = new HayatePoolBuilder<StringBuilder>()
    .WithPoolName("NativeHayateOP.SB")
    .WithMinSize(1)
    .WithMaxSize(32)
    .WithEnableAutoScaling(true)
    .WithRejectPolicy(HayatePoolRejectPolicy.BlockTimeout)
    .WithAcquireTimeout(TimeSpan.FromMilliseconds(100))
    .Build();

using var adapted = new HayateObjectPoolAdapter<StringBuilder>(nativePool);
var sb2 = adapted.Get();
try
{
    sb2.Append("adapted");
    Console.WriteLine($"   Borrowed via adapter -> \"{sb2}\" (current live objects in pool: {nativePool.GetStats().CurrentSize})");
}
finally
{
    adapted.Return(sb2);
}

Console.WriteLine();
Console.WriteLine("All demos completed.");

// ---------------------------------------------------------------------------
// Shared demo: the same Get/Return code path runs against any ObjectPool<StringBuilder>.
// ---------------------------------------------------------------------------
static void DemoPool(string label, ObjectPool<StringBuilder> pool)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var sb = pool.Get();            // First borrow from a cold pool: the HayateOP compat pool is subject to AcquireTimeout
    try
    {
        sb.Append("hello from ").Append(label);
        Console.WriteLine($"   Borrowed ({sw.ElapsedMilliseconds}ms) -> \"{sb}\"");
    }
    finally
    {
        pool.Return(sb);            // The policy's Return hook resets it before returning to the pool
    }

    var sb2 = pool.Get();           // Second borrow: hits an idle object already in the pool, no cold-start latency
    try
    {
        Console.WriteLine($"   Second borrow ({sw.ElapsedMilliseconds}ms, reused from pool) -> \"{sb2}\"(capacity reset)");
    }
    finally
    {
        pool.Return(sb2);
    }
}
