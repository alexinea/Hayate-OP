# HayateOP.Samples.RejectPolicies

> **Multiple Reject Policies** — when a borrow finds no idle object and the pool cannot (or should not)
> create one, the configured `HayatePoolRejectPolicy` decides what happens: `Abort`, `Block`, `BlockTimeout`,
> `CreateNew`, or `CreateOnDemand`.

## What this sample demonstrates

Five tiny pools (`MaxSize = 1`) — each fills up with one borrowed object, then a second borrow forces the
policy to act:

- **Abort** — throws `InvalidOperationException` immediately (no waiting).
- **BlockTimeout** — waits up to the acquire timeout, then throws `TimeoutException`.
- **CreateOnDemand** — grows the pool and serves the borrow (a 2nd object is created beyond `MaxSize`).
- **CreateNew** — also creates a new object to satisfy the borrow.
- **Block** — waits (no timeout) until an object is returned; a background task releases one and the blocked
  borrow proceeds.

## When to use this pattern

- **Abort** — callers must fail fast when the pool is saturated (fail-closed, no queueing).
- **BlockTimeout** — bounded queueing: callers wait a little, but a saturated pool still fails fast.
- **Block** — unbounded queueing when a borrow must eventually succeed and latency is acceptable.
- **CreateNew / CreateOnDemand** — absorb bursts by creating beyond `MaxSize` instead of refusing (the pool
  self-relieves as objects are returned); `CreateOnDemand` also raises the pool's capacity so the new object
  becomes a permanent member, while `CreateNew` creates a one-off outside the ceiling.

## How to run

```bash
dotnet run --project samples/HayateOP.Samples.RejectPolicies
```

## Walkthrough

| Policy | On exhaustion | Key API |
| --- | --- | --- |
| Abort | throw `InvalidOperationException` | `WithRejectPolicy(HayatePoolRejectPolicy.Abort)` |
| BlockTimeout | wait, then `TimeoutException` | `WithRejectPolicy(...BlockTimeout)` + `WithAcquireTimeout` |
| CreateOnDemand | create beyond `MaxSize` | `WithRejectPolicy(...CreateOnDemand)` |
| CreateNew | create beyond `MaxSize` | `WithRejectPolicy(...CreateNew)` |
| Block | wait until returned | `WithRejectPolicy(...Block)` |

## Key APIs used

- `HayatePoolBuilder<T>.WithRejectPolicy(HayatePoolRejectPolicy)`
- `HayatePoolBuilder<T>.WithAcquireTimeout(TimeSpan)`
- `HayatePoolRejectPolicy` (`Abort` / `Block` / `BlockTimeout` / `CreateNew` / `CreateOnDemand`)
- `IHayateObjectPool<T>.Acquire()` / `Release(T)` / `GetStats()`

## Notes & gotchas

- The default policy is `BlockTimeout`, so a saturated pool fails fast instead of queueing forever.
- `CreateNew` / `CreateOnDemand` trade a bounded footprint for burst absorption — watch `TotalCreated` in
  production; without a corresponding return path the pool can grow.
- `Block` has no timeout by design; pair it with an external `CancellationToken` on `AcquireAsync` if you need
  a cancelable unbounded wait.
- `Abort` reports against `MaxActiveObjects` (the concurrent-borrow ceiling), which defaults in step with
  `MaxPoolSize` — exhaust the active ceiling, not just the idle set, to trigger it.
