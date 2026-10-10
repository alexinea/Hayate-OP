# HayateOP.Samples.ActiveCeiling

> Bounding concurrent borrows with the active-object ceiling (`MaxActiveObjects`, the G-5 feature) — extra borrows wait instead of failing or over-creating.

## What this sample demonstrates

- `WithMaxActiveObjects(n)` caps how many objects may be borrowed at the same time, independent of `MaxPoolSize` (which still bounds how many the pool may *hold*).
- A fan-in of 20 concurrent borrows against a ceiling of 2: borrows block until an active slot frees, and the observed max concurrency never exceeds the ceiling.
- `ActiveLimitThrottledCount` counts how many borrows were held back by the ceiling (distinct from failures).

## When to use this pattern

Use when the resource behind the pool has a hard concurrency limit (e.g. a licensed connection count, a downstream rate limit, or a finite set of file handles) and you want to bound *in-flight* work while still allowing the pool to retain more objects when idle.

## How to run

```bash
dotnet run --project samples/HayateOP.Samples.ActiveCeiling
```

> Requires .NET 10. The app prints a step-by-step walkthrough and exits 0 on success.

## Walkthrough

| Step | Concept | Key API |
|------|---------|---------|
| 1 | Fan-in under a tight active ceiling | `WithMaxActiveObjects(2)` + 20 `Task.Run` borrows |

## Key APIs used

| API | Purpose |
|-----|---------|
| `HayatePoolBuilder<T>.WithMaxActiveObjects` | Cap concurrent borrows (in-flight ceiling). |
| `HayatePoolRejectPolicy.BlockTimeout` + `WithAcquireTimeout` | Wait (bounded) for a free active slot. |
| `HayatePoolStats.ActiveLimitThrottledCount` | Count of borrows delayed by the ceiling. |
| `HayatePoolStats.PeakActiveObjects` | Highest observed concurrent active count. |

## Notes & gotchas

- The ceiling bounds *concurrent active* borrows, not the pool's total size — set `MaxPoolSize` higher than the ceiling if you want spare objects parked for fast reuse.
- `BlockTimeout` is used here so the excess borrows wait for a slot; with `CreateOnDemand` the ceiling would instead be bypassed by creating more objects, defeating the limit.
