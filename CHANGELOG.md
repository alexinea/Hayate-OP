# Changelog

All notable changes to Hayate Object Pool are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Breaking changes are described in full — with migration guidance — in
[`docs/BREAKING-CHANGES.md`](docs/BREAKING-CHANGES.md).

## [Unreleased]

## [2.7.0] - Unreleased

### Added

- **Nullable reference types enabled across all `src` assemblies** (Q0). `<Nullable>enable</Nullable>`
  is now set in `asset/props/target.feature.props`, and every nullable warning
  (CS8618 / CS8601 / CS8603 / CS8625 / CS8600 / CS8602 / CS8604 / CS8622 / CS8765 / CS8767) in the
  core pool and all seven extension packages has been resolved. The change is annotation-only: public
  API nullability was corrected with additive `?` / `!` annotations, so existing consumers recompile
  unchanged and no runtime behaviour differs (see `docs/BREAKING-CHANGES.md` §2.7). Builds are
  warning-free on net6.0–net10.0.

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

[Unreleased]: https://github.com/alexinea/object-pool/compare/v2.5...HEAD
[2.5.0]: https://github.com/alexinea/object-pool/compare/v2.4...v2.5
[2.4.0]: https://github.com/alexinea/object-pool/compare/v2.3...v2.4
[2.3.0]: https://github.com/alexinea/object-pool/compare/v2.2...v2.3
[2.2.0]: https://github.com/alexinea/object-pool/compare/v2.1...v2.2
[2.1.0]: https://github.com/alexinea/object-pool/compare/v2.0-rc...v2.1
[2.0-rc]: https://github.com/alexinea/object-pool/releases/tag/v2.0-rc
[1.0.0]: https://github.com/alexinea/object-pool/releases/tag/v1.0
