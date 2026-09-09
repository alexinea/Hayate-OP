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
- **Cross-Version Compatibility**: Supports .NET 6, .NET 8 and .NET 10.

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

### Leak trace capture (breaking change in 2.1)

Since 2.1, stack-trace capture is **decoupled** from leak detection and **off by
default** (`LeakTraceCaptureMode.Off`). In 2.0 and earlier, every `Acquire` captured
a full `Environment.StackTrace` (tens of microseconds and 10–40 KB per borrow when
the call chain is deep). Leak detection itself — threshold detection and `LeakCount`
reporting — is unaffected and still costs nothing on the hot path; `LeakTraces`
entries become the placeholder text `"No stack trace available"` unless capture is
enabled.

| Mode | Behavior |
| :--- | :--- |
| `Off` (default) | Never capture; hot path pays zero forensics overhead |
| `Sampled` | Capture 1-in-N borrows (`LeakTraceSampleRate`, first borrow always captured) |
| `EveryAcquire` | Capture on every borrow (2.0 behavior; opt-in for diagnostics) |

```csharp
// Sampling mode: capture 1-in-256 borrows
var pool = new HayatePoolBuilder<MyPooledObject>()
    .WithLeakTraceCapture(HayateLeakTraceCaptureMode.Sampled, 256)
    .Build();
```

### Builder fails fast on orphaned custom metrics (breaking change in 2.2)

Since 2.2, `Build()` throws `InvalidOperationException` when a custom `IHayateMetrics`
is registered via `WithMetrics(...)` while metrics collection is disabled
(`EnableMetrics = false`). In 2.1 and earlier the custom instance was **silently
replaced** with `EmptyHayateMetrics`, so a forgotten `WithEnableMetrics(true)` went
undetected. Either enable metrics or remove the `WithMetrics` registration:

```csharp
// Throws: custom metrics registered but never activated
var pool = new HayatePoolBuilder<MyPooledObject>()
    .WithMetrics(myMetrics)
    .Build(); // InvalidOperationException since 2.2

// Correct: activate the custom metrics
var pool = new HayatePoolBuilder<MyPooledObject>()
    .WithEnableMetrics(true)
    .WithMetrics(myMetrics)
    .Build();
```

> The DI / Configuration integration is unaffected: pools built through
> `AddHayatePool` / `RegisterHayatePool` attach the DI-registered `IHayateMetrics`
> only when metrics are enabled, exactly as in 2.1.

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

Benchmark suite (BenchmarkDotNet, short-iteration job: 3 warmup + 10 iterations, Server GC)
comparing HayateOP against `Microsoft.Extensions.ObjectPool.DefaultObjectPool<T>` (baseline).
Run it with `dotnet run -c Release --project tests/HayateOP.Benchmarks`; archived reports live
in [`docs/benchmarks/`](docs/benchmarks).

Latest run (2026-09-09, .NET 10.0, Release):

| Suite | MEOOP (baseline) | HayateOP minimal | HayateOP 4-shard | HayateOP full-feature |
| :--- | ---: | ---: | ---: | ---: |
| Acquire+Release (single-thread) | 22.7 ns | 188.8 ns (8.3×) | 212.6 ns (9.4×) | 304.7 ns (13.5×)¹ |
| Release-only (with refill acquire) | 22.8 ns | 186.4 ns (8.2×) | — | — |
| Mixed 50/50 acquire/release | 42.3 ns | 226.4 ns (10.0×) | 200.4 ns (8.9×) | — |
| 100-thread concurrent borrow/return | 20.0 µs | ~1033 µs² | ~1292 µs² | — |

> ¹ Fixed in 2.3: full-feature mode (validation + metrics + leak detection + eviction enabled)
> dropped from ~37.5 µs / ~28.7 KB per acquire to ~305 ns / ~0.4 KB after the PR-C/PR-D eviction &
> timestamp rework (was dominated by per-iteration full free-list `ToArray()` allocation).
>
> ² The concurrent 100-thread suite is **high-variance** on this dev box (a hybrid-core laptop under a
> debugger); per-run means fluctuate by an order of magnitude. Treat it as a sanity/regression smoke
> test, not a cross-run numeric comparison.

HayateOP's single acquire/release round-trip is currently slower than MEOOP's minimal `Get()/Return()`
(HayateOP tracks per-object state, shard index, and supports validation/metrics hooks that MEOOP does not).
In exchange it provides sharding, eviction, leak detection, metrics, and management endpoints.

## Known Limitations

Behaviors to be aware of before adopting HayateOP (several are queued for follow-up releases):

* **Empty pool cold-boot (changed in 2.1).** If the pool is completely empty (e.g. `MinPoolSize = 0`
  and not pre-warmed), `Acquire` under `Block` / `BlockTimeout` — and `AcquireAsync` — now
  synchronously creates the first object on demand (cold-boot, atomically de-duplicated) instead of
  waiting for a return that may never come. Previously the call waited the full acquire timeout and
  then threw `TimeoutException` (or hung forever on the async path).
* **`CreateNew` reject policy creates lazily, not eagerly.** Under `RejectPolicy.CreateNew` the pool
  first waits up to the acquire timeout for a return, and only then synchronously creates a new
  tracked object. A cold first call on an empty pool therefore incurs up to a full-timeout delay
  before the new object is handed out.
* **Blocking wake-up granularity is ~100 ms.** Blocking waits (`Block` / `BlockTimeout` /
  `CreateNew`) re-check in fixed slices of 100 ms, so the observed tail latency of a
  return-to-acquire handoff is quantized to that slice size.
* **`MinPoolSize = 0` cold pools bootstrap on first acquire (changed in 2.1).** No background
  component pre-creates objects to reach `MinPoolSize`; the pool starts empty, and the first
  acquire on a fully empty pool creates its object on demand (see the cold-boot note above).
  Background scaling never grows toward `MinPoolSize = 0`.
* **`ApplyFeatureSwitches` capacity semantics (changed in 2.1).** With `EnableAutoScaling = false`,
  your explicit `MaxPoolSize` is kept as the hard capacity cap (previously it was forced to
  `MinPoolSize`, which silently collapsed capacity to 0 when `MinPoolSize = 0`). Only a
  `MaxPoolSize < MinPoolSize` misconfiguration is lifted to `MaxPoolSize = MinPoolSize`.

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

### Committers

Automated and release commits on this repository are attributed to the project
commit account so the history stays consistent across agents/tools:

- **Name:** `alexinea-intelligence`
- **GitHub:** <https://github.com/alexinea-intelligence>
- **Email:** `imliuyi@live.com` (temporary — will be replaced)

When committing in this repo, mirror that identity so authorship is uniform:

```bash
git config user.name "alexinea-intelligence"
git config user.email "imliuyi@live.com"
```

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
