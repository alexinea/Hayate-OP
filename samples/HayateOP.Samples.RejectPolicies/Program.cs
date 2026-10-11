// HayateOP sample: multiple reject policies
// Build & run:  dotnet run --project samples/HayateOP.Samples.RejectPolicies
//
// Maps to the README feature "Multiple Reject Policies": when a borrow finds no idle object and the pool
// cannot (or should not) create one, the configured HayatePoolRejectPolicy decides what happens — Abort,
// Block, BlockTimeout, CreateNew, or CreateOnDemand.
using System;
using System.Threading;
using System.Threading.Tasks;
using DotNetCore.HayateOP;
using DotNetCore.HayateOP.Samples.RejectPolicies;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// NOTE: top-level statements => global-namespace Program. All `using` directives above must stay.

// A tiny pool (MaxSize = 1) so a second borrow with the first still out forces the reject policy to act.
//
// MinSize = 1 pre-creates one object. This matters for Abort in particular: the Abort policy rejects a
// borrow IMMEDIATELY when no idle object is available — it never cold-boots an empty pool — so the pool
// must already hold an object for the *second* borrow (with the first still out) to be the one rejected.
IHayateObjectPool<Resource> Build(HayatePoolRejectPolicy policy) =>
    new HayatePoolBuilder<Resource>()
        .WithMinSize(1)
        .WithMaxSize(1)
        .WithEnableSharding(false)
        .WithRejectPolicy(policy)
        .WithAcquireTimeout(TimeSpan.FromMilliseconds(300))
        .Build();

// --- Abort: refuse immediately with InvalidOperationException when the pool is exhausted. ---
Console.WriteLine("=== Abort ===");
{
    var p = Build(HayatePoolRejectPolicy.Abort);
    var o1 = p.Acquire();
    try
    {
        p.Acquire();
        Console.WriteLine("   ERROR: Abort did not throw");
    }
    catch (InvalidOperationException)
    {
        Console.WriteLine("   Abort threw InvalidOperationException as expected (no wait)");
    }

    p.Release(o1);
    p.Dispose();
}

// --- BlockTimeout: wait up to the acquire timeout, then throw TimeoutException. ---
Console.WriteLine("=== BlockTimeout (300 ms) ===");
{
    var p = Build(HayatePoolRejectPolicy.BlockTimeout);
    var o1 = p.Acquire();
    try
    {
        p.Acquire();
        Console.WriteLine("   ERROR: BlockTimeout did not throw");
    }
    catch (TimeoutException)
    {
        Console.WriteLine("   BlockTimeout threw TimeoutException after the 300 ms acquire timeout");
    }

    p.Release(o1);
    p.Dispose();
}

// --- CreateOnDemand: grow the pool and serve the borrow (creates a 2nd object beyond MaxSize). ---
Console.WriteLine("=== CreateOnDemand (grows past MaxSize) ===");
{
    var p = Build(HayatePoolRejectPolicy.CreateOnDemand);
    var o1 = p.Acquire();
    var o2 = p.Acquire();
    // The two objects must be distinct instances: the pool created a 2nd one even though MaxSize = 1.
    Console.WriteLine($"   both borrows succeeded; the two objects are {(ReferenceEquals(o1, o2) ? "the SAME (ERROR)" : "distinct -> pool grew past MaxSize = 1")}");
    p.Release(o1);
    p.Release(o2);
    p.Dispose();
}

// --- CreateNew: like CreateOnDemand, also creates a new object to satisfy the borrow. ---
Console.WriteLine("=== CreateNew (grows past MaxSize) ===");
{
    var p = Build(HayatePoolRejectPolicy.CreateNew);
    var o1 = p.Acquire();
    var o2 = p.Acquire();
    Console.WriteLine($"   both borrows succeeded; the two objects are {(ReferenceEquals(o1, o2) ? "the SAME (ERROR)" : "distinct -> pool grew past MaxSize = 1")}");
    p.Release(o1);
    p.Release(o2);
    p.Dispose();
}

// --- Block: wait (no timeout) until an object becomes available; here a background task returns one. ---
Console.WriteLine("=== Block (waits until an object is returned) ===");
{
    var p = Build(HayatePoolRejectPolicy.Block);
    var o1 = p.Acquire();
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var t = Task.Run(() =>
    {
        Thread.Sleep(300);
        p.Release(o1);   // a borrowed object becomes available -> the blocked caller proceeds
    });
    var o2 = p.Acquire();   // blocks until o1 is released
    sw.Stop();
    Console.WriteLine($"   Block returned after {sw.ElapsedMilliseconds} ms once an object became available");
    p.Release(o2);
    p.Dispose();
    t.Wait();
}

Console.WriteLine();
Console.WriteLine("Reject-policies sample completed.");
