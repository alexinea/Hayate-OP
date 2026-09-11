# Hot-path cost of each feature switch

`HayatePoolOptions` exposes roughly forty knobs. This page states, for every feature
switch, **where** its cost lands — the borrow path, the return path, the background
worker, or a diagnostic path that is only paid when called — so a configuration can be
trimmed to what the workload actually needs.

Two kinds of statement appear below and they are deliberately kept apart:

* **Measured** figures come from the archived benchmark run
  [`docs/benchmarks/2026-09-10-o1-lean.md`](benchmarks/2026-09-10-o1-lean.md) and are
  quoted with the configuration they were measured on. Only whole configurations were
  benchmarked, not one switch at a time.
* **Structural** statements describe what the code adds to a path, read from the
  implementation. They are exact about *where* the work happens and deliberately silent
  about a nanosecond figure that was never measured.

## 1. The measured ladder

Single-threaded `Acquire+Release` round trip on .NET 10.0, `MinPoolSize = 250` /
`MaxPoolSize = 300`, all pools pre-warmed, every row measured in one run
(2026-09-10):

| Configuration | Mean | Allocated |
| :--- | ---: | ---: |
| `Microsoft.Extensions.ObjectPool` (reference) | 26.950 ns | 0 B |
| **HayateOP Lean** (`WithLean()`) | **27.063 ns** | **0 B** |
| HayateOP Sharded4 (sharding only, 4 shards) | 285.857 ns | 384 B |
| HayateOP AllOff (general engine, every optional feature off) | 286.773 ns | 384 B |
| HayateOP Full (general engine, every feature on) | 414.987 ns | 0 B |

Read from the four Hayate rows:

* **`EnableLean` is worth about 10.6x** on the same feature set — 27.1 ns against
  286.8 ns — because it removes the per-object wrapper, the per-shard free-list lock, the
  registry probe on return and every diagnostic write.
* **Sharding is free in single-threaded steady state** (285.9 ns against 286.8 ns, i.e.
  0.3%, inside the run-to-run noise). It is bought for contention, not for latency — see
  the 100-thread suite in the benchmark report.
* **The six general-engine features together cost about 129 ns per round trip**
  (415.0 ns against 285.9 ns). That aggregate is the only measurement the report
  supports for the feature set as a whole; the per-switch attribution in §2 is structural.

Two caveats that matter when reading any absolute number here:

* The two sub-50 ns rows sit at the resolution limit of the benchmark machine, and
  repeated runs of the *same* benchmark move by up to 2.4x. The ratio and the allocation
  column are the stable results.
* The `Allocated` column for the general engine is not stable across runs (0-1065 B for
  the same benchmark) because lazily created internal structures are amortised over the
  measured operation count. Do not read those cells as a steady-state allocation rate —
  the one reproducible allocation result is the lean path's **0 B**.

## 2. Where each switch costs

### Borrow path

| Switch | Effect on the borrow path | Source of the cost |
| :--- | :--- | :--- |
| `EnableLean` | Replaces the general engine entirely | No wrapper, no shard lock, no registry probe, no diagnostic write — see §1 |
| `EnableSharding` / `ShardCount` | One idle-list probe per shard until a hit | A hit normally comes from the first scanned shard, so the added cost is a function of misses, not of `ShardCount` |
| `ShardAffinityMode` | `None` costs nothing; `Thread` / `Custom` evaluate a start shard once per borrow | One thread-ID read or one user delegate invocation per `Acquire` (falling back to a sequential scan on a null / out-of-range / throwing delegate) |
| `EnableGenerationOptimization` | One extra `Stopwatch.GetTimestamp()`, a ticks-to-milliseconds division and a compare, plus a `ValidationSkipCount` write for old-generation objects | This is the only switch that adds a QPC read to the borrow path |
| `ValidateOnBorrow` | One `IHayateObjectPolicy.Validate` call per borrow, plus `Destroy` on a failed verdict | Only consulted when `EnableValidation` is also on. The cost is the policy's own work — often a connection-health probe |
| `EnableLeakDetection` | Two predicted branches when `LeakTraceCaptureMode.Off` | The scan itself runs in `TakeSnapshot()`, not here (§ diagnostic paths) |
| `LeakTraceCaptureMode` | `Off` nothing; `Sampled` one `Interlocked.Increment` plus a modulo every N borrows; `EveryAcquire` **one call-stack capture per borrow** | The dominant cost of the whole leak-detection surface: tens of microseconds and 10-40 KB per borrow for `EveryAcquire` |
| `EnableMetrics` | A min/max CAS-loop update, an `IHayateMetrics.RecordObjectAcquired` call and a debug log entry | Shares the already-running stopwatch for the wait time |
| `EnableAllocationTracking` | Two `GC.GetAllocatedBytesForCurrentThread()` calls (one before, one after) plus a counter pair | Unavailable on `net48` / `netstandard2.0`, where the counters stay 0 |
| `WarnAtRatio` / `CriticalAtRatio` | When either is non-zero, `CheckCapacityAlarm` runs on every borrow **and** every return and walks every shard to compute the utilization ratio | The walk takes one `SpinLock` per shard (`_shards.Sum(s => s.Count)`), so this is the most expensive optional switch per operation. At the default 0 it is one branch |
| `WaitForWarmup` | One branch when off; when on, borrows block on the warm-up signal until pre-warm finishes | The block is one-time per pool |
| `MinPoolSize` / `MaxPoolSize` | No per-operation cost | Sizing only; `MinPoolSize` is paid once during pre-warm |
| `RejectPolicy` | Consulted only after a miss | Behavioural, not a cost knob: `Block` / `BlockTimeout` wake on a ~100 ms slice, `CreateOnDemand` turns a miss into a synchronous creation |
| `DefaultAcquireTimeout` | No per-operation cost | Evaluated on the timeout path only |

### Return path

| Switch | Effect on the return path | Source of the cost |
| :--- | :--- | :--- |
| `EnableLean` | Replaces the general engine entirely | No registry probe, no free-list lock |
| `ValidateOnReturn` | One `Validate` call per return, plus `Destroy` on a failed verdict | Also requires `EnableValidation` |
| `EnableMetrics` | A min/max CAS-loop update for the lease time, the `TotalReleased` counter and an `IHayateMetrics.RecordObjectReleased` call | — |
| `EnableAllocationTracking` | Two allocation queries plus a counter pair | — |
| `WarnAtRatio` / `CriticalAtRatio` | Same shard-walking utilisation probe as on the borrow path | — |
| `EnableAutoScaling` | A watermark check after a rejected return | Only when `MinPoolSize > 0` and the pool fell below it |

### Background workers

These never touch the borrow or return path; they run on the pool's single shared timer.
A pool with all of them disabled creates **no timer at all**.

| Switch | Period | Work per run |
| :--- | :--- | :--- |
| `EnableEviction` | `EvictionIntervalMs` (30 s) | Scans `NumTestsPerEvictionRun` samples per shard and claims + destroys the matches (`OnDestroy`, then the object's own `Dispose`) |
| `EnableAutoScaling` | `ScalingIntervalMs` (5 s) | Compares utilisation against the up/down thresholds and grows or shrinks the shard capacities |
| `ValidateWhileIdle` | `ValidateIntervalMs` (30 s) | Walks the whole idle list of every shard, calls `Validate` per idle object, claims + destroys the failures |

> `EnableValidation` contributes its interval to the shared timer's tick period even when
> `ValidateWhileIdle` is off and the idle pass therefore returns immediately. Since
> auto-scaling is on by default at the shorter 5 s period, this only becomes visible once
> eviction and auto-scaling are both off.

### Diagnostic paths — paid only when called

| Member | Cost |
| :--- | :--- |
| `TakeSnapshot()` | O(n) walk of every registered object; additionally walks the leak-detection branch (threshold check and `LeakDetectedCount` increment, plus trace formatting) when `EnableLeakDetection` is on |
| `GetStats()` | O(shards) for the counts; reads the cumulative and timing fields atomically |
| `Evict(reason)` | Walks the idle list of every shard; unavailable on the lean path, which keeps no per-object timestamps |

## 3. Trimming by scenario

| Scenario | Configuration |
| :--- | :--- |
| High-frequency pooling of small, stateless objects | `UseLeanProfile()` — 27.1 ns / 0 B, parity with `DefaultObjectPool` |
| Connection / session pool | Keep validation (`ValidateOnBorrow`) and eviction; keep leak detection with `LeakTraceCaptureMode.Off`; turn metrics and allocation tracking off. The cost that matters is the policy's `Validate`, not the pool's |
| Batch processing | Sharding on; eviction and the capacity alarm off — objects churn continuously and the watermark only adds a shard walk per operation |
| Latency-critical steady state | Leave the capacity alarm at 0 and enable it only while tuning; it is the one switch that takes a lock per shard on every borrow and return |
| Memory-constrained host | Eviction on with a short `MaxIdleTime`; metrics and allocation tracking off |
| Contract that must never block | `RejectPolicy = CreateOnDemand` (or the lean path), which creates on a miss instead of waiting out the timeout |

## 4. The switch that also gates three counters

`EnableMetrics` is not only an observability switch — it also decides whether three of the
four cumulative counters are written at all. With metrics off, `TotalCreated`,
`TotalReleased` and `TotalMissed` stop being maintained (they report 0), while
`TotalAcquired` keeps being incremented unconditionally on every borrow. Turning metrics
off therefore buys back those counter writes, and anything reading the statistics object
has to know which of the two groups it is reading. The full audit — including why
`TotalAcquired` is deliberately left unconditional — is in
[`docs/metrics-gating.md`](metrics-gating.md).

## See also

* [`docs/benchmarks/2026-09-10-o1-lean.md`](benchmarks/2026-09-10-o1-lean.md) — the raw
  benchmark run behind §1, including the percentile columns, the variance analysis and
  the reproduction commands.
* [`docs/BREAKING-CHANGES.md`](BREAKING-CHANGES.md) — the behavioural notes worth reading
  before trimming (cold boot, `CreateNew` laziness, the ~100 ms blocking wake-up
  granularity).
