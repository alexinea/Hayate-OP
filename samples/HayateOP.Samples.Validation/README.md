# HayateOP.Samples.Validation

> Object health — validation hooks, leak detection, and on-demand eviction — so unhealthy or leaked objects never silently accumulate.

## What this sample demonstrates

- Validation at every opportunity: on borrow, on return, and on idle objects during maintenance (`WithValidateOnBorrow` / `WithValidateOnReturn` / `WithValidateWhileIdle` + `WithValidateInterval`).
- Pooled objects implementing `IHayateValidatable` whose `IsValid()` gates return-path reuse: a valid object is parked, an invalid one is destroyed.
- `WithEnableLeakDetection` + `WithLeakDetectionThreshold` to flag borrows that are never returned; `TakeSnapshot().LeakCount` reports suspected leaks.
- `Evict(HayateEvictReason.Idle)` for proactive, on-demand reclamation of idle objects by category.

## When to use this pattern

Use when pooled objects can become invalid in use (sessions, connections, parsed state) and you must guarantee that only healthy objects are reused and that forgotten borrows are surfaced as leaks.

## How to run

```bash
dotnet run --project samples/HayateOP.Samples.Validation
```

> Requires .NET 10. The app prints a step-by-step walkthrough and exits 0 on success.

## Walkthrough

| Step | Concept | Key API |
|------|---------|---------|
| 1 | Valid object returned and reused | `pool.Acquire()` / `pool.Release()` |
| 2 | Invalid object destroyed, not returned | `session.Close()` → `IsValid()` false |
| 3 | On-demand idle eviction | `pool.Evict(HayateEvictReason.Idle)` |
| 4 | Leak detection (borrow, never return) | `Thread.Sleep` past threshold → `TakeSnapshot().LeakCount` |

## Key APIs used

| API | Purpose |
|-----|---------|
| `IHayateValidatable.IsValid()` | Gate for reuse; false destroys the object on return. |
| `WithEnableValidation` / `WithValidateOnBorrow` / `WithValidateOnReturn` / `WithValidateWhileIdle` | Where validation runs. |
| `WithValidateInterval` | Maintenance cadence for idle validation. |
| `WithEnableLeakDetection` / `WithLeakDetectionThreshold` | Suspected-leak window. |
| `IHayateObjectPool<T>.Evict` | Reclaim idle objects on demand by `HayateEvictReason`. |

## Notes & gotchas

- Return a closed/invalid object and the pool destroys it (the validation failure path), so `PooledCount` stays `0` — that is the *expected* behavior, not a leak.
- A freshly `PreWarm`-ed object has `LeaseCount == 0`, so `Evict(HayateEvictReason.Touched)` (not `Idle`) is what reclaims objects that have actually been borrowed before.
