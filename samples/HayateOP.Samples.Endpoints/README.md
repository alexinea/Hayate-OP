# HayateOP.Samples.Endpoints

> Exposing a HayateOP pool over ASP.NET Core — DI registration, built-in management/health endpoints, and a sample endpoint that borrows, uses, and returns under timeout.

## What this sample demonstrates

- ASP.NET Core registration of HayateOP: `AddHayatePoolSupport()` → `RegisterGlobalConfig(configuration)` → `RegisterHayatePool<MyBizObj>(configuration)` → `RegisterHealthChecks<MyBizObj>()`, plus `HayateDiagnostics` as `IHayateMetrics`.
- Built-in management endpoints via `app.MapHayatePoolEndpoints()`:
  - `GET  /hayateop` — overview
  - `GET  /hayateop/{type}` — per-pool detail
  - `GET  /hayateop/{type}/stats` — per-pool stats
  - `POST /hayateop/{type}` — update config
  - `POST /hayateop/{type}/clear` — clear pool
- A standard health endpoint `app.MapHealthChecks("/health")` backed by `HayateOP`'s health check.
- A sample `GET /test-hayateop` handler that injects `IHayateObjectPool<MyBizObj>`, borrows with `pool.Acquire(TimeSpan.FromSeconds(5))` inside `try/finally`, returns `pool.GetStats()` / `pool.GetOptions()`, and maps `TimeoutException` → 503 and other exceptions → 500.

## When to use this pattern

Use when you host a service in ASP.NET Core and want pooled resources available to request handlers, observable through HTTP management/health endpoints, and tunable at runtime.

## How to run

```bash
dotnet run --project samples/HayateOP.Samples.Endpoints
```

> Requires .NET 10. The app hosts an HTTP server; browse `/test-hayateop`, `/health`, or `/hayateop` (Swagger/OpenAPI is enabled in development). Stop it with Ctrl+C.

## Walkthrough

| Step | Concept | Key API |
|------|---------|---------|
| registration | Wire HayateOP into the DI container | `AddHayatePoolSupport` / `RegisterHayatePool<MyBizObj>` / `RegisterHealthChecks` |
| `MapHayatePoolEndpoints` | Built-in management + stats endpoints | `app.MapHayatePoolEndpoints()` |
| `/health` | Health check | `app.MapHealthChecks("/health")` |
| `/test-hayateop` | Borrow → use → return under timeout | `IHayateObjectPool<MyBizObj>.Acquire(TimeSpan)` in `try/finally` |

## Key APIs used

| API | Purpose |
|-----|---------|
| `AddHayatePoolSupport` / `RegisterHayatePool<T>` / `RegisterGlobalConfig` | DI + config binding. |
| `RegisterHealthChecks<T>` | ASP.NET health-check integration. |
| `MapHayatePoolEndpoints` | Ready-made management/diagnostics HTTP surface. |
| `IHayateObjectPool<T>.Acquire(TimeSpan)` | Per-call timeout override. |

## Notes & gotchas

- The per-pool endpoints resolve `{type}` via reflection (`Type.GetType`) and have a known limitation; for reliable, programmatic per-pool access prefer the DI-resolved `IHayateObjectPool<T>` shown in `/test-hayateop`.
- Always `Release` in a `finally` (or use a lease/scope), and translate `TimeoutException` into a 503 so callers get clear backpressure signaling instead of a generic 500.
