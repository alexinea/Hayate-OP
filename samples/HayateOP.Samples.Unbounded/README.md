# HayateOP.Samples.Unbounded

> An unbounded pool for transient / bursty workloads that never blocks a borrower and only caps idle retention.

## What this sample demonstrates

- `HayateUnboundedPool<T>` — creates objects on demand and never makes a borrower wait or fail.
- Burst borrows: when the idle queue is empty the pool simply creates a new object.
- Optional return: `Release` is not required; an object that is never returned is reclaimed by the GC (the pool keeps no per-object bookkeeping).
- The idle-retention cap `MaxIdle`: only the *resident* set is bounded, not concurrent demand. A return that arrives when `MaxIdle` objects are already idle is destroyed instead of parked.
- A custom factory `Func<T>` supplied to the constructor for objects without a parameterless constructor.

## When to use this pattern

Use an unbounded pool for short-lived, array-like, or buffer-style objects whose lifetime is dominated by creation/reuse churn and where you never want a caller to block. It trades an unbounded ceiling for zero borrow contention; memory is bounded by `MaxIdle`, not by peak concurrency.

## How to run

```bash
dotnet run --project samples/HayateOP.Samples.Unbounded
```

> Requires .NET 10. The app prints a step-by-step walkthrough and exits 0 on success.

## Walkthrough

| Step | Concept | Key API |
|------|---------|---------|
| 1 | Burst borrow — created on demand, never blocks | `pool.Acquire()` / `pool.Release(buf)` (optional) |
| 2 | Idle retention cap (`MaxIdle`) | `pool.MaxIdle` / `pool.PooledCount` |
| 3 | Custom factory `Func<T>` | `new HayateUnboundedPool<T>(maxIdle, factory: ...)` |

## Key APIs used

| API | Purpose |
|-----|---------|
| `HayateUnboundedPool<T>.Acquire` | Borrow; creates on demand when empty. |
| `HayateUnboundedPool<T>.Release` | Return to the idle set (optional). |
| `HayateUnboundedPool<T>.MaxIdle` | Caps only the resident idle set. |
| `HayateUnboundedPool<T>.PooledCount` | Current number of parked objects. |

## Notes & gotchas

- A top-level `Program` lives in the global namespace, so `using DotNetCore.HayateOP;` and `using DotNetCore.HayateOP.Samples.Unbounded;` (`ReusableBuffer`) are required.
- Because `Release` is optional, an object that is never returned leaks until GC — only skip `Release` when you intentionally want GC to reclaim it.
