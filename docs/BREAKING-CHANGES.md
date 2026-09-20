# Breaking changes and behavioural notes

Migration guidance for every breaking change, newest first, plus the behavioural notes that
frequently surprise adopters. For a per-version summary of all changes see
[`../CHANGELOG.md`](../CHANGELOG.md).

## 2.8 — Pool-model expansion and specialization (no breaking API change)

2.8's additions — the unbounded pool model (`HayateUnboundedPool<T>`), the asynchronous
preparation / reconnect decorator (`IHayatePreparationStrategy<T>` / `HayatePreparationPool<T>`),
the ArrayPool direct-storage backend (`WithArrayPoolStorage()`), abandoned-object recovery
(`RemoveAbandonedOnBorrow` / `RemoveAbandonedOnMaintenance`), named pools and the run-time pool
factory (`HayateServiceKey` / `IHayatePoolFactory`), the diagnostics master switch
(`EnableDiagnostics`), the core `System.Diagnostics.Metrics` meter, the configuration presets
(`HayatePoolPreset`), the borrow-order and eviction-policy switches (`HayateBorrowStrategy` /
`IHayateEvictionPolicy<T>`), shared pools (`HayatePool.Shared<T>()`), on-demand pre-warming
(`PreWarm`), return-path soft capacity (`SoftCapacity`), the bucketed buffer pool package
(`DotNetCore.HayateOP.Extensions.Buffers`), the `Deterministic` preset and the specialized
string-building surface (Z1 / Z4a / Z5 / Z6 / Z-C-A) — are purely additive: new types, new
members, new options and new packages only, with no change to any existing member's signature.

One behavioural fix is worth calling out: `HayatePreparationPool<T>`'s asynchronous borrows (A4)
previously blocked a thread pool thread for the whole inner acquire timeout and silently dropped
the `timeout` argument. They now await the inner pool's own asynchronous borrow and forward the
timeout, so an exhausted pool suspends instead of occupying a thread. No signature changed; a
caller that depended on the buggy behaviour (there is no supported way to do so) would observe
the documented asynchronous semantics instead.

Two CI-side changes do not affect library consumers: the performance gate is now allocation-aware
and routes blocking/advisory from the baseline's `calibration` flag (Q1), and each TFM's XML
documentation asset now carries exactly that TFM's API surface instead of whatever TFM built last.

## 2.7 — Nullable reference type annotations (non-breaking)

2.7's other additions — scoped borrows (`AcquireScoped` / `AcquireScopeAsync`), the
`HayatePool.Simple` one-call factory, process-shutdown auto-dispose
(`EnableAutoDisposeWithSystem` / `IHayateShutdownHook`), per-object metadata
(`GetTimes` / `LastGetThreadId` / `CreateTime`), the native keyed pool
(`ParameterizedHayatePool<TKey, TValue>`) and the specialized pools package
(`DotNetCore.HayateOP.Extensions.Specialized`) — are purely additive: new types,
new members and new packages only, with no change to any existing member's
signature or behaviour. The only source-visible difference in 2.7 is the
nullability annotation pass below.

2.7 enables C# nullable reference type (NRT) analysis project-wide for every `src` assembly
(`<Nullable>enable</Nullable>` in `asset/props/target.feature.props`). This is an additive,
source-compatible change: every difference is the addition of `?` to a type, a null-forgiving
`!`, or an initializer. No member was removed and no existing non-null contract was tightened.

A consumer recompiling against 2.7 sees the *true* nullability of the API surface for the first
time — previously everything was treated as non-nullable because NRT was off. No recompilation of
callers is required and no runtime behaviour changed.

### Public API signatures whose nullability was annotated in 2.7

- `IHayateObjectPool.SetUnavailable(string? reason = null)` — parameter widened to nullable (the
  implementation already accepted `null`).
- `HayateObject<T>.LeaseContext` — now `HayateLeaseContext?` (it is `null` when lease capture is off).
- The pool shard's `TryTakeSpare()` — now returns `HayateObject<T>?` (returns `null` when the spare
  stack is empty).
- `HayatePoolOptions.CustomShardAffinity` — now `Func<int>?` (optional delegate, defaults to `null`).
- `HayatePoolOptions.OnCapacityWarning` / `OnCapacityCritical` / `OnAvailable` / `OnUnavailable` —
  now `Action<HayatePoolCapacityAlarmEventArgs>?` (optional callbacks, default `null`).
- `HayatePoolBuilder<T>.WithCircuitBreaker(..., Func<bool>? probe = null, ...)` — `probe` parameter
  widened to nullable (a `null` probe keeps recovery manual).
- `HayateCircuitBreakerOptions.Probe` — now `Func<bool>?`; its `Equals(HayateCircuitBreakerOptions? other)`
  parameter widened to nullable (aligns with `IEquatable<T>.Equals`).
- `HayateLeaseContext.Current` — now `HayateLeaseContext?` (null when not within a lease).
- `HayatePoolMetadata.ElementType` / `HayatePoolMetadata.PoolType` — now `Type?` (null for
  non-generic implementations).
- `HayatePoolSummary.ElementType` — now `string?` (null for the non-generic implementation).
- `HayatePrometheusExporter` constructor, and the DI / Configuration / Prometheus registration
  helpers — optional parameters (`registry`, `options`, `configure`, `onlyWhen`, `poolName`) widened
  to nullable. (The internal lazy fields (`HayateDiagnostics._cachedEventNames`,
  `HayatePrometheusExporter._registry`) were also annotated, but these are not part of the public
  surface.)
- `HayateMicrosoftLoggerAdapter<T>(ILogger<T>? logger)` — constructor parameter widened to nullable
  (the adapter already defends against a null logger internally).

### Adoption notes

- **Nothing to change for existing callers.** These are annotations only; a project that compiled
  against 2.6 continues to compile against 2.7. Callers with NRT enabled now receive accurate
  nullability feedback for the members listed above.
- **Build matrix.** With NRT on, the core pool and all seven extension packages compile with zero
  nullable warnings on net6.0–net10.0.
- **Test projects.** `common.tests.props` also enables `<Nullable>enable</Nullable>` for Q0, but the
  test code itself is not yet annotated; the `CS86xx` / `CS876x` warning codes are suppressed in the
  test props so the test build stays warning-free. Annotating the test code is tracked as a separate
  acceptance pass (the 2.7 plan permits "单列验收" for this item).

## 2.6 — No breaking API change

2.6 adds only opt-in features and documentation; existing code compiles and behaves as before.
Two adoption notes nonetheless deserve attention:

- **All runtime text is now English.** Exception messages, OpenTelemetry metric descriptions and
  `HayatePoolStats.ToString()` section labels previously emitted Chinese text. Code that matches
  on message content — for example `Assert.Contains` in a test, or log parsing — must be updated
  to the English strings.
- **New behaviours are opt-in only.** The lean fast path (`EnableLean`), the
  `CreateOnDemand` reject policy, the configuration profiles and the availability circuit
  breaker (`EnableCircuitBreaker`) all default to their pre-2.6 behaviour. The circuit breaker
  adds `HayatePoolUnavailableException` (derived from `InvalidOperationException`, so existing
  catch blocks keep working), thrown only when the breaker is enabled and open. See the
  [behavioural notes](#behavioural-notes-non-breaking) for the disposal semantics documented
  during this cycle.

## 2.5 — Structured lease context and lifecycle fields

Two observability-facing breaking changes land in 2.5.

### 1. `HayateObject<T>.AcquireTrace` (string) removed

When leak-trace capture is enabled (`LeakTraceCaptureMode = EveryAcquire` or `Sampled`), each
borrow now creates an immutable `HayateLeaseContext` (monotonic `LeaseId` + captured
`StackFrame[]` + `BorrowedAt`). The context is carried by an `AsyncLocal<HayateLeaseContext>`
flow (`HayateLeaseContext.Current`), so **concurrent acquires each own an isolated context
instead of overwriting a shared string**; the wrapper exposes the same instance via
`HayateObject<T>.LeaseContext`. Frames are captured with `new StackTrace(fNeedFileInfo: false)`
(no PDB / source-file I/O). `Release` ends the lease (the flow context is detached); `Destroy`
clears the wrapper reference. The three capture modes (`Off` by default / `Sampled` /
`EveryAcquire`) keep their 2.1+ semantics.

For the old text form, read `TakeSnapshot().LeakTraces`, which the pool formats as
`[Lease {id}] Type.Method+0xoffset <- ...`.

**Migration**

```csharp
// Before (2.4)
string trace = pooledObject.AcquireTrace;

// After (2.5)
HayateLeaseContext context = pooledObject.LeaseContext;   // may be null when capture is Off
if (context is not null)
{
    long id = context.LeaseId;
    IReadOnlyList<StackFrame> frames = context.Frames;
}
```

### 2. New lifecycle fields on `HayateObject<T>`

- `CreatedAtTick` — wall-clock creation time as `DateTimeOffset.UtcNow.Ticks`, complementing the
  Stopwatch-based `CreatedAt` used for durations.
- `LeaseCount` — monotonic borrow counter.
- `OwnerPoolName` — logical pool name stamped at creation.

These flow through snapshots via the new `HayatePoolSnapshot.ObjectDetails` collection
(`HayatePoolObjectDetail`), populated on every `TakeSnapshot()`.

Also new (non-breaking) in 2.5: shard affinity (`ShardAffinityMode`) and the full registry surface
(`GetAll` / `Remove` / `HayatePoolMetadata`).

## 2.4 — Idle pools actually scale down

Since 2.4 the background scaling callback honours the scaling strategy's scale-down decision. In
2.3 and earlier an internal gate (`totalIdle < currentTotal * 0.4f`, i.e. usage above 60%)
contradicted the default `ThresholdScalingStrategy`, which only proposes shrinking when usage is
below `ScaleDownThreshold` (0.2 by default). The two conditions could never hold at the same time,
so **with default settings the pool never scaled down** — auto-scaling only grew.

The gate has been removed: shrinking now proceeds whenever the strategy proposes a smaller target
and the scale-down cooldown has elapsed, still bounded by `MinPoolSize`.

**Migration**: if you relied on pools staying at their high-water mark, set
`EnableAutoScaling = false` or raise `ScaleDownThreshold`.

## 2.2 — Builder fails fast on orphaned custom metrics

Since 2.2 `Build()` throws `InvalidOperationException` when a custom `IHayateMetrics` is registered
via `WithMetrics(...)` while metrics collection is disabled (`EnableMetrics = false`). In 2.1 and
earlier the custom instance was **silently replaced** with `EmptyHayateMetrics`, so a forgotten
`WithEnableMetrics(true)` went undetected.

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

> The DI / Configuration integration is unaffected: pools built through `AddHayatePool` /
> `RegisterHayatePool` attach the DI-registered `IHayateMetrics` only when metrics are enabled,
> exactly as in 2.1.

## 2.1 — Leak trace capture

Since 2.1, stack-trace capture is **decoupled** from leak detection and **off by default**
(`LeakTraceCaptureMode.Off`). In 2.0 and earlier, every `Acquire` captured a full
`Environment.StackTrace` (tens of microseconds and 10–40 KB per borrow when the call chain is
deep). Leak detection itself — threshold detection and `LeakCount` reporting — is unaffected and
still costs nothing on the hot path; `LeakTraces` entries become the placeholder text
`"No stack trace available"` unless capture is enabled.

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

### Also changed in 2.1

- **Empty-pool cold boot.** If the pool is completely empty (e.g. `MinPoolSize = 0` and not
  pre-warmed), `Acquire` under `Block` / `BlockTimeout` — and `AcquireAsync` — now synchronously
  creates the first object on demand (cold-boot, atomically de-duplicated) instead of waiting for a
  return that may never come. Previously the call waited the full acquire timeout and then threw
  `TimeoutException` (or hung forever on the async path).
- **`ApplyFeatureSwitches` capacity semantics.** With `EnableAutoScaling = false`, your explicit
  `MaxPoolSize` is kept as the hard capacity cap (previously it was forced to `MinPoolSize`, which
  silently collapsed capacity to 0 when `MinPoolSize = 0`). Only a `MaxPoolSize < MinPoolSize`
  misconfiguration is lifted to `MaxPoolSize = MinPoolSize`.

## Behavioural notes (non-breaking)

Behaviours to be aware of before adopting HayateOP.

- **`CreateNew` reject policy creates lazily, not eagerly.** Under `RejectPolicy.CreateNew` the pool
  first waits up to the acquire timeout for a return, and only then synchronously creates a new
  tracked object. A cold first call on an empty pool therefore incurs up to a full-timeout delay
  before the new object is handed out.
- **Blocking wake-up granularity is ~100 ms.** Blocking waits (`Block` / `BlockTimeout` /
  `CreateNew`) re-check in fixed slices of 100 ms, so the observed tail latency of a
  return-to-acquire handoff is quantized to that slice size.
- **`MinPoolSize = 0` cold pools bootstrap on first acquire.** No background component pre-creates
  objects to reach `MinPoolSize`; the pool starts empty, and the first acquire on a fully empty
  pool creates its object on demand. Background scaling never grows toward `MinPoolSize = 0`.
- **A disposed pool is not guarded against further use.** `Dispose()` drains the objects the pool
  holds and releases the background timer and wake-up gate, but keeps no disposed flag, so a
  later `Acquire` is not rejected with `ObjectDisposedException` the way
  `Microsoft.Extensions.ObjectPool.DisposableObjectPool<T>` rejects it. On a drained pool the
  cold-boot path simply hands out a newly created object. Treat a disposed pool as unusable.
  Related: `IHayateObjectPolicy.OnDestroy` is not invoked for objects released by
  `Clear()` / `Dispose()` on the general-purpose engine, unlike every other destroy path. See
  [`disposal.md`](disposal.md).
