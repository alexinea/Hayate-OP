# Changelog

All notable changes to Hayate Object Pool are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Breaking changes are described in full — with migration guidance — in
[`docs/BREAKING-CHANGES.md`](docs/BREAKING-CHANGES.md).

## [Unreleased]

Planned for 2.6: performance optimization (lean lock-free fast path, wrapper
de-allocation, `ArrayPool`-backed storage backend) and ecosystem capability
fill-ins (keyed pool, circuit breaker, unbounded pool, specialized pool package).
See the release planning notes in the project workspace for the full breakdown.

### Added

- `CHANGELOG.md` and [`docs/BREAKING-CHANGES.md`](docs/BREAKING-CHANGES.md): release
  notes and migration guidance now live in dedicated documents, and the README links
  to them instead of duplicating them.
- GitHub Actions workflows: `ci.yml` (build + test on push and pull request),
  `publish-nuget.yml` (tag-triggered pack and push to nuget.org) and
  `perf-regression.yml` (BenchmarkDotNet comparison against the archived baseline).
- `netstandard2.0` assets for the core package, for hosts that can consume neither
  `net6.0`+ nor `net48`.

### Changed

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
