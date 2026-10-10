# HayateOP.Samples.Resilience

> Availability & backpressure — the pool-level circuit breaker and the reject policies that decide what happens at capacity.

## What this sample demonstrates

- The pool-level circuit breaker: `WithEnableCircuitBreaker` plus `WithOnUnavailable` / `WithOnAvailable` callbacks, and `CheckAvailable` / `SetUnavailable` / `SetAvailable` to take a pool out of (and back into) service as its dependency fails/recovers.
- The `Abort` reject policy: with the pool at capacity, the next borrow throws immediately instead of blocking.
- The `BlockTimeout` reject policy (the default): with the pool at capacity, the next borrow blocks until `WithAcquireTimeout` elapses, then throws `TimeoutException`.

## When to use this pattern

Use the circuit breaker when the dependency behind the pool can go down and you want callers to fail fast / shed load. Use `Abort` for hard fail-fast semantics and `BlockTimeout` for bounded backpressure where a short wait is acceptable.

## How to run

```bash
dotnet run --project samples/HayateOP.Samples.Resilience
```

> Requires .NET 10. The app prints a step-by-step walkthrough and exits 0 on success.

## Walkthrough

| Step | Concept | Key API |
|------|---------|---------|
| 1 | Circuit breaker availability toggle | `CheckAvailable` / `SetUnavailable` / `SetAvailable` |
| 2 | `Abort` throws immediately at capacity | `WithRejectPolicy(Abort)` |
| 3 | `BlockTimeout` waits, then times out | `WithRejectPolicy(BlockTimeout)` + `WithAcquireTimeout` |

## Key APIs used

| API | Purpose |
|-----|---------|
| `WithEnableCircuitBreaker` / `WithOnUnavailable` / `WithOnAvailable` | Availability transitions and their callbacks. |
| `CheckAvailable` / `SetUnavailable` / `SetAvailable` | Probe and drive the breaker manually. |
| `HayatePoolRejectPolicy.Abort` | Fail immediately when no object is free. |
| `HayatePoolRejectPolicy.BlockTimeout` | Wait up to `WithAcquireTimeout`, then `TimeoutException`. |

## Notes & gotchas

- `Abort` vs. `BlockTimeout` are the two "at capacity" behaviors shown here; other policies (`CreateNew`, `CreateOnDemand`) serve the caller instead of failing — see the other samples.
- The demo uses a very short `WithAcquireTimeout(200ms)` for `BlockTimeout` so the timeout is observable quickly; raise it for real backpressure.
