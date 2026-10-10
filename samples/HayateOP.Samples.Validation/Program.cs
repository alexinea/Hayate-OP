// HayateOP sample: object health -- validation, leak detection, on-demand eviction
// Build & run:  dotnet run --project samples/HayateOP.Samples.Validation
using System;
using System.Threading;
using DotNetCore.HayateOP;

using DotNetCore.HayateOP.Samples.Validation;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var pool = new HayatePoolBuilder<Session>()
    .WithMinSize(0)
    .WithMaxSize(16)
    .WithEnableValidation(true)
    .WithValidateOnBorrow(true)
    .WithValidateOnReturn(true)
    .WithValidateWhileIdle(true)
    .WithValidateInterval(5000)
    .WithEnableLeakDetection(true)
    .WithLeakDetectionThreshold(TimeSpan.FromSeconds(1))
    .Build();

Console.WriteLine("=== 1. Valid objects are returned and reused ===");
var s1 = pool.Acquire();
s1.Use();
pool.Release(s1);
Console.WriteLine($"   pooled after healthy return = {pool.GetStats().PooledCount}");

Console.WriteLine("=== 2. An invalid object is destroyed, not returned ===");
var s2 = pool.Acquire();
s2.Close(); // mark invalid; Validate() fails on return
pool.Release(s2);
Console.WriteLine($"   pooled after invalid return = {pool.GetStats().PooledCount} (0 => destroyed)");

Console.WriteLine("=== 3. On-demand idle eviction ===");
var s3 = pool.Acquire();
pool.Release(s3);
var evicted = pool.Evict(HayateEvictReason.Idle);
Console.WriteLine($"   evicted idle objects on demand = {evicted}");

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
