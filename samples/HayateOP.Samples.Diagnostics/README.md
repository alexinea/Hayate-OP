# HayateOP.Samples.Diagnostics

> Deep observability — live options, pre-warm, runtime reconfiguration, snapshots, the availability/circuit-breaker toggle, and on-demand eviction.

## What this sample demonstrates

- `GetOptions()` — read back the configuration the pool is actually running with.
- `PreWarm(n)` — pay creation cost up front so later borrows find an idle object ready.
- `ReloadConfig(o => ...)` — change options (e.g. `MinPoolSize`) at runtime without rebuilding the pool.
- `TakeSnapshot()` — a point-in-time survey of every live object (`PooledCount`, `BorrowedCount`, `ObjectDetails`, `LeakTraces`).
- The availability / circuit-breaker toggle: with `HayateCircuitBreakerOptions { FailureThreshold = 1 }` a single `SetUnavailable` trips the breaker and raises `OnUnavailable`; `SetAvailable` recovers it.
- On-demand eviction `Evict(HayateEvictReason.Touched)` to reclaim idle objects that have been borrowed before.

## When to use this pattern

Use when you operate a pool and need to introspect and tune it live — inspect options, warm it before a traffic spike, resize at runtime, take forensic snapshots, and drive the breaker from dependency health.

## How to run

```bash
dotnet run --project samples/HayateOP.Samples.Diagnostics
```

> Requires .NET 10. The app prints a step-by-step walkthrough and exits 0 on success.

## Walkthrough

| Step | Concept | Key API |
|------|---------|---------|
| 1 | Live options | `pool.GetOptions()` |
| 2 | Pre-warm | `pool.PreWarm(8)` |
| 3 | Reload config at runtime | `pool.ReloadConfig(o => o.MinPoolSize = 16)` |
| 4 | Snapshot | `pool.TakeSnapshot()` |
| 5 | Availability / circuit breaker | `SetUnavailable` / `SetAvailable` / `CheckAvailable` |
| 6 | On-demand eviction (`Touched`) | `pool.Evict(HayateEvictReason.Touched)` |

## Key APIs used

| API | Purpose |
|-----|---------|
| `GetOptions` | Live, running configuration. |
| `PreWarm` | Eagerly create idle objects. |
| `ReloadConfig` | Mutate options in place at runtime. |
| `TakeSnapshot` | Per-object diagnostic survey (`ObjectDetails`, `LeakTraces`). |
| `WithCircuitBreaker` / `SetUnavailable` / `SetAvailable` | Availability toggle + callbacks. |
| `Evict(HayateEvictReason)` | Reclaim idle objects on demand. |

## Notes & gotchas

- `FailureThreshold` defaults to 3; this sample sets it to 1 so a single `SetUnavailable` trips the breaker crisply for the demo.
- A freshly `PreWarm`-ed object has `LeaseCount == 0`, so `Evict(HayateEvictReason.Touched)` (not `Idle`) is what actually reclaims those warmed objects — the sample touches a few first to make the eviction observable.
