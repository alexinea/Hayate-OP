# Hayate Object Pool

A high-performance, feature-rich object pool implementation for .NET, designed to optimize resource management and reduce garbage collection overhead.

## Features

- **High Performance Core**: Optimized for both single-threaded and multi-threaded scenarios.
- **Sharding Support**: Reduces lock contention through sharded architecture.
- **Auto-Scaling**: Dynamically adjusts pool size based on usage patterns (aggressive scale-up / conservative scale-down steps).
- **Object Validation**: Validates objects on borrow, return, and while idle.
- **Eviction Policy**: Automatically removes expired or idle objects.
- **Generation Optimization**: Reduces validation frequency for long-lived objects.
- **Leak Detection**: Tracks and reports potential object leaks; stack-trace capture is opt-in (`Off` / `Sampled` / `EveryAcquire`) since 2.1.
- **Metrics & Monitoring**: Comprehensive statistics and snapshot capabilities.
- **Multiple Reject Policies**: `Abort`, `Block`, `BlockTimeout`, and `CreateNew`.
- **Dependency Injection Integration**: Seamless integration with `Microsoft.Extensions.DependencyInjection`.
- **ASP.NET Core Endpoints**: Built-in management endpoints for monitoring and configuration.
- **Health Checks**: ASP.NET Core health check integration to monitor pool status in production.
- **Diagnostics**: `System.Diagnostics` bridge for advanced performance monitoring and telemetry.
- **Configuration Binding**: Load pool settings from `appsettings.json` (global + per-pool).
- **Cross-Version Compatibility**: Supports .NET Framework 4.8 (core, ObjectPoolCompat and DependencyInjection packages) and .NET 6/7/8/9/10 (all packages).

Release notes live in [`CHANGELOG.md`](CHANGELOG.md); breaking changes and behavioural notes are
documented in [`docs/BREAKING-CHANGES.md`](docs/BREAKING-CHANGES.md).

## Installation

### NuGet Packages

| Package                                                | Description                                                                                          |
| :----------------------------------------------------- | :--------------------------------------------------------------------------------------------------- |
| `DotNetCore.HayateOP`                                | Core library with basic object pool functionality.                                                   |
| `DotNetCore.HayateOP.Extensions.DependencyInjection` | Seamless integration with Microsoft.Extensions.DependencyInjection.                                  |
| `DotNetCore.HayateOP.Extensions.Configuration`       | Integration with Microsoft.Extensions.Configuration for loading pool settings from appsettings.json. |
| `DotNetCore.HayateOP.Extensions.HealthCheck`         | ASP.NET Core Health Checks integration to monitor pool status in production.                         |
| `DotNetCore.HayateOP.Extensions.Endpoints`           | Built-in management endpoints for monitoring and configuration.                                      |
| `DotNetCore.HayateOP.Extensions.Diagnostics`         | Integration with System.Diagnostics for advanced performance monitoring and telemetry.               |

#### Supported Target Frameworks

| Package | Target Frameworks |
| :--- | :--- |
| `DotNetCore.HayateOP` | .NET Standard 2.0; .NET Framework 4.8; .NET 6/7/8/9/10 |
| `DotNetCore.HayateOP.Extensions.ObjectPoolCompat` | .NET Framework 4.8; .NET 6/7/8/9/10 |
| `DotNetCore.HayateOP.Extensions.DependencyInjection` | .NET Framework 4.8; .NET 6/7/8/9/10 |
| All other `DotNetCore.HayateOP.Extensions.*` packages | .NET 6/7/8/9/10 |

> **.NET Framework 4.8 support (added in 2.4)** targets `net48` for classic ASP.NET 4.x / WPF / WinForms
> hosts that consume the core pool or the `ObjectPool<T>` / DI bridges. Prefer `PackageReference` and add a
> `bindingRedirect` when the host already loads `Microsoft.Extensions.*` (6.x/8.x/10.x) assemblies.
> **.NET Standard 2.0 support (added in 2.5)** ships for the **core package only** (`DotNetCore.HayateOP`),
> targeting Unity / Xamarin / older .NET hosts that cannot consume `net6.0`+ or `net48` assets. On
> `netstandard2.0` / `net48`, allocation tracking reports `0` (no `GC.GetAllocatedBytesForCurrentThread`)
> and `Math.Clamp` falls back to an equivalent `Max/Min` composition.
> The ASP.NET Core–based packages (`Endpoints`, `HealthCheck`) and the `OpenTelemetry` / `Configuration` /
> `Diagnostics` extensions do not ship `net48` or `netstandard2.0` assets.

### .NET CLI

```bash
# Core package
dotnet add package DotNetCore.HayateOP

# Extensions
dotnet add package DotNetCore.HayateOP.Extensions.DependencyInjection
dotnet add package DotNetCore.HayateOP.Extensions.Configuration
dotnet add package DotNetCore.HayateOP.Extensions.HealthCheck
dotnet add package DotNetCore.HayateOP.Extensions.Endpoints
dotnet add package DotNetCore.HayateOP.Extensions.Diagnostics
```

## Quick Start

### Basic Usage

```csharp
using DotNetCore.HayateOP;

// Create a simple pooled object
public class MyPooledObject : IHayateResettable
{
    public int Data { get; set; }

    public void Reset()
    {
        Data = 0;
    }
}

// Create and configure the pool
var pool = new HayatePoolBuilder<MyPooledObject>()
    .WithMinSize(10)
    .WithMaxSize(100)
    .Build();

// Acquire an object from the pool
var obj = pool.Acquire();
obj.Data = 42;

// Use the object...

// Return the object to the pool (ALWAYS release in a finally block)
pool.Release(obj);

// Dispose the pool when done
pool.Dispose();
```

### One-call pools

`HayatePool.Simple` builds a pool from nothing more than a size and a factory — no options object, no
policy class. Objects keep every documented default, and the size you pass is the ceiling, not something
to fill eagerly: nothing is created until the first borrow.

```csharp
using DotNetCore.HayateOP;

using var pool = HayatePool.Simple<MyPooledObject>(32, () => new MyPooledObject());

// Optional third argument: a callback run on every borrow (e.g. reopening a connection).
using var connections = HayatePool.Simple<MyConnection>(8, () => new MyConnection(cs), c => c.Open());
```

When you need anything tuned — a minimum idle count, an interval, a single feature switch — reach for
`HayatePoolBuilder<T>` instead; nothing set by the shortcut is hidden afterwards.

### Fluent Builder & Auto-Scaling

Every option exposed by `HayatePoolOptions` has a matching `With*` method on `HayatePoolBuilder<T>`.
Auto-scaling uses **independent** scale-up and scale-down steps, so you can be aggressive when growing
and conservative when shrinking:

```csharp
using DotNetCore.HayateOP;

var pool = new HayatePoolBuilder<MyPooledObject>()
    .WithMinSize(10)
    .WithMaxSize(100)
    .WithEnableSharding(true)
    .WithShardCount(4)
    .WithEnableAutoScaling(true)
    .WithScalingInterval(5000)          // decision cadence (ms)
    .WithScaleUpThreshold(0.8)         // grow when usage >= 80%
    .WithScaleDownThreshold(0.2)       // shrink when usage < 20%
    .WithScaleUpStep(10)               // objects added per scale-up
    .WithScaleDownStep(5)              // objects removed per scale-down
    .WithScaleUpCooldownSeconds(3)
    .WithScaleDownCooldownSeconds(15)
    .WithRejectPolicy(HayatePoolRejectPolicy.BlockTimeout)
    .WithAcquireTimeout(TimeSpan.FromSeconds(5))
    .Build();
```

> **Tip:** When auto-scaling is disabled, the pool never grows on its own (scaling callbacks
> and timeout-triggered scale-up are gated by `EnableAutoScaling`); your explicit `MaxPoolSize`
> is kept as the hard capacity cap. (Breaking change in 2.1: previously `MaxPoolSize` was
> forced to `MinPoolSize`, which silently collapsed capacity to 0 when `MinPoolSize = 0`.)
> When sharding is disabled, `ShardCount` is forced to `1`.

### Custom Object Policy

Implement `IHayateObjectPolicy<T>` to control object creation, validation, and lifecycle callbacks.
Note that **`OnRelease` returns a `bool`**: returning `false` tells the pool the object is bad and must
be destroyed instead of returned to the pool.

```csharp
using DotNetCore.HayateOP;
using DotNetCore.HayateOP.Policies;

public class CustomPooledObjectPolicy : IHayateObjectPolicy<MyCustomObject>
{
    public MyCustomObject Create()
    {
        return new MyCustomObject
        {
            CreatedAt = DateTime.UtcNow,
            InstanceId = Guid.NewGuid()
        };
    }

    public bool Validate(MyCustomObject obj)
    {
        return obj != null && !obj.IsDisposed;
    }

    public void OnAcquire(MyCustomObject obj)
    {
        obj.LastAcquiredAt = DateTime.UtcNow;
        obj.AcquireCount++;
    }

    public void OnPassivate(MyCustomObject obj)
    {
        obj.Reset();
    }

    public bool OnRelease(MyCustomObject obj)
    {
        // Return false to reject the object (it will be destroyed).
        // Return true to return it to the pool.
        return !obj.IsDisposed;
    }

    public void OnDestroy(MyCustomObject obj)
    {
        obj.Dispose();
    }
}

// Use the custom policy
var pool = new HayatePoolBuilder<MyCustomObject>()
    .WithPolicy(new CustomPooledObjectPolicy())
    .WithMinSize(10)
    .WithMaxSize(100)
    .Build();
```

### Async Usage

```csharp
using DotNetCore.HayateOP;

var obj = await pool.AcquireAsync();
try
{
    // use the object
}
finally
{
    pool.Release(obj);
}
```

Since 2.4, `AcquireAsync` also accepts a timeout and a `CancellationToken` (mirroring the
synchronous `Acquire(TimeSpan)` semantics — timeout throws `TimeoutException`, external
cancellation propagates `TaskCanceledException`):

```csharp
// wait at most 5 seconds, but abort early if the token fires
var obj = await pool.AcquireAsync(TimeSpan.FromSeconds(5), cancellationToken);
```

### Scoped borrows

`AcquireScoped()` returns a lease that hands its object back when it is disposed, so a `using` block
replaces the manual `try`/`finally` and cannot forget the return — including when the body throws:

```csharp
using DotNetCore.HayateOP;

using (var lease = pool.AcquireScoped())
{
    lease.Value.Data = 42;
}   // returned here, even if the body threw

// Asynchronous equivalent
using (var lease = await pool.AcquireScopeAsync(cancellationToken))
{
    lease.Value.Data = 42;
}
```

The return happens exactly once: disposing a lease twice is a no-op, never a second return. One lease is
allocated per borrow — the price of never dropping a borrowed object on an exception path — so this is a
convenience for ordinary code rather than something to add to the hottest loop.

### With Dependency Injection

```csharp
using DotNetCore.HayateOP;

var builder = WebApplication.CreateBuilder(args);

// Register Hayate Object Pool services
builder.Services.AddHayatePoolSupport()
       .RegisterHayatePool<MyPooledObject>(options =>
{
    options.MinPoolSize = 10;
    options.MaxPoolSize = 100;
    options.EnableValidation = true;
    options.EnableAutoScaling = true;
});

var app = builder.Build();

// Use the pool in your services
app.MapGet("/", (IHayateObjectPool<MyPooledObject> pool) =>
{
    var obj = pool.Acquire();
    try
    {
        // Use the object
        return Results.Ok("Object acquired successfully");
    }
    finally
    {
        pool.Release(obj);
    }
});

app.Run();
```

### Configuration from `appsettings.json`

Bind global defaults and per-pool overrides directly from configuration. Global settings live under
`HayatePool:Global`; per-pool overrides live under `HayatePool:Pools:{TypeName}`.

```json
{
  "HayatePool": {
    "Global": {
      "MinPoolSize": 5,
      "MaxPoolSize": 50,
      "EnableMetrics": true,
      "ScalingIntervalMs": 5000,
      "ScaleUpThreshold": 0.8,
      "ScaleDownThreshold": 0.2
    },
    "Pools": {
      "MyPooledObject": {
        "MinPoolSize": 10,
        "MaxPoolSize": 100,
        "ShardCount": 4,
        "ScaleUpStep": 10,
        "ScaleDownStep": 5
      }
    }
  }
}
```

```csharp
using DotNetCore.HayateOP;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHayatePoolSupport()
    .RegisterGlobalConfig(builder.Configuration)        // binds HayatePool:Global
    .RegisterHayatePool<MyPooledObject>(builder.Configuration); // binds HayatePool:Pools:MyPooledObject

var app = builder.Build();
app.Run();
```

### With Management Endpoints

```csharp
using DotNetCore.HayateOP;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHayatePoolSupport()
                 .RegisterHayatePool<MyPooledObject>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Map Hayate Object Pool management endpoints
app.MapHayatePoolEndpoints();

app.Run();
```

**Available Endpoints**:

* `GET /hayateop` **- Pool overview**
* `GET /hayateop/{poolName}` **- Pool details**
* `POST /hayateop/{poolName}` **- Update pool configuration**
* `GET /hayateop/{poolName}/stats` **- Pool statistics**
* `POST /hayateop/{poolName}/clear` **- Clear all objects from pool**

> **Known limitation (tracked as T05):** the per-pool endpoints currently resolve `{poolName}` via
> reflection (`Type.GetType`), which requires an assembly-qualified type name and is fragile. For
> reliable, programmatic per-pool access, resolve `IHayateObjectPool<T>` through DI instead:
> `app.MapGet("/pools/{name}", (IHayateObjectPool<MyPooledObject> pool) => pool.GetStats());`

### Health Checks

```csharp
using DotNetCore.HayateOP;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHayatePoolSupport()
    .RegisterHayatePool<MyPooledObject>()
    .RegisterHealthChecks<MyPooledObject>();   // adds HayateOpHealthCheck<MyPooledObject>

var app = builder.Build();

// Standard ASP.NET Core health endpoint backed by the pool health check.
// Reports "Degraded" when the pool has no available slots.
app.MapHealthChecks("/health");

app.Run();
```

### Diagnostics

```csharp
using DotNetCore.HayateOP;
using DotNetCore.HayateOP.Metrics;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHayatePoolSupport()
    .RegisterHayatePool<MyPooledObject>();

// System.Diagnostics bridge for pool metrics (implements IHayateMetrics).
// AddHayatePoolSupport registers the no-op EmptyHayateMetrics via TryAdd,
// so adding HayateDiagnostics here replaces it as the active IHayateMetrics.
builder.Services.AddSingleton<IHayateMetrics, HayateDiagnostics>();

var app = builder.Build();
app.Run();
```

> The bridge is `HayateDiagnostics` (implements `IHayateMetrics`), which writes events to the
> `HayateOP` `DiagnosticListener` when `HayatePoolOptions.EnableMetrics` is on. Subscribe via
> `DiagnosticListener.AllListeners` to capture `HayateOP.Object.Acquire`, `HayateOP.Object.OnRelease`,
> `HayateOP.Object.Miss`, and `HayateOp.Pool.Scaled` events. Alternatively, supply it to a
> single pool via the builder's `WithMetrics(...)`.

### Configuration Options

These are the **default** values of `HayatePoolOptions`. All of them are also configurable through the
fluent `HayatePoolBuilder<T>` API.

```csharp
var options = new HayatePoolOptions
{
    // Pool size
    MinPoolSize = 5,
    MaxPoolSize = 50,

    // Timeouts & reject policy
    DefaultAcquireTimeout = TimeSpan.FromSeconds(5),
    RejectPolicy = HayatePoolRejectPolicy.BlockTimeout,
    CreationRetryCount = 3,
    CreationRetryDelay = TimeSpan.FromMilliseconds(100),

    // Sharding
    EnableSharding = true,
    ShardCount = 4,

    // Auto-scaling
    EnableAutoScaling = true,
    ScalingIntervalMs = 5000,
    ScaleUpThreshold = 0.8,
    ScaleDownThreshold = 0.2,
    ScaleUpCooldownSeconds = 3,
    ScaleDownCooldownSeconds = 15,
    ScaleUpStep = 5,
    ScaleDownStep = 5,

    // Validation
    EnableValidation = true,
    ValidateOnBorrow = false,
    ValidateOnReturn = false,
    ValidateWhileIdle = false,
    ValidateIntervalMs = 30000,

    // Eviction
    EnableEviction = true,
    MaxLifeTime = TimeSpan.FromMinutes(10),
    MaxIdleTime = TimeSpan.FromMinutes(5),
    SoftMinEvictableIdleTime = TimeSpan.FromMinutes(2),
    EvictionIntervalMs = 30000,
    NumTestsPerEvictionRun = 10,

    // Generation optimization
    EnableGenerationOptimization = true,
    GenerationThresholdMs = 30000,
    OldGenerationValidationInterval = 3,

    // Leak detection
    EnableLeakDetection = true,
    LeakDetectionThreshold = TimeSpan.FromMinutes(30),
    LeakTraceCaptureMode = HayateLeakTraceCaptureMode.Off, // Off (default) | Sampled | EveryAcquire
    LeakTraceSampleRate = 1024, // 1/N sampling, Sampled mode only

    // Metrics
    EnableMetrics = false
};
```

### Hot-path cost of each feature switch

Every optional feature above buys something and costs something. Measured on .NET 10.0,
single-threaded `Acquire+Release` round trip, 250 pre-warmed objects, one run
(2026-09-10):

| Configuration | Mean | Allocated |
| :--- | ---: | ---: |
| `Microsoft.Extensions.ObjectPool` (reference) | 26.950 ns | 0 B |
| HayateOP lean fast path (`WithLean()`) | 27.063 ns | 0 B |
| HayateOP general engine, every feature off | 286.773 ns | 384 B |
| HayateOP general engine, sharding only | 285.857 ns | 384 B |
| HayateOP general engine, every feature on | 414.987 ns | 0 B |

The short version of how to trim:

| Switch | Where the cost lands |
| :--- | :--- |
| `EnableLean` | Removes the wrapper allocation, the shard lock, the return-side registry probe and every diagnostic write — about 10.6x against the same feature set |
| `EnableSharding` | One extra idle-list probe per shard on a miss; free in single-threaded steady state |
| `EnableGenerationOptimization` | One extra `Stopwatch.GetTimestamp()` per borrow — the only switch that adds a QPC read to the borrow path |
| `ValidateOnBorrow` / `ValidateOnReturn` | One `IHayateObjectPolicy.Validate` call per borrow / return; the policy's own work (often a health probe) dominates |
| `LeakTraceCaptureMode` | The real cost of the leak-detection surface: `EveryAcquire` captures a call stack on every borrow; `Off` (the default) costs two branches |
| `WarnAtRatio` / `CriticalAtRatio` | The most expensive optional switch: when non-zero it walks every shard, taking one lock per shard, on **every** borrow and return |
| `EnableMetrics` | A min/max CAS loop, a metrics callback and a debug log per operation — and it is also what keeps `TotalCreated` / `TotalReleased` / `TotalMissed` alive |
| `EnableAllocationTracking` | Two `GC.GetAllocatedBytesForCurrentThread()` calls per operation; unavailable on `net48` / `netstandard2.0` |
| `EnableEviction`, `EnableAutoScaling`, `ValidateWhileIdle` | Background only: they drive the shared timer and never touch the borrow or return path. A pool with all of them off creates no timer at all |

The full per-switch reference — including which switches are pure background work, which
diagnostic members are paid for only when called, and a trim-by-scenario table — is in
[`docs/hot-path-costs.md`](docs/hot-path-costs.md).

### Breaking changes

HayateOP follows Semantic Versioning. Breaking changes so far:

| Version | Change |
| :--- | :--- |
| 2.5 | `HayateObject<T>.AcquireTrace` (string) replaced by `HayateLeaseContext`; new lifecycle fields (`CreatedAtTick`, `LeaseCount`, `OwnerPoolName`) |
| 2.4 | Idle pools actually scale down (the contradicting internal gate was removed) |
| 2.2 | `Build()` fails fast when custom metrics are registered while metrics are disabled |
| 2.1 | Leak stack-trace capture decoupled from leak detection and off by default |

Full migration guidance — plus the behavioural notes worth reading before adopting (cold boot, `CreateNew` laziness, ~100 ms blocking wake-up granularity) — lives in [`docs/BREAKING-CHANGES.md`](docs/BREAKING-CHANGES.md). A per-version summary of every change is in [`CHANGELOG.md`](CHANGELOG.md).

## Monitoring and Diagnostics

### Get current statistics

```csharp
var stats = pool.GetStats();

Console.WriteLine($"Pooled objects: {stats.PooledCount}");
Console.WriteLine($"Total created: {stats.TotalCreated}");
Console.WriteLine($"Total acquired: {stats.TotalAcquired}");
Console.WriteLine($"Total released: {stats.TotalReleased}");
Console.WriteLine($"Total missed: {stats.TotalMissed}");
Console.WriteLine($"Leak detected: {stats.LeakDetectedCount}");
```

### Take a snapshot for diagnostics

```csharp
var snapshot = pool.TakeSnapshot();

Console.WriteLine($"Snapshot taken at: {snapshot.Timestamp}");
Console.WriteLine($"Borrowed count: {snapshot.BorrowedCount}");
Console.WriteLine($"Leak count: {snapshot.LeakCount}");

foreach (var leakTrace in snapshot.LeakTraces)
{
    Console.WriteLine($"Leak trace: {leakTrace}");
}
```

### Reload configuration at runtime

```csharp
pool.ReloadConfig(config =>
{
    config.MaxPoolSize = 200;
    config.EnableAutoScaling = true;
});
```

### Clear all objects

```csharp
pool.Clear();
```

### Releasing objects and disposing the pool

Disposing the pool releases everything the pool is holding — a host does **not** have to
dispose each pooled object itself. This matches the contract of
`Microsoft.Extensions.ObjectPool.DisposableObjectPool<T>`.

```csharp
using var pool = new HayatePoolBuilder<MyResource>()
    .WithMaxSize(32)
    .Build();

var resource = pool.Acquire();
try { /* use resource */ }
finally { pool.Release(resource); }   // always return what you borrow
// leaving the using block disposes the pool and releases everything still in it
```

| Call | Effect |
| :--- | :--- |
| `Clear()` | Disposes the objects the pool holds right now, leaving the pool usable |
| `Dispose()` | `Clear()` plus releasing the background timer and the return-signal gate |

Three points worth knowing: checked-out objects are left alone and remain the caller's
responsibility; a user `Dispose` that throws is caught and logged so the drain continues;
and a pool that has been disposed is not guarded against further use, so treat it as
unusable. [`docs/disposal.md`](docs/disposal.md) covers the ownership model, the exact
behaviour of the drain on both engines, the side-by-side comparison with
`DisposableObjectPool<T>`, and the two differences from it.

### Disposing with the process (optional)

A pool owned by the process itself never gets its `Dispose` call, because nothing user-facing
runs at that point. `WithAutoDisposeWithSystem()` closes that gap: the pool subscribes to
process exit — and to a terminal Ctrl+C, since either can end a process — and releases its
objects on the way out.

```csharp
var pool = new HayatePoolBuilder<MyResource>()
    .WithAutoDisposeWithSystem()
    .Build();   // released at process exit; still fine to dispose it yourself
```

Nothing on the borrow or return path takes part in it, and the subscription is removed on
disposal, so a pool you dispose yourself never stays reachable from the process-wide hook.
Hosted applications normally have a shutdown step of their own — prefer disposing there, or
point the pool at that signal instead:

```csharp
var pool = new HayatePoolBuilder<MyResource>()
    .WithShutdownHook(new MyHostLifetimeHook(lifetime))   // any IHayateShutdownHook
    .WithAutoDisposeWithSystem()
    .Build();
```

### Pool-level circuit breaker (optional)

Pools whose objects front a network dependency (database, message broker, remote service)
can take themselves out of service when that dependency keeps failing, instead of handing
out objects that are likely to be broken and letting every caller discover the outage on
its own. The feature is opt-in (`EnableCircuitBreaker = false` by default), so a pool that
does not ask for it behaves exactly as before, and the lean profile always forces it off.

```csharp
var pool = new HayatePoolBuilder<MyDbConnection>()
    .WithCircuitBreaker(failureThreshold: 3,
                        resetTimeout: TimeSpan.FromSeconds(30),
                        probeInterval: TimeSpan.FromSeconds(5),
                        probe: () => database.Ping())
    .WithOnUnavailable(args => logger.LogError("pool down: {Reason}", args.Reason))
    .WithOnAvailable(_ => logger.LogInformation("pool recovered"))
    .Build();
```

| Member | Role |
| :--- | :--- |
| `SetUnavailable(reason)` | The application reports a dependency failure; the `FailureThreshold`-th consecutive report opens the breaker |
| `SetAvailable()` | Reports the dependency is healthy again: closes the breaker and clears a pending failure streak — the manual recovery path when no probe is configured |
| `CheckAvailable()` | Cheap availability probe: a single volatile read, `false` while the breaker is open |
| `OnAvailable` / `OnUnavailable` | Fire exactly once per availability transition, never on a state that does not change |

While the breaker is open every `Acquire` / `AcquireAsync` throws
`HayatePoolUnavailableException` (derived from `InvalidOperationException`, so existing
catch blocks keep working) **before** doing any pool work — no shard scan, no wait, no
create. Recovery is automatic when a `probe` is configured: the first probe runs one
`ResetTimeout` after the trip, then one per `ProbeInterval`, and a throwing probe counts
as a failed probe. Without a probe no background work is created at all and the pool
stays unavailable until the application calls `SetAvailable()`. The probe timer exists
only while the pool is unavailable — an available pool holds no extra handle and pays a
single predictable branch per borrow.

### OpenTelemetry metrics (optional package)

`DotNetCore.HayateOP.Extensions.OpenTelemetry` bridges `IHayateMetrics` onto
`System.Diagnostics.Metrics` (the native data source of the OpenTelemetry .NET SDK).
The package itself has **zero external NuGet dependencies** — `Meter` is built into
.NET 6+; you bring your own OpenTelemetry SDK/exporter (OTLP, Prometheus, …) on the
consumer side.

Published instruments (meter `DotNetCore.HayateOP`):

| Instrument | Type | Tags |
| :--- | :--- | :--- |
| `HayatePoolAcquire` | Counter | `pool.name` |
| `HayatePoolRelease` | Counter | `pool.name`, `valid` |
| `HayatePoolMiss` | Counter | `pool.name` |
| `HayatePoolScaled` | Counter | `pool.name`, `action` |
| `HayatePoolWaitTime` | Histogram (ms) | `pool.name` |
| `HayatePoolSize` | ObservableGauge | `pool.name` |
| `HayatePoolAvailable` | ObservableGauge | `pool.name` |

```csharp
builder.Services.AddHayatePoolSupport()
    .RegisterHayatePool<MyPooledObject>(builder.Configuration);

// Publish HayateOP metrics to System.Diagnostics.Metrics
builder.Services.AddHayateOpenTelemetryMetrics();

// Consume them with the OpenTelemetry SDK (or observe via `dotnet-counters`)
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter("DotNetCore.HayateOP"));
```

## Performance

BenchmarkDotNet compares HayateOP against `Microsoft.Extensions.ObjectPool.DefaultObjectPool<T>`
(baseline) and plain `new` (no-pool lower bound) across the `Acquire`, `Release` and `AcquireAsync`
paths, with `[MemoryDiagnoser]` / `[ThreadingDiagnoser]` and P50/P90/P95/P99 latency columns.

```bash
dotnet run -c Release --project tests/HayateOP.Benchmarks
```

Archived runs and the machine-readable baseline used by the CI performance-regression gate live in
[`docs/benchmarks/`](docs/benchmarks); the full methodology, the percentile columns and the
run-to-run variance are recorded in
[`docs/benchmarks/2026-09-10-o1-lean.md`](docs/benchmarks/2026-09-10-o1-lean.md). Single-threaded
`Acquire+Release` round-trip, every row below measured in one run on .NET 10.0 (2026-09-10):

| Configuration | Mean | Allocated |
| :--- | ---: | ---: |
| `Microsoft.Extensions.ObjectPool` (reference) | 27.0 ns | 0 B |
| **HayateOP lean fast path** (`WithLean()`) | **27.1 ns** | **0 B** |
| HayateOP general engine, every feature off | 286.8 ns | 384 B |
| HayateOP general engine, 4 shards | 285.9 ns | 384 B |
| HayateOP general engine, all features on | 415.0 ns | 0 B |

The lean fast path (`HayatePoolOptions.EnableLean`) stores the pooled value in a bounded array and
moves it through `Interlocked` compare-exchange, so an `Acquire+Release` round-trip allocates nothing
at all and costs what MEOP's minimal `Get()/Return()` costs — measured at parity in the same run,
while running roughly ten times faster than the general-purpose engine with every optional feature
disabled. It is the right choice for high-frequency pooling of small, stateless objects; the
general-purpose engine remains the choice whenever validation, eviction, leak detection, metrics,
auto-scaling or sharding are required, and its per-operation cost stays above MEOP's because it
tracks per-object state and a shard index that MEOP does not.

## Known limitations

Behavioural notes to be aware of before adopting HayateOP — cold boot on an empty pool, the lazy
`CreateNew` policy, ~100 ms blocking wake-up granularity, and `ApplyFeatureSwitches` capacity
semantics — are collected in
[`docs/BREAKING-CHANGES.md`](docs/BREAKING-CHANGES.md#behavioural-notes-non-breaking).

## Samples

Runnable examples live under [`samples/`](samples):

* **`samples/HayateOP.Samples`** — a console tour covering the fluent builder, custom policy
  (`bool OnRelease`), synchronous / timeout / async acquire, statistics & snapshots, runtime config
  reload, clearing, Dependency Injection, configuration binding, and diagnostics registration.
* **`samples/HayateOP.Samples.Endpoints`** — an ASP.NET Core app wiring DI, configuration binding,
  health checks, diagnostics, the management endpoints, and a DI-resolved pool endpoint
  (`/test-hayateop`).

## Contributing

Contributions are welcome.

Please feel free to submit issues, feature requests, or pull requests.

### Development Requirements

- .NET 10 SDK (for development)
- .NET 6 SDK (for compatibility testing)
- Visual Studio 2026, VS Code, or Rider

### Building the Project

```bash
# Restore dependencies
dotnet restore

# Build the solution
dotnet build

# Run tests
dotnet test
```

## License

This project is licensed under the MIT License - see the [LICENSE] file for details.
