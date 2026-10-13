// HayateOP sample: reset timing switches (ResetOnReturn / ResetOnBorrow) — 3.1, G-7
// Build & run:  dotnet run --project samples/HayateOP.Samples.ResetTiming
using System;
using DotNetCore.HayateOP;
using DotNetCore.HayateOP.Samples.ResetTiming;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// NOTE: this file uses top-level statements, so the implicit `Program` lives in the GLOBAL namespace.
// Both `using` directives above must stay: `DotNetCore.HayateOP` brings in the pool types, and
// `DotNetCore.HayateOP.Samples.ResetTiming` brings in `ResettableBuffer` / `SelfResettingPolicy`.

// A deterministic pool, so the object a case borrows is the object it gets back: one shard (a per-shard
// capacity of MaxPoolSize / ShardCount would floor to zero with the default four), no auto-scaling so the
// ceiling stays put, no eviction so nothing is destroyed between the two halves of a cycle, and
// CreateOnDemand so an empty pool hands out a new object instead of making the case wait out a timeout.
static HayatePoolBuilder<ResettableBuffer> Builder() => new HayatePoolBuilder<ResettableBuffer>()
    .WithShardCount(1)
    .WithEnableAutoScaling(false)
    .WithEnableEviction(false)
    .WithMinSize(0)
    .WithMaxSize(4)
    .WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand);

// Borrow, dirty and return one object, then borrow again — the full cycle both timings are about. The
// second borrow is asserted to be the same instance, so the reset counts read afterwards belong to one
// object's round trip and not to two different objects.
static ResettableBuffer RunOneCycle(IHayateObjectPool<ResettableBuffer> pool)
{
    var buffer = pool.Acquire();
    buffer.Payload = 7;
    pool.Release(buffer);
    return pool.Acquire();
}

// SECTION 1 — The default timing: reset on the way back into the pool. Every pool before 3.1 behaved
// this way, so a pool that never asks is unchanged. The borrow path does no reset work at all.
Console.WriteLine("=== 1. Default: ResetOnReturn (historical behaviour) ===");
using (var pool = Builder().Build())
{
    var first = pool.Acquire();
    Console.WriteLine($"   borrow #1 -> ResetCount={first.ResetCount}, Payload={first.Payload}");

    first.Payload = 7;
    pool.Release(first);
    Console.WriteLine($"   after release -> ResetCount={first.ResetCount}, Payload={first.Payload}  (clean on return)");

    var second = pool.Acquire();
    Console.WriteLine($"   borrow #2 (same instance: {ReferenceEquals(first, second)}) -> ResetCount={second.ResetCount}");
}

// SECTION 2 — Move the reset to the borrow side. The reset cost follows the object out to the borrower,
// and an object that is never borrowed again is never reset at all. The discriminating observation is
// that a returned object still holds what its borrower left in it.
Console.WriteLine("=== 2. ResetOnBorrow: reset on the way out ===");
using (var pool = Builder()
           .WithResetOnReturn(false)
           .WithResetOnBorrow(true)
           .Build())
{
    var first = pool.Acquire();
    Console.WriteLine($"   borrow #1 -> ResetCount={first.ResetCount}  (the create-on-miss hand-out runs the acquire hook)");

    first.Payload = 7;
    pool.Release(first);
    Console.WriteLine($"   after release -> ResetCount={first.ResetCount}, Payload={first.Payload}  (dirty across the return)");

    var second = pool.Acquire();
    Console.WriteLine($"   borrow #2 (same instance: {ReferenceEquals(first, second)}) -> ResetCount={second.ResetCount}, Payload={second.Payload}");
}

// SECTION 3 — Both switches on. Legal on purpose: a borrower that may hand back an object it has already
// dirtied, and a next borrower that must not see it either. The price is two resets a cycle.
Console.WriteLine("=== 3. Both switches: reset on both sides of the cycle ===");
using (var pool = Builder()
           .WithResetOnReturn(true)
           .WithResetOnBorrow(true)
           .Build())
{
    var buffer = RunOneCycle(pool);
    Console.WriteLine($"   one cycle -> ResetCount={buffer.ResetCount}, Payload={buffer.Payload}  (hand-out + return + reuse)");
}

// SECTION 4 — Neither switch. Also legal: the pool of stateless payloads that simply does not need a
// reset, and the object keeps the state its borrower left in it all the way round.
Console.WriteLine("=== 4. Neither switch: never reset ===");
using (var pool = Builder()
           .WithResetOnReturn(false)
           .WithResetOnBorrow(false)
           .Build())
{
    var buffer = RunOneCycle(pool);
    Console.WriteLine($"   one cycle -> ResetCount={buffer.ResetCount}, Payload={buffer.Payload}  (state survives)");
}

// SECTION 5 — Lean pins the reset back to the return side rather than dropping it. A caller who had moved
// the reset to the borrow side must land on the return side, not on "no reset at all" — so the lean
// fast path writes both halves. Read the effective value back through GetOptions().
Console.WriteLine("=== 5. Lean: pinned to the return side ===");
using (var pool = Builder()
           .WithResetOnBorrow(true)   // asked for the borrow side ...
           .WithLean()                // ... then the mode overrides it
           .WithMinSize(0)
           .WithMaxSize(4)
           .Build())
{
    var options = pool.GetOptions();
    Console.WriteLine($"   effective GetOptions -> ResetOnReturn={options.ResetOnReturn}, ResetOnBorrow={options.ResetOnBorrow}");

    var first = pool.Acquire();
    first.Payload = 7;
    pool.Release(first);
    Console.WriteLine($"   after release -> ResetCount={first.ResetCount}, Payload={first.Payload}");
}

// SECTION 6 — Jurisdiction boundary: a custom policy owns its own hooks. The switches reach only the
// reset the pool and the default policy drive, so this policy's reset survives ResetOnReturn = false.
Console.WriteLine("=== 6. Custom policy: not governed by the switches ===");
using (var pool = Builder()
           .WithPolicy(new SelfResettingPolicy())
           .WithResetOnReturn(false)
           .WithResetOnBorrow(true)
           .Build())
{
    var buffer = RunOneCycle(pool);
    Console.WriteLine($"   one cycle -> ResetCount={buffer.ResetCount}, Payload={buffer.Payload}  (the policy reset once, on release)");
}

// SECTION 7 — HayateUnboundedPool<T> takes no options object, so the switches have no route into it and
// its timing is fixed to the return side. The defaults GetOptions() reports are a true statement of that.
Console.WriteLine("=== 7. Unbounded pool: fixed to the return side ===");
using (var pool = new HayateUnboundedPool<ResettableBuffer>(4))
{
    var options = pool.GetOptions();
    var first = pool.Acquire();

    first.Payload = 7;
    pool.Release(first);
    Console.WriteLine($"   GetOptions -> ResetOnReturn={options.ResetOnReturn}, ResetOnBorrow={options.ResetOnBorrow}");
    Console.WriteLine($"   after release -> ResetCount={first.ResetCount}, Payload={first.Payload}");
}

Console.WriteLine();
Console.WriteLine("Reset-timing sample completed.");
