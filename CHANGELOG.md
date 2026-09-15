# Changelog

All notable changes to Hayate Object Pool are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Breaking changes are described in full — with migration guidance — in
[`docs/BREAKING-CHANGES.md`](docs/BREAKING-CHANGES.md).

## [Unreleased]

### Added

- **Zero-allocation value builder** (Z1, from the sbpool performance assessment): `HayateValueStringBuilder`
  is a `ref struct` over a rented `ArrayPool<char>` buffer — doubling growth hands the old buffer
  straight back, `ToString()` materializes the content and returns the buffer in the same call
  (dispose-by-ToString, so the build's only allocation is the final string), `Dispose` is idempotent,
  and `TryCopyTo(Span<char>, out int)` / `AsSpan()` hand out the content without ending the borrow.
  Appends follow ZString's shape and return `void`: chained calls on a mutable ref struct would run
  on struct copies and lose their position state. Every member after the builder has ended throws
  `ObjectDisposedException`, so a use-after-return surfaces cleanly instead of reading a buffer the
  next renter owns. The type complements `StringBuilderPool` (transient format-and-return flows run
  here with no heap traffic; long-lived, observable builders stay on the pool) and does not touch the
  HayateOP engine. Available on every target framework: net6+ natively, net48 through `System.Memory`
  — the specialized package's first net48-only NuGet dependency (the core library is unaffected);
  a thread-static fast path stays a future enhancement.
- **Fast append surface** (Z4a): `PooledStringBuilder` and `HayateValueStringBuilder` gain generic
  `Append<T>(value, format)` plus full sets of named primitive overloads (`int`, `long`, `short`,
  `byte`, `uint`, `ulong`, `ushort`, `sbyte`, `double`, `float`, `decimal`, `DateTime`,
  `DateTimeOffset`, `TimeSpan`, `Guid`). On net6+ an `ISpanFormattable` value formats straight into
  the destination — no intermediate string, no boxing — through a struct-constrained helper whose
  per-type instantiations devirtualize `TryFormat`; the named overloads exist because the generic
  path box-frames its type check on value types (24 bytes per call), the same reason ZString and
  Cosmos ship full sets of named appends. On net48 the surface degrades to `IFormattable`, the same
  intermediate string the builders' own primitive appends cost there. The Z-C-A `Format`/`Concat`/
  `Join` helpers route through the same writer, so their argument writes gained the direct path too,
  and a null format now formats through `IFormattable.ToString(null)` (string.Format semantics)
  instead of dropping custom default formatting.
- **Declared-capacity borrows with tier routing** (Z-C-A, folding the 2.7 C-A convenience batch into
  the specialized pools with ZString semantics): `StringBuilderPool.GetObject(int minCapacity)` /
  `Acquire(int minCapacity)` (and the `MemoryStreamPool` equivalents) let the borrower declare how much
  the object must hold. Requests within the creation capacity are served by the base tier as before;
  anything larger routes to a lazily created internal capacity tier — one engine pool per bucket of the
  exponential ladder anchored at the minimum, `ArrayPool`-style — so repeated borrows of the same size
  reuse a parked builder instead of paying the grow ladder every time. A tier accepts its declared size
  back even when that exceeds `MaximumStringBuilderCapacity` / `MaximumMemoryStreamCapacity`: the buffer
  exists by declaration rather than by accidental growth, which is what stops a mixed-size workload
  churning through grow → destroy → rebuild. Only growth beyond the declared envelope (and the global
  maximum) destroys an object. `Clear`, `Dispose`, `Evict`, `CheckAvailable`, `SetAvailable` /
  `SetUnavailable`, `ReloadConfig` and the stats/snapshot aggregation cover the tier engines, raising
  the minimum capacity retires the tiers with the parked builders (P89OP setter parity), and a manual
  `Release` routes the object back to the tier it came from. Each tier retains at most `maxPoolSize`
  objects, the same budget as the base pool.
- **Seeded borrows fill robustly** (Z-C-A): `GetObject(string)` now clears the builder before appending
  the seed — CPL's append-only fill relied on every return path clearing, which breaks silently the day
  one path forgets — and a seed longer than the creation capacity borrows through a capacity tier, so
  the builder already fits the seed instead of growing to it. A failed fill disposes the builder back
  to the pool instead of leaking it.
- **Convert-and-return** (Z-C-A, CPL's `ToStringReturn` / `ToByteArrayReturn`):
  `PooledStringBuilder.ToStringReturn()` and `PooledMemoryStream.ToByteArrayReturn()` produce the
  result and end the borrow in one call. The return is atomic with the conversion — it runs even when
  the conversion throws, so a failing path can never leak the object — and stays exactly-once per
  borrow, like `Dispose`.
- **Generic no-`params` formatting helpers** (Z-C-A, ZString's signature surface):
  `StringBuilderPool.Format<T1..T8>` / `Concat<T2..T8>` / `Join<T>` (char and string separators over
  `IEnumerable<T>`) borrow one builder from the shared pool, write the arguments through their concrete
  types and return the finished string — no `object[]`, no boxing of the arguments, one borrow/return
  cycle whose return is atomic with the conversion. The format parser implements the common
  `string.Format` grammar (`{{`/`}}` escapes, `{index}`, `{index,alignment}`, `{index,alignment:spec}`)
  with `string.Format`-matching current-culture semantics and the same `FormatException` behaviour for
  malformed holes and out-of-range indexes; values with a format specifier format through
  `IFormattable`. Allocation benchmarks live in `tests/HayateOP.Benchmarks`:
  `SpecializedStringBuilderTierBenchmarks` quantifies the tiering benefit — on net10 a 300K-character
  build that the default borrow cannot retain costs 1,229,426 B per operation, and the declared borrow
  brings it to 614,843 B (the final string alone), halving both allocation and time — while
  `SpecializedStringBuilderFormatBenchmarks` separates the helpers' own writes (no `object[]`, no
  boxing) from the engine's fixed borrow-cycle bookkeeping floor.

## [2.7.0] - 2026-09-14

Usability and ecosystem release, no breaking API change. Highlights: nullable
reference type annotations across every `src` assembly (Q0), scoped borrows with
a one-call factory (S2), process-shutdown auto-dispose with a pluggable shutdown
signal (S3), per-object metadata on the wrapper and in snapshots (S4), native
keyed object pools (`ParameterizedHayatePool<TKey, TValue>`, O-A) and a
specialized pools extension package for `MemoryStream` / `StringBuilder` /
shared builders aligned with P89OP (O-B).

### Added

- **Nullable reference types enabled across all `src` assemblies** (Q0). `<Nullable>enable</Nullable>`
  is now set in `asset/props/target.feature.props`, and every nullable warning
  (CS8618 / CS8601 / CS8603 / CS8625 / CS8600 / CS8602 / CS8604 / CS8622 / CS8765 / CS8767) in the
  core pool and all seven extension packages has been resolved. The change is annotation-only: public
  API nullability was corrected with additive `?` / `!` annotations, so existing consumers recompile
  unchanged and no runtime behaviour differs (see `docs/BREAKING-CHANGES.md` §2.7). Builds are
  warning-free on net6.0–net10.0.

- **Scoped borrows** (S2): `pool.AcquireScoped()` (plus a `TimeSpan` overload) and
  `pool.AcquireScopeAsync(ct)` (plus a timeout overload) borrow an object together with a
  `HayatePoolScope<T>` lease that returns it when disposed, so `using` replaces the manual
  `try`/`finally` and the return cannot be forgotten — including when the body throws. Disposal is
  exactly-once and idempotent: a second `Dispose` is a no-op instead of a duplicate return, confirmed
  atomically so concurrent disposals collapse into one return. A failed borrow throws without producing
  a lease, so there is nothing left unreturned. Implemented as **extension methods** on
  `IHayateObjectPool<T>` — every existing implementation gets them without implementing anything, and
  no existing call site needs to change. The cost is one allocation per borrow.
- **One-call factory** (S2): `HayatePool.Simple<T>(poolSize, create, onGet)` builds a pool from a size
  and a factory lambda, with no options object and no policy class. Nothing is pre-created, and every
  other option keeps its documented default. It is deliberately placed on `HayatePool` rather than on
  `HayatePoolBuilder<T>`: the builder constrains `T : class, new()`, which would demand a parameterless
  constructor even when a factory supplies creation.
- **Process-shutdown auto-dispose** (S3): `HayatePoolOptions.EnableAutoDisposeWithSystem` (default
  `false`), plus the `HayatePoolBuilder.WithAutoDisposeWithSystem()` shortcut. A pool whose owner is the
  process never gets its `Dispose` call, so enabling this releases pooled objects — and the handles or
  connections they hold — on the way out. Nothing changes on the borrow or return path: the subscription
  is taken once at construction and removed on disposal, so a disposed pool never stays reachable from the
  process-wide hook, and disposal stays idempotent whichever route it took. `HayateProcessShutdownHook`
  subscribes to both `ProcessExit` and `Console.CancelKeyPress` (either can end a process), and each
  handler is shielded so one failing disposal cannot abandon the pools behind it.
- **Pluggable shutdown signal** (S3): `IHayateShutdownHook`, together with
  `HayatePoolBuilder.WithShutdownHook(hook)`, replaces the process-wide hook with any host signal — an
  application lifetime, a container, or a test double. Supplying a hook does not enable the feature on its
  own; it is combined with `WithAutoDisposeWithSystem()`.
- **Per-object metadata** (S4): `HayateObject<T>` now exposes `GetTimes` (cumulative borrow count, the
  same value as `LeaseCount` under the name used by the pool libraries this type is compared against),
  `LastGetThreadId` (managed thread id of the most recent borrow, `0` when never borrowed) and
  `CreateTime` (creation instant derived from `CreatedAtTick`). The borrow path records the thread id
  alongside the existing borrow timestamp and lease counter — one thread-id read and one field write
  per borrow, no allocation and no synchronization, and costing the lean fast path nothing because it
  stores the pooled value directly instead of a wrapper.
- **Snapshot carries the borrowing thread** (S4): `HayatePoolObjectDetail.LastGetThreadId` mirrors the
  wrapper field, so the per-object rows in `TakeSnapshot()` answer "which thread used this object last"
  — whether a pool is handing objects across threads, or one thread is quietly monopolising a shard.
  The id identifies the borrowing thread only, and the runtime recycles ids after a thread dies.
- **Keyed pooling** (O-A): `ParameterizedHayatePool<TKey, TValue>` gives every key an ordinary pool of its
  own, so objects are only ever reused by the key that created them and capacity, eviction, validation and
  the circuit breaker are all per key — one tenant exhausting its connections cannot slow the next.
  `GetObject(key)` / `GetObjectAsync(key)` borrow, `ReturnObject(key, value)` returns, and
  `GetPool(key)` hands back the sub-pool itself for its statistics, snapshot or eviction. Sub-pools are
  created on first use, registered in the pool registry under `Name[key]` so management endpoints and
  metrics can address a single key, and disposed with the keyed pool; `KeysInPoolCount` reports how many
  exist and `TryRemove(key)` retires one, which is how an open-ended key space stays bounded.
- **Two defaults a sub-pool cannot inherit** (O-A): the convenience constructor sizes shards to the per-key
  size (`min(default, maxSizePerKey)`), because sharding divides capacity across shards and a size of two
  split four ways leaves shards that can hold nothing; and it creates on demand
  (`RejectPolicy.CreateOnDemand`), because a sub-pool starts empty and holds nothing in reserve, so with the
  default wait-then-timeout policy every borrow past the first would stall until the background scaler
  caught up. Both are overridable through the `configure` callback, and the constructor that takes a
  sub-pool factory changes nothing at all.
- **Specialized pools extension package** (O-B): `DotNetCore.HayateOP.Extensions.Specialized` ships the
  ready-to-use `MemoryStream` / `StringBuilder` pools that generic engines leave to you, aligned with
  P89OP's `CodeProject.ObjectPool.Specialized` — `MemoryStreamPool` / `PooledMemoryStream`,
  `StringBuilderPool` / `PooledStringBuilder` and `SharedStringBuilder`, with the P89OP default capacity
  tiers (4KB/512KB bytes for streams, 4096/524288 characters for builders, default pool size 16) and the
  P89OP `Instance` singletons, `GetObject(string)` prefill, and clear-on-tighten capacity setters.
  Disposing a borrowed stream or builder returns it through the pool's ordinary return path, so `using`
  is the whole borrow/return pair and the return cannot be forgotten — including when the body throws.
  A returned stream stays open for the next borrower (only the pool really closes a buffer, when a
  returned object fails validation — most commonly one that grew past the maximum capacity); returned
  objects are reset (empty stream, cleared builder) before the next borrow; and disposal routes exactly
  one return, so a double dispose never double-parks an object. Each pool is an ordinary
  `IHayateObjectPool<T>` built on the core engine (create-on-demand, no sharding, no background
  features), so `AcquireAsync`, `GetStats` and the rest of the surface behave as usual. The package
  targets .NET Framework 4.8 and .NET 6/7/8/9/10.

### Changed

- Public API surface now carries accurate nullability annotations (non-breaking). See
  `docs/BREAKING-CHANGES.md` §2.7.

## [2.6.0] - 2026-09-12

Performance and ecosystem release, no breaking API change. Highlights: a lock-free
lean fast path that matches the Microsoft `DefaultObjectPool` reference on both
latency and allocations (`EnableLean`), wrapper re-allocation elimination, one-call
configuration profiles (`UseLeanProfile()` / `UseFullProfile()`), a create-on-demand
reject policy for the `Microsoft.Extensions.ObjectPool` compatibility layer, an
opt-in pool-level availability circuit breaker with automatic recovery, a merged
single-timer background scheduler, per-switch hot-path cost documentation, a
counter-gating contract, and a machine-readable performance baseline with a CI
regression gate.

### Added

- **`CreateOnDemand` reject policy**: a request that finds no idle object is served by creating one
  straight away while the pool still has room to grow, without waiting out the acquire timeout. An empty
  pool goes through the same synchronous first-object creation the block policies use, so the first
  borrow of a cold pool returns immediately; the timeout is only paid when the pool already holds
  `MaxPoolSize` objects and every one of them is lent out, where the request waits for a return and then
  creates one anyway rather than throwing. This matches the reference `DefaultObjectPool` contract of
  "create on a miss instead of blocking", with creation bounded by the configured capacity.
- **Lean (wrapper-free) fast path**: `HayatePoolOptions.EnableLean` — plus the
  `HayatePoolBuilder.WithLean()` shortcut — stores the pooled value directly in a bounded array and
  moves it through `Interlocked` compare-exchange, removing the per-object wrapper allocation, the
  per-shard free-list lock, the registry lookup on return and every diagnostic write from the
  borrow/return path. Pool sizing, pre-warming, the four reject policies, the object-policy hooks,
  `Clear`/`Dispose` and the async path all behave as usual.
  Lean is a *mode*, not a knob: enabling it switches sharding, auto-scaling, validation, eviction,
  generation optimization, leak detection, metrics, allocation tracking and the capacity alarm off
  during configuration normalization, whatever order the builder calls are made in. The normalized
  configuration is observable through `GetOptions()`.
  Trade-offs to be aware of: the lean path keeps no cumulative counters (`HayatePoolStats.TotalCreated`
  and friends report `0`), `Evict` throws `InvalidOperationException`, `ReloadConfig` rejects a
  `MaxPoolSize` change, and   `Release` trusts the caller because there is no registry to verify
  ownership against.
- **Wrapper recycling**: destroyed `HayateObject<T>` wrappers are parked on a bounded per-shard
  spare stack and reused by the next object creation, removing the per-wrapper allocation from
  create/destroy churn. No observable semantics change: pooled objects are still freshly created
  and counted as before (`TotalCreated` counts objects, not wrappers), a recycled wrapper carries
  pristine lease state (lease count, lease duration, generation and creation timestamps all reset),
  and a parked wrapper holds no reference to its destroyed pooled value. When a destroy's cleanup
  fails partway, the wrapper is not parked, so a stale registry entry can never survive onto a
  reused wrapper. The release-rejection path also drops a now-redundant registry removal.
- **Single background timer**: eviction, auto-scaling and idle validation are now driven by one timer
  instead of one timer per concern. The shared timer ticks at the smallest enabled period and each
  concern still fires on its own configured interval, so cadence is unchanged while the pool holds one
  timer handle instead of three. A pool whose three background features are all disabled — including
  the lean path, where they are normalized off — creates no timer at all and performs no periodic
  wake-ups. Overrunning ticks are skipped rather than overlapped, and `Dispose` releases the handle and
  drops the reference to it.
- **Configuration profiles**: `HayatePoolOptions.UseLeanProfile()` and `UseFullProfile()` — plus the
  `HayatePoolBuilder.WithLeanProfile()` / `WithFullProfile()` shortcuts — land a complete feature set in
  one call, removing the need to work through roughly forty options to express "minimal pooling" or
  "everything on". The lean profile writes out the state the lean mode normalizes to (lean on; sharding,
  auto-scaling, validation, eviction, generation optimization, leak detection, metrics, allocation
  tracking and the capacity alarm all off), and the full profile turns every optional feature switch back
  on. Neither profile touches pool sizing, timeouts or the reject policy, so a profile can be applied and
  then tuned in either call order. The profiles are order-deterministic against each other (the last one
  wins), and a later feature call still overrides a profile.
- **Pool-level availability circuit breaker**: `HayatePoolOptions.EnableCircuitBreaker` (default off) with
  `HayateCircuitBreakerOptions` (failure threshold 3, 30 s reset timeout, 5 s probe interval, optional
  health probe) and the `OnAvailable` / `OnUnavailable` callbacks. The application reports dependency
  failures with `pool.SetUnavailable(reason)`; the `FailureThreshold`-th consecutive report opens the
  breaker and every further `Acquire` / `AcquireAsync` throws the new `HayatePoolUnavailableException`
  (derived from `InvalidOperationException`, so existing catch blocks keep working) before doing any pool
  work, instead of handing out objects that are likely to be broken. `pool.CheckAvailable()` reports the
  state with a single volatile read. Recovery is automatic when a probe is configured — it runs on a timer
  that is created on the trip and disposed on the recovery, so an available pool holds no extra handle —
  or manual through `pool.SetAvailable()`, which is also how a pending failure streak is cleared. The
  feature is fixed at build time (the lean profile forces it off and `ReloadConfig` cannot toggle it),
  and the API surface (`CheckAvailable` / `SetUnavailable` / `SetAvailable`) aligns with
  SafeObjectPool's availability model.
- `CHANGELOG.md` and [`docs/BREAKING-CHANGES.md`](docs/BREAKING-CHANGES.md): release
  notes and migration guidance now live in dedicated documents, and the README links
  to them instead of duplicating them.
- GitHub Actions workflows: `ci.yml` (build + test on push and pull request),
  `publish-nuget.yml` (tag-triggered pack and push to nuget.org) and
  `perf-regression.yml` (BenchmarkDotNet comparison against the archived baseline).
- `netstandard2.0` assets for the core package, for hosts that can consume neither
  `net6.0`+ nor `net48`.

### Changed

- **`HayateObjectPoolCompatProvider` cold start no longer waits.** The provider now builds its pool with
  the `CreateOnDemand` policy instead of `CreateNew`, so a `Get` that finds no idle object creates one
  synchronously while the pool has room — including the very first `Get` of a cold pool, which used to
  pay up to `HayateCompatOptions.AcquireTimeout` (1s by default) before an object appeared. `AcquireTimeout`
  now bounds only the at-capacity request. A pool below capacity therefore serves concurrent misses by
  creating an object per request, exactly as `DefaultObjectPool` does, instead of making callers wait for
  a return.
- **All runtime text is now English.** Exception messages, OpenTelemetry metric
  descriptions and `HayatePoolStats.ToString()` section labels previously emitted
  Chinese text. Code that matches on message content — for example
  `Assert.Contains` in a test — may need to be updated.
- Documentation, XML doc comments, samples and CI-facing Markdown are English-only.
  Internal development phase identifiers (batch/step labels) have been removed from
  source comments, which previously leaked into IDE IntelliSense tooltips.
- Publishing to nuget.org moves from the local `scripts/Release.bat` to GitHub
  Actions; the batch script is retained as an offline fallback.

## [2.5.0] - 2026-09-10

Observability, registry/searchability, foundational benchmarks, and cross-target reach.
No breaking change beyond the lease-context rework listed below.

### Added

- **Capacity warnings**: `HayatePoolOptions.WarnAtRatio` / `CriticalAtRatio` and the
  `OnCapacityWarning` / `OnCriticalCapacity` callbacks. The callbacks fire once per state
  transition (debounced), not on every borrow/return.
- **Tolerant build entry point**: `HayatePoolBuilder.BuildOrThrow(bool)` — with `false`,
  pre-warm/creation failures degrade to a usable empty pool plus a log entry instead of throwing.
  The default `Build()` behaviour is unchanged.
- **Leak suspicion reporting with leak detection disabled**:
  `HayatePoolStats.LeakSuspectedCount`, reported by `TakeSnapshot()` for objects borrowed longer
  than the threshold while `EnableLeakDetection = false`. Forensic behaviour is unchanged.
- **Full registry surface**: `IHayateObjectPoolRegistry` gains `GetAll()`, `Remove()`,
  `Count` and `HayatePoolMetadata` (name, type, creation time). The management endpoints enumerate
  through the registry (the `Type.GetType` fallback is retained).
- **Shard affinity**: `ShardAffinityMode` (`None` default / `Thread` / `Custom`) with
  `WithShardAffinity` / `WithCustomShardAffinity`. `None` stays branch-free on the hot path.
- **Structured lease context**: immutable `HayateLeaseContext` (monotonic
  `LeaseId`, captured `StackFrame[]`, `BorrowedAt`) carried by an `AsyncLocal<HayateLeaseContext>`
  flow; new lifecycle fields `CreatedAtTick`, `LeaseCount`, `OwnerPoolName`; new
  `HayatePoolSnapshot.ObjectDetails` collection.
- **Categorised eviction**: `IHayateObjectPool<T>.Evict(HayateEvictReason)` for
  `Touched` / `Idle` / `Expired`; borrowed objects are never touched.
- **Per-pool logger factory**: `IHayateLoggerFactory` with
  `DefaultHayateLoggerFactory` / `DelegateHayateLoggerFactory` and `WithLoggerFactory`. Resolution
  order is explicit `WithLogger` > factory > built-in singleton (non-breaking).
- **BenchmarkDotNet orchestration**: `tests/HayateOP.Benchmarks` rebuilt as a standard
  BenchmarkDotNet project covering `Acquire` / `Release` / `AcquireAsync` against MEOP and plain
  `new`, with custom `P50/P90/P95/P99` columns and `[MemoryDiagnoser]` / `[ThreadingDiagnoser]`.
  Archived baseline: [`docs/benchmarks/`](docs/benchmarks).
- **Allocation tracking**: opt-in `EnableAllocationTracking` (default off) plus per-acquire /
  per-release allocation counters on `HayatePoolStats` and `HayatePoolSnapshot`.
- **Prometheus serializer**: zero-dependency Prometheus text exposition (0.0.4) with
  `AddHayatePrometheusExporter` / `UseHayatePrometheusExporter` (net8.0+ endpoint).
- **Warmup-ready signal**: `WaitForWarmup` (default `false`) gates `Acquire` on a
  `TaskCompletionSource` until pre-warming completes; pre-warm failures also release the gate.

### Changed

- **`netstandard2.0` assets**: the core package now also targets `netstandard2.0` for
  Unity / Xamarin / hosts that cannot consume `net6.0`+ or `net48`. Allocation tracking returns `0`
  and `Math.Clamp` falls back to a `Max`/`Min` equivalent on that target.
- `LastBorrowedAt` and `LastReleasedAt` are now recorded unconditionally on the borrow/release
  paths, so the suspected-leak and categorised-eviction semantics hold even with every feature
  switch disabled.

### Breaking

- `HayateObject<T>.AcquireTrace` (string) removed in favour of `HayateLeaseContext`. See
  [BREAKING-CHANGES](docs/BREAKING-CHANGES.md#25--structured-lease-context-and-lifecycle-fields).

## [2.4.0] - 2026-09-09

### Added

- **.NET Framework 4.8 support**: `net48` assets for the core, `ObjectPoolCompat` and
  DependencyInjection packages (classic ASP.NET 4.x / WPF / WinForms hosts).
- `AcquireAsync` overload accepting a timeout.

### Changed

- **Idle pools now actually scale down**: the internal scale-down gate that contradicted
  `ThresholdScalingStrategy` was removed. See
  [BREAKING-CHANGES](docs/BREAKING-CHANGES.md#24--idle-pools-actually-scale-down).

### Fixed

- Hardened thread-safety paths uncovered by the fuzz suite.

## [2.3.0] - 2026-09-09

### Changed

- **Timestamps are measured in `Stopwatch` ticks** instead of wall-clock time.
- Snapshot head sampling to reduce snapshot cost on large pools.

### Fixed

- Eviction and validation scheduling issues carried over from 2.2.

## [2.2.0] - 2026-09-09

### Added

- Metrics and eviction statistics persist through the configured callbacks.

### Changed

- **`Build()` fails fast on orphaned custom metrics**: registering `IHayateMetrics` while metrics
  collection is disabled now throws. See
  [BREAKING-CHANGES](docs/BREAKING-CHANGES.md#22--builder-fails-fast-on-orphaned-custom-metrics).

### Security

- Upgraded `Microsoft.OpenApi` to 2.7.6 (the ASP.NET Core Endpoints package).

## [2.1.0] - 2026-09-08

### Added

- Leak-forensics capture modes: `HayateLeakTraceCaptureMode` (`Off` / `Sampled` / `EveryAcquire`).
- Lock-free statistics counters and cached event names.
- Cold-boot acquire: a fully empty pool synchronously creates the first object instead of waiting
  for a return that may never arrive.

### Changed

- **Leak stack-trace capture decoupled from leak detection and off by default.** See
  [BREAKING-CHANGES](docs/BREAKING-CHANGES.md#21--leak-trace-capture).
- **`ApplyFeatureSwitches` capacity semantics**: with auto-scaling disabled the explicit
  `MaxPoolSize` is kept as the hard cap (previously forced to `MinPoolSize`).
- Custom metrics registered while metrics are disabled now fail fast (see 2.2 for the enforcement).

## [2.0-rc] - 2026-09-08

### Added

- Sharded architecture with atomic shard-local removal.
- Auto-scaling with aggressive scale-up / conservative scale-down.
- Object validation (borrow / return / idle), eviction policy, generation optimization,
  leak detection, metrics and snapshots.
- Reject policies: `Abort`, `Block`, `BlockTimeout`, `CreateNew`.
- Integration packages: DependencyInjection, Configuration, Diagnostics, Endpoints, HealthCheck,
  OpenTelemetry, ObjectPoolCompat.

## [1.0.0] - 2026-04-01

Initial release.

[Unreleased]: https://github.com/alexinea/object-pool/compare/v2.7...HEAD
[2.7.0]: https://github.com/alexinea/object-pool/compare/v2.6...v2.7
[2.6.0]: https://github.com/alexinea/object-pool/compare/v2.5...v2.6
[2.5.0]: https://github.com/alexinea/object-pool/compare/v2.4...v2.5
[2.4.0]: https://github.com/alexinea/object-pool/compare/v2.3...v2.4
[2.3.0]: https://github.com/alexinea/object-pool/compare/v2.2...v2.3
[2.2.0]: https://github.com/alexinea/object-pool/compare/v2.1...v2.2
[2.1.0]: https://github.com/alexinea/object-pool/compare/v2.0-rc...v2.1
[2.0-rc]: https://github.com/alexinea/object-pool/releases/tag/v2.0-rc
[1.0.0]: https://github.com/alexinea/object-pool/releases/tag/v1.0
