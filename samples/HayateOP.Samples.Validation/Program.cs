// HayateOP sample: object health -- validation, leak detection, on-demand eviction
// Build & run:  dotnet run --project samples/HayateOP.Samples.Validation
using System;
using System.Threading;
using DotNetCore.HayateOP;

using DotNetCore.HayateOP.Samples.Validation;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// Build a pool of Session objects (which implement IHayateValidatable). Validation is turned on at
// every opportunity: on borrow, on return, and on idle objects during maintenance, each interval apart.
var pool = new HayatePoolBuilder<Session>()
    .WithMinSize(0)
    .WithMaxSize(16)
    .WithEnableValidation(true)
    .WithValidateOnBorrow(true)
    .WithValidateOnReturn(true)
    .WithValidateWhileIdle(true)
    .WithValidateInterval(5000)
    // Leak detection watches for objects borrowed and never returned; the threshold below is how long
    // a borrow may stay out before it is counted as a suspected leak.
    .WithEnableLeakDetection(true)
    .WithLeakDetectionThreshold(TimeSpan.FromSeconds(1))
    .Build();

// Section 1: a healthy session is validated on return and parked for reuse. GetStats().PooledCount
// shows 1, proving the object survived the return-path validation instead of being destroyed.
Console.WriteLine("=== 1. Valid objects are returned and reused ===");
var s1 = pool.Acquire();
s1.Use();
pool.Release(s1);
Console.WriteLine($"   pooled after healthy return = {pool.GetStats().PooledCount}");

// Section 2: close the session before returning it. IsValid() now returns false, so the return-path
// validation fails and the pool destroys the object rather than parking it -> PooledCount stays 0.
Console.WriteLine("=== 2. An invalid object is destroyed, not returned ===");
var s2 = pool.Acquire();
s2.Close(); // mark invalid; Validate() fails on return
pool.Release(s2);
Console.WriteLine($"   pooled after invalid return = {pool.GetStats().PooledCount} (0 => destroyed)");

// Section 3: Evict() lets you proactively reclaim idle objects by category on demand. Idle evicts
// objects that have exceeded the configured idle time; here the just-returned object qualifies.
Console.WriteLine("=== 3. On-demand idle eviction ===");
var s3 = pool.Acquire();
pool.Release(s3);
var evicted = pool.Evict(HayateEvictReason.Idle);
Console.WriteLine($"   evicted idle objects on demand = {evicted}");

// Section 4: leak detection. We borrow an object and deliberately never return it, then wait past the
// leak threshold. TakeSnapshot().LeakCount reports how many borrows are suspected of being leaked.
Console.WriteLine("=== 4. Leak detection (borrow and never return) ===");
var leaked = pool.Acquire(); // intentionally not released
Console.WriteLine("   borrowed an object and will not release it; waiting for leak threshold...");
Thread.Sleep(1500);
var snapshot = pool.TakeSnapshot();
Console.WriteLine($"   snapshot LeakCount={snapshot.LeakCount}");
pool.Release(leaked); // clean up

pool.Dispose();
Console.WriteLine();
Console.WriteLine("Validation sample completed.");
