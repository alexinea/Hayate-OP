# HayateOP.Samples.ResetTiming

> Where the automatic reset of an `IHayateResettable` object runs — on the way back into the pool or on the way out to a borrower.

## What this sample demonstrates

- The **default timing**: an `IHayateResettable` object is reset on release, so every idle object is already clean and the borrow path does no reset work. This is what every pool before 3.1 did.
- **`ResetOnBorrow`**: moving the reset to the borrow side, so a returned object still holds the state its borrower left in it, and an object that is never borrowed again is never reset at all.
- **Both switches on**: a cycle resets twice — legal, for when the previous borrower may hand back a dirtied object and the next one must not see it either.
- **Neither switch on**: a pool of stateless payloads that never resets, and the state survives to the next borrower.
- **Lean pins the reset back to the return side** rather than dropping it — read the effective value back through `GetOptions()`.
- The **jurisdiction boundary**: the switches govern only the reset the pool and the default policy drive; a custom `IHayateObjectPolicy<T>` keeps its own `OnRelease` / `OnAcquire`.
- `HayateUnboundedPool<T>` takes no options object, so its reset timing is fixed to the return side.

## When to use this pattern

Reach for `ResetOnBorrow` when the reset is expensive and the pool over-creates: an object that is created and never borrowed again is then never reset, so you pay for reset only on the objects you actually hand out. The price is that the borrower waits for it, so measure the borrow latency before adopting it on a hot path. Leave the default alone when the borrow path is latency-sensitive — the reset moves, it does not multiply.

## How to run

```bash
dotnet run --project samples/HayateOP.Samples.ResetTiming
```

> Requires .NET 10. The app prints a step-by-step walkthrough and exits 0 on success.

## Walkthrough

| Step | Concept | Key API |
|------|---------|---------|
| 1 | Default: reset on the way back in | (no call — the historical default) |
| 2 | Reset on the way out to a borrower | `WithResetOnReturn(false).WithResetOnBorrow(true)` |
| 3 | Both sides of the cycle | `WithResetOnReturn().WithResetOnBorrow()` |
| 4 | Never reset (stateless payloads) | `WithResetOnReturn(false).WithResetOnBorrow(false)` |
| 5 | Lean pins the reset back to the return side | `WithResetOnBorrow(true).WithLean()` |
| 6 | A custom policy keeps its own reset | `WithPolicy(new SelfResettingPolicy())` |
| 7 | The unbounded pool is fixed to the return side | `new HayateUnboundedPool<T>(maxIdle)` |

## Key APIs used

| API | Purpose |
|-----|---------|
| `HayatePoolBuilder<T>.WithResetOnReturn` / `WithResetOnBorrow` | Choose the side of the cycle the reset runs on. |
| `IHayateResettable` | The marker whose `Reset()` the pool runs (default policy only). |
| `IHayateObjectPolicy<T>` | A custom policy owns `OnRelease` / `OnAcquire`, so the switches never touch it. |
| `IHayateObjectPool<T>.GetOptions()` | Read the **effective** switches back — the lean mode normalizes them at build time. |
| `HayateUnboundedPool<T>` | The second pool model; no options object, reset fixed to the return side. |

## Notes & gotchas

- A top-level `Program` lives in the global namespace, so `using DotNetCore.HayateOP;` (pool types) and `using DotNetCore.HayateOP.Samples.ResetTiming;` (`ResettableBuffer`) are both required.
- With `ResetOnBorrow` on, the pool runs the acquire hook on a **create-on-miss** hand-out too, so a brand-new object is reset once before its first borrower sees it — a reset for state it never had.
- `EnableLean` forces `ResetOnReturn = true` and `ResetOnBorrow = false`. The call is **normalized away rather than rejected**, so read the effective value back through `GetOptions()` instead of assuming the builder call took effect.
- The switches govern the reset the pool and the **default** policy drive only. A policy supplied through `WithPolicy` owns its own hooks and is unaffected — step 6 asserts that boundary rather than assuming it.
