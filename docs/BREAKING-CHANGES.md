# Breaking changes and behavioural notes

Migration guidance for every breaking change, newest first, plus the behavioural notes that
frequently surprise adopters. For a per-version summary of all changes see
[`../CHANGELOG.md`](../CHANGELOG.md).

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
