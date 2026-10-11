# HayateOP.Samples.SoftCapacity

> **Soft Capacity** — `WithSoftCapacity(n)` bounds the pool's *retained* set on the return path. A
> returned object past the soft ceiling is disposed instead of parked, while the pool still lends out its
> whole hard ceiling (`MaxPoolSize`).

## What this sample demonstrates

Two pools with the same `MaxPoolSize = 8`:

- **No soft capacity** — all 8 returned objects are parked; `PooledCount` is 8.
- **Soft capacity = 3** — only the first 3 returned objects are kept; the remaining 5 are dropped
  (disposed) on return. `PooledCount` settles at 3.

This lets a burst create as many objects as the hard ceiling allows, then shed the excess once the
pressure subsides, capping steady-state retention.

## When to use this pattern

- You must absorb short-lived peaks (`MaxPoolSize` high) but want to avoid *permanently* retaining a huge
  idle set afterwards (soft capacity low).
- You want a leak/cap on memory footprint without refusing bursts.

## How to run

```bash
dotnet run --project samples/HayateOP.Samples.SoftCapacity
```

## Walkthrough

| Step | Concept | Key API |
| --- | --- | --- |
| Build pool, MaxPoolSize = 8 | hard ceiling for lending | `WithMaxSize(8)` |
| Borrow & return 8 | fills the pool | `Acquire()` / `Release(T)` |
| Observe `PooledCount` | all 8 retained | `GetStats().PooledCount` |
| Rebuild with `WithSoftCapacity(3)` | retained set capped on return | `WithSoftCapacity(3)` |
| Borrow & return 8 again | only 3 kept, 5 dropped | `GetStats().PooledCount` |

## Key APIs used

- `HayatePoolBuilder<T>.WithSoftCapacity(int)`
- `HayatePoolBuilder<T>.WithMaxSize(int)`
- `IHayateObjectPool<T>.GetStats()` → `HayatePoolStats.PooledCount`

## Notes & gotchas

- Soft capacity is a *retention* limit, not a *lending* limit: a borrow still succeeds up to `MaxPoolSize`
  even when the retained set is smaller — the pool simply creates on demand.
- `SoftCapacity` must satisfy `0 < SoftCapacity <= MaxPoolSize` (and `>= MinPoolSize`); an invalid value
  fails `IsValid` at build time.
- Unlike bounded idle eviction (which targets stale objects), soft capacity drops objects purely by slot
  position on return — it is the simplest, most predictable way to cap footprint after a burst.
