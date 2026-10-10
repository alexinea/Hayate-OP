# HayateOP.Samples

> The introductory tour — exercises the current public API surface of HayateOP end to end: build, borrow, inspect, snapshot, reconfigure, clear, and wire it up through DI and `appsettings.json`.

## What this sample demonstrates

- Building a fully-configured pool with the fluent `HayatePoolBuilder<T>` (sizing, sharding, auto-scaling, validation, eviction, leak detection, reject policy, and a custom `IHayateObjectPolicy<T>`).
- Synchronous and asynchronous borrow/use/`Release` (always in `try/finally`).
- A per-borrow acquire timeout that overrides the pool default.
- Runtime statistics (`GetStats`) and a diagnostic `TakeSnapshot` (borrowed objects + leak traces).
- `ReloadConfig` to resize at runtime and `Clear` to drop every pooled object.
- DI integration: `AddHayatePoolSupport()` + `RegisterHayatePool<T>(options)` resolving `IHayateObjectPool<T>`.
- Configuration binding from `appsettings.json` (`HayatePool:Global` and `HayatePool:Pools:{TypeName}`) via `RegisterGlobalConfig` + `RegisterHayatePool<T>(configuration)`, with `HayateDiagnostics` registered as `IHayateMetrics`.

## When to use this pattern

Start here to learn the vocabulary of the whole library before diving into the scenario-specific samples. It doubles as a copy-paste skeleton for a real `Program.cs`.

## How to run

```bash
dotnet run --project samples/HayateOP.Samples
```

> Requires .NET 10. The app prints a step-by-step walkthrough and exits 0 on success.

## Walkthrough

| Step | Concept | Key API |
|------|---------|---------|
| 1 | Fluent build with every option | `new HayatePoolBuilder<MyPooledObject>().With*...` |
| 2 | Acquire / use / Release (sync) | `pool.Acquire()` / `pool.Release(obj)` |
| 3 | Acquire with an explicit timeout | `pool.Acquire(TimeSpan)` |
| 4 | Acquire asynchronously | `await pool.AcquireAsync()` |
| 5 | Statistics | `pool.GetStats()` |
| 6 | Diagnostic snapshot | `pool.TakeSnapshot()` |
| 7 | Reload config at runtime | `pool.ReloadConfig(o => ...)` |
| 8 | Clear the pool | `pool.Clear()` |
| 9 | Dependency Injection | `AddHayatePoolSupport` / `RegisterHayatePool<T>` |
| 10 | Config binding (`appsettings.json`) | `RegisterGlobalConfig` / `RegisterHayatePool<T>(configuration)` |

## Key APIs used

| API | Purpose |
|-----|---------|
| `HayatePoolBuilder<T>` fluent `With*` set | Configure every `HayatePoolOptions` field. |
| `IHayateObjectPolicy<T>` | Custom create/validate/release/destroy hooks (`CustomPolicy`). |
| `IHayateResettable` / `IDisposable` | Pooled object contracts (`MyPooledObject`). |
| `AddHayatePoolSupport` / `RegisterHayatePool<T>` | DI registration. |
| `RegisterGlobalConfig` / `RegisterHayatePool<T>(IConfiguration)` | Bind options from `appsettings.json`. |

## Notes & gotchas

- A top-level `Program` lives in the global namespace, so `using DotNetCore.HayateOP;`, `using DotNetCore.HayateOP.Samples;` (helpers), and the Metrics/Policy `using`s are all required.
- `AddHayatePoolSupport` registers a default `EmptyHayateMetrics` via `TryAddSingleton`; registering `HayateDiagnostics` *after* it replaces the effective `IHayateMetrics`. The old `RegisterDiagnostics<T>` helper has been removed.
- The sample disposes the first pool with `(pool as IDisposable)?.Dispose()` to stop background timers (scaling/eviction/validation/leak detection) before showing the DI sections.
