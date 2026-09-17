# HayateOP — N5: marklauter `MSL.Pool` head-to-head column (2026-09-18)

> Purpose: extend the M1 head-to-head matrix with the marklauter reference line (plan item **N5**,
> "head-to-head 基准 marklauter 线（扩展 M1，追加 `MSL.Pool` 对照列）"). Until this change the
> HayateOP-vs-marklauter comparison carried an explicit caveat that **no direct head-to-head benchmark
> existed** between the two libraries and that its performance conclusions were derived from
> architectural differences only. This report closes that gap with measured data taken from the shared
> matrix, under one capacity basis, one warm-up and one payload.

## 1. What was added

Three columns, all driven by `MSL.Pool` **7.2.1** (net10.0-only; MIT; referenced by the benchmarks
project only — it is not a dependency of any shipped package):

| Row (BDN description) | Suite | What it measures |
| :--- | :--- | :--- |
| `Release | MSL.Pool` | Suite 2 — release path | `LeaseAsync()` then `Release()` — return and immediate re-lease, the borrow set held constant |
| `AcquireAsync+Release | MSL.Pool (CT)` | Suite 3 — asynchronous path | the token-carrying `LeaseAsync(CancellationToken)` overload, a payload write, then `Release()` |
| `Concurrent-100 | MSL.Pool` | Suite 4 — contention | 100-thread `Parallel.For` lease/release round trip |

`MSL.Pool` cannot appear in Suite 1 (single-threaded synchronous `Acquire+Release`): the library
exposes **no synchronous acquire path** — items are leased exclusively through `LeaseAsync`. Suite 1
is therefore N/A for this line, mirroring how MEOP and plain `new` are N/A in the asynchronous suite.
Both facts are recorded in the benchmark file's coverage-matrix header.

### 1.1 Fairness provisions

The row is built to differ from the HayateOP rows in **one** dimension only — the pool implementation:

| Dimension | HayateOP rows | `MSL.Pool` row |
| :--- | :--- | :--- |
| Capacity basis | `MinPoolSize = 250`, `MaxPoolSize = 300` | `PoolOptions.MinSize = 250`, `MaxSize = 300` |
| Warm-up | full borrow/return cycle up to `MaxPoolSize` | identical loop in `GlobalSetup` |
| Payload | `PooledObject` (one `int` property) | the same class, produced by an `IItemFactory<T>` of the same shape |
| Optional features | `AllOff` = every toggle off | no `IPreparationStrategy` supplied, so the lease route is the plain "take an idle item, or create one while below `MaxSize`" path |
| Timeouts / eviction | eviction, auto-scaling and validation off | `LeaseTimeout` and `IdleTimeout` set explicitly to `Timeout.InfiniteTimeSpan` (which is also the library default: no lease timeout, no lazy idle eviction) |
| Metrics | `WithEnableMetrics(false)`, or lean storage | a no-op `IPoolMetrics` sink (the real implementation publishes `System.Diagnostics.Metrics` instruments) |
| Construction | `HayatePoolBuilder<T>` | the public `Pool<T>` constructor; the DI extension methods add nothing to the measured path |

Two properties of this comparison are worth stating explicitly, because they bound what the numbers
can be used to claim:

1. **The asynchronous Suite 3 row is the apples-to-apples one.** `MSL.Pool` has no synchronous lease,
   so its Suite 2 row measures an asynchronous lease against HayateOP's synchronous release path.
2. **The infinite timeouts are set explicitly although they equal the defaults**, so that a future
   default change in `MSL.Pool` cannot silently introduce a lease timeout or idle eviction into what
   this row measures.

### 1.2 Steady state verified before trusting the allocation column

An allocation figure of 440 B per round trip is large enough that it is worth ruling out the harness
before reporting it. A standalone probe (same payload, same `MinSize`/`MaxSize`, same warm-up, same
no-op metrics sink) confirms the pool stays warm and that the allocation belongs to the library:

| Probe step | `ItemsAllocated` | `ItemsAvailable` | `ActiveLeases` | Allocation |
| :--- | ---: | ---: | ---: | ---: |
| after warm-up | 250 | 250 | 0 | – |
| after 20,000 lease+release round trips | 250 | 250 | 0 | 440.0 B/op |
| 300 leases held | 250 | 0 | 250 | 364.0 B/op |
| 300 releases | 250 | 250 | 0 | 135.5 B/op |

The counters are unchanged before and after 20,000 round trips, so **the create path never runs
during the measurement** and the per-operation allocation is entirely the library's lease/release
machinery (roughly 364 B on the lease side and 136 B on the release side) — not pooled-object
creation and not an artefact of this harness.

## 2. Runtime environment and configuration

| Item | Value |
| :--- | :--- |
| BenchmarkDotNet | 0.15.8 |
| Runtime | .NET 10.0.11 (SDK 10.0.400), X64 RyuJIT x86-64-v3 |
| Hardware | 12th Gen Intel Core i7-1260P 2.10GHz, 1 CPU / 16 logical cores (12 physical cores) |
| OS | Windows 11 (10.0.28120.3002) |
| Job | `Short`: WarmupCount=3, IterationCount=10, Server GC + Concurrent GC |
| Diagnosers | `MemoryDiagnoser` + `ThreadingDiagnoser` |
| Percentile columns | custom `P50/P90/P95/P99` (per-iteration mean time) |
| Capacity basis | Min=250 / Max=300, warmed up to `MaxPoolSize` |
| Matrix filter | `--filter '*HayateOpBenchmarks*'` (the head-to-head matrix only) |
| Command | `HayateOP.Benchmarks.exe --filter '*HayateOpBenchmarks*' --buildTimeout 900 --artifacts _n5_artifacts` |

> **On `--buildTimeout 900`**: a local-run workaround only, and not part of the committed benchmark
> definition. On the capture machine MSBuild node reuse and the shared compiler are disabled (they
> otherwise hold handles on the git-tracked generated XML documentation files), which pushes the
> auto-generated boilerplate build past BenchmarkDotNet's default 120 s build timeout. CI and a
> normal developer machine need no such flag and the runbook commands stay unchanged.

> **Absolute values are machine- and run-specific.** In this run `Acquire+Release | Hayate Lean`
> measures 58.4 ns where the 2026-09-10 local baseline recorded 27.1 ns for the same row, which is the
> documented run-to-run spread of the sub-100 ns rows (see
> [`2026-09-10-o1-lean.md`](2026-09-10-o1-lean.md)). The committed machine-readable baseline is
> therefore deliberately **not** regenerated from this run; only ratios measured *within this single
> run* are meaningful, and none of these numbers should be compared against `baseline.json`.

## 3. Results (BDN MarkdownExporter output)

Two rendering notes on the table below: the `|` inside the benchmark descriptions is escaped as `\|`
so the table parses (BDN 0.15.8 does not escape it itself), and BDN's Markdown exporter prints a zero
allocation as `-` where its CSV exporter writes `0 B`. Every mean, standard deviation, percentile and
median value below matches [`2026-09-18-n5-marklauter-line.csv`](2026-09-18-n5-marklauter-line.csv)
verbatim.

```text

BenchmarkDotNet v0.15.8, Windows 11 (10.0.28120.3002)
12th Gen Intel Core i7-1260P 2.10GHz, 1 CPU, 16 logical and 12 physical cores
.NET SDK 10.0.400
  [Host] : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3
  Short  : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3

Job=Short  Concurrent=True  Server=True  
IterationCount=10  WarmupCount=3  

```

| Method | Mean | Error | StdDev | P50 | P90 | P95 | P99 | Median | Ratio | RatioSD | Rank | Completed Work Items | Lock Contentions | Gen0 | Gen1 | Gen2 | Allocated | Alloc Ratio |
|------------------------------------- |-----------------:|------------------:|----------------:|------------:|------------:|------------:|------------:|-----------------:|----------:|----------:|-----:|---------------------:|-----------------:|-------:|-------:|-------:|----------:|------------:|
| &#39;Concurrent-100 \| MEOP&#39; | 25,815.913 ns | 775.475 ns | 461.4729 ns | 25,823.7 | 26,479.3 | 26,479.3 | 26,479.3 | 25,823.657 ns | 699.26 | 29.49 | 11 | 12.0865 | 0.0001 | 0.0610 | - | - | 4327 B | NA |
| &#39;Concurrent-100 \| Hayate AllOff&#39; | 631,216.719 ns | 720,607.591 ns | 476,637.3042 ns | 408,207.4 | 991,654.7 | 1,686,335.5 | 1,686,335.5 | 441,358.984 ns | 17,097.47 | 12,343.24 | 13 | 26.9492 | 0.6914 | - | - | - | 45450 B | NA |
| &#39;Concurrent-100 \| Hayate Lean&#39; | 20,791.103 ns | 1,170.604 ns | 774.2820 ns | 20,757.6 | 21,512.5 | 21,938.3 | 21,938.3 | 20,889.828 ns | 563.16 | 29.55 | 10 | 10.8309 | - | 0.0610 | - | - | 4138 B | NA |
| &#39;Concurrent-100 \| Hayate Sharded4&#39; | 758,932.080 ns | 319,117.582 ns | 166,904.6660 ns | 725,246.7 | 927,520.5 | 927,520.5 | 927,520.5 | 807,507.227 ns | 20,556.83 | 4,335.07 | 14 | 18.4492 | 0.0078 | - | - | - | 44115 B | NA |
| &#39;Concurrent-100 \| MSL.Pool&#39; | 165,228.706 ns | 10,900.169 ns | 7,209.7871 ns | 161,332.0 | 175,668.0 | 175,721.4 | 175,721.4 | 163,620.715 ns | 4,475.47 | 254.12 | 12 | 13.5874 | 4.3425 | 1.2207 | - | - | 48506 B | NA |
| &#39;Acquire+Release \| Hayate AllOff&#39; | 416.473 ns | 103.050 ns | 68.1613 ns | 410.5 | 497.0 | 521.7 | 521.7 | 414.431 ns | 11.28 | 1.82 | 6 | - | - | 0.0100 | - | - | 384 B | NA |
| &#39;Acquire+Release \| Hayate Lean&#39; | 58.404 ns | 4.537 ns | 3.0008 ns | 58.0 | 61.4 | 62.7 | 62.7 | 58.724 ns | 1.58 | 0.10 | 4 | - | - | - | - | - | - | NA |
| &#39;Acquire+Release \| Hayate ArrayPool (O-D)&#39; | 57.541 ns | 5.033 ns | 3.3292 ns | 57.2 | 60.5 | 63.7 | 63.7 | 57.410 ns | 1.56 | 0.11 | 4 | - | - | - | - | - | - | NA |
| &#39;Acquire+Release \| Hayate Sharded4&#39; | 386.404 ns | 33.538 ns | 19.9580 ns | 389.2 | 421.0 | 421.0 | 421.0 | 389.194 ns | 10.47 | 0.65 | 6 | - | - | 0.0100 | - | - | 384 B | NA |
| &#39;Acquire+Release \| Hayate Full&#39; | 448.208 ns | 65.750 ns | 43.4896 ns | 426.2 | 502.3 | 519.9 | 519.9 | 432.816 ns | 12.14 | 1.22 | 6 | 0.0000 | - | 0.0110 | - | - | 416 B | NA |
| &#39;Release \| Hayate AllOff&#39; | 326.536 ns | 42.424 ns | 28.0610 ns | 312.6 | 365.4 | 367.3 | 367.3 | 323.858 ns | 8.84 | 0.80 | 6 | - | - | 0.0110 | 0.0005 | 0.0005 | - | NA |
| &#39;Release \| Hayate Lean&#39; | 32.092 ns | 1.941 ns | 1.1548 ns | 32.3 | 33.9 | 33.9 | 33.9 | 32.284 ns | 0.87 | 0.04 | 3 | - | - | - | - | - | - | NA |
| &#39;AcquireAsync+Release \| Hayate AllOff&#39; | 388.641 ns | 78.084 ns | 51.6480 ns | 401.7 | 442.1 | 459.7 | 459.7 | 405.891 ns | 10.53 | 1.40 | 6 | - | - | 0.0110 | 0.0005 | 0.0005 | - | NA |
| &#39;AcquireAsync+Release \| Hayate Lean&#39; | 149.333 ns | 19.952 ns | 13.1970 ns | 154.7 | 159.5 | 162.1 | 162.1 | 155.585 ns | 4.04 | 0.38 | 5 | - | - | 0.0038 | - | - | 144 B | NA |
| &#39;AcquireAsync+Release \| Hayate Full&#39; | 567.508 ns | 13.996 ns | 9.2572 ns | 568.8 | 576.3 | 582.8 | 582.8 | 569.191 ns | 15.37 | 0.64 | 7 | - | - | 0.0105 | - | - | 392 B | NA |
| &#39;Acquire+Release \| MEOP (baseline)&#39; | 36.976 ns | 2.643 ns | 1.5727 ns | 36.7 | 40.4 | 40.4 | 40.4 | 36.729 ns | 1.00 | 0.06 | 3 | - | - | - | - | - | - | NA |
| &#39;Acquire+Release \| plain new (no-pool lower bound)&#39; | 7.187 ns | 1.373 ns | 0.9082 ns | 6.7 | 8.0 | 9.0 | 9.0 | 6.925 ns | 0.19 | 0.02 | 1 | - | - | 0.0008 | - | - | 24 B | NA |
| &#39;Release \| MEOP Return&#39; | 25.822 ns | 1.599 ns | 1.0579 ns | 25.6 | 26.9 | 27.7 | 27.7 | 25.652 ns | 0.70 | 0.04 | 2 | - | - | - | - | - | - | NA |
| &#39;Release \| MSL.Pool&#39; | 1,330.975 ns | 277.738 ns | 183.7064 ns | 1,285.4 | 1,538.8 | 1,622.2 | 1,622.2 | 1,323.424 ns | 36.05 | 4.95 | 8 | 0.0000 | - | 0.0114 | - | - | 440 B | NA |
| &#39;AcquireAsync+Release \| MSL.Pool (CT)&#39; | 1,606.045 ns | 86.647 ns | 57.3118 ns | 1,609.3 | 1,675.5 | 1,686.4 | 1,686.4 | 1,611.515 ns | 43.50 | 2.24 | 9 | - | - | 0.0114 | - | - | 440 B | NA |

## 4. Reading the marklauter rows

Measured in this run, with every other row measured in the same process:

| Observation | Value |
| :--- | :--- |
| `LeaseAsync` + `Release`, single-threaded | 1.331 µs, 440 B, no lock contentions recorded |
| `LeaseAsync(CT)` + payload write + `Release` | 1.606 µs, 440 B — the token overload costs ~275 ns over the token-less one |
| 100-thread contention, per 100-op iteration | 165.2 µs (≈1.65 µs/op), 48.5 KB/iteration, 4.34 lock contentions per iteration |
| Ratio against the same-run MEOP baseline row | ≈36× (release suite) / ≈43× (async suite) |
| Ratio against the same-run HayateOP lean fast path | ≈23× (`Release`), ≈11× (asynchronous suite) |

Interpretation, kept separate from the measurements above:

1. **The lease path is not allocation-free, by design.** `MSL.Pool` is a connection-pool-class
   library: it runs an asynchronous readiness/preparation check before reuse, tracks each lease so
   that a lease which is garbage-collected without being released can be reported as leaked
   (`pool.leases.leaked`), and publishes `System.Diagnostics.Metrics` instruments. Per-lease
   bookkeeping of that kind is the most likely source of the 364 B/op measured on the lease side; the
   exact internal cause was not verified against the library source and is not claimed here.
2. **This is not a like-for-like workload comparison.** HayateOP's fast paths are built for
   high-frequency pooling of small, stateless objects, and the lean path is explicitly the
   wrapper-free variant of that. `MSL.Pool` targets expensive-to-create instances (SMTP connections,
   database connections, sockets) where a ~1 µs lease is irrelevant next to the cost of the resource
   it guards. The columns answer "what does each library's public lease/release path cost under an
   identical capacity basis, warm-up and payload" — not "which library is better".
3. **Under contention the async-only design shows up.** The concurrent row's 4.34 lock contentions
   per iteration and its allocation volume are consistent with a queue-plus-semaphore core guarding
   an asynchronous lease, where the comparable HayateOP lean row records none.
4. **The two Suite 2 rows are not directly comparable.** `Release | MSL.Pool` awaits an asynchronous
   lease where `Release | Hayate Lean` calls a synchronous one; the Suite 3 pair (async against
   async) is the like-for-like reading, and it is the pair the N5 acceptance refers to.

## 5. Gate impact

- The three new rows are **external-reference rows**. `scripts/bench-compare.py` gates only entries
  whose description contains `Hayate` and is not a `Concurrent-100` row, so none of them can
  influence pass/fail; they are reported for context exactly like the MEOP and plain-`new` rows.
- `docs/benchmarks/baseline/baseline.json` is **intentionally untouched**. The Q1 runbook requires a
  CI-native capture, and committing a local capture as the gate baseline is precisely the
  cross-environment comparison the calibration flag guards against.
- Consequence to be aware of: the CI workflow runs the `hot` category only
  (`--anyCategories hot`), while these rows carry `reference` / `concurrent` — consistent with the
  existing reference rows. They therefore appear in local full-matrix runs and in this report, but
  not in the CI hot-only run. If continuous CI visibility of the marklauter column is wanted, either
  the two single-threaded rows can take an additional `hot` category or the workflow's category
  filter can be widened; that is a separate decision because it also touches what the gate compares.
- When the CI-native baseline is captured, both new single-threaded reference rows are emitted by
  `--emit-baseline` automatically as `gated: false`; no manual baseline edit is needed.
