# HayateOP — C-B: CnCSharp-Dev `PoolingLib` head-to-head column (2026-09-18)

> Purpose: extend the M1 head-to-head matrix with the `PoolingLib` reference line (plan item **`C-B`**,
> "CPL head-to-head 基准列（`PoolingLib`，须 retarget ns2.0 到 bench TFM）"). The workspace comparison
> document `HayateOP-vs-CPL-comparison-2026-09-09.md` states in its own header and again in §3 that "当前
> 仓库无 HayateOP↔CPL 的 head-to-head 基准", so every performance statement it makes about the two
> libraries is an architectural extrapolation. This report replaces that extrapolation with measured data
> taken from the shared matrix — one capacity basis, one warm-up, one payload.
>
> This is the last outstanding **P1** item of the 2.8 window.

## 1. What was added

Three rows, all driven by `PoolingLib` **1.0.3** (referenced by the benchmarks project only — it is not a
dependency of any shipped package):

| Row (BDN description) | Suite | What it measures |
| :--- | :--- | :--- |
| `Acquire+Release \| PoolingLib` | Suite 1 — single-threaded synchronous | `Get()`, a payload write, then `Return(obj)` |
| `Release \| PoolingLib` | Suite 2 — release path | `Get()` then `Return(obj)` immediately — return and re-borrow, the borrow set held constant |
| `Concurrent-100 \| PoolingLib` | Suite 4 — contention | 100-thread `Parallel.For` borrow/write/return round trip |

`PoolingLib` cannot appear in Suite 3 (asynchronous `AcquireAsync+Release`): the library exposes **no
asynchronous API at all**. Suite 3 is therefore N/A for this line, mirroring how MEOP, plain `new`,
TinyPools, PowerPools and Chopin.Pooling are N/A there and how `MSL.Pool` is N/A in the synchronous
suites. The benchmark file's coverage-matrix header records this.

### 1.1 Package and API facts (resolved from the package, not from documentation prose)

The plan entry names the item `PoolingLib`; that is also the actual **package id**. Facts below come from
the nupkg, the nuspec, the in-package XML documentation and a reflection dump of the assembly, in that
order of precedence:

| Fact | Value |
| :--- | :--- |
| Package id / version | `PoolingLib` 1.0.3 (author `CnCSharp-DevTeam`) |
| Repository | `github.com/CnCSharp-Dev/PoolingLib` (declared as `<repository type="git">` in the nuspec) |
| Description | "A simple pooling library for dotnet 一个面向.NET简易的池化库" |
| Assets shipped | **`lib/netstandard2.0` only** (assembly `PoolingLib, Version=1.0.0.3`), plus the in-package `PoolingLib.xml` documentation |
| Resolution on net10.0 | **direct** selection of `lib/netstandard2.0/PoolingLib.dll` as a compatible modern target — no asset-target fallback, no `NU1701`, hence **no `NoWarn`**. Verified in `obj/project.assets.json` after restore (`compile` and `runtime` both `lib/netstandard2.0/PoolingLib.dll`); restore reports **0 warnings**. This puts the reference on the Chopin.Pooling side of the divide, not the TinyPools one |
| Dependencies | **none** — the nuspec declares a single empty `<group targetFramework=".NETStandard2.0" />` |
| Licence | **MIT**, declared as a file (`<license type="file">LICENSE</license>`); the MIT text is inside the nupkg (`Copyright (c) 2026 CnCSharp-Dev`). Fully machine-readable, unlike the Chopin.Pooling reference |
| Adoption | 234 total downloads; first release 2026-03. A 2026-era library with effectively no adoption — the comparison report notes "2026-03 首发、2026-03-31 最后提交" |

Public surface, from a reflection dump of the shipped `netstandard2.0` assembly (the in-package XML
documentation agrees, and is in Chinese):

```text
-- PoolingLib.BasePool`1[TObject] --              (base: System.Object; NOT abstract, NOT sealed)
  generic constraint : where TObject : new()
  ctor               : protected ()
  field              : protected readonly ConcurrentQueue<TObject> _pool
  prop               : static BasePool<TObject> Pool  { get; }
  method             : virtual TObject Get()
  method             : virtual Void    Return(TObject obj)
  method             : virtual Void    Release(TObject obj)   [Obsolete("已更名为Return方法")]

-- PoolingLib.BasePool`2[TPool,TObject] --        (same shape; static Pool is typed TPool)
  generic constraints: where TPool : IPool<TObject>, new()   and   where TObject : new()
  ctor               : protected ()

-- interfaces
  IPool`1[TObject]      : TObject Get(); Void Return(TObject obj)
  ICapacityPool`1[TObject] : TObject Get(Int32 capacity)

-- PoolingLib.Pools.*  (14 zero-config specialized pools, each with a static `.Pool`)
  ListPool<T>/LinkedListPool<T>/QueuePool<T>/StackPool<T>/ConcurrentBagPool<T>/CollectionPool<T>/
  HashSetPool<T>/SortedSetPool<T>            (8 collections)
  Dictionaries.DictionaryPool<K,V>/SortedDictionaryPool<K,V>   (2 dictionaries)
  StringBuilderPool/MemoryStreamPool         (2 special)
  plus Get(capacity) / Get(seed IEnumerable) / Get(string) / ToArrayReturn / ToArrayRelease /
  ToStringReturn / ToStringRelease convenience overloads and PoolingLib.Extensions.PoolExtensions
  (obj.ReturnTo(pool), sb.ReturnTo(bool toString))
```

Three structural notes that matter for the reading below:

1. **The only entry point for a plain object pool is a static singleton.** `BasePool<T>`'s constructor
   is `protected` and the type is not abstract, but there is no public constructor and no factory, so
   `BasePool<T>.Pool` — one instance per closed generic type — is the sole public way in. Repeated reads
   return the same reference (verified). A derived type would add nothing to the measured path, so the
   static instance is used directly and **this column needed no adapter class at all** — a first for the
   reference columns added in this window (marklauter needed a metrics sink, Chopin needed an object
   factory, TinyPools and PowerPools needed nothing).
2. **`Release` is an obsolete alias.** The library carries both `Return(T)` and `Release(T)`, the latter
   annotated `[Obsolete("已更名为Return方法")]`. The benchmark uses the canonical `Return`. The workspace
   comparison report's §1.1 API table does not mention `Release` at all; that is an omission rather than
   an error.
3. **`Return` does not reset the object.** With the plain `BasePool<T>` a round trip hands back the very
   same instance with its state intact (`Data` read back as 42). Only the collection-specialized pools
   clear their payload inside their own `Return`. This is **level with every other row in the matrix**,
   none of which resets either, so it does not bias the comparison.

### 1.2 Fairness provisions

The rows are built to differ from the HayateOP rows in **one** dimension only — the pool implementation:

| Dimension | HayateOP rows | `PoolingLib` row |
| :--- | :--- | :--- |
| Capacity basis | `MinPoolSize = 250`, `MaxPoolSize = 300` | **the library has no capacity surface at all** — no maximum-retained bound, no min size, no soft/hard limit. Its return path is unbounded: every returned object is enqueued and never dropped. This cannot be matched and is not pretended to be; it is the single largest structural difference between this row and every other row, and it is exactly the unbounded anti-pattern the comparison report told the O-D storage backend to avoid |
| Warm-up | full borrow/return cycle up to `MaxPoolSize` | identical single-loop idiom in `GlobalSetup` (same statement shape as every other row) |
| Payload | `PooledObject` (one `int` property) | the same class, produced by the pool's own `new()` constraint (`BasePool<T>.Get()` calls `new TObject()` on a miss) |
| Optional features | `AllOff` = every toggle off; lean = toggles normalized away | none exist to switch off: the library has no validation, no eviction, no timer, no auto-scaling, no sharding, no reject policy |
| Metrics / diagnostics | `WithEnableMetrics(false)`, or lean storage | the library has no metrics, no logging and no diagnostics surface whatsoever |
| Construction | `HayatePoolBuilder<T>` | the static `Pool` property; there is no public constructor to pin anything through |
| Explicit default pinning | feature toggles written out | **nothing to pin** — there is no options object, no configuration type and no timeout. `PooledObject` satisfies `where TObject : new()` because it has an implicit parameterless constructor, which is the library's only requirement |
| Disposal | `Dispose()` in `GlobalCleanup` | nothing to dispose: the type is not `IDisposable` and offers no teardown verb. The static instance keeps its idle queue alive for the life of the process |

One bound on what these numbers can be used to claim: **the warm-up idiom is the matrix's, and it does
not fill a pool.** Every row warms up with `for (i < MaxPoolSize) pool.Return(pool.Get())`, which borrows
and returns in the same statement. At the end of `GlobalSetup` such a loop leaves the pool holding
**one** idle object, not `MaxPoolSize`. This is pre-existing matrix behaviour shared with the MEOP,
`MSL.Pool`, TinyPools, PowerPools and Chopin.Pooling rows (kept, because comparability with the archived
runs depends on the idiom not changing). It is not a problem for the measurement: the steady state is
established by BenchmarkDotNet's own warm-up iterations, verified in §1.3.

### 1.3 Steady state verified before trusting the numbers

A standalone probe (`_cbsteady/Program.cs`, workspace; same payload, same warm-up idiom, reading the
pool's private storage through reflection) establishes the read-back state and proves the create path
stays out of the measurement. `GC.GetAllocatedBytesForCurrentThread()` is an intrinsic and allocates
nothing itself:

| Probe step | Retained idle objects | Allocation |
| :--- | ---: | ---: |
| after the matrix's borrow/return warm-up loop | **1** | — |
| **1,000,000 borrow/return round trips** | **1** | **0 B total → 0.000 B/op** |
| half-split: the `Get()` half | — | **0.000 B/op** |
| half-split: the `Return()` half | — | **0.000 B/op** |
| known-answer control: `new PooledObject()` × 1,000,000 | — | 24.000 B/op (instrument trusted) |
| mechanism control: a bare `ConcurrentQueue<T>` cycled empty↔non-empty × 1,000,000 | — | **0.000 B/op** |
| create-path canary: drain the pool, then one `Get()` on an empty pool | 0 before | **248 B for that single call** |

Two independent conclusions follow. First, the storage is confirmed at runtime to be
`System.Collections.Concurrent.ConcurrentQueue<PooledObject>` — read from the live pool's `_pool` field,
not inferred from behaviour — and cycling it empty↔non-empty costs nothing, which is why the pair is
exactly zero-allocation. Second, the canary gives a **counterfactual for the measurement window**: if the
create path were reached during the measured loop the row would read ≈248 B/op instead of 0, so 0 B/op is
itself the proof that the create path is out of the window. After the full 1,000,000 pairs the pool still
retains exactly **1** idle object, i.e. it never had to create a second one.

### 1.4 The plan's stated prerequisite turned out not to be needed

The 2.8 plan annotates this item "**须 retarget ns2.0 到 bench TFM**". That step is **not required and was
not performed**: a package whose only asset is `netstandard2.0` resolves on `net10.0` by *direct*
compatible-target selection, not through the `net461` `AssetTargetFallback` that produces `NU1701` for
.NET Framework-era assets. This was verified rather than assumed — `obj/project.assets.json` lists
`lib/netstandard2.0/PoolingLib.dll` under both `compile` and `runtime` for `net10.0`, and restore reports
zero warnings, so adding `NoWarn="NU1701"` would have suppressed nothing. The analogous precedent already
in this matrix is the `Chopin.Pooling` reference (`netstandard1.6`, also no `NoWarn`), while `TinyPools`
(assets: `net40` only) is the genuine fallback case that does carry the suppression. Note that the
package's TFM selection is the *file* it ships, not the TFM the upstream project targets, so no retarget
of a source project was ever applicable to a binary reference.

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
| Matrix filter | `--filter '*HayateOpBenchmarks*'` (the head-to-head matrix only) |
| Command | `HayateOP.Benchmarks.exe --filter '*HayateOpBenchmarks*' --buildTimeout 900 --artifacts <dir>` |
| Row count | 29 (26 before this change, +3 for this column) |

> **On `--buildTimeout 900`**: a local-run workaround only, and not part of the committed benchmark
> definition. On the capture machine MSBuild node reuse and the shared compiler are disabled (they
> otherwise hold handles on the git-tracked generated XML documentation files), which pushes the
> auto-generated boilerplate build past BenchmarkDotNet's default 120 s build timeout. CI and a normal
> developer machine need no such flag and the runbook commands stay unchanged.

> **Absolute values are machine- and run-specific.** Only ratios measured *within this single run* are
> meaningful. The committed machine-readable baseline is deliberately **not** regenerated from this run
> (Q1 requires a CI-native capture), and none of these numbers should be compared against
> `baseline.json`. The contended suite is the least reproducible part of the matrix, so every
> cross-column ratio below is a same-suite, same-run comparison, never a cross-suite rank.

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

| Method                                              | Mean             | Error             | StdDev          | P50         | P90         | P95         | P99         | Median           | Ratio     | RatioSD   | Rank | Completed Work Items | Lock Contentions | Gen0   | Gen1   | Allocated | Alloc Ratio |
|---------------------------------------------------- |-----------------:|------------------:|----------------:|------------:|------------:|------------:|------------:|-----------------:|----------:|----------:|-----:|---------------------:|-----------------:|-------:|-------:|----------:|------------:|
| &#39;Concurrent-100 \| MEOP&#39; | 21,863.649 ns | 356.5883 ns | 235.8611 ns | 21,886.1 | 22,116.7 | 22,118.5 | 22,118.5 | 21,891.893 ns | 1,037.53 | 58.79 | 12 | 12.0146 | 0.0000 | 0.4578 | - | 4326 B | NA |
| &#39;Concurrent-100 \| Hayate AllOff&#39; | 139,742.249 ns | 28,185.0661 ns | 18,642.6761 ns | 131,504.9 | 157,640.3 | 176,833.0 | 176,833.0 | 131,669.116 ns | 6,631.43 | 922.08 | 14 | 24.0688 | 0.0107 | 4.6387 | 0.2441 | 45022 B | NA |
| &#39;Concurrent-100 \| Hayate Lean&#39; | 18,223.369 ns | 703.2108 ns | 465.1304 ns | 17,972.7 | 18,736.3 | 18,943.0 | 18,943.0 | 18,145.071 ns | 864.79 | 52.59 | 11 | 11.1731 | - | 0.4272 | - | 4200 B | NA |
| &#39;Concurrent-100 \| Hayate Sharded4&#39; | 1,824,090.547 ns | 1,295,003.9877 ns | 856,564.9565 ns | 1,948,761.7 | 2,370,155.5 | 3,079,521.1 | 3,079,521.1 | 1,987,222.266 ns | 86,561.75 | 39,114.44 | 16 | 19.8086 | 0.0313 | 3.9063 | - | 44296 B | NA |
| &#39;Concurrent-100 \| MSL.Pool&#39; | 115,313.750 ns | 5,745.7916 ns | 3,800.4854 ns | 113,579.2 | 119,824.8 | 123,023.4 | 123,023.4 | 113,629.297 ns | 5,472.18 | 350.18 | 14 | 13.1755 | 3.8903 | 5.1270 | - | 48449 B | NA |
| &#39;Concurrent-100 \| TinyPools&#39; | 108,297.946 ns | 3,186.5982 ns | 1,666.6525 ns | 108,234.7 | 110,651.9 | 110,651.9 | 110,651.9 | 108,589.874 ns | 5,139.25 | 296.25 | 14 | 12.3783 | 4.6140 | 1.2207 | - | 11527 B | NA |
| &#39;Concurrent-100 \| PowerPools&#39; | 100,839.111 ns | 4,135.7891 ns | 2,735.5684 ns | 100,592.7 | 104,577.9 | 104,631.2 | 104,631.2 | 100,934.613 ns | 4,785.29 | 294.04 | 14 | 11.7545 | 4.6781 | 0.3662 | - | 4224 B | NA |
| &#39;Concurrent-100 \| Chopin.Pooling&#39; | 183,646.821 ns | 3,929.7177 ns | 2,338.5120 ns | 183,740.0 | 187,504.7 | 187,504.7 | 187,504.7 | 183,740.039 ns | 8,714.91 | 497.16 | 15 | 16.7178 | 9.1689 | 0.9766 | - | 10651 B | NA |
| &#39;Concurrent-100 \| PoolingLib&#39; | 28,413.143 ns | 841.4894 ns | 556.5931 ns | 28,258.7 | 28,926.0 | 29,378.1 | 29,378.1 | 28,380.428 ns | 1,348.34 | 79.25 | 13 | 11.7890 | - | 0.4578 | - | 4258 B | NA |
| &#39;Acquire+Release \| Hayate AllOff&#39; | 287.814 ns | 37.4322 ns | 24.7591 ns | 274.8 | 323.2 | 331.6 | 331.6 | 275.738 ns | 13.66 | 1.36 | 7 | 0.0000 | - | 0.0405 | 0.0014 | 384 B | NA |
| &#39;Acquire+Release \| Hayate Lean&#39; | 27.791 ns | 1.9028 ns | 1.2586 ns | 27.8 | 29.0 | 29.3 | 29.3 | 28.075 ns | 1.32 | 0.09 | 3 | - | - | - | - | - | NA |
| &#39;Acquire+Release \| Hayate ArrayPool (O-D)&#39; | 27.741 ns | 2.2887 ns | 1.5138 ns | 28.3 | 29.0 | 30.0 | 30.0 | 28.357 ns | 1.32 | 0.10 | 3 | - | - | - | - | - | NA |
| &#39;Acquire+Release \| Hayate Sharded4&#39; | 247.984 ns | 24.2000 ns | 16.0068 ns | 244.0 | 266.2 | 270.3 | 270.3 | 247.652 ns | 11.77 | 0.98 | 7 | - | - | 0.0405 | - | 384 B | NA |
| &#39;Acquire+Release \| Hayate Full&#39; | 343.064 ns | 22.6635 ns | 13.4867 ns | 347.2 | 355.7 | 355.7 | 355.7 | 347.176 ns | 16.28 | 1.09 | 8 | - | - | 0.0439 | 0.0005 | 416 B | NA |
| &#39;Release \| Hayate AllOff&#39; | 252.521 ns | 18.3798 ns | 12.1571 ns | 248.8 | 268.0 | 269.1 | 269.1 | 249.608 ns | 11.98 | 0.87 | 7 | - | - | 0.0405 | 0.0014 | 384 B | NA |
| &#39;Release \| Hayate Lean&#39; | 26.308 ns | 1.2780 ns | 0.8453 ns | 26.1 | 27.3 | 27.4 | 27.4 | 26.259 ns | 1.25 | 0.08 | 3 | - | - | - | - | - | NA |
| &#39;AcquireAsync+Release \| Hayate AllOff&#39; | 230.924 ns | 9.3582 ns | 6.1898 ns | 230.0 | 236.9 | 239.0 | 239.0 | 230.219 ns | 10.96 | 0.67 | 7 | - | - | 0.0415 | 0.0017 | 392 B | NA |
| &#39;AcquireAsync+Release \| Hayate Lean&#39; | 64.982 ns | 3.2267 ns | 2.1343 ns | 63.9 | 67.7 | 68.9 | 68.9 | 63.964 ns | 3.08 | 0.20 | 6 | - | - | 0.0153 | - | 144 B | NA |
| &#39;AcquireAsync+Release \| Hayate Full&#39; | 243.562 ns | 9.7949 ns | 6.4787 ns | 243.8 | 249.8 | 251.3 | 251.3 | 244.403 ns | 11.56 | 0.71 | 7 | - | - | 0.0415 | 0.0005 | 392 B | NA |
| &#39;Acquire+Release \| MEOP (baseline)&#39; | 21.138 ns | 1.8809 ns | 1.2441 ns | 20.4 | 22.6 | 22.7 | 22.7 | 20.895 ns | 1.00 | 0.08 | 2 | - | - | - | - | - | NA |
| &#39;Acquire+Release \| plain new (no-pool lower bound)&#39; | 5.217 ns | 0.8004 ns | 0.5294 ns | 5.1 | 5.6 | 6.4 | 6.4 | 5.182 ns | 0.25 | 0.03 | 1 | - | - | 0.0025 | - | 24 B | NA |
| &#39;Acquire+Release \| TinyPools&#39; | 73.192 ns | 2.2305 ns | 1.3273 ns | 73.0 | 75.5 | 75.5 | 75.5 | 72.979 ns | 3.47 | 0.20 | 6 | - | - | 0.0025 | - | 24 B | NA |
| &#39;Acquire+Release \| PowerPools&#39; | 39.051 ns | 2.5866 ns | 1.7109 ns | 39.4 | 40.5 | 41.0 | 41.0 | 39.594 ns | 1.85 | 0.13 | 4 | - | - | - | - | - | NA |
| &#39;Acquire+Release \| Chopin.Pooling&#39; | 463.716 ns | 26.5463 ns | 17.5587 ns | 461.6 | 484.4 | 488.0 | 488.0 | 463.551 ns | 22.01 | 1.46 | 9 | 0.0000 | - | 0.0057 | - | 56 B | NA |
| &#39;Acquire+Release \| PoolingLib&#39; | 18.207 ns | 1.0223 ns | 0.6762 ns | 18.0 | 19.0 | 19.5 | 19.5 | 18.030 ns | 0.86 | 0.06 | 2 | - | - | - | - | - | NA |
| &#39;Release \| MEOP Return&#39; | 21.467 ns | 1.9969 ns | 1.3208 ns | 21.3 | 22.7 | 23.0 | 23.0 | 21.832 ns | 1.02 | 0.08 | 2 | - | - | - | - | - | NA |
| &#39;Release \| MSL.Pool&#39; | 569.228 ns | 27.9397 ns | 16.6265 ns | 573.1 | 597.7 | 597.7 | 597.7 | 573.114 ns | 27.01 | 1.68 | 10 | 0.0000 | - | 0.0467 | - | 440 B | NA |
| &#39;Release \| TinyPools&#39; | 55.785 ns | 2.4285 ns | 1.6063 ns | 54.9 | 57.7 | 58.2 | 58.2 | 55.399 ns | 2.65 | 0.16 | 5 | - | - | 0.0025 | - | 24 B | NA |
| &#39;Release \| PowerPools&#39; | 37.306 ns | 1.9334 ns | 1.2788 ns | 37.7 | 38.3 | 39.0 | 39.0 | 37.808 ns | 1.77 | 0.11 | 4 | - | - | - | - | - | NA |
| &#39;Release \| Chopin.Pooling&#39; | 441.375 ns | 22.7707 ns | 11.9095 ns | 444.1 | 454.6 | 454.6 | 454.6 | 444.596 ns | 20.95 | 1.28 | 9 | 0.0000 | - | 0.0057 | - | 56 B | NA |
| &#39;Release \| PoolingLib&#39; | 18.430 ns | 1.0508 ns | 0.6950 ns | 18.4 | 19.2 | 19.7 | 19.7 | 18.450 ns | 0.87 | 0.06 | 2 | - | - | - | - | - | NA |
| &#39;AcquireAsync+Release \| MSL.Pool (CT)&#39; | 572.459 ns | 23.0762 ns | 13.7322 ns | 574.3 | 592.8 | 592.8 | 592.8 | 574.254 ns | 27.17 | 1.64 | 10 | 0.0000 | - | 0.0467 | - | 440 B | NA |

## 4. Reading the PoolingLib rows

Measured in this run, with every other row measured in the same process:

| Observation | Value |
| :--- | :--- |
| `Get()` + payload write + `Return()`, single-threaded | **18.2 ns, 0 B**, ratio **0.86** against the same-run MEOP baseline row |
| `Get()` + `Return()`, single-threaded | **18.4 ns, 0 B**, ratio **0.87** |
| Position in Suite 1 | rank 2 — **the fastest pool row in the matrix**. The only faster row is `plain new` (5.2 ns), the no-pool lower bound |
| Against MEOP / `Hayate Lean` / `Hayate ArrayPool (O-D)` | 1.16× / **1.53×** / 1.52× faster |
| Against `/PowerPools` / `TinyPools` / `Chopin.Pooling` | 2.14× / 4.02× / 25.5× faster |
| Against the same-run general engine, every feature off (`AllOff`, 287.8 ns) | 15.8× faster |
| 100-thread contention, per 100-op iteration | **28.4 µs (≈284 ns/op), 4,258 B/iteration, 0.0000 lock contentions** |
| Position in Suite 4 | **fastest external row**; 1.30× slower than MEOP, 1.56× slower than `Hayate Lean` |
| Against the same-run `PowerPools` / `TinyPools` / `MSL.Pool` / `AllOff` / `Chopin.Pooling` | 3.55× / 3.81× / 4.06× / 4.92× / 6.46× faster |

Allocation under contention, decomposed the way the T8, O8 and O5 reports did — the
`Concurrent-100 | Hayate Lean` row is the zero-allocation control measured in the same run, so the
`Parallel.For` harness cost cancels:

| Row | Allocated / iteration | Minus the `Hayate Lean` row | Per operation |
| :--- | ---: | ---: | ---: |
| `Concurrent-100 \| Hayate Lean` | 4,200 B | 0 B | 0 B |
| `Concurrent-100 \| PowerPools` | 4,224 B | 24 B | 0.2 B |
| **`Concurrent-100 \| PoolingLib`** | **4,258 B** | **58 B** | **0.6 B** |
| `Concurrent-100 \| MEOP` | 4,326 B | 126 B | 1.3 B |
| `Concurrent-100 \| Chopin.Pooling` | 10,651 B | 6,451 B | 64.5 B |
| `Concurrent-100 \| TinyPools` | 11,527 B | 7,327 B | 73.3 B |
| `Concurrent-100 \| Hayate Sharded4` | 44,296 B | 40,096 B | 401.0 B |
| `Concurrent-100 \| Hayate AllOff` | 45,022 B | 40,822 B | 408.2 B |
| `Concurrent-100 \| MSL.Pool` | 48,449 B | 44,249 B | 442.5 B |

The decomposition validates itself on the two rows whose standalone probes are on record in this
project: TinyPools' delta (73.3 B/op) reproduces its probe figure (72.0 B/op) and MSL.Pool's
(442.5 B/op) reproduces its (440 B/op). PoolingLib's own delta is **0.6 B/op, below the MEOP row's
1.3 B/op** — and MEOP is a row known to be allocation-free on this path, measured through the identical
harness. That places PoolingLib's contended residual inside the harness-noise band rather than on a
per-operation allocation, so the conclusion "effectively allocation-free under contention" does not
need the dedicated residual-attribution probe the O8 column required, and none was written. As a second
bound, a pool forced to create objects during the measurement would add ≈248 B **per created round trip**
(§1.3), which no row shows; the create path is out of the measured window for every row above.

Interpretation, kept separate from the measurements above:

1. **The borrow/return pair is exactly allocation-free, on both halves.** §1.3 splits the pair with the
   per-thread counter and reads `Get()` = 0.000 B/op and `Return()` = 0.000 B/op. This is the structural
   contrast with the Chopin.Pooling column, where the *entire* 56 B/op sits on the return half because
   that port builds a dictionary key and pushes a stack node per return. The reflection dump explains
   why: `BasePool<T>` declares exactly **one** instance field (`_pool`), so there is no identity map, no
   per-object wrapper and no timestamp to allocate for.
2. **PoolingLib is the cheapest pool row in Suite 1 — including cheaper than HayateOP's lean fast path.**
   That is the honest headline, and it is uncomfortable: 18.2 ns against the lean path's 27.8 ns is a
   1.53× margin. It must be read with the variance caveat that governs every sub-50 ns row in this
   project: run-to-run variation on those rows has been documented at up to 2.4×, so same-run ratios are
   the only trustworthy ones and a 1.53× same-run margin is of the same order as that band. The
   defensible statement is "at least comparable, and plausibly faster", not a settled ranking. What the
   measurement does establish beyond the variance band is the contrast with the *general engine*: 15.8×
   against `AllOff` is far outside it, and it confirms the O1/O2/O-D design direction — a
   wrapper-free, bookkeeping-free store is the right target model. It now also means the gap between the
   lean fast path and the cheapest possible pair is a **quantified, measured** quantity (≈9.6 ns per
   round trip in this run) rather than an unknown; whether that gap is worth attacking is a scope
   decision for a future window, not a 2.8 item, and nothing here proposes one.
3. **Under contention the ranking flips, and the flip is large.** PoolingLib is the fastest *external*
   row at 28.4 µs per 100-op iteration, but it is 1.30× slower than MEOP and 1.56× slower than
   HayateOP's lean path. Its `Lock Contentions` is **0.0000** — consistent with a lock-free
   `ConcurrentQueue<T>` and with the fact that the type has no other synchronisation surface — in
   contrast to PowerPools (4.68), TinyPools (4.61), MSL.Pool (3.89) and Chopin.Pooling (9.17), all of
   which take a monitor per operation and all of which measure 3.5–6.5× slower. So the single
   `ConcurrentQueue<T>` absorbs 100-thread contention well; it simply is not faster than MEOP's
   interlocked array or the lean path.
4. **A cross-run disagreement that is reported, not smoothed.** `Concurrent-100 | Hayate Sharded4`
   measures **1.82 ms** in this run (StdDev 856 µs, Error 1.29 ms, BDN additionally emitted a
   `MinIterationTime` warning for it) against **497.6 µs** for the same row in the O5 run on the same
   machine earlier the same day — a 3.7× swing. That row is **not usable from this run** and no ratio
   involving it appears above, even though its number is printed in §3. The same applies, less
   dramatically, to the sub-100 ns rows: MEOP reads 21.1 ns here against 22.0 ns in the O5 run,
   `Hayate Lean` 27.8 against 27.1, `O-D` 27.7 against 26.9, and `Concurrent-100 | Chopin.Pooling`
   183.6 µs against 145.1 µs. BDN removed or flagged outliers on eleven of the 32 rows. This is the
   standard evidence for "compare within a run only".
5. **It is not a like-for-like workload, and the direction of the mismatch favours PoolingLib.**
   PoolingLib is an unbounded, synchronous, unobservable library: no `MaxPoolSize`, no timeout, no
   validation, no eviction, no auto-scaling, no sharding, no metrics, no logging, no DI, no async, no
   AOT annotations, and a mandatory `where TObject : new()` constraint that rules out pooling objects
   without a parameterless constructor. HayateOP's rows carry the bookkeeping that makes those features
   possible, and they pay for it. 18.2 ns buys the cheapest pair in the matrix *together with none of
   them*. "Which library has the cheapest borrow/return pair" and "which library is the better pool" are
   different questions; this matrix answers only the first, and the comparison report's remaining
   conclusions (bounded memory, async, observability, TCO) are unaffected by it.
6. **Two extrapolations in the source comparison report are falsified by these numbers**, and the
   document has been corrected accordingly (see §5). Its §3.1 predicted "低争用单操作 `CPL > HayateOP`，
   但弱于 POP 的 lean 内核" — the first half held, the second did not: measured in one run, PoolingLib
   (18.2 ns) is **2.14× faster than PowerPools** (39.1 ns), not slower. Its §0 ③ and §3.2 claimed
   "高并发 … HayateOP 全面更优" — measured, HayateOP's lean path does win the contended suite
   (18.2 µs vs 28.4 µs), but the claim does not hold against HayateOP's own general-engine rows, which
   PoolingLib beats by 4.9× (`AllOff`) and by ≈5× (`Sharded4`, on this run's unusable-but-printed
   figure). The parts of that claim that survive are the ones that are structural rather than
   performance-based: unbounded memory growth, no async, no observability.
7. **Read the suite pairs, not the ranks.** `Acquire+Release | PoolingLib` (18.2 ns) is comparable to
   `Acquire+Release | MEOP` (21.1 ns), `Hayate Lean` (27.8 ns) and `AllOff` (287.8 ns);
   `Release | PoolingLib` (18.4 ns) to `Release | MEOP Return` (21.5 ns) and `Release | Hayate Lean`
   (26.3 ns). The `Ratio` column is relative to the MEOP round-trip row for *every* row in the table,
   including rows from other suites, so it must not be read across suites.

## 5. Gate impact

- The three new rows are **external-reference rows**. `scripts/bench-compare.py` gates only entries
  whose description contains `Hayate` and is not a `Concurrent-100` row (`gated = "Hayate" in method
  and "Concurrent" not in method`), so none of them can influence pass/fail; they are reported for
  context exactly like the MEOP, plain-`new`, TinyPools, PowerPools, Chopin.Pooling and MSL.Pool rows.
- `docs/benchmarks/baseline/baseline.json` is **intentionally untouched**. The Q1 runbook requires a
  CI-native capture, and committing a local capture as the gate baseline is precisely the
  cross-environment comparison the calibration flag guards against.
- Consequence to be aware of: the CI workflow runs the `hot` category only (`--anyCategories hot`),
  while these rows carry `reference` / `concurrent` — consistent with the existing reference rows. They
  therefore appear in local full-matrix runs and in this report, but not in the CI hot-only run. If
  continuous CI visibility of the PoolingLib column is wanted, either of the two single-threaded rows
  can take an additional `hot` category or the workflow's category filter can be widened; that is a
  separate decision because it also touches what the gate compares.
- When the CI-native baseline is captured, both new single-threaded reference rows are emitted by
  `--emit-baseline` automatically as `gated: false`; no manual baseline edit is needed.
- **No `NoWarn` and no retarget were needed.** net10.0 resolves the shipped `netstandard2.0` asset
  directly, not through an asset-target fallback, and the project restore reports 0 warnings — so the
  plan's stated prerequisite "须 retarget ns2.0 到 bench TFM" was unnecessary as well as inapplicable
  (§1.4). This distinguishes the reference from TinyPools, which is the matrix's genuine fallback case.
- **Source report synced (outside this repository, so outside this commit).** The workspace document
  `HayateOP-vs-CPL-comparison-2026-09-09.md` opened by declaring "当前仓库无 HayateOP↔CPL 的 head-to-head
  基准" and used that absence to mark its performance section as architectural extrapolation. That caveat
  has been replaced with the measured figures from this run: its header note, §0 conclusion ③, §3
  preamble, §3.1, §3.2's contention row and §4.2's `C-B` entry are all updated, and the two
  extrapolations listed in §4 point 6 above are corrected in place rather than deleted. The document
  lives in the workspace, not in this repository, so it is deliberately not part of this commit.

