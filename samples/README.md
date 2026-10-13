# HayateOP Samples

One self-contained console project per capability. Each folder under `samples/`
(`HayateOP.Samples.*`) is a runnable example with its own `README.md` and an
inline-commented `Program.cs`. Targets `net10.0`; references the `HayateOP`
(and, where relevant, `HayateOP.Extensions.*`) projects directly.

```bash
# Run any sample (from the repository root):
dotnet run --project samples/HayateOP.Samples.BorrowOrder
```

All samples build and run in `Release`. They are wired into `HayateOP.slnx`
under the `/samples/` solution folder.

---

## Index

| Sample | Focus | README feature |
|---|---|---|
| [BorrowOrder](HayateOP.Samples.BorrowOrder) | FIFO vs LIFO hand-out order from the idle list | Borrow Order |
| [SoftCapacity](HayateOP.Samples.SoftCapacity) | Bound the *retained* set on return; drop extras instead of parking | Soft Capacity |
| [BucketedBuffer](HayateOP.Samples.BucketedBuffer) | Fixed ladder of size buckets; round-up routing for array rents | Bucketed Buffer Pool |
| [Eviction](HayateOP.Samples.Eviction) | Replace the idle-eviction rule with a custom `IHayateEvictionPolicy<T>` | Eviction Policy |
| [Generation](HayateOP.Samples.Generation) | Promote long-lived objects to the old generation to cut validation | Generation Optimization |
| [LeakDetection](HayateOP.Samples.LeakDetection) | Flag borrows never returned; opt-in stack-trace capture | Leak Detection |
| [AbandonedRecovery](HayateOP.Samples.AbandonedRecovery) | Destructively reclaim borrowed-but-never-returned objects past a timeout | Abandoned Recovery |
| [RejectPolicies](HayateOP.Samples.RejectPolicies) | Abort / Block / BlockTimeout / CreateNew / CreateOnDemand at capacity | Multiple Reject Policies |
| [Sharding](HayateOP.Samples.Sharding) | Split capacity across shards; three affinity modes | Sharding Support |
| [Scaling](HayateOP.Samples.Scaling) | Auto scale-up on demand, scale-down on idle; runtime hot-resize | Auto-Scaling |
| [ActiveCeiling](HayateOP.Samples.ActiveCeiling) | Cap concurrent borrows with `MaxActiveObjects` (independent of size) | — (active-object ceiling) |
| [Presets](HayateOP.Samples.Presets) | Start from a tuned `HayatePoolPreset`, override individual knobs | Configuration Presets |
| [Validation](HayateOP.Samples.Validation) | Validate on borrow/return/idle; destroy unhealthy objects | Object Validation |
| [Preparation](HayateOP.Samples.Preparation) | Repair/reconnect objects before delivery via `IHayatePreparationStrategy<T>` | — (preparation strategy) |
| [ResetTiming](HayateOP.Samples.ResetTiming) | Choose which side of the cycle the automatic `IHayateResettable` reset runs on | — (reset timing) |
| [Resilience](HayateOP.Samples.Resilience) | Pool-level circuit breaker + reject-policy backpressure | Multiple Reject Policies |
| [Lease](HayateOP.Samples.Lease) | Zero-allocation value-type `HayatePoolLease<T>` on the hot path | High Performance Core |
| [Scoped](HayateOP.Samples.Scoped) | Class-based `HayatePoolScope<T>` crossing frames / fields / `await` | High Performance Core |
| [Async](HayateOP.Samples.Async) | `await using` leases, fan-out, cancellation, async timeout | High Performance Core |
| [Unbounded](HayateOP.Samples.Unbounded) | Create-on-demand pool that never blocks a borrower; bounds only idle retention | — (unbounded variant) |
| [Metrics](HayateOP.Samples.Metrics) | Metrics meter, allocation tracking, capacity alarms, `GetStats()` | Metrics & Monitoring |
| [Diagnostics](HayateOP.Samples.Diagnostics) | Live options, pre-warm, `ReloadConfig`, snapshot, breaker toggle, evict | Diagnostics |
| [NamedPools](HayateOP.Samples.NamedPools) | Several independent pools of one type resolved by name via DI | Dependency Injection |
| [Keyed](HayateOP.Samples.Keyed) | `ParameterizedHayatePool<TKey,TValue>` — per-key isolated sub-pools | — (keyed/parameterized pool) |
| [ObjectPoolCompat](HayateOP.Samples.ObjectPoolCompat) | Drop-in `Microsoft.Extensions.ObjectPool` backend swap + DI | Cross-Version / DI |
| [Endpoints](HayateOP.Samples.Endpoints) | ASP.NET Core registration, management + health endpoints | ASP.NET Endpoints / Health Checks |

> The README also lists **Configuration Binding** (load settings from
> `appsettings.json`) and **Dependency Injection Integration** as features.
> DI is exercised by `NamedPools` and `ObjectPoolCompat`; configuration binding
> itself is a library feature consumed through those samples' `configuration`
> registration rather than a standalone example.

---

## By theme

### Borrow order & capacity retention
- **[BorrowOrder](HayateOP.Samples.BorrowOrder)** — `WithBorrowStrategy(HayateBorrowStrategy.Fifo|Lifo)`
  selects whether the oldest or most-recently-returned object is handed out.
- **[SoftCapacity](HayateOP.Samples.SoftCapacity)** — `WithSoftCapacity(n)` caps how many returned
  objects are *kept*; the rest are disposed on return, while the pool still lends up to its
  hard `MaxPoolSize`.

### Buffers
- **[BucketedBuffer](HayateOP.Samples.BucketedBuffer)** — `HayateBufferPool<T>` is an optional fixed
  ladder of size buckets (256 / 1024 / 4096 …). `Rent(size)` rounds **up** to the smallest fitting
  bucket; a request above the largest bucket is rejected with `ArgumentException`.

### Health, validation & lifecycle
- **[Validation](HayateOP.Samples.Validation)** — `IsValid()` gates reuse on borrow, return, and idle;
  invalid objects are destroyed. Pairs with leak detection and on-demand `Evict`.
- **[Generation](HayateOP.Samples.Generation)** — `WithEnableGenerationOptimization` promotes objects
  past `WithGenerationThreshold` to the old generation, which then skips validation on most borrows.
- **[Eviction](HayateOP.Samples.Eviction)** — the background run consults a custom `IHayateEvictionPolicy<T>`
  (e.g. retire a connection after N leases); the built-in idle rule is shown for contrast.
- **[LeakDetection](HayateOP.Samples.LeakDetection)** — `WithEnableLeakDetection` + threshold flags borrows
  never returned; `WithLeakTraceCapture(Off|Sampled|EveryAcquire)` controls stack capture.
- **[AbandonedRecovery](HayateOP.Samples.AbandonedRecovery)** — destructive reclamation of borrowed-but-never
  returned objects past `RemoveAbandonedTimeout`; **off by default**, opt in deliberately.
- **[Preparation](HayateOP.Samples.Preparation)** — `WithPreparation` runs a reconnect/repair strategy before
  delivery so callers never receive a broken object.
- **[ResetTiming](HayateOP.Samples.ResetTiming)** — `WithResetOnReturn` / `WithResetOnBorrow` move the
  `IHayateResettable` reset between the return side (the default) and the borrow side; lean pins it back to
  the return side, and a custom policy keeps its own hooks.

### Capacity, scaling & backpressure
- **[Sharding](HayateOP.Samples.Sharding)** — `WithShardCount` splits capacity into independent free-list
  locks; `None` / `Thread` / `Custom` affinity picks where each borrow scan *starts*.
- **[Scaling](HayateOP.Samples.Scaling)** — scale-up on demand, scale-down on idle in `ScaleDownStep` chunks,
  cooldowns, and `ReloadConfig` runtime hot-resize.
- **[ActiveCeiling](HayateOP.Samples.ActiveCeiling)** — `WithMaxActiveObjects` bounds *in-flight* borrows
  independently of `MaxPoolSize`; extra borrows wait, counted by `ActiveLimitThrottledCount`.
- **[RejectPolicies](HayateOP.Samples.RejectPolicies)** — the five outcomes when a borrow finds no idle
  object and the pool cannot create: `Abort`, `Block`, `BlockTimeout`, `CreateNew`, `CreateOnDemand`.
- **[Resilience](HayateOP.Samples.Resilience)** — pool-level circuit breaker (`SetUnavailable` /
  `SetAvailable`) plus the `Abort` / `BlockTimeout` reject policies for fail-fast vs bounded backpressure.

### Configuration
- **[Presets](HayateOP.Samples.Presets)** — build from a `HayatePoolPreset` (`Default`, `Lean`, `Full`,
  `HighThroughput`, `LowLatency`, `MemoryConstrained`, `ConnectionPool`, `BatchProcessing`,
  `Deterministic`), then override only what differs.

### Performance & leasing
- **[Lease](HayateOP.Samples.Lease)** — value-type `HayatePoolLease<T>` allocates nothing per borrow;
  idempotent return; `.Value` after dispose throws.
- **[Scoped](HayateOP.Samples.Scoped)** — class-based `HayatePoolScope<T>` for borrows that must live in a
  field, cross a method boundary, or span an `await`.
- **[Async](HayateOP.Samples.Async)** — `await using` leases, `Task.WhenAll` fan-out, cancellation, and the
  async acquire-timeout overload.
- **[Unbounded](HayateOP.Samples.Unbounded)** — `HayateUnboundedPool<T>` creates on demand and never blocks;
  only the resident idle set is capped (`MaxIdle`).

### Observability
- **[Metrics](HayateOP.Samples.Metrics)** — `HayateMetricsMeter` (System.Diagnostics.Metrics), allocation
  tracking, capacity alarms (`WithCapacityAlarm`), and `GetStats()`.
- **[Diagnostics](HayateOP.Samples.Diagnostics)** — `GetOptions()`, `PreWarm`, `ReloadConfig`, `TakeSnapshot()`,
  the availability/circuit-breaker toggle, and `Evict(reason)`.

### Integration
- **[NamedPools](HayateOP.Samples.NamedPools)** — register several independent pools of one type by name
  via DI (`AddNamedPool` / `IHayateNamedPoolAccessor`).
- **[Keyed](HayateOP.Samples.Keyed)** — `ParameterizedHayatePool<TKey,TValue>` gives every key a complete,
  isolated sub-pool (per-key capacity, eviction, breaker, scaling).
- **[ObjectPoolCompat](HayateOP.Samples.ObjectPoolCompat)** — swap `DefaultObjectPoolProvider` for
  `HayateObjectPoolCompatProvider` to back `Microsoft.Extensions.ObjectPool` with HayateOP; also the
  reverse adapter and .NET Framework 4.8 compatibility.
- **[Endpoints](HayateOP.Samples.Endpoints)** — ASP.NET Core registration, built-in management endpoints
  (`/hayateop/...`), and health checks.

---

## Conventions used across samples

- **Deterministic demos** use a single shard (`WithEnableSharding(false)` or `WithShardCount(1)`) plus
  `WithRejectPolicy(CreateOnDemand)` so returned objects are reused and nothing blocks — the default
  `BlockTimeout` policy waits for a return on a non-empty pool, which would otherwise deadlock a
  sequential borrow/return demonstration.
- **Interval minimums**: `WithValidateInterval`, `WithGenerationThreshold`, and `WithEvictionInterval`
  all require **≥ 1000 ms** (the builder throws otherwise).
- **Abort never cold-boots**: the `Abort` policy rejects an empty-pool borrow immediately rather than
  creating an object, so a demo that wants the *second* borrow to be rejected seeds the pool with
  `WithMinSize(1)`.
- Each sample is a small, focused program — read its `Program.cs` comments for the exact API calls and
  the `README.md` in the same folder for the capability narrative.
