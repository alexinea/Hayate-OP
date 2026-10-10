# HayateOP.Samples.ObjectPoolCompat

> Drop-in compatibility with `Microsoft.Extensions.ObjectPool` (MEOP) — switch the pool backend with (almost) a one-line change, tune via `HayateCompatOptions`, integrate through DI, and adapt a native HayateOP pool the other way.

## What this sample demonstrates

- Reusing the built-in MEOP `StringBuilderPooledObjectPolicy` (and any `IPooledObjectPolicy<T>`) unchanged — the compat pool maps its `Create`/`Return` hooks onto HayateOP's `Create`/`OnRelease` via `HayateCompatPooledObjectPolicy<T>`.
- The MEOP baseline: `new DefaultObjectPoolProvider().Create(policy)` (native MEOP behavior).
- The one-line switch: `new DefaultObjectPoolProvider()` → `new HayateObjectPoolCompatProvider()` — the `Get`/`Return` caller code is identical, but the backend becomes HayateOP (sharding, statistics, observability, etc. ship with the package).
- Tuning via `HayateCompatOptions`: `MinSize` warm-up (eliminates the cold-start `AcquireTimeout` wait), a shorter `AcquireTimeout`, and a `PoolNamePrefix` for observability.
- DI integration: register `ObjectPoolProvider` as a singleton (swapping only the provider instance) and inject `ObjectPool<StringBuilder>` — consumers depend only on MEOP's `ObjectPool<T>`.
- The reverse adapter `HayateObjectPoolAdapter<T>`: wrap a native HayateOP pool as MEOP's `ObjectPool<T>` for MEOP-style consumers, keeping HayateOP's full feature set.

## When to use this pattern

Use when you already have code on `Microsoft.Extensions.ObjectPool` and want HayateOP's richer behavior (sharding, auto-scaling, leak detection, metrics) without rewriting every borrow site, or when you must hand a HayateOP pool to a library that only accepts `ObjectPool<T>`.

## How to run

```bash
dotnet run --project samples/HayateOP.Samples.ObjectPoolCompat
```

> Requires .NET 10. The app prints a step-by-step walkthrough and exits 0 on success.

## Walkthrough

| Step | Concept | Key API |
|------|---------|---------|
| 0 | MEOP-style policy reuse | `StringBuilderPooledObjectPolicy` / `IPooledObjectPolicy<T>` |
| 1 | MEOP baseline | `new DefaultObjectPoolProvider().Create(policy)` |
| 2 | One-line switch to HayateOP | `new HayateObjectPoolCompatProvider().Create(policy)` |
| 3 | Tuning via `HayateCompatOptions` | `MinSize` / `AcquireTimeout` / `PoolNamePrefix` |
| 4 | DI integration | register `ObjectPoolProvider` singleton |
| 5 | Reverse adapter | `new HayateObjectPoolAdapter<StringBuilder>(nativePool)` |

## Key APIs used

| API | Purpose |
|-----|---------|
| `HayateObjectPoolCompatProvider` | MEOP-compatible pool provider backed by HayateOP. |
| `HayateCompatOptions` | Warm-up, timeout, and naming for the compat pool. |
| `HayateCompatPooledObjectPolicy<T>` | Maps `IPooledObjectPolicy<T>` to HayateOP semantics. |
| `HayateObjectPoolAdapter<T>` | Wrap a native HayateOP pool as `ObjectPool<T>`. |
| `ObjectPool<T>.Get` / `Return` | The unchanged MEOP caller surface. |

## Notes & gotchas

- HayateOP's `CreateNew` semantics are *create-after-timeout* (unlike MEOP, which creates immediately on an empty pool). On a cold pool the first `Get` can therefore wait up to `AcquireTimeout` (default 1s). Use the `MinSize` warm-up or a shorter `AcquireTimeout` to eliminate that wait — this is the single behavioral difference to design around when switching from MEOP.
