# HayateOP.Samples.Metrics

> Observability — metrics, allocation tracking and capacity alarms — so operators can see a pool's health in real time.

## What this sample demonstrates

- `HayateMetricsMeter` (built on `System.Diagnostics.Metrics`) attached via `WithMetrics(meter)`, consumable by `dotnet-counters` / OpenTelemetry.
- `WithEnableMetrics(true)` + `WithEnableAllocationTracking(true)`.
- Capacity alarms: `WithCapacityAlarm(0.8, 0.95)` fires `WithOnCapacityWarning` / `WithOnCapacityCritical` callbacks once per state transition as usage (`borrowed / MaxPoolSize`) crosses the warn/critical ratio.
- Reading cumulative point-in-time statistics with `GetStats()`.
- A heavier, object-by-object `TakeSnapshot()` distinct from the cheap `GetStats()`.
- The `CreateOnDemand` reject-policy gotcha that prevents the demo from blocking on a full pool.

## When to use this pattern

Use when a pool is a shared production resource and you need live counters (created/acquired/released/missed), allocation accounting, and proactive capacity-alarm callbacks before callers start timing out.

## How to run

```bash
dotnet run --project samples/HayateOP.Samples.Metrics
```

> Requires .NET 10. The app prints a step-by-step walkthrough and exits 0 on success.

## Walkthrough

| Step | Concept | Key API |
|------|---------|---------|
| 1 | Exercise the pool (borrow/return cycles) | `pool.Acquire()` / `pool.Release()` |
| 2 | Cumulative statistics | `pool.GetStats()` |
| 3 | Capacity alarm wiring | `WithCapacityAlarm` + `WithOnCapacityWarning` / `WithOnCapacityCritical` |
| 4 | Object-by-object snapshot | `pool.TakeSnapshot()` |

## Key APIs used

| API | Purpose |
|-----|---------|
| `HayateMetricsMeter` | `System.Diagnostics.Metrics` sink for HayateOP instruments. |
| `HayatePoolBuilder<T>.WithMetrics` / `WithEnableMetrics` | Attach a metrics sink (metrics must be enabled). |
| `HayatePoolBuilder<T>.WithEnableAllocationTracking` | Count allocations attributable to the pool. |
| `WithCapacityAlarm` / `WithOnCapacityWarning` / `WithOnCapacityCritical` | Threshold callbacks; args carry `PoolName`, `UsageRatio`, `BorrowedCount`, `MaxPoolSize`. |
| `IHayateObjectPool<T>.GetStats` / `TakeSnapshot` | Cheap counters vs. heavy per-object survey. |

## Notes & gotchas

- The default reject policy is `BlockTimeout`. When the pool is full (all `MaxPoolSize` objects lent out), a borrow blocks until the acquire timeout and then throws `TimeoutException`. This sample sets `WithRejectPolicy(HayatePoolRejectPolicy.CreateOnDemand)` so a borrow that finds no idle object is served immediately instead of blocking.
