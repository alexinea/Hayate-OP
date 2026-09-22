# Breaking changes and behavioural notes

Migration guidance for every breaking change, newest first, plus the behavioural notes that
frequently surprise adopters. For a per-version summary of all changes see
[`../CHANGELOG.md`](../CHANGELOG.md).

## 3.0 — Lifetime semantics named, blocking wake-up made signal-driven

3.0 states the meaning `MaxLifeTime` always had, in full, and leaves the enforcement of the borrowed
half exactly where 2.9 put it: behind an opt-in switch that is off by default. It also removes the
100 ms quantisation that blocking borrows used to carry. No public signature changes and no default
option value moves — but the last two bullets below are an observable behaviour change, so read them
before adopting: a blocking wait now wakes on the signal, and its timeout is a boundary rather than a
floor.

- **`MaxLifeTime` is documented as the maximum lifetime of a pooled object from the moment it is
  created — idle or borrowed** (β-1). The documented scope used to be the idle half only, which read as
  though a borrowed object could never age. The value, its default (10 minutes) and the pool's behaviour
  are all unchanged: what changed is that the meaning is now written down in full, on the option's XML
  documentation and in the README, as the same sentence. Which half the pool enforces is a choice, and
  the choice is the switch below.
- **The borrowed half stays opt-in, and off by default** (β-2). `EnableLifetimeRotationOnBorrow` — added
  in 2.9 and unchanged in 3.0 — is still `false` by default, so **the default behaviour does not change**:
  only idle objects are retired for age, and an object held by the application for longer than
  `MaxLifeTime` is never treated as expired. That is the behaviour of every release before 2.9, and it is
  what 3.0 keeps.
- **Blocking wake-up is signal-driven, not quantised** (B6). `Block`, `BlockTimeout` and
  `CreateNew` / `CreateOnDemand` used to re-check their shard in fixed 100 ms slices, so a waiter
  could not observe a return sooner than the next slice boundary and the tail latency of the
  release-to-wake-to-borrow handoff was quantised to that step. The wait is now a single
  `SemaphoreSlim` wait: it returns the moment a signal is published, and sleeps until its timeout
  when none is. Every site that makes an object available — a return, the constructor's pre-warm,
  `PreWarm(n)`, the background scaler, and an abandoned-lease reclaim — publishes that signal, so a
  woken waiter no longer re-scans the shard on a timer. **Lean mode (`EnableLean`) is not covered**:
  it keeps the 100 ms slice, because its borrow path grows from a fixed buffer before it parks and
  a return that is rejected or overflows that buffer frees a slot without publishing a signal.
- **The blocking timeout is a boundary, not a floor** (B6). `BlockTimeout` and `CreateNew` threw
  once `elapsed >= timeout`, but only at a slice boundary, so a configured 400 ms timeout could
  surface anywhere up to roughly 500 ms. The same check now runs when the wait itself expires, so
  the exception arrives at the configured timeout plus scheduling noise. The `TimeoutException`
  type, its message and the throw-rather-than-return-null contract are unchanged, and `Block` still
  has no timeout at all: it waits until a signal arrives.

### Migration

Nothing to migrate for the lifetime semantics: an application that compiled and ran against 2.9
compiles and behaves the same against 3.0 on that count — those two bullets record a naming decision,
not a change.

If you do want the borrowed half enforced — the case where a server-side `max_connection_lifetime` or an
intermediary's idle timeout can invalidate a connection the pool still believes is good — turn the switch
on explicitly:

```csharp
var pool = new HayatePoolBuilder<MyConnection>()
    .WithMaxLifeTime(TimeSpan.FromMinutes(30))
    .WithEnableLifetimeRotationOnBorrow()   // opt-in; still off by default in 3.0
    .Build();
```

Evaluate long-held objects first. With the switch on, a borrow that finds an idle object past
`MaxLifeTime` destroys it and hands out a replacement, so a borrow / hold / release / borrow cycle that
used to get the same instance back now gets a fresh one, and any state your policy keeps on the instance
is rebuilt. An object that is currently borrowed is not affected: the rotation happens on the borrow path
and never takes an object away from its borrower.

Two constraints come with the switch, and 3.0 changes neither. It cannot be combined with `EnableLean` —
the lean fast path keeps no per-object timestamps, so the combination fails validation rather than being
silently ignored. And making it the default would change behaviour for every existing pool, so that would
need its own major release and its own migration guide.

### Migrating to the signal-driven wake-up

The wake-up change needs no code change either, but three things are worth checking before you adopt
it:

- **Re-measure blocking tail latency if you baseline it.** A p99 that used to sit just under 100 ms
  because of the slice now reflects the real release-to-wake path. Your load tests, SLOs and capacity
  plans move; the pool's throughput characteristics do not.
- **Tighten any bound that assumed the old floor.** A test that allowed a 400 ms timeout to surface as
  late as 500 ms still passes, but one that asserted the *floor* — "`Block` returned no sooner than
  100 ms", or a tail latency of "at least a slice" — no longer describes the behaviour.
- **`Block` now waits until a signal is published, with no timeout of its own.** That is what it
  always documented; what changed is that the slice used to recover a waiter the signal missed. The
  library now publishes a signal from every path that frees an object or a slot, so the reachable set
  is the same: a pool whose objects are all leaked and never reclaimed parks for good, exactly as it
  did before.

## 2.9 — Asynchronous contracts and logging (no breaking API change)

2.9's additions — the asynchronous creation contract (`IHayateAsyncObjectPolicy<T>`), its
disposal counterpart (`IHayateAsyncObjectPool<T>`), the asynchronous return hooks
(`OnReleaseAsync` / `OnPassivateAsync` / `OnDestroyAsync`), the DI policy factory, metrics and a
logger on keyed sub-pools, the object health probe (`IHayateObjectHealthProbe<T>`), `net48`
assets for the Configuration and HealthCheck packages, borrow-side lifetime rotation past
`MaxLifeTime` (`WithEnableLifetimeRotationOnBorrow`, opt-in and off by default), the per-pool
Microsoft.Extensions.Logging factory (`HayateMicrosoftLoggerFactory`), and the relaxed `new()`
constraints on the twelve entry points that accept a policy or a factory — are purely additive:
new types, new members, new options and new packages only, with no change to any existing
member's signature. The asynchronous members are gated to `net6.0` and later, so
`netstandard2.0` and `net48` consumers keep the fully synchronous pool and gain none of them.
The eight entry points that take no policy and no factory deliberately keep their `new()`
constraint: `new T()` is the only construction they can perform, and dropping the constraint
would turn a compile-time error into a run-time `MissingMethodException`.

Three behavioural changes are worth calling out. None of them breaks a caller that compiled
against 2.8, and each is a case where the old behaviour was the surprising one:

- **Log categories changed** (L2). A pool hosted through the DependencyInjection or Configuration
  packages is now logged under a Microsoft.Extensions.Logging category named after the pool — the
  pool name for a named pool, the element type's short name for an unnamed one — where the
  category used to be the element type's namespace-qualified display name (`MyApp.MyConnection`
  becomes `MyConnection`). A sink that filters on the old category has to be re-pointed at the
  new one; an application that does not filter by category is unaffected. The category rule and
  the measurements behind it are in [`../CHANGELOG.md`](../CHANGELOG.md) (L2).
- **A container with no logging provider no longer silences the pool** (L3). The MEL bridge used
  to hand the pool a logger that discarded everything whenever no `ILoggerFactory` was
  registered, so a host that had simply not called `AddLogging()` got no diagnostics at all. It
  now falls back to the built-in logger, which is a no-op on a Release build and writes to the
  console on a Debug one — so the difference is observable only in Debug, and only in a host that
  registered no logging provider.
- **An open circuit breaker is no longer reported as a healthy pool** (B4).
  `HayateOpHealthCheck<T>` decided healthy-versus-degraded on available slots alone, so a pool
  whose breaker had tripped still reported `Healthy` while every acquire was being rejected. A
  tripped breaker now reports `Unhealthy`, and the health payload grows from 6 fields to 14. A
  monitoring rule that treated "not `Healthy`" as pageable now fires on a condition it previously
  missed, which is the point of the change.

One addition deserves a note for readers of `HayatePoolStats` rather than for callers. The six
ratio-class members added by G-1 (`ReuseEfficiency`, `CreatesPerAcquire`, `AcquiresPerSecond`,
`PeakActiveObjects`, `StartedAt` / `UptimeSeconds`, `LastActivityTime`) are maintained only while
`EnableMetrics` is on, so with the default configuration (metrics off) they read `0`, `null` or
`default` rather than a value. That is a property of the new members — no existing member changes
type or meaning — and `MetricsEnabled` exists to tell "the gate is closed" apart from "the pool is
idle", which a bare zero cannot. `ToString()` also gains an `[Operational (gated by EnableMetrics)]`
section. The reasoning and the measurements are in [`../CHANGELOG.md`](../CHANGELOG.md) (G-1).

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
- **Lean mode's blocking wake-up granularity is ~100 ms.** In lean mode (`EnableLean`) blocking waits
  (`Block` / `BlockTimeout` / `CreateNew` / `CreateOnDemand`) still re-check in fixed slices of 100 ms,
  so the observed tail latency of a return-to-acquire handoff is quantized to that slice size. The
  general-purpose engine no longer has that step: since 3.0 its blocking waits are signal-driven (see
  [3.0](#30--lifetime-semantics-named-blocking-wake-up-made-signal-driven)).
- **`MinPoolSize = 0` cold pools bootstrap on first acquire.** No background component pre-creates
  objects to reach `MinPoolSize`; the pool starts empty, and the first acquire on a fully empty
  pool creates its object on demand. (A non-zero `MinPoolSize` is pre-warmed once, at construction,
  not in the background.) Background scaling treats `MaxPoolSize` as its growth ceiling and
  `MinPoolSize` only as the floor it will not shrink below — it never grows the pool *toward*
  `MinPoolSize`.
- **`MaxPoolSize` is a ceiling for the wait-based policies, not a growth target.** `Block`,
  `BlockTimeout` and `CreateNew` never grow the pool on the borrow path: the only object they create
  there is the very first one, and only on a completely empty pool. Every other miss waits for a
  return — until the timeout, under `BlockTimeout` — even while `MaxPoolSize` would still allow more
  objects: with `MinPoolSize = 5`, `MaxPoolSize = 8` and all five objects lent out, the sixth borrow
  waits out the timeout and throws `TimeoutException` rather than creating a sixth object. A pool can
  still be grown while a caller waits, but only from the outside: by the background scaler on its own
  period, or by the one-step forced scale-up the timeout paths run just before giving up. Growing the
  pool on a miss is what `CreateOnDemand` is for.
- **A disposed pool is not guarded against further use.** `Dispose()` drains the objects the pool
  holds and releases the background timer and wake-up gate, but keeps no disposed flag, so a
  later `Acquire` is not rejected with `ObjectDisposedException` the way
  `Microsoft.Extensions.ObjectPool.DisposableObjectPool<T>` rejects it. On a drained pool the
  cold-boot path simply hands out a newly created object. Treat a disposed pool as unusable.
  Related: `IHayateObjectPolicy.OnDestroy` is not invoked for objects released by
  `Clear()` / `Dispose()` on the general-purpose engine, unlike every other destroy path. See
  [`disposal.md`](disposal.md).
