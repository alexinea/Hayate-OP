# HayateOP.Samples.Preparation

> Connection readiness on every borrow via `IHayatePreparationStrategy<T>` — broken objects are repaired before delivery, never handed to a caller.

## What this sample demonstrates

- `HayatePreparationPool<T>` obtained from `inner.WithPreparation(strategy, onDiscard, maxPrepareAttempts)`.
- A readiness/reconnect strategy implementing `IHayatePreparationStrategy<T>` (`IsReadyAsync` + `PrepareAsync`).
- The healthy path: a ready object is delivered exactly as taken (one async ready-check round-trip).
- Repair-on-next-borrow: a dropped connection is **not** repaired on return; the *next* borrow runs `IsReadyAsync` (false), then `PrepareAsync` (reconnect), so the caller always receives a usable object.
- `onDiscard` disposes a connection whose prepare chain failed, and `maxPrepareAttempts` bounds how many objects one borrow may run through before giving up.

## When to use this pattern

Use preparation when pooled objects can become stale while idle (DB connections, gRPC channels, authenticated sessions) and you want every delivered object to be guaranteed-ready, without paying for validation on healthy returns.

## How to run

```bash
dotnet run --project samples/HayateOP.Samples.Preparation
```

> Requires .NET 10. The app prints a step-by-step walkthrough and exits 0 on success.

## Walkthrough

| Step | Concept | Key API |
|------|---------|---------|
| 1 | Healthy connection delivered as-is | `await pool.AcquireAsync()` |
| 2 | Dropped connection repaired before next delivery | `connection.Drop()` then `await pool.AcquireAsync()` |

## Key APIs used

| API | Purpose |
|-----|---------|
| `HayatePoolBuilder<T>.WithPreparation` | Wrap a pool with a preparation strategy → `HayatePreparationPool<T>`. |
| `IHayatePreparationStrategy<T>.IsReadyAsync` | Cheap readiness probe run on every borrow. |
| `IHayatePreparationStrategy<T>.PrepareAsync` | Reconnect/repair when `IsReadyAsync` returns false. |
| `onDiscard` / `maxPrepareAttempts` | Dispose failed objects; cap retry attempts per borrow. |

## Notes & gotchas

- Validation is deliberately turned **off** on the inner pool (`WithEnableValidation(false)`) because the preparation strategy already performs the readiness check — enabling both would overlap.
- The repair happens on the *next* borrow, not on return, so latency is paid by the caller that triggers it.
