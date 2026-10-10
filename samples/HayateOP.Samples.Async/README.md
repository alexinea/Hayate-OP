# HayateOP.Samples.Async

> Asynchronous borrows — `await using` leases, a `Task.WhenAll` fan-out, external cancellation, and the async acquire-timeout overload.

## What this sample demonstrates

- A single async borrow/return with `await using var lease = await pool.AcquireLeaseAsync(...)` — the lease returns the object on the async path, so no hand-written `try/finally` is needed.
- A concurrent fan-out: 20 tasks sharing a small pool, each using a value-type `HayatePoolLease<T>` (allocation-free) and waiting with `Task.WhenAll`. With `CreateOnDemand` the pool spins up extra connections instead of making callers wait.
- Cancellation: with the only object held, an already-cancelled `CancellationToken` makes `AcquireLeaseAsync` throw `OperationCanceledException` and consumes nothing.
- Timeout: the `(TimeSpan, CancellationToken)` overload bounds the wait; a blocked borrow throws `TimeoutException` while a cancel signal would have surfaced as `OperationCanceledException`.

## When to use this pattern

Use for I/O-bound, highly concurrent workloads (DB/HTTP/gRPC clients) where you borrow over `await` and must support cooperative cancellation and bounded timeouts without blocking threads.

## How to run

```bash
dotnet run --project samples/HayateOP.Samples.Async
```

> Requires .NET 10. The app prints a step-by-step walkthrough and exits 0 on success.

## Walkthrough

| Step | Concept | Key API |
|------|---------|---------|
| 1 | Single async borrow (`await using`) | `await pool.AcquireLeaseAsync(CancellationToken.None)` |
| 2 | Concurrent fan-out (`Task.WhenAll`) | `Task.Run` + `await using var l = await pool.AcquireLeaseAsync(...)` |
| 3 | Cancellation → `OperationCanceledException` | pre-cancelled `CancellationTokenSource` |
| 4 | Async acquire timeout → `TimeoutException` | `AcquireLeaseAsync(TimeSpan, CancellationToken)` |

## Key APIs used

| API | Purpose |
|-----|---------|
| `HayatePoolLease<T>` / `AcquireLeaseAsync` | Async, allocation-free value-type lease. |
| `HayatePoolRejectPolicy.CreateOnDemand` | Grow the pool instead of blocking when all idle objects are borrowed. |
| `CancellationToken` | Cooperative cancellation of a blocked borrow. |
| `AcquireLeaseAsync(TimeSpan, CancellationToken)` | Bounded async wait. |

## Notes & gotchas

- Capture loop variables into a local before `Task.Run` — closures would otherwise all see the final loop value.
- `CreateOnDemand` is what lets the fan-out exceed the idle count without failing; a `BlockTimeout` pool would instead make the excess borrows wait (or time out).
