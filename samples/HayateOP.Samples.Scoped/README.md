# HayateOP.Samples.Scoped

> Class-based scoped borrows (`HayatePoolScope<T>`) for leases whose lifetime must cross method frames, live in a field, or be passed by reference.

## What this sample demonstrates

- `HayatePoolScope<T>` from `AcquireScoped` / `AcquireScopeAsync` — the reference-type twin of the value-type `HayatePoolLease<T>`.
- A basic in-frame `using` borrow.
- Handing the scope to another method (only possible because it is a reference type, not a stack-only `struct`).
- Storing the scope in a field and using it while alive (then disposed).
- An async scope spanning an `await`, disposed with `await using`.
- The contrast with `HayatePoolLease<T>`: a `struct` cannot be stored in a field or passed across frames; a `class` can.

## When to use this pattern

Prefer `HayatePoolScope<T>` whenever the borrowed object must escape the borrowing frame — a field on a longer-lived object, an argument to another method, or a value carried across an `await`. Otherwise prefer the allocation-free `HayatePoolLease<T>`.

## How to run

```bash
dotnet run --project samples/HayateOP.Samples.Scoped
```

> Requires .NET 10. The app prints a step-by-step walkthrough and exits 0 on success.

## Walkthrough

| Step | Concept | Key API |
|------|---------|---------|
| 1 | Scoped borrow (`using`) | `pool.AcquireScoped()` |
| 2 | Scope handed to another method | `ProcessAcrossFrames(scope)` |
| 3 | Scope held in a field | `HayatePoolScope<Resource> field = scope;` |
| 4 | Async scope (`await using`) | `await pool.AcquireScopeAsync(CancellationToken.None)` |

## Key APIs used

| API | Purpose |
|-----|---------|
| `HayatePoolScope<T>.Value` | The pooled object; throws `ObjectDisposedException` after disposal. |
| `AcquireScoped` / `AcquireScopeAsync` | Synchronous / asynchronous class-based lease. |
| `HayatePoolLease<T>` | The value-type alternative (stack-only, not cross-frame). |

## Notes & gotchas

- The scope is a heap object, so it allocates once per borrow — that is the trade for reference-type flexibility.
- Touching `.Value` after the scope is disposed throws `ObjectDisposedException`, the same safety net as the value-type lease.
