# HayateOP.Samples.AbandonedRecovery

> **Abandoned Recovery** — with `RemoveAbandonedOnBorrow` / `RemoveAbandonedOnMaintenance` enabled and a
> `RemoveAbandonedTimeout` set, the pool reclaims (destroys) objects that were borrowed and never returned
> past the timeout. This is **destructive** and OFF by default — opt in deliberately (CHOPIN `AbandonedConfig`
> semantics).

## What this sample demonstrates

A pool with abandoned recovery on (`RemoveAbandonedTimeout = 300 ms`, both on-borrow and on-maintenance scan).
The sample borrows an object and **never returns it**, then waits past the timeout. The *next* borrow triggers
the on-borrow reclaim scan, which destroys the overdue object — `GetStats().AbandonedRemovedCount` becomes
non-zero and the subsequent borrow still succeeds (the reclaimed slot is reused).

## When to use this pattern

- You run untrusted call paths where a lease can be lost (exception before `Release`, captured `await` never
  resumed) and you cannot afford objects to leak permanently.
- You want the pool to self-heal by destroying overdue borrows, trading the (rare) risk of destroying an
  object a slow caller still holds for bounded, predictable footprint.

## How to run

```bash
dotnet run --project samples/HayateOP.Samples.AbandonedRecovery
```

## Walkthrough

| Step | Concept | Key API |
| --- | --- | --- |
| Enable recovery | on-borrow + on-maintenance scans | `WithRemoveAbandonedOnBorrow()` / `WithRemoveAbandonedOnMaintenance()` |
| Set the timeout | borrow age that counts as "abandoned" | `WithRemoveAbandonedTimeout(TimeSpan)` |
| Speed up the scan | maintenance run fires often | `WithRemoveAbandonedInterval(200)` |
| Borrow and don't return | simulate a lost lease | `Acquire()` |
| Trigger reclaim | next borrow scans for overdue borrows | `Acquire()` |
| Observe reclamation | destroyed overdue object | `GetStats().AbandonedRemovedCount` |

## Key APIs used

- `HayatePoolBuilder<T>.WithRemoveAbandonedOnBorrow(bool)`
- `HayatePoolBuilder<T>.WithRemoveAbandonedOnMaintenance(bool)`
- `HayatePoolBuilder<T>.WithRemoveAbandonedTimeout(TimeSpan)`
- `HayatePoolBuilder<T>.WithRemoveAbandonedInterval(int)`
- `HayatePoolStats.AbandonedRemovedCount`

## Notes & gotchas

- Recovery is **destructive** and OFF by default. Only the single caller that wins the claim destroys the
  object, so a borrow returned just in time is never destroyed under its holder.
- On-borrow recovery runs as part of the next `Acquire`; if you prefer not to depend on a borrow happening,
  enable `RemoveAbandonedOnMaintenance` so the background run also reclaims.
- After an object is reclaimed, it is no longer tracked by the pool — **do not** `Release` it afterwards.
- Recovery is independent of leak detection: leak detection *counts/flags* overdue borrows, abandoned recovery
  *physically reclaims* them.
