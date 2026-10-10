# HayateOP.Samples.Lease

> Zero-allocation value-type leasing with `HayatePoolLease<T>` for latency-sensitive, high-frequency borrows.

## What this sample demonstrates

- The value-type lease `HayatePoolLease<T>` returned by `AcquireLeased` / `AcquireLeaseAsync` — a `struct`, so the borrow bookkeeping itself allocates nothing on the hot path.
- Synchronous leases wrapped in `using` and asynchronous leases wrapped in `await using`.
- Per-borrow acquire timeouts via `AcquireLeased(TimeSpan)`.
- A 1,000-iteration hot-path loop that borrows and returns without per-borrow allocation.
- Idempotent return: a second `Dispose()` is a no-op, and reading `.Value` after disposal throws `ObjectDisposedException`.
- The class-based twin `HayatePoolScope<T>` (`AcquireScoped` / `AcquireScopeAsync`) for when the borrow must be stored in a field, passed to another method, or held across an `await` as a reference.

## When to use this pattern

Use the lease when you borrow and return an object inside a single method frame on a hot path and want to avoid any per-borrow heap allocation. Reach for `HayatePoolScope<T>` instead when the borrowed object must escape the frame (field, method argument, cross-`await` reference).

## How to run

```bash
dotnet run --project samples/HayateOP.Samples.Lease
```

> Requires .NET 10. The app prints a step-by-step walkthrough and exits 0 on success.

## Walkthrough

| Step | Concept | Key API |
|------|---------|---------|
| 1 | Synchronous lease via `using` | `pool.AcquireLeased()` |
| 2 | Lease with an explicit per-borrow timeout | `pool.AcquireLeased(TimeSpan)` |
| 3 | Asynchronous lease via `await using` | `await pool.AcquireLeaseAsync(CancellationToken)` |
| 4 | Hot-path loop with no per-borrow allocation | `using var l = pool.AcquireLeased()` |
| 5 | Idempotent return + `Value` guard | `lease.Dispose()`; `lease.Value` |
| 6 | Class-based scope (reference-friendly) | `pool.AcquireScoped()` / `AcquireScopeAsync` |

## Key APIs used

| API | Purpose |
|-----|---------|
| `HayatePoolBuilder<T>.WithMinSize` / `WithMaxSize` | Warm floor and hard ceiling of idle objects. |
| `HayatePoolBuilder<T>.WithEnableSharding` | Spread objects across shards to reduce lock contention. |
| `HayatePoolBuilder<T>.WithRejectPolicy` / `WithAcquireTimeout` | `BlockTimeout` waits, then throws on timeout. |
| `HayatePoolLease<T>.Value` | The pooled object; throws `ObjectDisposedException` after return. |
| `HayatePoolScope<T>` | Reference-type lease for cross-frame / field lifetime. |

## Notes & gotchas

- A top-level `Program` lives in the global namespace, so `using DotNetCore.HayateOP;` (pool types) and `using DotNetCore.HayateOP.Samples.Lease;` (`Worker`) are both required.
- The lease is a `struct`: returning it twice is safe (idempotent), but it cannot be stored in a field or passed by reference — use `HayatePoolScope<T>` for that.
