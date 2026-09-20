# Changelog

All notable changes to Hayate Object Pool are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Breaking changes are described in full — with migration guidance — in
[`docs/BREAKING-CHANGES.md`](docs/BREAKING-CHANGES.md).

## [Unreleased]

### Added

- **Deterministic preset** (O-G): `HayatePoolPreset.Deterministic` — the preset for hosts where nothing
  may run without the caller asking for it (AOT / IL2CPP runtimes and other deterministic
  environments). It is the lean fast path — no maintenance timer, no per-object bookkeeping, which
  Lean already guarantees structurally by disabling every concern the shared timer serves — plus the
  two switches Lean leaves open that could still move work off the calling thread, both closed:
  pre-warming stays synchronous with the constructor (`WaitForWarmup` off, so no background pre-warm
  task) and the pool never subscribes to process-exit events (`EnableAutoDisposeWithSystem` off, so
  `Dispose` happens only when the caller calls it). The built pool touches nothing except on an
  explicit `Acquire` / `Release`. The preset owns exactly its field set, so it composes like the other
  presets: sizing, timeouts and the reject policy are left alone, and every owned switch is
  overrulable by a later feature call — re-enabling eviction after the preset reintroduces a timer by
  choice rather than by surprise. The preset also pins the core package's AOT story: a single
  `netstandard2.0` assembly with no dependencies, a borrow/return path free of reflection and code
  generation — see the Unity / IL2CPP guidance in the README (empirical IL2CPP validation on a real
  Unity project remains backlog). Note that `Deterministic` and `Lean` therefore differ in two
  options, not in storage or speed; the preset exists to name the whole guarantee so a host audit can
  check one word.

- **Bucketed buffer pool** (O-H): the new optional package
  [`DotNetCore.HayateOP.Extensions.Buffers`](https://www.nuget.org/packages/DotNetCore.HayateOP.Extensions.Buffers)
  ships `HayateBufferPool<T>` — a bucketed array pool with round-up-to-nearest-size routing, the
  aligned counterpart of TinyPools' `MemoryPool<T>` + `SegmentDefinition` under HayateOP naming and
  validation conventions. The bucket ladder is declared up front
  (`new HayateBufferPool<byte>(new HayateSegmentDefinition(256, 4), …)`), a rent request routes to the
  smallest bucket whose size covers it and is handed back as a `PooledBuffer<T>` whose `Dispose` (or
  `Return`) parks the array in its home bucket, and a request above the largest declared size is
  rejected with `ArgumentException` rather than served by an undeclared bucket — the ladder is exactly
  what was configured. Each bucket creates arrays on demand and caps only what it *retains*: any
  number of buffers may be outstanding at once, and a return past the bucket's declared capacity is
  dropped for the garbage collector, so the pool bounds retained memory without limiting concurrency.
  Buckets guard their stacks with one lock each, so different sizes do not contend, and the pool runs
  no background workers. Returned arrays are not cleared — the same contract
  `ArrayPool<T>.Shared` has. This is deliberately an independent package rather than core surface:
  `System.Buffers.ArrayPool<T>` covers most workloads, and the value here is the explicit, inspectable
  configuration; it targets `net48` and `net6.0`–`net10.0` and has no dependencies. See the "Bucketed
  buffer pool" section of the README.

- **Soft capacity** (T-R): `HayatePoolOptions.SoftCapacity` (builder: `WithSoftCapacity(n)`) bounds the
  pool's *retained* set on the return path — once the pool already holds `SoftCapacity` idle objects, a
  returned object is disposed instead of stored, so a pool that lent out its whole hard ceiling during a
  burst drops the objects the burst no longer needs instead of keeping them until eviction or scale-down
  reclaims them. The default `0` keeps the previous behaviour of retaining up to `MaxPoolSize` idle
  objects, and the switch is additive: `MaxPoolSize` still caps what the pool ever lends out, so a pool
  with a soft capacity below its hard ceiling still serves the full ceiling — it only keeps fewer
  objects back. The check is a read of the idle count followed by the store, so two returns racing for
  the last slot may both be retained — it is a memory-shape control, not a mutual-exclusion guarantee —
  and the value must sit between `MinPoolSize` and `MaxPoolSize` (or be zero), because a ceiling below
  the floor would make the minimum unreachable and one above the hard ceiling could never fire; both
  are rejected by `IsValid` rather than accepted as a knob that silently does nothing. `ReloadConfig`
  rejects a change the same way, because the return path reads a construction-time snapshot (in lean
  mode the ceiling also fixes the fast path's slot-scan limit) and a runtime change could not take
  effect. The lean fast path honours the ceiling structurally — the fast lane plus
  `SoftCapacity - 1` slots — so it costs the return hot path nothing beyond the existing slot scan, and
  the unbounded pool model does not read it: that model's retained set is already bounded by its own
  `MaxIdle`, which drops a return once the queue is full. Dropped objects go through the regular
  destroy path (the policy's `OnDestroy` fires, the registry entry is removed) and the outcome is
  traced at debug level, not warned — a warning would fire once per return through a burst drain. See
  the "Soft capacity" section of the README and the return-path table in
  [`docs/hot-path-costs.md`](docs/hot-path-costs.md).

- **Shared pools** (O-C): `HayatePool.Shared<T>()` returns the process-wide pool for an element type —
  the first caller builds it, every caller after that gets the same instance — so a type has one pool
  instead of one per call site. It is the "just give me a pool" entry point beside
  `HayatePool.Simple<T>`, which builds a fresh pool on every call; the difference is ownership, not
  capability. A shared pool is an ordinary `IHayateObjectPool<T>` with the full configuration surface,
  created with the default settings and starting empty, so the first borrow pays the object-creation
  cost exactly once. Because creation happens once, only the call that creates the pool can configure
  it: the `configure` overload applies the callback while the pool is built, and a later call returns
  the existing instance and ignores its callback, rather than reconfiguring a pool that other callers
  are already using or making the shape depend on call order. `TryGet` is there for the caller that
  needs to know which of the two it is looking at, and `GetOrCreateNamed<T>(name, …)` — deliberately a
  separate method rather than an overload, so that a `null` name cannot bind to the delegate-taking
  member and quietly build the default pool — gives one element type several independently configured
  shared pools under the same logical names `HayateServiceKey` and the named DI registrations use.
  The catalog, `HayateSharedPoolRegistry`, is a thin layer over the existing
  `IHayateObjectPoolRegistry` rather than a second registry: it registers under the canonical name
  (`MyBuffer:shared`, which is also the pool name it logs and reports under), so `GetAll()` enumerates
  shared pools with their metadata and a catalog handed the application's registry makes them visible
  to the management endpoints, metrics and diagnostics beside the DI-created ones, and a pool that
  registry already holds is honoured instead of being shadowed. It is a normal class with a
  `Default` singleton on top, so a test or a subsystem can have a catalog of its own.
  Ownership is explicit and one-directional: the catalog owns the pools it creates, so `Remove<T>(…)`,
  `Clear()` and `Dispose()` dispose them — the opposite of `IHayateObjectPoolRegistry.Remove`, which
  only unregisters and leaves the lifetime to the registrar — while a pool the catalog did not create
  is never touched, which is what lets it share a registry with DI-managed pools. After `Dispose` the
  catalog reports empty and rejects further creation but keeps answering lookups, and creating is
  serialized, so simultaneous first calls for one key yield one pool rather than a race where the loser
  leaks a background-timer pool nobody can reach. A borrower must not dispose the pool it was handed:
  the catalog, not the caller, decides when a shared pool goes away. No existing signature, option or
  default changed; `Shared<T>()` is an addition to the one-call entry-point class, and nothing
  pre-existing routes through the catalog. See the "Shared pools" section of the README.

- **On-demand pre-warming** (O-F): `IHayateObjectPool<T>.PreWarm(int count)` tops the pool up to a
  requested number of idle objects and returns how many it created, so a process can pay the creation
  cost up front instead of letting the first borrowers pay it. The count is a floor on *idle* objects,
  not on total objects — objects currently lent out do not count towards it, so warming after a burst
  creates replacements rather than finding the pool already full. The pool's own ceiling still applies,
  the maximum pool size in the sharded engine and the retained-object limit in the unbounded model, and
  a request above the ceiling warms to the ceiling instead of failing. A negative count is rejected with
  `ArgumentOutOfRangeException`, and a creation failure is reported with `InvalidOperationException`
  rather than logged and swallowed: the construction-time warm-up carries on because the pool is still
  usable, but here the caller asked for the objects explicitly. The call is safe alongside concurrent
  borrows and returns, and concurrent calls never take the pool past its ceiling — a caller that finds
  the target already met creates nothing and returns `0`. This is the explicit counterpart of the
  construction-time warm-up switch rather than a replacement for it: construction warms the configured
  minimum, synchronously or in the background according to `WaitForWarmup`, while this call warms any
  target at any time — after a configuration reload raised the minimum, or to pre-pay creation without
  making the first borrow wait for it. Warming beyond the configured minimum is not a retention promise,
  so the extra objects age like every other idle object and are reclaimed by the idle timeout and by
  background scale-down; that the options themselves are left alone is checked rather than asserted, by
  a case that warms a pool and then verifies `MinPoolSize` and `MaxPoolSize` are exactly what they were.
  Every pool model answers the call. The sharded engine creates the shortfall round-robin and reads
  capacity per shard instead of deriving it from the configured maximum, so a pool that has already
  scaled down warms to what it currently allows and a shard that loses the last slot to a concurrent
  return has the object destroyed through the single destroy path rather than the live count pushed past
  the ceiling. The lean fast path creates through its reservation path, which enforces the ceiling on the
  live count. The unbounded model claims a slot and parks the object in the same claim-then-park order a
  return uses, which keeps its resident count and its idle queue in lockstep under concurrent borrows,
  returns and warm-ups; having no background warm-up of its own, it is the model where this call is the
  only way to make the first borrows cheap. The preparation decorator forwards to the inner pool, as it
  does for eviction, and the specialized pools warm their default-capacity base pool rather than the
  tiering layer. A warmed object is not a prepared one — warming creates and stops there, so every borrow
  still runs the preparation chain and the first borrows pay the readiness check and any repair exactly
  as before. No existing signature, option or default changed; `PreWarm` is an addition to the interface,
  alongside `Evict`. See the "Pre-warming the pool" section of the README.

- **Configuration presets** (C6): `HayatePoolPreset` names eight configurations for the shapes the pool
  is most often asked to take — `Default` (the shipped defaults), `Lean` (the wrapper-free fast path),
  `Full` (every feature switch on), `HighThroughput` (scale up early and in large steps, retain objects
  well past a lull, bookkeeping off), `LowLatency` (no background pass and no optional bookkeeping that
  can take a shard lock, first acquire waits for the warm floor), `MemoryConstrained` (one shard, zero
  idle floor, quick reclamation), `ConnectionPool` (bounded capacity, validate before a handle is handed
  out, scheduled recycling plus abandoned-handle reclaim, circuit breaker, metrics) and `BatchProcessing`
  (react every second, grow in steps of ten, release the burst capacity once the batch drains). All three
  entry points route through one catalogue — `HayatePoolPresets.Create(preset)`,
  `HayatePoolOptions.UsePreset(preset)` and `HayatePoolBuilder<T>.WithPreset(preset)` — and the builder
  also accepts a caller-supplied `HayatePoolOptions`, which is how a team keeps a named configuration of
  its own beside this one. A preset is a starting point rather than a lock: it assigns the options it owns
  and leaves every other option at the value it already had, so a preset composes with ordinary
  configuration, and any option it does own is overruled by a later `With*` call. That contract is checked
  rather than asserted — a case primes every writable option with a value other than the shipped default,
  applies each preset, and verifies that owned options follow the preset while unowned ones survive
  untouched. Sizing, timeouts and the reject policy are deliberately not owned, because the right ceiling
  depends on the machine and the workload; `MemoryConstrained` is the exception in that direction, where
  a zero idle floor *is* the trade-off, and it therefore also selects
  `HayatePoolRejectPolicy.CreateOnDemand`, since the block policies only shortcut creation while the pool
  tracks nothing and the next borrower would otherwise wait out the whole acquire timeout. `Default` is
  the exception in the other direction and owns the whole surface, sizing and callbacks included, so it
  doubles as an explicit reset to the shipped behaviour. An unrecognised preset value is rejected rather
  than ignored — the same fail-fast rule the borrow-order switch follows. No existing option, signature or
  default changed. See the "Configuration presets" section of the README.

- **Explicit borrow order** (O8): `HayateBorrowStrategy` selects which end of a shard's idle list a
  borrow is served from, and `HayatePoolBuilder<T>.WithBorrowStrategy(...)` installs it. `Fifo` — the
  default, and the order every release before the switch used — hands out the longest-idle object;
  `Lifo` hands out the one that came back most recently, which keeps a small working set hot in the CPU
  caches and is the order the reference CHOPIN pool hard-codes. The idle list itself is unchanged: it
  is still kept in return order, so its head is the oldest-returned end under both strategies, the
  eviction scan therefore keeps offering the coldest objects to the `IHayateEvictionPolicy<T>`, and
  abandoned-object recovery keeps walking its own list oldest-borrow-first. The switch governs the
  general-purpose engine; the lean fast path keeps no ordered idle list, so `Lifo` on a lean pool fails
  the build instead of being accepted and ignored — the outcome this item exists to remove. An
  unrecognised enum value falls back to `Fifo`, the same robustness rule the shard-affinity mode
  follows. On the borrow path the strategy costs one predicted branch on a constructor-time flag, so a
  pool that does not set it behaves and measures exactly as before. See the "Borrow order" section of
  the README.
- **Pluggable eviction rule** (O7, the counterpart of CHOPIN's `EvictionPolicyClassName`):
  `IHayateEvictionPolicy<T>` decides whether an idle object leaves the pool during the background
  eviction run and is installed with `HayatePoolBuilder<T>.WithEvictionPolicy(...)`. The policy is
  asked about every idle candidate and its verdict is what the run acts on — it **replaces** the
  built-in rule rather than extending it, so a policy that always returns `false` keeps every idle
  object. A pool that installs none asks `HayateDefaultEvictionPolicy<T>.Instance`, which is exactly
  the rule the run applied before the extension point existed (expired lifetime, exceeded idle time,
  or soft-min idleness above the shard's share of `MinPoolSize`), so existing behavior and the
  eviction log line are unchanged. A candidate carries everything a rule needs without reaching into
  the options — the idle instance itself, its `Age`, its `IdleTime`, its `LeaseCount`, the shard's
  `IdleCount` and its `MinIdleCount`, and the effective `MaxLifeTime` / `MaxIdleTime` /
  `SoftMinEvictableIdleTime` — which also keeps a policy correct across a configuration reload. Only
  idle objects are offered: a borrowed object never reaches a policy, and no verdict can destroy one
  under its borrower, because the shard re-checks ownership in its claim protocol before anything is
  removed; each shard contributes at most `NumTestsPerEvictionRun` candidates, taken from the head of
  its idle list. Installing a custom policy on a pool that never runs an eviction scan
  (`EnableEviction = false`, or the lean fast path, which disables idle eviction by construction)
  fails the build instead of leaving a rule that can never be consulted. Abandoned-object recovery
  (K2) and the explicit `Evict(reason)` API do not go through the policy. See the "Custom eviction
  policy" section of the README.
- **A `System.Diagnostics.Metrics` meter in the core package** (N6): the pool instrument set —
  `HayatePoolAcquire`, `HayatePoolRelease`, `HayatePoolMiss`, `HayatePoolScaled`, the
  `HayatePoolWaitTime` histogram and the `HayatePoolSize` / `HayatePoolAvailable` observable gauges —
  is now published by `HayateMetricsMeter` on a meter of the core package's own (name `"HayateOP"`),
  so a consumer that references nothing but `DotNetCore.HayateOP` can observe a pool with
  `dotnet-counters monitor --counters HayateOP`, or through the OpenTelemetry .NET SDK's
  `AddMeter("HayateOP")`, and needs no exporter package. The instrument names, units, tags,
  descriptions and the pull-based gauges live in that one implementation: the OpenTelemetry package's
  `HayateOtelMetrics` is now the meter-name and packaging front for it and keeps its
  `"DotNetCore.HayateOP"` meter name and its public tag constants (which now alias the core ones), so
  existing subscriptions and the instrument inventory are unchanged. A pool takes **one** sink — the
  core meter or the bridge meter — rather than both, since both publish the same instruments and
  wiring both would count every event twice. `Meter` is part of the .NET 6+ base class library, so
  the type is compiled out on netstandard2.0/net48 rather than pulling a NuGet dependency into the
  core package, and the gauges remain pull-based: their callbacks run on collection only, never on the
  borrow or return path. See the "OpenTelemetry metrics" section of the README.
- **Named pools, typed clients and a run-time pool factory** (N4, extending the 2.5 M11+ registry):
  several independently configured pools of one element type can now coexist and be addressed by
  name. `HayateServiceKey.Create<T>(name)` is the identity a named pool is addressed by — the element
  type plus a logical name, registered under the canonical name `{TypeName}:{name}` so logs,
  statistics, snapshots and the management endpoints tell the pools apart, while the unnamed pool of
  the same type keeps its bare type name. On the DI side `AddNamedPool<T>(name, configure)` declares
  one, `IHayateNamedPoolAccessor.GetPool<T>(name)` resolves it (and `GetPool<T>()` resolves the
  unnamed one), and `AddPool<T, TClient>(name)` binds a client class to a named pool, the way
  `AddHttpClient<TClient>()` does — the binding is decided at registration, so the client keeps a
  plain constructor taking `IHayateObjectPool<T>` and needs no attribute. The unnamed
  `IHayateObjectPool<T>` registration stays reserved for `RegisterHayatePool<T>()`, so "the pool of
  `T`" never silently resolves to one of several named pools. Pools are built on first use, and
  resolution never creates one on demand: an unknown name throws instead of silently starting an
  unconfigured pool. `RegisterNamedHayatePool<T>(configuration, name)` is the configuration-driven
  counterpart, merging `HayatePool:Global` with `HayatePool:Pools:{name}` exactly as
  `RegisterHayatePool<T>` does, hot reload included. Outside a container `HayatePoolFactory` builds
  named pools at run time — `GetOrCreate` returns the pool already registered for a key and only
  builds one when the key is new, which is what a multi-tenant or plugin host needs. Named pools are
  deliberately **not** built on Microsoft.Extensions.DependencyInjection keyed services: those only
  exist in version 8.0 and later of the abstractions package, so a keyed registration would be
  unavailable to the net6.0 / net7.0 targets this library ships, and would couple net48 behavior to
  the version of `Microsoft.Extensions.DependencyInjection` the application happens to resolve.
  Instead a named pool is one instance of `HayateNamedPoolRegistration<T>` in the container — the
  container's own "many registrations of one service type" facility, available on every supported
  target — and the observable behavior is identical from net48 through net10.0: one lazily built
  singleton per (type, name), owned and disposed by the container. Typed registry lookups
  (`TryGetPool<T>` / `GetPool<T>` / `GetRequiredPool<T>`) verify the element type rather than
  handing back a pool that cannot lend the requested type, and are extension methods so
  `IHayateObjectPoolRegistry` itself stays at its original size.
- **Diagnostics master switch** (O11, equivalent to P89OP's `Diagnostics.Enabled = false`): the new
  `HayatePoolOptions.EnableDiagnostics` (default `true`, settable through
  `HayatePoolBuilder<T>.WithEnableDiagnostics()`) is a **master gate over the whole diagnostic
  surface** — the cumulative counters, the timing statistics, the `IHayateMetrics` sink and the
  per-operation debug trace — rather than a peer of `EnableMetrics`. It is the one configuration that
  also stops `TotalAcquired`, the borrow-count contract that `EnableMetrics` deliberately leaves
  running (see `docs/metrics-gating.md`), so with diagnostics off a borrow performs no counter write,
  no metrics callback and no trace entry at all, and every cumulative counter and timing statistic
  reads 0. It is a master gate in the "mode wins" sense the options already use: switching it off
  normalizes `EnableMetrics` and `EnableAllocationTracking` off with it, and the collapsed result is
  visible through `GetOptions()`. The lean fast path forces it off (it keeps no bookkeeping by
  construction) and `UseFullProfile()` re-opens it explicitly. Lifecycle and problem logs
  (`Information` / `Warning` / `Error`) are untouched, so construction, disposal and failure reporting
  still reach the log; counters owned by another switch — leak detection, the capacity alarm — keep
  following their own switch. Registering a custom `IHayateMetrics` with diagnostics off fails the
  build, so a sink can never be silently discarded. The dependency-injection registration consults the
  master switch before attaching its custom sink, keeping the "a DI user is never fast-failed by a
  configuration they did not write" semantics. See the "Diagnostics" section of the README.
- **Abandoned-object recovery** (K2 → M4+, closing the CHOPIN `AbandonedConfig` gap): the M4 leak
  surface stays forensics-only, and borrowed objects that are never returned past
  `RemoveAbandonedTimeout` (default 300 s, aligned with CHOPIN) can now be reclaimed through
  `RemoveAbandonedOnBorrow` (a bounded, oldest-borrow-first scan on every borrow) and/or
  `RemoveAbandonedOnMaintenance` (the shared background timer at `RemoveAbandonedIntervalMs`,
  default 30 s). Reclamation is a destructive opt-in — both toggles default to `false`, so the
  default configuration behaves exactly like M4 (count in `LeakDetectedCount` /
  `LeakSuspectedCount`, never reclaim). A reclaimed object is destroyed under the shard claim
  protocol (no double-destroy on races), removed from the registry, counted in the new
  `HayatePoolStats.AbandonedRemovedCount` / `HayatePoolSnapshot.AbandonedRemovedCount`, and is not
  also reported as a leak. `LogAbandoned` logs a warning with the captured lease trace on reclaim;
  releasing an already-reclaimed object follows the pool's existing foreign-return path. Both
  toggles are forced off in lean mode.
- **ArrayPool direct-storage backend for the lean fast path** (O-D, folding the POP
  `PooledStack` storage shape into the O1/O2 design line): `WithArrayPoolStorage()` switches the
  lean buffer's slot array from a fixed `MaxPoolSize`-sized array to one rented from
  `ArrayPool<T>.Shared` that grows on demand (×2) up to `MaxPoolSize`. A large pool therefore only
  holds the storage its demand actually reached — the fixed array no longer sits in memory for the
  pool's whole lifetime — and the rented array returns to the shared pool on `Dispose`. Semantics
  are identical to fixed-buffer lean (same ceiling, same wrapper-free borrow/return, same reject
  policies, same stats/snapshot shape); the fast lane is shared. A logical slot limit keeps the
  retention ceiling exact even though `ArrayPool.Rent` returns bucket-aligned arrays that can be
  larger than requested. Available on net6.0+ (`ArrayPool<T>` is a BCL type there); on
  netstandard2.0/net48 the flag is ignored and lean keeps its fixed buffer, preserving the core
  package's no-dependency policy. The O2 allocation quantification for this backend lands in the
  benchmarks project (`Acquire+Release | Hayate ArrayPool (O-D)` row).
- **Asynchronous preparation / reconnect strategy** (N3, closing the marklauter connection-pool
  gap): `IHayatePreparationStrategy<T>` with `IsReadyAsync` / `PrepareAsync`, installed through
  `pool.WithPreparation(strategy, onDiscard, maxPrepareAttempts)` which wraps any
  `IHayateObjectPool<T>` — the bounded engine, the lean fast path, keyed sub-pools, or
  `HayateUnboundedPool<T>` — in a `HayatePreparationPool<T>`. Every borrow runs the ready-check;
  a not-ready object goes through the asynchronous prepare (the classic reconnect), so the caller
  receives a usable object or the reconnect error, never a silently broken one. A failed prepare
  hands the object to `onDiscard` (give it the object's real disposal) and retries with another
  borrow; when the budget (default 3) is exhausted the last failure propagates as
  `HayatePoolPreparationException`, and cancellation propagates with the object discarded. The
  synchronous `Acquire` runs the identical chain by blocking on it (documented sync-over-async
  caveat under a `SynchronizationContext`); everything else delegates to the inner pool. The six
  synchronous policy hooks are unchanged — the interface exists precisely because reconnecting a
  connection cannot be expressed in them.
- **Unbounded pool model** (N1, the second pool model, aligned with marklauter's `UnboundedPool`):
  `HayateUnboundedPool<T>` borrows ArrayPool-style — it never blocks, never waits and never rejects;
  when the idle queue is empty the next borrow creates a new object. The lease is ownership:
  returning is optional, and a borrowed object that is never returned is left to the garbage
  collector (the pool keeps no per-object bookkeeping, so a forgotten return cannot leak or block
  anyone). The only knob is `maxIdle` (default 32): a return that arrives while that many objects
  are parked destroys the object instead, bounding the resident set by policy rather than demand.
  The pool is a full `IHayateObjectPool<T>` — scoped borrows, `GetStats`/`TakeSnapshot` and the DI
  surface work unchanged — implements the same reset/validate-on-return hooks as the bounded engine
  (a failed validation destroys the object), ignores timeouts on the synchronous overload and
  completes `AcquireAsync` synchronously, and reports the new `HayatePoolStats.TotalDestroyed`
  counter for returns rejected past `MaxIdle` and destroy sweeps. There is no `MaxSize`, no
  rejection policy and no background machinery; `HayatePoolStats.TotalDestroyed` is 0 on the
  bounded engine. See the "Unbounded pool" section of the README for the model choice between the
  two.

### Changed

- **Asynchronous preparation borrows are actually asynchronous** (A4, behavioral fix of N3): the
  borrows of `HayatePreparationPool<T>` did not await anything — `AcquireAsync` ran the inner pool's
  *synchronous* `Acquire()` inside the asynchronous method, so a borrow that had to wait occupied a
  thread for the whole of the inner acquire timeout (5 s by default) and the `timeout` argument of
  `AcquireAsync(TimeSpan, …)` — and of the synchronous `Acquire(TimeSpan)` — was silently dropped.
  The chain now awaits the inner pool's own asynchronous borrow and forwards the timeout to it, so an
  exhausted pool suspends the borrowing context instead of a thread, and a borrow converges on the
  timeout it was given (surfacing the inner pool's `TimeoutException`). No public signature changed:
  the synchronous `Acquire`/`Acquire(TimeSpan)` still block their caller by design — they now block on
  a borrow that honours the timeout. The lean, bounded, keyed and unbounded inner pools all gain the
  behavior, since the fix lives in the wrapper.
- **Performance gate promotion machinery** (Q1): `scripts/bench-compare.py` now implements the
  allocation gate that the threshold table always documented — allocation growth `>= allocFailBytes`
  fails and `>= allocWarnBytes` warns (previously both keys were parsed but never referenced) — and
  supports per-target `warnMeanPercentOverride` / `failMeanPercentOverride`. The PG4 convergence
  outcome applies them to the sub-100 ns lean rows (warn 30 / fail 60), which isolated local reruns
  measured across a ~1.9× range; the >=200 ns rows keep the global 15/30. `--emit-baseline-file`
  writes a complete, committable baseline (schema, thresholds, capture metadata, percentiles,
  `calibration: false`), and the workflow's `update_baseline` input produces it as the `ci-baseline`
  artifact. `perf-regression.yml` no longer carries a static `continue-on-error`: it reads the
  baseline's `calibration` flag and routes to a blocking or an advisory compare step accordingly, so
  committing the CI-native baseline hardens the gate in the same commit — the runbook lives in
  `docs/benchmarks/baseline/README.md`. Until that capture lands, the committed baseline stays in
  calibration mode and the gate stays advisory **by design, not by omission**.
- **Multi-TFM XML documentation fix** (root-cause fix folded into Q1): the canonical
  `$(AssemblyName).xml` is now produced by the highest target framework only; lower-TFM builds write
  their documentation into `obj/$(TargetFramework)/`, where it is packed as that TFM's own asset.
  Every `lib/<tfm>/*.xml` in the package now documents exactly that TFM's API surface (previously
  every TFM's asset carried whatever TFM built last — a net48 asset could ship documentation for
  net6+-only members it does not have, and a stray low-TFM build could strip higher-TFM members from
  the canonical file, which 2.6/2.7 worked around by hand-restoring the XML). The canonical file
  survives forced single-TFM rebuilds untouched, verified.

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
- **Generic direct-write Build overloads** (Z6): `SharedStringBuilder.Build<T1..T8>(format, args)`
  formats against the shared builder with `string.Format` semantics but no `object[]` and no argument
  boxing — the values travel as their concrete types through the same direct-write formatting core the
  pool helpers use. The lock discipline is unchanged: the write and the snapshot happen under the
  lock, the builder is cleared before the lock is released, and a failing format releases the lock.
  The parsing core itself moved to a shared internal class, so the pool and shared surfaces parse
  identical grammar.
- **Span output surface** (Z5): `PooledStringBuilder.TryCopyTo(Span<char>, out int)` hands the content
  out as chars without materializing a string — on net6+ the copy walks the builder's chunk chain
  straight into the span, on net48 it degrades through one intermediate `ToString`, never worse than
  the string it replaces. `WriteTo(Stream)` (net6+) streams the content as UTF-8 through a rented
  scratch buffer with a persistent encoder, so a stream consumer skips the final string without a
  whole-content allocation and a surrogate pair split across a chunk boundary stays intact. Neither
  ends the borrow; both snapshot the content as it stands when called. The Z1 value builder already
  carried `TryCopyTo` / `AsSpan` from the start.
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

### Fixed

- **The ArrayPool storage switch now survives an option copy** (follow-up to O-D):
  `HayatePoolOptions.CopyTo` never carried `EnableArrayPoolStorage`, so every copy-based surface lost the
  storage shape without saying so. `GetOptions()` reported a fixed-buffer lean pool for a pool that runs
  on the rented-slot backend, and the configuration binding and the DI registration dropped a bound
  `EnableArrayPoolStorage` between the bound options and the builder — the setting was accepted, reported
  as off, and never applied. The switch now travels with `EnableLean`, which is the execution mode it
  belongs to. A reflection-driven copy-fidelity case writes a value other than the shipped default into
  **every** writable option and asserts the copy carries it, so an option declared without its `CopyTo`
  line fails the suite instead of reaching a release; reverting the fix fails three of the four new cases
  while the circuit-breaker non-aliasing case keeps passing.

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
