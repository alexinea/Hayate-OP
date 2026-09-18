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
- **Abandoned Recovery**: Optional reclamation of borrowed objects never returned past `RemoveAbandonedTimeout` (CHOPIN `AbandonedConfig` semantics; destructive, off by default).
- **Metrics & Monitoring**: Comprehensive statistics and snapshot capabilities.
- **Multiple Reject Policies**: `Abort`, `Block`, `BlockTimeout`, and `CreateNew`.
- **Dependency Injection Integration**: Seamless integration with `Microsoft.Extensions.DependencyInjection`.
- **ASP.NET Core Endpoints**: Built-in management endpoints for monitoring and configuration.
- **Health Checks**: ASP.NET Core health check integration to monitor pool status in production.
- **Diagnostics**: `System.Diagnostics` bridge for advanced performance monitoring and telemetry.
- **Configuration Binding**: Load pool settings from `appsettings.json` (global + per-pool).
- **Cross-Version Compatibility**: Supports .NET Framework 4.8 (core, ObjectPoolCompat, DependencyInjection and Specialized packages) and .NET 6/7/8/9/10 (all packages).

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
| `DotNetCore.HayateOP.Extensions.Specialized`         | Ready-to-use specialized pools for `MemoryStream` and `StringBuilder`, with `using`-based returns.    |

#### Supported Target Frameworks

| Package | Target Frameworks |
| :--- | :--- |
| `DotNetCore.HayateOP` | .NET Standard 2.0; .NET Framework 4.8; .NET 6/7/8/9/10 |
| `DotNetCore.HayateOP.Extensions.ObjectPoolCompat` | .NET Framework 4.8; .NET 6/7/8/9/10 |
| `DotNetCore.HayateOP.Extensions.DependencyInjection` | .NET Framework 4.8; .NET 6/7/8/9/10 |
| `DotNetCore.HayateOP.Extensions.Specialized` | .NET Framework 4.8; .NET 6/7/8/9/10 |
| All other `DotNetCore.HayateOP.Extensions.*` packages | .NET 6/7/8/9/10 |

> **.NET Framework 4.8 support (added in 2.4)** targets `net48` for classic ASP.NET 4.x / WPF / WinForms
> hosts that consume the core pool or the `ObjectPool<T>` / DI bridges. Prefer `PackageReference` and add a
> `bindingRedirect` when the host already loads `Microsoft.Extensions.*` (6.x/8.x/10.x) assemblies.
> The Specialized package's net48 asset additionally depends on `System.Memory` (4.5.5), which backs the
> `Span<char>` surface of `HayateValueStringBuilder`; it is that package's only net48-only NuGet
> dependency and the core library stays free of it (added in the 2.8 window).
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
dotnet add package DotNetCore.HayateOP.Extensions.Specialized
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

### Keyed pools

When objects are not interchangeable — a connection per tenant, a client per endpoint — give every key
its own pool with `ParameterizedHayatePool<TKey, TValue>`:

```csharp
using DotNetCore.HayateOP;

var pools = new ParameterizedHayatePool<string, Connection>(
    connectionString => new Connection(connectionString),
    maxSizePerKey: 8);

var connection = pools.GetObject("tenant-a");
try { connection.Query("..."); }
finally { pools.ReturnObject("tenant-a", connection); }
```

Each key gets an ordinary pool of its own, so objects are only ever reused by the key that created them,
and capacity, eviction, validation and the circuit breaker are all per key: one tenant exhausting its
eight connections does not slow down the next. Sub-pools are created on first use and disposed with the
keyed pool; `KeysInPoolCount` tells you how many exist, and `TryRemove(key)` retires one — worth knowing
if the key space is open-ended, because keys otherwise accumulate.

`getObject` needs the key to return an object, because a keyed pool cannot tell which sub-pool owns one:
the same object may exist under two keys, and searching every sub-pool would make returning cost more
than borrowing. `GetPool(key)` hands back the sub-pool itself when you want its statistics, a snapshot or
its own eviction, and every sub-pool is registered in the pool registry under `Name[key]`, so management
endpoints and metrics can address a single key.

### Unbounded pool (second pool model)

`HayateUnboundedPool<T>` is the ArrayPool-style counterpart to the bounded engine: borrowing never
blocks, never waits and never rejects — when the idle queue is empty the next borrow creates a new
object, whatever the momentary demand. The lease is **ownership**: returning is optional, and a
borrowed object that is never returned is simply collected by the garbage collector, so a forgotten
return cannot leak or block anyone. The only knob is `maxIdle` — how many objects the pool retains;
a return that arrives while that many are parked destroys the object instead, so memory does not
grow with peak demand:

```csharp
using DotNetCore.HayateOP;

var pool = new HayateUnboundedPool<MessageBuffer>(maxIdle: 64);

// Serves any burst: reuse when idle objects exist, creation when they do not.
var buffer = pool.Acquire();
try { /* use the buffer */ }
finally { pool.Release(buffer); }   // optional — not returning is fine too
```

It is a full `IHayateObjectPool<T>` (scoped borrows, stats, snapshots and the DI surface work
unchanged), implements the same reset/validate-on-return hooks as the bounded engine, and reports
`TotalDestroyed` for the returns that `MaxIdle` rejected. Choose it for transient buffers and
short-lived large objects under bursty demand; choose the bounded engine when capacity must be
gated against an external resource. `HayateUnboundedPool<T>` has no `MaxSize`, no timeouts, no
rejection policies and no background features — there is nothing to wait for and nothing to scale.

### Preparation / reconnect strategy

`IHayatePreparationStrategy<T>` (N3) closes the connection-pool gap: a strategy can check whether a
borrowed object is actually usable and repair it asynchronously before the caller sees it, which is
the classic reconnect flow for SMTP, database or broker connections. Install it with
`WithPreparation` — the returned `HayatePreparationPool<T>` wraps any `IHayateObjectPool<T>`
(bounded engine, lean, keyed sub-pool, or the unbounded pool):

```csharp
using DotNetCore.HayateOP;

var pool = new HayatePoolBuilder<SmtpConnection>()
    .WithMaxPoolSize(10)
    .Build()
    .WithPreparation(
        new SmtpReconnectStrategy(),      // IHayatePreparationStrategy<SmtpConnection>
        onDiscard: conn => conn.Dispose());

var conn = await pool.AcquireAsync();    // ready or repaired, or the reconnect error throws
```

Every borrow runs `IsReadyAsync`; a not-ready object goes through `PrepareAsync` (the reconnect).
A failed prepare hands the object to `onDiscard` and retries with another borrow (budget
`maxPrepareAttempts`, default 3), then throws `HayatePoolPreparationException` with the last
failure attached — a caller never receives a silently broken object. The synchronous `Acquire`
runs the identical chain by blocking; the six policy hooks are unchanged.

### Specialized pools

The optional [`DotNetCore.HayateOP.Extensions.Specialized`](https://www.nuget.org/packages/DotNetCore.HayateOP.Extensions.Specialized)
package ships the ready-to-use `MemoryStream` / `StringBuilder` pools that generic engines leave to you
(aligned with P89OP's `CodeProject.ObjectPool.Specialized`):

```csharp
using DotNetCore.HayateOP.Specialized;

// A MemoryStream whose Dispose returns it to the pool — `using` is the whole borrow/return pair.
using var stream = MemoryStreamPool.Instance.GetObject();
stream.Write(payload, 0, payload.Length);   // empty and back in the pool after the block

// The same for string building; GetObject(string) starts from an initial value.
using var sb = StringBuilderPool.Instance.GetObject("order: ");
sb.StringBuilder.Append(42);
var text = sb.ToString();
```

Streams are created with 4KB and builders with 4096 characters; a returned object that grew past the
pool's maximum capacity (512KB by default, tunable through `MaximumMemoryStreamCapacity` /
`MaximumStringBuilderCapacity`) is destroyed on return instead of being parked, and returned objects are
reset (empty stream, cleared builder) before the next borrow. Each pool is an ordinary
`IHayateObjectPool<T>` underneath, so `AcquireAsync`, `GetStats` and friends work as usual, and each owns
its own lifetime — construct it, or use the `Instance` singleton and never dispose it.

**Declared-capacity borrows.** `GetObject(minCapacity)` lets the borrower state how much the object must
hold up front, so the pool reserves that capacity once instead of growing through the ladder on every
use, and mixed-size workloads stop churning through grow-past-the-maximum-and-destroy: sizes above the
minimum are served by lazily created internal capacity tiers (an exponential ladder anchored at the
minimum), a tier parks its builders/streams even above the pool's global maximum — the buffer exists by
declaration — and only growth beyond the declared envelope destroys an object:

```csharp
using var sb = StringBuilderPool.Instance.GetObject(200_000);   // capacity ≥ 200K chars, no grow ladder
```

**Convert and return.** `ToStringReturn()` / `ToByteArrayReturn()` produce the result and end the borrow
in one exactly-once call — the return runs even when the conversion throws, so nothing leaks:

```csharp
var sb = StringBuilderPool.Instance.GetObject();
sb.StringBuilder.Append("order: ").Append(42);
var text = sb.ToStringReturn();   // builder back in the pool here
byte[] bytes = stream.ToByteArrayReturn();
```

**Formatting helpers.** `StringBuilderPool.Format` / `Concat` / `Join` are generic, `params`-free
alternatives to `string.Format` / `string.Concat` / `string.Join` (ZString-style, up to eight
arguments): no `object[]`, no boxing of the arguments, one borrow/return cycle, current-culture
semantics matching `string.Format`.

**Zero-allocation value builder.** For the transient build-a-line-and-return-it flow,
`HayateValueStringBuilder` skips `StringBuilder` entirely: a `ref struct` whose buffer is rented from
`ArrayPool<char>`, with `ToString()` as the build's only allocation (the buffer goes back to the pool
in the same call — dispose-by-ToString). It has no pool around it, no statistics and no capacity
ceiling; long-lived builders that need observability or a capacity guard stay on
`StringBuilderPool`:

```csharp
using var sb = new HayateValueStringBuilder();   // ref struct — no heap, no async, no lambdas
sb.Append("order: ");
sb.Append(id);
var text = sb.ToString();                        // the one allocation; buffer returned here
```

`SharedStringBuilder` rounds the package out for the opposite use case: one `StringBuilder` shared by
everyone behind a lock, where the holder's `Dispose` clears it and releases the lock — zero per-use
allocations, at the price of serialising all users, so hold it briefly:

```csharp
var message = SharedStringBuilder.Build(b => b.Append("hello, ").Append(name));
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

    // Abandoned recovery (K2; destructive opt-in, both toggles off by default)
    RemoveAbandonedOnBorrow = false,        // reclaim abandoned objects on the borrow path
    RemoveAbandonedOnMaintenance = false,   // reclaim abandoned objects on the background maintenance pass
    RemoveAbandonedTimeout = TimeSpan.FromSeconds(300), // abandoned judgment timeout (aligned with CHOPIN)
    LogAbandoned = false,                   // log a warning (with the lease trace) when reclaiming
    RemoveAbandonedIntervalMs = 30000,      // maintenance-pass cadence

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
| `RemoveAbandonedOnBorrow` | One bounded oldest-borrow scan per borrow when enabled; off by default → constant branch, zero cost |
| `RemoveAbandonedOnMaintenance` | Background only: drives the shared timer, never touches the borrow or return path |
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

### Inspect per-object metadata

A snapshot carries one row per live object, idle and borrowed alike. Each row is a point-in-time
copy for diagnostics — reading it does not borrow anything, and the rows carry no consistency
guarantee between themselves.

```csharp
var snapshot = pool.TakeSnapshot();

foreach (var detail in snapshot.ObjectDetails)
{
    // How many times this object has been handed out, and which thread took it last (0 = never borrowed).
    Console.WriteLine($"leases={detail.LeaseCount} lastThread={detail.LastGetThreadId}");

    // When the object was created. The field is raw ticks; wrap it to get an instant.
    var created = new DateTimeOffset(detail.CreatedAtTick, TimeSpan.Zero);
    Console.WriteLine($"created={created:O} borrowed={detail.IsBorrowed}");

    // The most recent lease duration, and the shard that owns the object.
    Console.WriteLine($"lastLeaseMs={detail.LeaseTimeMs} shard={detail.ShardIndex}");
}
```

`LastGetThreadId` is an id, not a handle: it tells you which thread borrowed the object last,
which is what you want when checking whether a pool is handing objects across threads (and so
whether state left on an object is visible elsewhere), or whether one thread is monopolising a
shard. The runtime recycles ids after a thread dies, so treat one as stable only while its
thread is alive. Objects served by the lean fast path carry no metadata at all — that path
stores the pooled value directly instead of wrapping it.

### Abandoned object recovery

Leak detection is forensics: it **counts** borrowed objects that stay out past
`LeakDetectionThreshold`, it never touches them. **Abandoned recovery** (CHOPIN's
`AbandonedConfig`) goes one step further — it **destroys** borrowed objects that are not returned
within `RemoveAbandonedTimeout` (default 300 s), reclaiming the capacity they silently hold. An
object is "abandoned" when a lease was borrowed and never released — typically a caller that lost
track of it.

Reclamation is **destructive and off by default**, because the pool cannot tell an abandoned
object from a legitimately long-lived lease: reclaiming destroys the value while a caller may
still hold the reference. Opt in explicitly when your leases are short and bounded:

```csharp
var pool = new HayatePoolBuilder<MyResource>()
    .WithRemoveAbandonedOnBorrow()            // reclaim on the borrow path
    .WithRemoveAbandonedOnMaintenance()       // and/or on the background maintenance pass
    .WithRemoveAbandonedTimeout(TimeSpan.FromMinutes(2))
    .WithLogAbandoned()                       // warn with the captured lease trace on reclaim
    .Build();
```

Semantics and costs:

- `RemoveAbandonedOnBorrow` runs a bounded, oldest-borrow-first scan on every borrow (the FIFO
  borrowed list guarantees the scan stops at the first lease not yet past the timeout, so the
  bounded budget is exact). `RemoveAbandonedOnMaintenance` drives the shared background timer at
  `RemoveAbandonedIntervalMs` (default 30 s) and reclaims everything past the timeout.
- A reclaimed object is destroyed (policy `OnDestroy` + `Dispose`), removed from the registry, and
  counted in `HayatePoolStats.AbandonedRemovedCount` — it is **not** also reported as a leak, so
  recovery and the forensics counters never double-flag the same object.
- Releasing an already-reclaimed object is treated as a foreign return (the pool's existing
  "object does not belong to this pool" path): it is disposed again and logged, and the pool keeps
  working. Reclamation is a documented opt-in contract, not a safety net.
- Both toggles are forced off in lean mode (no wrapper registry) and default to `false`, so the
  default configuration stays forensics-only — exactly the M4 behavior.

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
(baseline), plain `new` (no-pool lower bound), marklauter `MSL.Pool`, RRode `TinyPools`, Hertzole
`PowerPools` and labijie `Chopin.Pooling` across the `Acquire`, `Release` and `AcquireAsync` paths, with
`[MemoryDiagnoser]` / `[ThreadingDiagnoser]` and P50/P90/P95/P99 latency columns. `MSL.Pool` exposes an
async-only lease API and `TinyPools`, `PowerPools` and `Chopin.Pooling` synchronous-only ones, so the
lines cover complementary halves of the matrix: `MSL.Pool` appears in the `Release`, `AcquireAsync` and
concurrent suites, `TinyPools`, `PowerPools` and `Chopin.Pooling` in the `Acquire+Release`, `Release`
and concurrent suites, and none of them appears in the other's.

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

**ArrayPool direct-storage backend** (O-D): `WithArrayPoolStorage()` keeps every lean property but
rents the retention array from `ArrayPool<T>.Shared` and grows it on demand (×2) up to `MaxPoolSize`,
instead of allocating a fixed `MaxPoolSize`-sized array for the pool's whole lifetime — a large pool
holds only the storage its demand actually reached, and the array returns to the shared pool on
`Dispose`. Borrow/return cost is unchanged (measured at parity with fixed-buffer lean: 27.8 ns / 0 B
for `Acquire+Release`, same as MEOP). net6.0+ only; older targets ignore the flag and keep the fixed
buffer.

### String building (specialized package)

A dedicated comparison column (`SpecializedStringComparisonBenchmarks` in the benchmarks project,
net10, one build of `"order: 42 units"` per operation) puts the specialized string surfaces
head-to-head with `string.Format` and Cysharp.ZString:

| Approach | Mean | Allocated |
| :--- | ---: | ---: |
| **HayateValueStringBuilder** (Z1 value builder) | **36.6 ns** | **56 B** |
| Cysharp.ZString value builder | 36.1 ns | 56 B |
| ZString.Format | 73.4 ns | 56 B |
| `string.Format` (BCL baseline) | 89.2 ns | 80 B |
| StringBuilder direct (fresh per call) | 45.3 ns | 160 B |
| SharedStringBuilder (lock-guarded) | 51.8 ns | 120 B |
| StringBuilderPool (borrow + ToStringReturn) | 341.6 ns | 440 B |
| StringBuilderPool.Format (generic helper) | 383.6 ns | 440 B |

Readings: the Z1 value builder is at parity with ZString — its 56 B is the final string alone, the
build itself allocates nothing. The pooled rows carry the general engine's fixed 384 B borrow-cycle
bookkeeping (see the core table above) on top of the final string, so `StringBuilderPool` trades
allocation for observability and the capacity guard; where that floor matters and the flow is
transient, prefer `HayateValueStringBuilder`. The declared-capacity tier rows in
`SpecializedStringBuilderTierBenchmarks` quantify the tiering benefit separately: a 300K-character
build that the default borrow cannot retain costs 1,229,416 B per operation; the declared borrow
brings it to 614,864 B (0.500x) and 1,077 µs down to 413 µs.

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
