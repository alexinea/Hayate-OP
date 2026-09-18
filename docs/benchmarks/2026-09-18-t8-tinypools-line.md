# HayateOP — T8: RRode `TinyPools` head-to-head column (2026-09-18)

> Purpose: extend the M1 head-to-head matrix with the TinyPools reference line (plan item **T8**,
> "TP head-to-head 基准列（`RRode.TinyPools`）"). The TinyPools comparison previously carried the same
> caveat the marklauter line carried before N5: **no direct head-to-head benchmark existed**, so any
> performance statement about the two libraries was an architectural extrapolation. This report
> closes that gap with measured data taken from the shared matrix, under one capacity basis, one
> warm-up and one payload.

## 1. What was added

Three columns, all driven by `TinyPools` **1.0.1** (MIT; referenced by the benchmarks project only —
it is not a dependency of any shipped package):

| Row (BDN description) | Suite | What it measures |
| :--- | :--- | :--- |
| `Acquire+Release \| TinyPools` | Suite 1 — single-threaded synchronous | `GetObject()`, a payload write, then `Dispose()` |
| `Release \| TinyPools` | Suite 2 — release path | `GetObject()` then `Dispose()` immediately — return and re-borrow, the borrow set held constant |
| `Concurrent-100 \| TinyPools` | Suite 4 — contention | 100-thread `Parallel.For` borrow/write/dispose round trip |

`TinyPools` cannot appear in Suite 3 (asynchronous `AcquireAsync+Release`): the library exposes **no
asynchronous API** — the only acquire path is the synchronous `GetObject()`. Suite 3 is therefore N/A
for this line, mirroring how MEOP and plain `new` are N/A there and how `MSL.Pool` is N/A in the
synchronous suites. This is recorded in the benchmark file's coverage-matrix header.

### 1.1 Package and API facts (resolved from the package, not from documentation prose)

The plan entry names the item `RRode.TinyPools`; `RRode` is the author, and the **package id is
`TinyPools`**. The facts below come from the nupkg, the in-package XML documentation, a reflection
dump of the assembly and the library's own source (`RRode/TinyPools`), in that order of precedence:

| Fact | Value |
| :--- | :--- |
| Package id / version | `TinyPools` 1.0.1 (2 versions ever published: 1.0.0, 1.0.1) |
| Assembly | `TinyPools, Version=1.0.1.0`, strong-named, `ImageRuntime v4.0.30319` (2016) |
| Assets shipped | **`lib/net40/TinyPools.dll` only** |
| Resolution on net10.0 | through NuGet's `net461` asset-target fallback, i.e. the warning `NU1701` |
| Licence | MIT (`Copyright (c) 2016 TinyPools contributors`) |
| Dependencies | none |

Public surface (reflection dump; the in-package XML documentation and the library source agree):

```text
-- TinyPools.ObjectPool`1 --          (where T : class)
  ctor(Func`1 factory)                            // unlimited capacity
  ctor(Func`1 factory, Int32 capacity)            // capacity = max retained, < 1 throws
  prop Int32 StoredObjects                        // currently available objects
  method PooledObject`1 GetObject()               // dequeue or create
  implements IDisposable : False
-- TinyPools.PooledObject`1 --        (sealed, : IDisposable)
  prop T Object        // throws ObjectDisposedException once returned
  method Void Dispose()   // returns the item; idempotent
```

The library has **no** min-size, timeout, metrics, eviction, sharding or preparation concept: the
whole pool is a `Queue<T>` guarded by a single monitor, and each borrow hands out a freshly
constructed `PooledObject<T>` lease wrapper (which itself constructs a private lock object). Nothing
in this row has to be switched off, because there is nothing to switch off.

**On the net40-only asset:** the package predates `netstandard`, so there is no newer asset to
prefer. It restores and runs on net10.0, and the reference is benchmark-project-only, so the
`NU1701` warning is suppressed per package (`NoWarn="NU1701"` on that one `PackageReference`) rather
than project-wide, keeping the diagnostic meaningful for any future package. CI restores the same
package from nuget.org; the suppression is not local-only.

### 1.2 Fairness provisions

The row is built to differ from the HayateOP rows in **one** dimension only — the pool implementation:

| Dimension | HayateOP rows | `TinyPools` row |
| :--- | :--- | :--- |
| Capacity basis | `MinPoolSize = 250`, `MaxPoolSize = 300` | the capacity-taking constructor with `MaxPoolSize = 300`, i.e. a max-retained bound — the same retention semantics the MEOP row uses (`MaximumRetained = 300`). The library has no min-size notion to match |
| Warm-up | full borrow/return cycle up to `MaxPoolSize` | identical loop in `GlobalSetup` (same idiom as every other row) |
| Payload | `PooledObject` (one `int` property) | the same class, produced by the pool's own factory delegate |
| Optional features | `AllOff` = every toggle off; lean = toggles normalized away | no preparation, no metrics, no eviction, no timeouts exist in the library at all |
| Metrics / diagnostics | `WithEnableMetrics(false)`, or lean storage | no metrics surface in the library; no adapter needed |
| Construction | `HayatePoolBuilder<T>` | the public `ObjectPool<T>(factory, capacity)` constructor |
| Disposal | `Dispose()` in `GlobalCleanup` | `ObjectPool<T>` is not `IDisposable` — nothing to release |

Two bounds on what these numbers can be used to claim:

1. **Suite 1 and Suite 2 are the like-for-like rows.** `TinyPools` has a synchronous acquire path and
   no asynchronous one, so it is directly comparable to `Hayate Lean` and MEOP in both synchronous
   suites, and it cannot be compared with the asynchronous suite at all.
2. **The warm-up idiom is the matrix's, and it does not fill a pool.** Every row warms up with
   `for (i < MaxPoolSize) pool.Return(pool.Get())`, which borrows and returns in the same statement.
   At the end of `GlobalSetup` such a loop leaves a pool holding **one** idle object, not
   `MaxPoolSize` — each iteration returns the item before the next borrow, so the pool cycles a single
   object and never reaches the warm-up count. This is pre-existing matrix behaviour shared with the
   MEOP and `MSL.Pool` rows (kept, because comparability with the archived runs depends on the idiom
   not changing), and it is not a problem for the measurement: the steady state is established by
   BenchmarkDotNet's own warm-up iterations and verified below.

### 1.3 Steady state verified before trusting the numbers

A standalone probe (`_t8_steady_state_probe.cs`, workspace; same payload, same capacity, same
factory-counting delegate) establishes both the capacity semantics and the fact that the create path
stays out of the measurement:

| Probe step | `StoredObjects` | Factory calls | Comment |
| :--- | ---: | ---: | :--- |
| right after construction | 0 | 0 | pool starts empty |
| 300 concurrent leases held | 0 | 300 | creates exactly as many as are held |
| all 300 released | 300 | 300 | retains all 300 |
| 100,000 steady-state round trips | **1** | **1 (delta 0)** | the create path never runs again |
| 350 releases into a 300-capacity pool | 300 | – | excess is dropped, the bound holds |
| 300 objects filled by hold-then-release, then 100,000 round trips | **300** | **300 (delta 0)** | the same result with the pool genuinely full |

Also confirmed by the probe, and relevant to how the row may be used: `Dispose()` is idempotent (a
second call is a no-op, not an exception), reading `Object` after the item was returned throws
`ObjectDisposedException`, and a capacity below 1 throws `ArgumentException`.

The 100-thread row is verified a second way, because no standalone probe covers `Parallel.For`. Under
contention the per-iteration allocation decomposes as:

| Row | Allocated / iteration | Minus the shared `Parallel.For` harness (~4.2 KB, taken from the `Hayate Lean` row) | Per operation |
| :--- | ---: | ---: | ---: |
| `Concurrent-100 \| Hayate Lean` | 4,228 B | 0 B | 0 B |
| `Concurrent-100 \| MEOP` | 4,281 B | 53 B | 0.5 B |
| `Concurrent-100 \| TinyPools` | 11,519 B | 7,291 B | **72.9 B** |
| `Concurrent-100 \| MSL.Pool` | 48,591 B | 44,363 B | **443.6 B** |

`MSL.Pool`'s delta reproduces its own single-threaded figure (440 B/op) to within 1 %, and
`TinyPools`' delta reproduces the probe's per-round-trip machinery figure (72.0 B/op, see §1.4) to
within 1.3 %. A pool that had to create objects during the measurement would show an extra ~96 B per
created round trip on top of that, so **the create path is not part of the measured window in either
suite**.

### 1.4 Finding: the allocation column for this row is context-dependent

The single-threaded rows report **24 B per operation**, while the standalone probe reports **72.00 B
per round trip** — the sum of the lease wrapper (48 B: two references, a bool and an object lock,
plus header) and the lock object it constructs (24 B). The discrepancy was pursued, not averaged
away:

| Measurement context (probe) | Allocated per round trip |
| :--- | ---: |
| loop inlined in the probe's ~150-line `Main` | 72.00 B |
| same loop in a small method (the shape BenchmarkDotNet compiles a benchmark body into) | 72.00 B |
| same loop behind a `[MethodImpl(NoInlining)]` call (the shape the `Parallel.For` suite takes) | 72.00 B |
| all of the above with `DOTNET_gcServer=1` (the matrix job runs server GC) | 72.00 B |

The probe cannot reproduce 24 B in any context tested, and the 3× difference is not a BenchmarkDotNet
artefact: in the same run `MSL.Pool`'s single-threaded `440 B/op` matches its probe figure exactly,
and `plain new`'s `24 B/op` matches the payload size. The read that is **safe to state** is therefore
the conservative one: *a `TinyPools` round trip is not allocation-free — the public lease API
allocates at least one small heap object per borrow/return, and the standalone probe measures 72 B/op
of library machinery (wrapper + its internal lock), which is also what the contended row's allocation
delta shows.* Which of those two allocations the JIT manages to remove in the benchmark's own
compilation context is **not verified** and is not claimed.

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
| Capacity basis | Min=250 / Max=300 (HayateOP), max-retained 300 (`TinyPools`, MEOP, `MSL.Pool`) |
| Matrix filter | `--filter '*HayateOpBenchmarks*'` (the head-to-head matrix only) |
| Command | `HayateOP.Benchmarks.exe --filter '*HayateOpBenchmarks*' --buildTimeout 900 --artifacts <dir>` |

> **On `--buildTimeout 900`**: a local-run workaround only, and not part of the committed benchmark
> definition. On the capture machine MSBuild node reuse and the shared compiler are disabled (they
> otherwise hold handles on the git-tracked generated XML documentation files), which pushes the
> auto-generated boilerplate build past BenchmarkDotNet's default 120 s build timeout. CI and a
> normal developer machine need no such flag and the runbook commands stay unchanged.

> **Absolute values are machine- and run-specific.** In this run `Acquire+Release | Hayate Lean`
> measures 26.5 ns where the 2026-09-10 local baseline recorded 27.1 ns and the N5 run recorded
> 58.4 ns for the same row, and `Acquire+Release | Hayate AllOff` measures 255.8 ns against N5's
> 416.5 ns — that is the documented run-to-run spread of the sub-100 ns rows (see
> [`2026-09-10-o1-lean.md`](2026-09-10-o1-lean.md)). The committed machine-readable baseline is
> deliberately **not** regenerated from this run; only ratios measured *within this single run* are
> meaningful, and none of these numbers should be compared against `baseline.json`.

## 3. Results (BDN MarkdownExporter output)


```text
BenchmarkDotNet v0.15.8, Windows 11 (10.0.28120.3002)
12th Gen Intel Core i7-1260P 2.10GHz, 1 CPU, 16 logical and 12 physical cores
.NET SDK 10.0.400
  [Host] : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3
  Short  : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3
Job=Short  Concurrent=True  Server=True
IterationCount=10  WarmupCount=3
```

| Method                                              | Mean             | Error           | StdDev          | P50       | P90         | P95         | P99         | Median         | Ratio     | RatioSD   | Rank | Gen0   | Completed Work Items | Lock Contentions | Gen1   | Allocated | Alloc Ratio |
|---------------------------------------------------- |-----------------:|----------------:|----------------:|----------:|------------:|------------:|------------:|---------------:|----------:|----------:|-----:|-------:|---------------------:|-----------------:|-------:|----------:|------------:|
| &#39;Concurrent-100 \| MEOP&#39;                             |    19,894.415 ns |     623.0376 ns |     412.1008 ns |  19,714.3 |    20,411.0 |    20,593.6 |    20,593.6 |  19,725.443 ns |    944.61 |     46.55 |   10 | 0.4578 |              11.7100 |                - |      - |    4281 B |          NA |
| &#39;Concurrent-100 \| Hayate AllOff&#39;                    |   180,120.825 ns |  73,856.5085 ns |  48,851.5075 ns | 154,173.4 |   244,265.1 |   258,261.4 |   258,261.4 | 170,058.600 ns |  8,552.33 |  2,247.22 |   13 | 5.0049 |              32.3882 |           0.0363 | 0.2441 |   46619 B |          NA |
| &#39;Concurrent-100 \| Hayate Lean&#39;                      |    17,550.795 ns |     302.1870 ns |     199.8780 ns |  17,514.4 |    17,765.5 |    17,924.3 |    17,924.3 |  17,539.696 ns |    833.33 |     38.70 |    9 | 0.4272 |              11.2917 |           0.0000 |      - |    4228 B |          NA |
| &#39;Concurrent-100 \| Hayate Sharded4&#39;                  | 1,042,610.703 ns | 758,707.6341 ns | 501,838.1239 ns | 795,937.9 | 1,606,744.9 | 2,067,997.7 | 2,067,997.7 | 897,328.711 ns | 49,504.27 | 22,851.38 |   14 | 3.9063 |              15.1211 |           0.0039 |      - |   43132 B |          NA |
| &#39;Concurrent-100 \| MSL.Pool&#39;                         |   135,754.995 ns |   4,656.1760 ns |   3,079.7721 ns | 134,213.4 |   139,368.2 |   141,227.6 |   141,227.6 | 135,414.343 ns |  6,445.79 |    322.73 |   12 | 5.1270 |              14.0195 |           5.4478 |      - |   48591 B |          NA |
| &#39;Concurrent-100 \| TinyPools&#39;                        |   100,851.903 ns |   5,161.7101 ns |   3,071.6509 ns | 101,062.3 |   106,803.3 |   106,803.3 |   106,803.3 | 101,062.268 ns |  4,788.56 |    256.79 |   11 | 1.2207 |              12.3550 |           4.4614 |      - |   11519 B |          NA |
| &#39;Acquire+Release \| Hayate AllOff&#39;                   |       255.763 ns |      17.0176 ns |      11.2561 ns |     256.5 |       267.2 |       270.1 |       270.1 |     257.147 ns |     12.14 |      0.75 |    6 | 0.0405 |                    - |                - | 0.0014 |     384 B |          NA |
| &#39;Acquire+Release \| Hayate Lean&#39;                     |        26.476 ns |       2.0565 ns |       1.3603 ns |      25.9 |        28.0 |        28.5 |        28.5 |      26.521 ns |      1.26 |      0.08 |    3 |      - |                    - |                - |      - |         - |          NA |
| &#39;Acquire+Release \| Hayate ArrayPool (O-D)&#39;          |        26.231 ns |       1.7061 ns |       1.1285 ns |      26.0 |        27.6 |        27.8 |        27.8 |      26.231 ns |      1.25 |      0.08 |    3 |      - |                    - |                - |      - |         - |          NA |
| &#39;Acquire+Release \| Hayate Sharded4&#39;                 |       230.639 ns |       9.5986 ns |       6.3489 ns |     231.8 |       237.7 |       239.4 |       239.4 |     232.302 ns |     10.95 |      0.57 |    6 | 0.0408 |                    - |                - | 0.0005 |     384 B |          NA |
| &#39;Acquire+Release \| Hayate Full&#39;                     |       319.101 ns |       6.7474 ns |       4.4630 ns |     318.5 |       323.9 |       324.8 |       324.8 |     319.678 ns |     15.15 |      0.71 |    7 | 0.0439 |                    - |                - | 0.0005 |     416 B |          NA |
| &#39;Release \| Hayate AllOff&#39;                           |       237.269 ns |      13.0471 ns |       8.6299 ns |     235.1 |       246.7 |       251.2 |       251.2 |     235.632 ns |     11.27 |      0.64 |    6 | 0.0408 |                    - |                - | 0.0017 |     384 B |          NA |
| &#39;Release \| Hayate Lean&#39;                             |        25.045 ns |       0.8774 ns |       0.5803 ns |      25.0 |        25.5 |        26.1 |        26.1 |      25.010 ns |      1.19 |      0.06 |    3 |      - |                    - |                - |      - |         - |          NA |
| &#39;AcquireAsync+Release \| Hayate AllOff&#39;              |       222.471 ns |      10.7404 ns |       7.1041 ns |     221.2 |       230.1 |       232.5 |       232.5 |     222.701 ns |     10.56 |      0.58 |    6 | 0.0415 |               0.0000 |                - | 0.0017 |     392 B |          NA |
| &#39;AcquireAsync+Release \| Hayate Lean&#39;                |        64.714 ns |       3.8515 ns |       2.5475 ns |      64.5 |        67.3 |        67.4 |        67.4 |      65.030 ns |      3.07 |      0.18 |    5 | 0.0153 |                    - |                - |      - |     144 B |          NA |
| &#39;AcquireAsync+Release \| Hayate Full&#39;                |       242.193 ns |      18.4105 ns |      12.1774 ns |     244.6 |       255.2 |       256.0 |       256.0 |     245.389 ns |     11.50 |      0.76 |    6 | 0.0415 |                    - |                - | 0.0005 |     392 B |          NA |
| &#39;Acquire+Release \| MEOP (baseline)&#39;                 |        21.104 ns |       1.5143 ns |       1.0016 ns |      20.9 |        22.1 |        22.8 |        22.8 |      20.984 ns |      1.00 |      0.06 |    2 |      - |                    - |                - |      - |         - |          NA |
| &#39;Acquire+Release \| plain new (no-pool lower bound)&#39; |         5.439 ns |       0.4569 ns |       0.3022 ns |       5.5 |         5.7 |         5.8 |         5.8 |       5.525 ns |      0.26 |      0.02 |    1 | 0.0025 |                    - |                - |      - |      24 B |          NA |
| &#39;Acquire+Release \| TinyPools&#39;                       |        72.828 ns |       3.6875 ns |       2.4390 ns |      71.9 |        75.2 |        76.0 |        76.0 |      72.717 ns |      3.46 |      0.19 |    5 | 0.0025 |                    - |                - |      - |      24 B |          NA |
| &#39;Release \| MEOP Return&#39;                             |        20.426 ns |       1.2990 ns |       0.8592 ns |      20.1 |        21.4 |        21.8 |        21.8 |      20.314 ns |      0.97 |      0.06 |    2 |      - |                    - |                - |      - |         - |          NA |
| &#39;Release \| MSL.Pool&#39;                                |       573.560 ns |      40.6506 ns |      24.1905 ns |     578.4 |       618.8 |       618.8 |       618.8 |     578.355 ns |     27.23 |      1.64 |    8 | 0.0467 |               0.0000 |                - |      - |     440 B |          NA |
| &#39;Release \| TinyPools&#39;                               |        52.999 ns |       1.5361 ns |       0.8034 ns |      52.9 |        54.1 |        54.1 |        54.1 |      52.920 ns |      2.52 |      0.12 |    4 | 0.0025 |                    - |                - |      - |      24 B |          NA |
| &#39;AcquireAsync+Release \| MSL.Pool (CT)&#39;              |       597.834 ns |      56.5545 ns |      37.4073 ns |     586.6 |       626.4 |       675.7 |       675.7 |     588.473 ns |     28.39 |      2.13 |    8 | 0.0467 |               0.0000 |                - |      - |     440 B |          NA |


## 4. Reading the TinyPools rows

Measured in this run, with every other row measured in the same process:

| Observation | Value |
| :--- | :--- |
| `GetObject()` + payload write + `Dispose()`, single-threaded | 72.8 ns, 24 B, ratio 3.46 against the same-run MEOP baseline row |
| `GetObject()` + `Dispose()`, single-threaded | 53.0 ns, 24 B |
| Ratio against the same-run HayateOP lean fast path | ≈2.8× (`Acquire+Release`), ≈2.1× (`Release`) |
| Ratio against the same-run general-engine row (`AllOff`) | ≈0.28× — the simple queue/lock pool is ≈3.5× **faster** than the general engine |
| 100-thread contention, per 100-op iteration | 100.9 µs (≈1.01 µs/op), 11.5 KB/iteration, 4.46 lock contentions per iteration |
| Ratio against the same-run MEOP / lean concurrent rows | ≈5.1× / ≈5.7× (per operation) |
| Ratio against the same-run `MSL.Pool` concurrent row | ≈0.74× — TinyPools is ≈26 % faster per operation |

Interpretation, kept separate from the measurements above:

1. **The cost is the design, and the design is visible in the source.** `TinyPools` is a small
   2016-era pool: a single `Queue<T>` behind one monitor, plus a per-borrow `PooledObject<T>` lease
   whose `Object` getter and `Dispose` take their own monitor and check a disposed flag. One
   single-threaded round trip therefore costs two queue-lock acquisitions, two lease-lock
   acquisitions, and the allocation of at least one small object — against MEOP's lock-free
   `Interlocked` fast path, which allocates nothing. Measured at 3.5× MEOP on the round trip and 2.5×
   on the release path, the numbers are consistent with that, but the per-instruction attribution is
   an inference, not a measurement.
2. **Under contention the single lock is the whole story.** The contended row is the only row in the
   matrix whose `Lock Contentions` column is populated for a synchronous pool (4.46 per 100-op
   iteration, against 0 for the HayateOP lean row), and it pays ≈5× MEOP/lean per operation. This is
   the expected shape of a global monitor in front of a hot borrow/return loop, and it is the axis on
   which *all* single-structure pools — including the HayateOP general engine's non-sharded rows —
   lose to the sharded and lock-free variants.
3. **It is not a like-for-like workload.** `TinyPools` targets "objects that are expensive to create"
   with a very small API (no builder, no policies, no diagnostics, no lifecycle surface). Nothing in
   this matrix measures creation cost, so the columns answer only "what does each library's public
   borrow/return path cost under an identical capacity basis, warm-up and payload" — not "which
   library is better". On the one axis where the two libraries are directly comparable, the
   allocation axis, `TinyPools` is allocation-positive where MEOP and the HayateOP lean path are
   exactly zero.
4. **Read the suite pairs, not the ranks.** `Acquire+Release | TinyPools` (72.8 ns) is directly
   comparable to `Acquire+Release | Hayate Lean` (26.5 ns) and `Acquire+Release | MEOP` (21.1 ns);
   `Release | TinyPools` (53.0 ns) is comparable to `Release | Hayate Lean` (25.0 ns) and
   `Release | MEOP Return` (20.4 ns). The `Ratio` column is relative to the MEOP round-trip row for
   *every* row in the table, including rows from other suites, so it must not be read across suites.

## 5. Gate impact

- The three new rows are **external-reference rows**. `scripts/bench-compare.py` gates only entries
  whose description contains `Hayate` and is not a `Concurrent-100` row, so none of them can
  influence pass/fail; they are reported for context exactly like the MEOP and plain-`new` rows.
- `docs/benchmarks/baseline/baseline.json` is **intentionally untouched**. The Q1 runbook requires a
  CI-native capture, and committing a local capture as the gate baseline is precisely the
  cross-environment comparison the calibration flag guards against.
- Consequence to be aware of: the CI workflow runs the `hot` category only (`--anyCategories hot`),
  while these rows carry `reference` / `concurrent` — consistent with the existing reference rows.
  They therefore appear in local full-matrix runs and in this report, but not in the CI hot-only run.
  If continuous CI visibility of the TinyPools column is wanted, either the two single-threaded rows
  can take an additional `hot` category or the workflow's category filter can be widened; that is a
  separate decision because it also touches what the gate compares.
- When the CI-native baseline is captured, both new single-threaded reference rows are emitted by
  `--emit-baseline` automatically as `gated: false`; no manual baseline edit is needed.
- The `NU1701` suppression is scoped to the single `PackageReference` in the benchmarks project, so
  it cannot mask the same warning for any other package, in CI or locally.
