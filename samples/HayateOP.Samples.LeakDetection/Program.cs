// HayateOP sample: leak detection
// Build & run:  dotnet run --project samples/HayateOP.Samples.LeakDetection
//
// Maps to the README feature "Leak Detection": with WithEnableLeakDetection(true) the pool tracks borrowed
// objects against WithLeakDetectionThreshold; an object borrowed past the threshold without being returned
// is counted as a leak. Stack-trace capture is opt-in via WithLeakTraceCapture (Off / Sampled / EveryAcquire).
using System;
using System.Threading;
using DotNetCore.HayateOP;
using DotNetCore.HayateOP.Samples.LeakDetection;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// NOTE: top-level statements => global-namespace Program. Both `using` directives must stay.

var pool = new HayatePoolBuilder<Resource>()
    .WithMinSize(0)
    .WithMaxSize(8)
    .WithEnableSharding(false)
    .WithEnableLeakDetection(true)
    .WithLeakDetectionThreshold(TimeSpan.FromMilliseconds(200))
    .WithLeakTraceCapture(HayateLeakTraceCaptureMode.Sampled, 1)   // capture the stack on every borrow (demo)
    .Build();

Console.WriteLine("   borrowing an object and deliberately NOT returning it...");
var leaked = pool.Acquire();
Thread.Sleep(300);   // borrowed past the leak threshold without being returned

var snap = pool.TakeSnapshot();
Console.WriteLine($"   snapshot.LeakCount = {snap.LeakCount} (borrowed past threshold without return)");
Console.WriteLine($"   stats.LeakDetectedCount = {pool.GetStats().LeakDetectedCount}");
if (snap.LeakTraces.Count > 0)
{
    Console.WriteLine($"   leak trace captured (first): {snap.LeakTraces[0]}");
}

pool.Release(leaked);   // return it so the pool shuts down cleanly
pool.Dispose();

Console.WriteLine();
Console.WriteLine("Leak-detection sample completed.");
