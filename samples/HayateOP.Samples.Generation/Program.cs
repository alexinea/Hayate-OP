// HayateOP sample: generation optimization
// Build & run:  dotnet run --project samples/HayateOP.Samples.Generation
//
// Maps to the README feature "Generation Optimization": long-lived objects are promoted to the OLD
// generation once they pass WithGenerationThreshold of age, and the old generation then skips validation
// on most borrows (controlled by WithOldGenerationValidationInterval) — reducing validation frequency for
// objects that are clearly healthy.
using System;
using System.Threading;
using DotNetCore.HayateOP;
using DotNetCore.HayateOP.Samples.Generation;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// NOTE: top-level statements => global-namespace Program. Both `using` directives must stay.

var pool = new HayatePoolBuilder<Resource>()
    .WithMinSize(0)
    .WithMaxSize(8)
    .WithEnableSharding(false)
    .WithEnableValidation(true)
    .WithValidateInterval(1000)               // idle validation cadence (minimum 1000 ms)
    .WithEnableGenerationOptimization(true)   // long-lived objects -> old generation
    .WithGenerationThreshold(1000)            // promote after 1000 ms of age (minimum 1000 ms)
    .WithOldGenerationValidationInterval(5)    // old gen: skip validation on 4 of every 5 borrows
    .Build();

// Hold one object borrowed (alive) past the generation threshold so it promotes on the next borrow.
var res = pool.Acquire();
Thread.Sleep(1100);   // age > GenerationThresholdMs (1000 ms)
pool.Release(res);

// Borrow it again — on THIS borrow the engine promotes it to the old generation (Generation = 1).
var res2 = pool.Acquire();
pool.Release(res2);

var snap = pool.TakeSnapshot();
Console.WriteLine("=== Per-object generation after promotion ===");
foreach (var d in snap.ObjectDetails)
{
    Console.WriteLine($"   object (lease count {d.LeaseCount}) -> Generation = {d.Generation} (0 = young, 1 = old)");
}

Console.WriteLine();
Console.WriteLine("Generation-optimization sample completed.");
pool.Dispose();
