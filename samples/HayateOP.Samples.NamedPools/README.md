# HayateOP.Samples.NamedPools

> Multiple named pools of the same type via DI — register and resolve independent pools by name, alongside a default unnamed pool.

## What this sample demonstrates

- `AddHayatePoolSupport()` to enable HayateOP's DI integration.
- `AddNamedPool<T>(name, configure)` to register several independent pools of the same `T`, each with its own options (e.g. different `MaxPoolSize`).
- `RegisterHayatePool<T>(configure)` to register an unnamed default pool.
- `IHayateNamedPoolAccessor.GetPool<T>(name)` / `GetPoolNames<T>()` to resolve a named pool and enumerate registered names.
- The unnamed pool resolving through the normal `IHayateObjectPool<T>` service.

## When to use this pattern

Use when a single process needs several differently-tuned pools of the same object type — for example a `primary` and a `replica` DB connection pool, or per-purpose pools — all managed by the DI container and retrieved by name.

## How to run

```bash
dotnet run --project samples/HayateOP.Samples.NamedPools
```

> Requires .NET 10. The app prints a step-by-step walkthrough and exits 0 on success.

## Walkthrough

| Step | Concept | Key API |
|------|---------|---------|
| 1 | Named pools coexist per name | `accessor.GetPool<DbConnection>("primary")` / `GetPoolNames<DbConnection>()` |
| 2 | Borrow from a named pool | `primary.Acquire()` / `primary.Release(conn)` |
| 3 | Unnamed pool as `IHayateObjectPool<T>` | `sp.GetRequiredService<IHayateObjectPool<DbConnection>>()` |

## Key APIs used

| API | Purpose |
|-----|---------|
| `AddHayatePoolSupport` | Register HayateOP core DI services. |
| `AddNamedPool<T>(name, configure)` | Register a named, independently-configured pool. |
| `RegisterHayatePool<T>(configure)` | Register the default (unnamed) pool. |
| `IHayateNamedPoolAccessor.GetPool<T>` / `GetPoolNames<T>` | Resolve by name / list names. |

## Notes & gotchas

- The named-pool APIs live in the `HayateOP.Extensions.DependencyInjection` package, so the project references that package (and `Microsoft.Extensions.DependencyInjection`) in addition to the core `HayateOP`.
- A named pool and the unnamed pool of the same `T` are separate instances — configuring one does not affect the other.
