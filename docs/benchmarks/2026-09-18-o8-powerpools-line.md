# HayateOP — O8→M1+: Hertzole `PowerPools` head-to-head column (2026-09-18)

> Purpose: extend the M1 head-to-head matrix with the PowerPools reference line (plan item
> **`O8→M1+`**, "POP head-to-head 基准列（`Hertzole.PowerPools`，含分配口径）"). The POP-derived
> comparison previously carried the same caveat the marklauter and TinyPools lines carried before N5
> and T8: **no direct head-to-head benchmark existed**, so performance statements about the two
> libraries were architectural extrapolations. This report closes that gap with measured data taken
> from the shared matrix, under one capacity basis, one warm-up and one payload.
>
> **Disambiguation**: the 2.8 plan uses `O8` twice — this item (`O8→M1+`, the benchmark column, P1)
> and a CHOPIN-derived `O8` (a `BorrowStrategy{Lifo,Fifo}` switch, P2). This report covers the
> benchmark column only; the numbering collision is a known defect in the plan's item ledger and is
> left for a one-pass renumbering rather than silently renumbered here.

## 1. What was added

Three columns, all driven by `Hertzole.PowerPools` **1.0.0** (MIT; referenced by the benchmarks
project only — it is not a dependency of any shipped package):

| Row (BDN description) | Suite | What it measures |
| :--- | :--- | :--- |
| `Acquire+Release \| PowerPools` | Suite 1 — single-threaded synchronous | `Rent()`, a payload write, then `Return()` |
| `Release \| PowerPools` | Suite 2 — release path | `Rent()` then `Return()` immediately — return and re-borrow, the borrow set held constant |
| `Concurrent-100 \| PowerPools` | Suite 4 — contention | 100-thread `Parallel.For` rent/write/return round trip |

`PowerPools` cannot appear in Suite 3 (asynchronous `AcquireAsync+Release`): the library exposes **no
asynchronous API** — the only acquire path is the synchronous `Rent()`. Suite 3 is therefore N/A for
this line, mirroring how MEOP, plain `new` and TinyPools are N/A there and how `MSL.Pool` is N/A in
the synchronous suites. This is recorded in the benchmark file's coverage-matrix header.

### 1.1 Package and API facts (resolved from the package, not from documentation prose)

The plan entry names the item `Hertzole.PowerPools`; that is also the actual **package id**. The facts
below come from the nupkg, the nuspec, the in-package XML documentation and a reflection dump of the
assembly, in that order of precedence:

| Fact | Value |
| :--- | :--- |
| Package id / version | `Hertzole.PowerPools` 1.0.0 (only version ever published; 303 total downloads at capture time) |
| Assets shipped | `lib/netstandard2.0`, `netstandard2.1`, `net5.0`, `net6.0`, `net7.0`, `net8.0`, **`net9.0`** |
| Resolution on net10.0 | nearest-TFM selection of `lib/net9.0/Hertzole.PowerPools.dll` — **no asset-target fallback, no `NU1701`** |
| Licence | MIT (`<license type="expression">MIT</license>`) |
| Dependencies | none for every `net5.0`+ target group; `System.Buffers` 4.6.0 on `netstandard2.0` only |
| Repository | `https://github.com/Hertzole/power-pools`, commit `bc18dd1` |

Public surface (reflection dump; the in-package XML documentation agrees):

```text
-- Hertzole.PowerPools.ObjectPool`1 --   (sealed, no public constructor)
  static ObjectPool`1 Create(Func`1 factory,
                             Action`1 onRent    = null,
                             Action`1 onReturn  = null,
                             Action`1 onDispose = null,
                             Int32 initialCapacity = 16)
  prop Int32 Capacity   // 300 requested is reported back as 512 (rounded up)
  prop Int32 InPool
  prop Int32 InUse
  method T Rent()
  method Void Return(T item)
  method Int32 PreWarm(Int32 count)
  implements IObjectPool`1, IDisposable
```

`ObjectPool<T>` is the only PowerPools type that takes an explicit item factory, which makes it the
direct counterpart of the HayateOP general engine; the library's other generic types
(`GenericObjectPool<T>`, `FixedSizeObjectPool<T>`, `GenericFixedSizeObjectPool<T>`) either construct
through `new T()` or are fixed-size, and the README describes them as different contracts. The
library's own README states that its storage is **`ArrayPool`-backed**, which is the same storage
strategy the HayateOP ArrayPool row (O-D) uses — the reason this column is a like-for-like
storage comparison rather than a mismatch.

**On the reported capacity (300 requested → 512):** `Capacity` is "the amount that can be stored in
the pool before it needs to resize" (the library's own interface documentation), and the probe shows
it reporting 512 for a pool created with `initialCapacity: 300`. The value requested at the call site
is the pinned one; the reported figure is larger, and the rounding is upward, so it can never
constrain the borrow set — the same retention-bound semantics the MEOP row's `MaximumRetained` has (a
bound on what is retained, not a hard cap on concurrent borrowers). The exact rounding rule was not
reverse-engineered and is not claimed.

### 1.2 Fairness provisions

The row is built to differ from the HayateOP rows in **one** dimension only — the pool implementation:

| Dimension | HayateOP rows | `PowerPools` row |
| :--- | :--- | :--- |
| Capacity basis | `MinPoolSize = 250`, `MaxPoolSize = 300` | `initialCapacity: MaxPoolSize = 300` — the same retention/capacity basis the MEOP row uses (`MaximumRetained = 300`). The library has no min-size notion to match |
| Warm-up | full borrow/return cycle up to `MaxPoolSize` | identical single-loop idiom in `GlobalSetup` (same statement shape as every other row) |
| Payload | `PooledObject` (one `int` property) | the same class, produced by the pool's own factory delegate |
| Optional features | `AllOff` = every toggle off; lean = toggles normalized away | no preparation, no metrics, no eviction, no timeouts exist in the library at all; the three optional callbacks are left at their `null` defaults |
| Metrics / diagnostics | `WithEnableMetrics(false)`, or lean storage | no metrics surface in the library; no adapter needed |
| Construction | `HayatePoolBuilder<T>` | the public static factory `ObjectPool<T>.Create(...)` (the type is sealed with no public constructor); the DI extensions are not used, so nothing outside the measured path is exercised |
| Explicit default pinning | feature toggles written out | `initialCapacity` pinned to `MaxPoolSize` rather than relying on the library default of `16` |
| Disposal | `Dispose()` in `GlobalCleanup` | `Dispose()` in `GlobalCleanup`; the type is `IDisposable` |

Two bounds on what these numbers can be used to claim:

1. **Suite 1 and Suite 2 are the like-for-like rows.** `PowerPools` has a synchronous acquire path and
   no asynchronous one, so it is directly comparable to `Hayate Lean`, `Hayate ArrayPool (O-D)` and
   MEOP in both synchronous suites, and it cannot be compared with the asynchronous suite at all.
2. **The warm-up idiom is the matrix's, and it does not fill a pool.** Every row warms up with
   `for (i < MaxPoolSize) pool.Return(pool.Rent())`, which borrows and returns in the same statement.
   At the end of `GlobalSetup` such a loop leaves a pool holding **one** idle object, not
   `MaxPoolSize`. This is pre-existing matrix behaviour shared with the MEOP, `MSL.Pool` and TinyPools
   rows (kept, because comparability with the archived runs depends on the idiom not changing), and it
   is not a problem for the measurement: the steady state is established by BenchmarkDotNet's own
   warm-up iterations and verified below. PowerPools also offers `PreWarm(int)`, which is
   **deliberately not used** — using it would give this row a different warm-up contract from the rest
   of the matrix.

### 1.3 Steady state verified before trusting the numbers

A standalone probe (`_o8probe/Program.cs`, workspace; same payload, same capacity, factory-counting
delegate) establishes both the capacity semantics and the fact that the create path stays out of the
measurement:

| Probe step | `InPool` | `InUse` | `Capacity` | Factory calls |
| :--- | ---: | ---: | ---: | ---: |
| right after `Create(initialCapacity: 300)` | 0 | 0 | 512 | 0 |
| `PreWarm(300)` returned 300 | 300 | 0 | 512 | 300 |
| after the matrix's borrow/return warm-up loop | 300 | 0 | 512 | 300 |
| **1,000,000 rent/return round trips** | 300 | 0 | 512 | **300 (delta 0)** |
| 100-thread concurrent burst | 300 | 0 | 512 | **300 (delta 0)** |

The factory counter never moves after warm-up and `InPool`/`InUse` return to their steady values, so
the measured loop provably never re-enters the create path in either the single-threaded or the
contended shape.

The 100-thread row is verified a second way, using the same decomposition the T8 report used, because
no probe covers the full `Parallel.For` harness. Under contention the per-iteration allocation
decomposes as (same run, `Concurrent-100 | Hayate Lean` is the 0 B/op control):

| Row | Allocated / iteration | Minus the `Hayate Lean` row | Per operation |
| :--- | ---: | ---: | ---: |
| `Concurrent-100 \| Hayate Lean` | 4,246 B | 0 B | 0 B |
| `Concurrent-100 \| MEOP` | 4,413 B | 167 B | 1.7 B |
| `Concurrent-100 \| PowerPools` | 5,018 B | 772 B | **7.7 B** |
| `Concurrent-100 \| TinyPools` | 11,176 B | 6,930 B | **69.3 B** |
| `Concurrent-100 \| Hayate Sharded4` | 43,457 B | 39,211 B | 392.1 B |
| `Concurrent-100 \| MSL.Pool` | 47,647 B | 43,401 B | **434.0 B** |
| `Concurrent-100 \| Hayate AllOff` | 47,743 B | 43,497 B | 435.0 B |

The method validates itself on the two known rows: TinyPools' delta (69.3 B/op) reproduces its
standalone probe figure (72.0 B/op) and MSL.Pool's (434.0 B/op) reproduces its probe figure
(440 B/op). A pool that had to create objects during the measurement would show an extra ~24 B per
created round trip on top of that, so **the create path is not part of the measured window**.

### 1.4 Finding: the contended path is not allocation-free, but the single-threaded path is

The two single-threaded rows report exactly **0 B per operation**, matching MEOP and the HayateOP lean
and ArrayPool rows — consistent with the library's ArrayPool-backed storage and with its README's
"minimize allocations" claim. The contended row does **not**: it reports ~5.0 KB per 100-op iteration
against the same run's ~4.2–4.4 KB for the zero-allocation control rows.

Because BenchmarkDotNet cannot attribute that residual, a second standalone probe
(`_o8concprobe/Program.cs`, workspace) isolates it. Every variant is driven through the identical
`Parallel.For(0, 100, ...)` shape and allocation is read as a
`GC.GetTotalAllocatedBytes(precise: true)` delta around the loop, so the harness cancels out when the
variants are compared; the variant order is rotated between repeats and a full `GC.Collect()` is
forced before each read. Medians over 40 repeats:

| Variant | Median B / iteration | Above the empty harness | Per operation |
| :--- | ---: | ---: | ---: |
| harness only (empty body) | 2,848 B | — | — |
| `new PooledObject()` per iteration (known-answer control) | 5,248 B | +2,400 B | **24.00 B** |
| MEOP | 3,008 B | +160 B | 1.60 B |
| **PowerPools** | 3,328 B | +480 B | **4.80 B** |

The known-answer control returns exactly the payload's 24 B, which validates the instrument. Against
MEOP as the control (MEOP's own single-threaded path is exactly 0 B, and the empty-body variant is a
poorer baseline because an empty body changes how `Parallel.For` partitions its work), PowerPools
sits **≈320 B per iteration above MEOP, i.e. ≈3.2 B/op**; against the matrix's `Hayate Lean` row the
same residual reads 7.7 B/op. The two readings bracket the same order of magnitude.

What is safe to state is therefore the conservative one: *the contended PowerPools path is **not**
exactly allocation-free — it allocates a few bytes per operation (≈3–8 B/op depending on the
reference row, with medians over 40 repeats) where MEOP, the HayateOP lean path and PowerPools' own
single-threaded path allocate nothing.* The mechanism was **not** resolved and is not claimed; the
corroborating signal in the matrix is the `Gen0` column (0.4883 per 1000 ops for PowerPools against
0.4272 for lean), which is directionally consistent with a small extra allocation. For scale, this
residual is roughly 2 % of TinyPools' contended per-op allocation and well under 1 % of MSL.Pool's.

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
| Capacity basis | Min=250 / Max=300 (HayateOP), max-retained/initial capacity 300 (`PowerPools`, `TinyPools`, MEOP, `MSL.Pool`) |
| Matrix filter | `--filter '*HayateOpBenchmarks*'` (the head-to-head matrix only) |
| Command | `HayateOP.Benchmarks.exe --filter '*HayateOpBenchmarks*' --buildTimeout 900 --artifacts <dir>` |
| Row count | 26 (23 before this change, +3 for this column) |

> **On `--buildTimeout 900`**: a local-run workaround only, and not part of the committed benchmark
> definition. On the capture machine MSBuild node reuse and the shared compiler are disabled (they
> otherwise hold handles on the git-tracked generated XML documentation files), which pushes the
> auto-generated boilerplate build past BenchmarkDotNet's default 120 s build timeout. CI and a
> normal developer machine need no such flag and the runbook commands stay unchanged.

> **Absolute values are machine- and run-specific.** In this run `Acquire+Release | MEOP (baseline)`
> measures 16.3 ns where the T8 run recorded 21.1 ns for the same row, and
> `Concurrent-100 | TinyPools` measures 131.4 µs against T8's 100.9 µs — that is the documented
> run-to-run spread of the sub-100 ns rows and of the contended suite (see
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

| Method                                              | Mean           | Error          | StdDev         | P50       | P90       | P95       | P99       | Median         | Ratio     | RatioSD | Rank | Completed Work Items | Lock Contentions | Gen0   | Gen1   | Allocated | Alloc Ratio |
|---------------------------------------------------- |---------------:|---------------:|---------------:|----------:|----------:|----------:|----------:|---------------:|----------:|--------:|-----:|---------------------:|-----------------:|-------:|-------:|----------:|------------:|
| &#39;Concurrent-100 \| MEOP&#39; | 21,162.256 ns | 807.5091 ns | 480.5357 ns | 21,157.3 | 21,902.3 | 21,902.3 | 21,902.3 | 21,157.321 ns | 1,295.18 | 35.78 | 8 | 12.5370 | - | 0.4578 | - | 4413 B | NA |
| &#39;Concurrent-100 \| Hayate AllOff&#39; | 140,481.727 ns | 11,337.2700 ns | 6,746.6275 ns | 139,356.6 | 150,938.3 | 150,938.3 | 150,938.3 | 139,356.641 ns | 8,597.82 | 419.26 | 10 | 36.1177 | 1.5991 | 4.8828 | - | 47743 B | NA |
| &#39;Concurrent-100 \| Hayate Lean&#39; | 18,397.525 ns | 616.4853 ns | 366.8605 ns | 18,299.8 | 19,132.5 | 19,132.5 | 19,132.5 | 18,299.780 ns | 1,125.97 | 28.86 | 8 | 11.4587 | - | 0.4272 | - | 4246 B | NA |
| &#39;Concurrent-100 \| Hayate Sharded4&#39; | 134,133.240 ns | 29,727.7718 ns | 15,548.1995 ns | 129,112.1 | 165,335.8 | 165,335.8 | 165,335.8 | 129,367.236 ns | 8,209.27 | 908.45 | 10 | 17.1367 | 0.0215 | 3.9063 | - | 43457 B | NA |
| &#39;Concurrent-100 \| MSL.Pool&#39; | 90,768.573 ns | 10,331.5111 ns | 5,403.5801 ns | 88,833.8 | 102,482.1 | 102,482.1 | 102,482.1 | 89,805.994 ns | 5,555.25 | 326.29 | 9 | 8.3901 | 0.5566 | 4.8828 | - | 47647 B | NA |
| &#39;Concurrent-100 \| TinyPools&#39; | 131,397.148 ns | 16,072.0018 ns | 10,630.6341 ns | 130,787.3 | 142,319.0 | 144,512.7 | 144,512.7 | 130,904.248 ns | 8,041.82 | 636.53 | 10 | 10.4187 | 3.0977 | 0.9766 | - | 11176 B | NA |
| &#39;Concurrent-100 \| PowerPools&#39; | 171,442.561 ns | 24,059.4046 ns | 15,913.8065 ns | 168,758.5 | 191,967.5 | 192,405.2 | 192,405.2 | 173,080.920 ns | 10,492.69 | 947.42 | 11 | 16.8918 | 8.4617 | 0.4883 | - | 5018 B | NA |
| &#39;Acquire+Release \| Hayate AllOff&#39; | 291.661 ns | 56.6107 ns | 37.4445 ns | 300.1 | 338.8 | 339.9 | 339.9 | 303.104 ns | 17.85 | 2.21 | 6 | - | - | 0.0405 | 0.0014 | 384 B | NA |
| &#39;Acquire+Release \| Hayate Lean&#39; | 27.477 ns | 3.1397 ns | 2.0767 ns | 27.2 | 29.6 | 30.1 | 30.1 | 27.859 ns | 1.68 | 0.12 | 3 | - | - | - | - | - | NA |
| &#39;Acquire+Release \| Hayate ArrayPool (O-D)&#39; | 24.935 ns | 1.5181 ns | 1.0041 ns | 24.8 | 25.8 | 26.7 | 26.7 | 24.929 ns | 1.53 | 0.06 | 3 | - | - | - | - | - | NA |
| &#39;Acquire+Release \| Hayate Sharded4&#39; | 251.491 ns | 37.0150 ns | 24.4832 ns | 240.8 | 286.5 | 290.3 | 290.3 | 241.101 ns | 15.39 | 1.46 | 6 | - | - | 0.0405 | - | 384 B | NA |
| &#39;Acquire+Release \| Hayate Full&#39; | 348.888 ns | 26.9092 ns | 17.7988 ns | 347.5 | 363.9 | 382.5 | 382.5 | 350.490 ns | 21.35 | 1.10 | 6 | - | - | 0.0439 | 0.0005 | 416 B | NA |
| &#39;Release \| Hayate AllOff&#39; | 227.632 ns | 7.9260 ns | 4.7166 ns | 225.9 | 237.0 | 237.0 | 237.0 | 225.916 ns | 13.93 | 0.36 | 6 | - | - | 0.0408 | 0.0017 | 384 B | NA |
| &#39;Release \| Hayate Lean&#39; | 23.527 ns | 0.8255 ns | 0.5460 ns | 23.7 | 24.0 | 24.1 | 24.1 | 23.738 ns | 1.44 | 0.04 | 3 | - | - | - | - | - | NA |
| &#39;AcquireAsync+Release \| Hayate AllOff&#39; | 238.561 ns | 26.7414 ns | 17.6878 ns | 227.6 | 261.6 | 264.4 | 264.4 | 230.779 ns | 14.60 | 1.06 | 6 | - | - | 0.0415 | 0.0017 | 392 B | NA |
| &#39;AcquireAsync+Release \| Hayate Lean&#39; | 67.848 ns | 2.7146 ns | 1.6154 ns | 68.4 | 69.7 | 69.7 | 69.7 | 68.351 ns | 4.15 | 0.12 | 5 | 0.0000 | - | 0.0153 | - | 144 B | NA |
| &#39;AcquireAsync+Release \| Hayate Full&#39; | 234.492 ns | 7.8715 ns | 5.2065 ns | 232.7 | 241.0 | 242.8 | 242.8 | 233.656 ns | 14.35 | 0.39 | 6 | - | - | 0.0415 | 0.0005 | 392 B | NA |
| &#39;Acquire+Release \| MEOP (baseline)&#39; | 16.344 ns | 0.5718 ns | 0.2991 ns | 16.3 | 16.8 | 16.8 | 16.8 | 16.378 ns | 1.00 | 0.02 | 2 | - | - | - | - | - | NA |
| &#39;Acquire+Release \| plain new (no-pool lower bound)&#39; | 6.846 ns | 1.2627 ns | 0.8352 ns | 6.5 | 8.0 | 8.4 | 8.4 | 6.619 ns | 0.42 | 0.05 | 1 | - | - | 0.0025 | - | 24 B | NA |
| &#39;Acquire+Release \| TinyPools&#39; | 71.017 ns | 2.0284 ns | 1.2071 ns | 70.9 | 73.1 | 73.1 | 73.1 | 70.856 ns | 4.35 | 0.10 | 5 | - | - | 0.0025 | - | 24 B | NA |
| &#39;Acquire+Release \| PowerPools&#39; | 36.081 ns | 1.0490 ns | 0.6243 ns | 36.0 | 37.0 | 37.0 | 37.0 | 36.005 ns | 2.21 | 0.05 | 4 | - | - | - | - | - | NA |
| &#39;Release \| MEOP Return&#39; | 17.094 ns | 0.9686 ns | 0.6407 ns | 16.9 | 17.8 | 18.2 | 18.2 | 17.016 ns | 1.05 | 0.04 | 2 | - | - | - | - | - | NA |
| &#39;Release \| MSL.Pool&#39; | 591.712 ns | 24.5220 ns | 16.2198 ns | 592.6 | 615.4 | 617.6 | 617.6 | 593.021 ns | 36.21 | 1.14 | 7 | 0.0000 | - | 0.0467 | - | 440 B | NA |
| &#39;Release \| TinyPools&#39; | 60.958 ns | 6.0753 ns | 3.6153 ns | 61.5 | 65.2 | 65.2 | 65.2 | 61.507 ns | 3.73 | 0.22 | 5 | - | - | 0.0025 | - | 24 B | NA |
| &#39;Release \| PowerPools&#39; | 37.785 ns | 4.0559 ns | 2.6827 ns | 35.8 | 41.4 | 41.7 | 41.7 | 37.195 ns | 2.31 | 0.16 | 4 | - | - | - | - | - | NA |
| &#39;AcquireAsync+Release \| MSL.Pool (CT)&#39; | 591.413 ns | 24.6596 ns | 12.8974 ns | 594.3 | 610.2 | 610.2 | 610.2 | 595.517 ns | 36.20 | 0.97 | 7 | - | - | 0.0467 | - | 440 B | NA |

## 4. Reading the PowerPools rows

Measured in this run, with every other row measured in the same process:

| Observation | Value |
| :--- | :--- |
| `Rent()` + payload write + `Return()`, single-threaded | 36.1 ns, **0 B**, ratio 2.21 against the same-run MEOP baseline row |
| `Rent()` + `Return()`, single-threaded | 37.8 ns, **0 B** |
| Ratio against the same-run general-engine row (`AllOff`) | ≈0.12× — PowerPools is ≈8.1× **faster** than the general engine |
| Ratio against the same-run `Hayate Lean` / `Hayate ArrayPool (O-D)` | ≈1.31× / ≈1.45× |
| Ratio against the same-run `TinyPools` | ≈0.51× — PowerPools is ≈1.97× faster on the round trip, ≈1.61× on the release path |
| 100-thread contention, per 100-op iteration | 171.4 µs (≈1.71 µs/op), 5.0 KB/iteration, **8.46 lock contentions per iteration** |
| Ratio against the same-run MEOP / lean concurrent rows | ≈8.1× / ≈9.3× (per operation) |
| Ratio against the same-run `MSL.Pool` / `TinyPools` concurrent rows | ≈1.89× / ≈1.31× — the slowest contended row in the matrix (rank 11) |

Interpretation, kept separate from the measurements above:

1. **The single-threaded story is "ArrayPool-backed, lock-free-ish, allocation-free".** At 36.1 ns
   with exactly 0 B, PowerPools lands between MEOP (16.3 ns / 0 B) and the HayateOP lean fast path
   (27.5 ns / 0 B), and it is the only external reference row besides MEOP that is allocation-free on
   the round trip. This is consistent with the library's own README claim about ArrayPool-backed
   storage and with the fact that `ObjectPool<T>` is not a fixed-size pool: `Rent()` either takes an
   idle slot or grows, so on a warm pool no per-borrow wrapper is constructed. It also explains why
   it beats `TinyPools` — TinyPools allocates a lease wrapper plus its internal lock per borrow
   (24 B in this run's column, 72 B/op in its own probe).
2. **The contended story is the opposite, and it is the clearest signal in the run.** PowerPools is
   the **only** row in the matrix whose `Lock Contentions` count exceeds TinyPools' (8.46 against
   3.10 per 100-op iteration; MEOP and lean are exactly 0), and it pays ≈8× MEOP and ≈9× the lean
   path per operation. A global monitor in front of a hot borrow/return loop is the expected shape of
   that cost, and it is the axis on which the single-structure pools — including the HayateOP general
   engine's non-sharded rows — lose to the lock-free and sharded variants. The per-instruction
   attribution is an inference from the contention counter, not a measurement.
3. **The allocation column separates the three external lines cleanly.** Single-threaded: PowerPools
   0 B, TinyPools 24 B, MSL.Pool 440 B. The contended column preserves the ordering (≈3–8 B/op,
   69.3 B/op, 434.0 B/op) and additionally reproduces each library's standalone probe figure — which
   is why the decomposition in §1.3 is trustworthy rather than coincidental.
4. **It is not a like-for-like workload.** PowerPools targets "powerful, simple, performant object
   pools" with ArrayPool-backed storage and no lifecycle surface at all (no policies, no diagnostics,
   no eviction, no timeouts). Nothing in this matrix measures creation cost, so the columns answer
   only "what does each library's public borrow/return path cost under an identical capacity basis,
   warm-up and payload" — not "which library is better".
5. **Read the suite pairs, not the ranks.** `Acquire+Release | PowerPools` (36.1 ns) is directly
   comparable to `Acquire+Release | Hayate Lean` (27.5 ns), `Acquire+Release | Hayate ArrayPool (O-D)`
   (24.9 ns) and `Acquire+Release | MEOP` (16.3 ns); `Release | PowerPools` (37.8 ns) is comparable to
   `Release | Hayate Lean` (23.5 ns) and `Release | MEOP Return` (17.1 ns). The `Ratio` column is
   relative to the MEOP round-trip row for *every* row in the table, including rows from other suites,
   so it must not be read across suites.

## 5. Gate impact

- The three new rows are **external-reference rows**. `scripts/bench-compare.py` gates only entries
  whose description contains `Hayate` and is not a `Concurrent-100` row (`gated = "Hayate" in method
  and "Concurrent" not in method`), so none of them can influence pass/fail; they are reported for
  context exactly like the MEOP, plain-`new`, TinyPools and MSL.Pool rows.
- `docs/benchmarks/baseline/baseline.json` is **intentionally untouched**. The Q1 runbook requires a
  CI-native capture, and committing a local capture as the gate baseline is precisely the
  cross-environment comparison the calibration flag guards against.
- Consequence to be aware of: the CI workflow runs the `hot` category only (`--anyCategories hot`),
  while these rows carry `reference` / `concurrent` — consistent with the existing reference rows.
  They therefore appear in local full-matrix runs and in this report, but not in the CI hot-only run.
  If continuous CI visibility of the PowerPools column is wanted, either the two single-threaded rows
  can take an additional `hot` category or the workflow's category filter can be widened; that is a
  separate decision because it also touches what the gate compares.
- When the CI-native baseline is captured, both new single-threaded reference rows are emitted by
  `--emit-baseline` automatically as `gated: false`; no manual baseline edit is needed.
- No `NU1701` suppression is needed for this package, unlike TinyPools: the net10.0 target resolves
  the shipped `net9.0` asset by nearest-TFM selection, not through an asset-target fallback.
