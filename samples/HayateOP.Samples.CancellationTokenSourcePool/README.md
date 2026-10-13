# HayateOP.Samples.CancellationTokenSourcePool

> A reusable `CancellationTokenSource` whose disposal returns it to the pool — and the reason a used source cannot simply be handed out again.

## What this sample demonstrates

- **Rent / return.** `Rent()` hands out a source that returns itself when disposed, so a per-operation cancellation scope is `using var cts = pool.Rent();` and nothing else.
- **Which mode the runtime is in.** `CancellationTokenSourcePool.IsReuseSupported` is `true` on .NET 6.0 and later, where `CancellationTokenSource.TryReset()` exists, and `false` on .NET Framework 4.8, where it does not. The pool never claims reuse it cannot deliver.
- **A canceled source is dropped, not reused.** No reset can make a canceled source usable again, so it is destroyed on return rather than parked — the next rental is a different source.
- **Callbacks do not survive the return.** A callback the previous renter registered on the token must not fire when the next renter cancels; the reset is what removes it.
- **`RentLinked(CancellationToken)`.** The rental follows an upstream token, and the link is registered on the pooled source rather than created outside the pool. The link belongs to the rental: disposal drops it (waiting out an upstream callback that is still running) before the source can be handed to anyone else.
- **`Clear()`.** Drops the sources the pool holds; a parked source has already been reset, so there is nothing left running to release.

## When to use this pattern

Reach for the pool wherever you would otherwise write `new CancellationTokenSource(TimeSpan.FromSeconds(5))` on a request path: the source is cheap to reuse and expensive to allocate per call, and `RentLinked` covers the "cancel when the caller gives up" case without leaving the pool. Keep the rental scoped to one operation — the pool is what enforces that a source is only ever held by one renter, and a source that has been canceled can never be returned to service.

## How to run

```bash
dotnet run --project samples/HayateOP.Samples.CancellationTokenSourcePool
```

> Requires .NET 10. The app prints a step-by-step walkthrough and exits 0 on success.

## Walkthrough

| Step | Concept | Key API |
|------|---------|---------|
| 0 | Which mode the runtime is in | `CancellationTokenSourcePool.IsReuseSupported` |
| 1 | Rent / return, and the reset that makes it safe | `Rent()` / `Dispose()` |
| 2 | A canceled source is destroyed, never parked | `Cancel()` then `Dispose()` |
| 3 | The previous renter's callbacks do not survive | `CancellationToken.Register` |
| 4 | A rental that follows an upstream token | `RentLinked(CancellationToken)` |
| 5 | Dropping the pooled sources | `Clear()` |

## Key APIs used

| API | Purpose |
|-----|---------|
| `CancellationTokenSourcePool.Rent` | Rent a source that returns to the pool when disposed. |
| `CancellationTokenSourcePool.RentLinked` | Rent a source that is canceled when an upstream token is canceled. |
| `CancellationTokenSourcePool.IsReuseSupported` | Whether this runtime can reset a returned source and hand it out again. |
| `CancellationTokenSourcePool.Instance` | A shared pool for callers that do not want to manage a lifetime. |
| `PooledCancellationTokenSource` | The pooled source; `Dispose` routes exactly one return per rental. |
| `IHayateObjectPool<T>.GetStats` | Observe the idle set (`PooledCount`). |

## Notes & gotchas

- **The pool is only as good as the runtime's reset.** On .NET Framework 4.8 there is no `TryReset()`, so a returned source cannot be cleaned — its stale timer and callbacks would fire during the next rental. The pool destroys such a source instead of parking it, which means `PooledCount` stays `0` there and every rental allocates. That is deliberate, and `IsReuseSupported` is how you tell.
- **The reset is not a general-purpose `Reset()`.** `TryReset()` refuses a source whose cancellation has already been requested or whose timer has already fired; that refusal is exactly why a canceled source is dropped rather than parked.
- **`RentLinked` links the pooled source, it does not create one.** `CancellationTokenSource.CreateLinkedTokenSource` would produce a source outside the pool, which then could not be returned. A rental whose upstream token is already canceled comes back canceled — and is therefore destroyed on return.
- **One renter at a time.** The pool hands a source to a single renter and only resets it after that rental has ended; using a source concurrently with its own reset is exactly the case `TryReset` documents as unsupported.
- **`Clear()` drops, it does not force-dispose.** A parked source has already been reset, so it holds no timer and no callbacks; the dropped object is collected like any other garbage. A rental still out when the pool is cleared is destroyed when it comes back, because the pool no longer tracks it.
