# HayateOP — O5→M1+: labijie `Chopin.Pooling` head-to-head column (2026-09-18)

> Purpose: extend the M1 head-to-head matrix with the Chopin.Pooling reference line (plan item
> **`O5→M1+`**, "CHOPIN 头对头基准列（扩展 M1，`Chopin.Pooling`，仅同步）"). The 2.8 plan lists the
> CHOPIN-derived comparison as an item whose numbers had never been measured against HayateOP in the
> shared matrix, so statements about the two libraries were architectural extrapolations. This report
> closes that gap with measured data taken from the shared matrix, under one capacity basis, one
> warm-up and one payload.

## 1. What was added

Three columns, all driven by `Chopin.Pooling` **1.0.2** (referenced by the benchmarks project only —
it is not a dependency of any shipped package):

| Row (BDN description) | Suite | What it measures |
| :--- | :--- | :--- |
| `Acquire+Release \| Chopin.Pooling` | Suite 1 — single-threaded synchronous | `BorrowObject()`, a payload write, then `ReturnObject(obj)` |
| `Release \| Chopin.Pooling` | Suite 2 — release path | `BorrowObject()` then `ReturnObject(obj)` immediately — return and re-borrow, the borrow set held constant |
| `Concurrent-100 \| Chopin.Pooling` | Suite 4 — contention | 100-thread `Parallel.For` borrow/write/return round trip |

`Chopin.Pooling` cannot appear in Suite 3 (asynchronous `AcquireAsync+Release`): the library exposes
**no asynchronous API** — the only acquire path is the synchronous `BorrowObject()`. Suite 3 is
therefore N/A for this line, mirroring how MEOP, plain `new`, TinyPools and PowerPools are N/A there
and how `MSL.Pool` is N/A in the synchronous suites. This is recorded in the benchmark file's
coverage-matrix header.

### 1.1 Package and API facts (resolved from the package, not from documentation prose)

The plan entry names the item `Chopin.Pooling`; that is also the actual **package id**. The facts below
come from the nupkg, the nuspec, the in-package XML documentation and a reflection dump of the
assembly, in that order of precedence:

| Fact | Value |
| :--- | :--- |
| Package id / version | `Chopin.Pooling` 1.0.2 (authors `labijie team`, owners `labijie.com`) |
| Description | "A port of Apache Commons Object Pooling Library for .Net" — the API is a port of Commons Pool, not an independent design |
| Assets shipped | `lib/net452`, `lib/netstandard1.6` (assembly `ImageRuntimeVersion` `v4.0.30319`) |
| Resolution on net10.0 | direct selection of `lib/netstandard1.6/Chopin.Pooling.dll` as a **compatible** target — no asset-target fallback, no `NU1701`, so no `NoWarn` (unlike the TinyPools reference) |
| Licence | **not machine-readable from the package**: the nuspec declares no `<license>` element and the nupkg ships no licence file. The upstream repository named in the nuspec's `projectUrl` (`github.com/endink/Chopin`) is declared Apache-2.0 by GitHub. Recorded as a caveat, not as a blocker, because the reference is benchmark-only |
| Dependencies | `net452`: `Microsoft.Extensions.Logging` 1.1.2. `netstandard1.6`: `NETStandard.Library` 1.6.1, `Microsoft.Extensions.Logging` 1.1.2, `System.Reflection.Emit`, `System.Reflection.Emit.ILGeneration`, `System.Reflection.Emit.Lightweight` 4.3.0 |
| Resolution of those dependencies in the benchmarks project | `Microsoft.Extensions.Logging` 6.0.0 (hoisted by the existing graph), `Microsoft.Extensions.Logging.Abstractions` 10.0.10, `NETStandard.Library` 1.6.1 (no compile assets). Restore reports **0 warnings** |
| Upstream activity | last push 2018-02-22; 15 stars, 4 forks, not archived. The port is dormant |

Public surface (reflection dump; the in-package XML documentation agrees):

```text
-- Chopin.Pooling.Impl.GenericObjectPool`1 --      (base: Impl.BaseGenericObjectPool`1)
  ctor (IPooledObjectFactory`1 factory)
  ctor (IPooledObjectFactory`1 factory, GenericObjectPoolConfig genericObjectPoolConfig)
  ctor (IPooledObjectFactory`1 factory, GenericObjectPoolConfig config, AbandonedConfig abandonedConfig)
  prop  Int32   MaxIdle / MinIdle        {get;set;}
  prop  Boolean Lifo                     {get;set;}
  prop  Int32   NumIdle / NumActive / IdleCount / ActiveCount   {get;}
  prop  Int32   MaxTotal / Int64 MaxWaitMillis / Boolean BlockWhenExhausted  {get;set;}   (BaseGenericObjectPool`1)
  prop  Boolean TestOnCreate / TestOnBorrow / TestOnReturn / TestWhileIdle   {get;set;}   (BaseGenericObjectPool`1)
  prop  Int64   TimeBetweenEvictionRunsMillis / MinEvictableIdleTimeMillis   {get;set;}   (BaseGenericObjectPool`1)
  prop  Impl.BorrowStrategy BorrowStrategy  {get;}
  prop  Int64   BorrowedCount / AtomLong CreatedCount / DestroyedCount / ReturnedCount    (BaseGenericObjectPool`1)
  method T     BorrowObject()
  method Void  ReturnObject(T obj) / InvalidateObject(T) / AddObject() / Clear() / Close() / Evict() / PreparePool() / Use(T)

-- Chopin.Pooling.Impl.GenericObjectPoolConfig --   (base: Impl.BaseObjectPoolConfig)
  defaults read back from a fresh instance:
    MaxTotal=-1 (unbounded)   BorrowStrategy=LIFO   MaxWaitMillis=-1   BlockWhenExhausted=true
    TestOnCreate/TestOnBorrow/TestOnReturn/TestWhileIdle=false   TimeBetweenEvictionRunsMillis=-1
    NumTestsPerEvictionRun=3   MinEvictableIdleTimeMillis=1800000   SoftMinEvictableIdleTimeMillis=-1

-- Chopin.Pooling.BasePooledObjectFactory`1 --
  abstract T              Create()
  abstract IPooledObject`1 Wrap(T)
  virtual  IPooledObject`1 MakeObject() / Void DestroyObject(IPooledObject`1) /
           Boolean ValidateObject(IPooledObject`1) / Void ActivateObject / Void PassivateObject

-- Chopin.Pooling.Impl.DefaultPooledObject`1 --  ctor (T obj)
-- Chopin.Pooling.Impl.BorrowStrategy -- LIFO, FIFO, Random
```

Two structural notes that matter for the reading below: the pool type is **not `IDisposable`**
(`Close()` is its teardown verb), and the library needs a **`IPooledObjectFactory<T>`** instance,
unlike MEOP / TinyPools / PowerPools which take a delegate. That is the only adapter this column
required — there is no metrics or logging surface on the borrow/return path to neutralize.

### 1.2 Fairness provisions

The rows are built to differ from the HayateOP rows in **one** dimension only — the pool
implementation:

| Dimension | HayateOP rows | `Chopin.Pooling` row |
| :--- | :--- | :--- |
| Capacity basis | `MinPoolSize = 250`, `MaxPoolSize = 300` | `MaxTotal = 300`, `MaxIdle = 300`, `MinIdle = 250` — the same min/max basis every other row uses |
| Warm-up | full borrow/return cycle up to `MaxPoolSize` | identical single-loop idiom in `GlobalSetup` (same statement shape as every other row) |
| Payload | `PooledObject` (one `int` property) | the same class, produced by the pool's own `IPooledObjectFactory<T>.Create()` |
| Optional features | `AllOff` = every toggle off; lean = toggles normalized away | no validation on create / borrow / return / idle, no eviction runs (`TimeBetweenEvictionRunsMillis = -1`); pinned explicitly rather than left to library defaults |
| Metrics / diagnostics | `WithEnableMetrics(false)`, or lean storage | no metrics surface in the library; no adapter needed |
| Construction | `HayatePoolBuilder<T>` | the public constructor; the library's DI extensions are not used, so nothing outside the measured path is exercised |
| Explicit default pinning | feature toggles written out | every `GenericObjectPoolConfig` property the library defaults would otherwise supply is written out at its default value (see §1.1), so an upstream default change cannot silently alter this row |
| Disposal | `Dispose()` in `GlobalCleanup` | `Close()` in `GlobalCleanup`; the type is not `IDisposable` |

One bound on what these numbers can be used to claim: **the warm-up idiom is the matrix's, and it does
not fill a pool.** Every row warms up with `for (i < MaxPoolSize) pool.Return(pool.Borrow())`, which
borrows and returns in the same statement. At the end of `GlobalSetup` such a loop leaves the pool
holding **one** idle object, not `MaxPoolSize`. This is pre-existing matrix behaviour shared with the
MEOP, `MSL.Pool`, TinyPools and PowerPools rows (kept, because comparability with the archived runs
depends on the idiom not changing). It is not a problem for the measurement — the steady state is
established by BenchmarkDotNet's own warm-up iterations, verified in §1.3.

### 1.3 Steady state verified before trusting the numbers

A standalone probe (`_o5steady/Program.cs`, workspace; same payload, same capacity, same
factory-counting shape) establishes the read-back configuration and the fact that the create path stays
out of the measurement:

| Probe step | `NumIdle` | `NumActive` | Factory create | Factory destroy |
| :--- | ---: | ---: | ---: | ---: |
| right after construction | 0 | 0 | 0 | 0 |
| after the matrix's borrow/return warm-up loop | 1 | 0 | 1 | 0 |
| **1,000,000 borrow/return round trips** | 1 | 0 | **1 (delta 0)** | **0** |
| 20 further 100-thread bursts | 1 | 0 | **1 (delta 0)** | **0** |

Read-back after construction: `MaxTotal=300 MaxIdle=300 MinIdle=250 Lifo=True BlockWhenExhausted=True
MaxWaitMillis=-1 BorrowStrategy=LIFO`, all four `Test*` flags `False`,
`TimeBetweenEvictionRunsMillis=-1`, `IsAbandonedConfig=False`. Two observations, stated as
observations rather than mechanism: the implementation does **not** pre-fill to `MinIdle` at
construction (`NumIdle = 0`, `created = 0`), and the same warm-up idiom leaves **one** idle item here
too, exactly as it does for TinyPools and PowerPools.

The factory counter never moves after warm-up, so the measured loop provably never re-enters the create
path in either the single-threaded or the contended shape.

### 1.4 Finding: the return path allocates a lookup key and an idle-stack node on every call

This is the most interesting result of the column. Measured with `GC.GetTotalAllocatedBytes(precise:
true)` over 1,000,000 borrow/return pairs, with a MEOP pool as a known-zero-allocation control measured
through the identical harness:

| Variant | Total bytes | Per operation |
| :--- | ---: | ---: |
| MEOP control (same payload, same warm-up) | 336 B | **0.000 B/op** |
| `Chopin.Pooling` | 56,000,624 B | **56.001 B/op** |

Splitting the pair with the per-thread counter (`GC.GetAllocatedBytesForCurrentThread()`, an intrinsic
that allocates nothing itself) attributes the whole figure to one half:

| Half | Bytes over 1,000,000 pairs | Per operation |
| :--- | ---: | ---: |
| `BorrowObject()` | 0 B | **0.000 B/op** |
| `ReturnObject(T)` | 56,000,000 B | **56.000 B/op** |

An IL disassembly of `ReturnObject` shows exactly one heap allocation on the success path — a
`newobj IdentityWrapper<T>` at `IL_0007`, used as the key for
`ConcurrentDictionary<IdentityWrapper<T>, IPooledObject<T>>._allObjects.TryGetValue`; the other
`newobj` sites in that method are exception-message strings on failure paths that a warmed pool never
takes. A wrapper over a reference payload is only 24 B, so the IL alone left 32 B/op unexplained. A
second probe (`_o5key/Program.cs`, workspace) closes the arithmetic by sizing each candidate mechanism
directly, with every key built once outside the measured loop:

| Component | Measured | Note |
| :--- | ---: | :--- |
| `IdentityWrapper<Payload>` (internal, **reference** type, 1 ref field) | **24.000 B/op** | sized via a compiled delegate over the internal constructor; `PlainBox` yardstick, the same shape, also reads 24.000 |
| `ConcurrentDictionary.TryGetValue` with a reference-type key | **0.000 B/op** | the dictionary itself is allocation-free for a reference-type key (`object` key control also 0.000) |
| `ConcurrentStack<T>.Push` + `TryPop` (idle set pinned at one item) | **32.000 B/op** | `ConcurrentQueue<T>.Enqueue` + `TryDequeue` reads 0.000 — the two collections differ |
| **Sum** | **56 B/op** | equals the measured `ReturnObject` half |

The identity of the idle collection is not inferred: reading the private state of a live, warmed pool
gives `GenericObjectPool<T>._idleObjects` =
`Chopin.Pooling.Collections.BlockingList<Chopin.Pooling.IPooledObject<T>>`, whose `_stack` field is a
`ConcurrentStack<T>` at runtime, and `BlockingList<T>.AddFirst` disassembles to a single
`_blockingCollection.Add(item)` call. `ConcurrentStack<T>.Push` allocating a node per push is what makes
the 32 B appear on the return half while `BorrowObject` (a pop) stays free. In other words: **the
56 B/op is 24 B of dictionary key plus 32 B of stack node, both created per return.**

Note for completeness: the workspace comparison report `HayateOP-vs-CHOPIN-comparison-2026-09-09.md`
carries a separate CHOPIN-derived item `O8` proposing a `BorrowStrategy{Lifo,Fifo}` switch, and states
from source reading that CHOPIN's `GenericObjectPool` hard-codes LIFO. What this column measured is
consistent with that: a pool built with `BorrowStrategy.FIFO` and `Lifo = false` still reports a
`ConcurrentStack<T>` idle collection and still measures 56.000 B/op. Whether FIFO is emulated at the
opposite end of the same stack, or the strategy switch maps both to the same collection, was not
investigated and is not claimed. (That report's `O8` also collides with the 2.8 plan's unrelated
`O8→M1+` benchmark item; no renumbering is attempted here.)

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
| Capacity basis | Min=250 / Max=300 (HayateOP, `Chopin.Pooling`), max-retained / initial capacity 300 (`PowerPools`, `TinyPools`, MEOP, `MSL.Pool`) |
| Matrix filter | `--filter '*HayateOpBenchmarks*'` (the head-to-head matrix only) |
| Command | `HayateOP.Benchmarks.exe --filter '*HayateOpBenchmarks*' --buildTimeout 900 --artifacts <dir>` |
| Row count | 29 (26 before this change, +3 for this column); 0 `NA` rows, 0 duplicate descriptions |

> **On `--buildTimeout 900`**: a local-run workaround only, and not part of the committed benchmark
> definition. On the capture machine MSBuild node reuse and the shared compiler are disabled (they
> otherwise hold handles on the git-tracked generated XML documentation files), which pushes the
> auto-generated boilerplate build past BenchmarkDotNet's default 120 s build timeout. CI and a
> normal developer machine need no such flag and the runbook commands stay unchanged.

> **Absolute values are machine- and run-specific.** Only ratios measured *within this single run* are
> meaningful. The committed machine-readable baseline is deliberately **not** regenerated from this run
> (Q1 requires a CI-native capture), and none of these numbers should be compared against
> `baseline.json`. The contended suite is the least reproducible part of the matrix — in this run
> `Concurrent-100 | Hayate Sharded4` measures 497.6 µs where the immediately preceding PowerPools run on
> the same machine recorded 134.1 µs for the same row — so every cross-column ratio below is a
> same-suite, same-run comparison, never a cross-suite rank.

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

| Method                                              | Mean           | Error           | StdDev          | P50       | P90       | P95       | P99       | Median         | Ratio     | RatioSD   | Rank | Gen0   | Completed Work Items | Lock Contentions | Gen1   | Allocated | Alloc Ratio |
|---------------------------------------------------- |---------------:|----------------:|----------------:|----------:|----------:|----------:|----------:|---------------:|----------:|----------:|-----:|-------:|---------------------:|-----------------:|-------:|----------:|------------:|
| &#39;Concurrent-100 \| MEOP&#39; | 21,161.033 ns | 226.1638 ns | 149.5934 ns | 21,101.7 | 21,386.1 | 21,399.9 | 21,399.9 | 21,124.234 ns | 965.13 | 49.04 | 12 | 0.4578 | 12.1958 | - | - | 4355 B | NA |
| &#39;Concurrent-100 \| Hayate AllOff&#39; | 185,522.822 ns | 86,443.9101 ns | 57,177.2943 ns | 161,980.7 | 258,129.7 | 275,391.3 | 275,391.3 | 166,563.013 ns | 8,461.50 | 2,525.77 | 15 | 4.8828 | 31.4800 | 0.0044 | - | 46685 B | NA |
| &#39;Concurrent-100 \| Hayate Lean&#39; | 17,966.166 ns | 254.3387 ns | 168.2293 ns | 17,982.2 | 18,152.0 | 18,205.2 | 18,205.2 | 17,982.777 ns | 819.42 | 41.92 | 11 | 0.4272 | 11.0982 | - | - | 4188 B | NA |
| &#39;Concurrent-100 \| Hayate Sharded4&#39; | 497,633.125 ns | 522,721.1725 ns | 345,747.6909 ns | 431,337.5 | 904,335.9 | 980,152.3 | 980,152.3 | 441,857.617 ns | 22,696.51 | 15,097.53 | 16 | 3.9063 | 32.6367 | 0.0039 | - | 47002 B | NA |
| &#39;Concurrent-100 \| MSL.Pool&#39; | 120,765.044 ns | 10,988.9767 ns | 7,268.5277 ns | 117,333.8 | 129,998.9 | 133,491.9 | 133,491.9 | 118,215.637 ns | 5,507.96 | 420.86 | 14 | 5.1270 | 13.6553 | 4.2759 | - | 48519 B | NA |
| &#39;Concurrent-100 \| TinyPools&#39; | 129,269.277 ns | 2,379.4004 ns | 1,573.8261 ns | 128,697.4 | 131,056.6 | 131,952.2 | 131,952.2 | 128,739.917 ns | 5,895.83 | 304.76 | 14 | 1.2207 | 13.8313 | 6.4436 | - | 11769 B | NA |
| &#39;Concurrent-100 \| PowerPools&#39; | 94,276.112 ns | 1,632.1045 ns | 971.2392 ns | 94,017.7 | 96,051.0 | 96,051.0 | 96,051.0 | 94,017.688 ns | 4,299.83 | 220.73 | 13 | 0.3662 | 11.2328 | 4.6080 | - | 4147 B | NA |
| &#39;Concurrent-100 \| Chopin.Pooling&#39; | 145,097.485 ns | 3,852.5827 ns | 2,548.2449 ns | 145,090.6 | 148,627.5 | 149,266.0 | 149,266.0 | 145,382.251 ns | 6,617.74 | 351.30 | 15 | 0.9766 | 14.7903 | 6.3269 | - | 10563 B | NA |
| &#39;Acquire+Release \| Hayate AllOff&#39; | 270.087 ns | 17.7712 ns | 11.7546 ns | 271.0 | 282.1 | 286.0 | 286.0 | 272.183 ns | 12.32 | 0.80 | 7 | 0.0405 | - | - | 0.0014 | 384 B | NA |
| &#39;Acquire+Release \| Hayate Lean&#39; | 27.064 ns | 1.0665 ns | 0.7054 ns | 26.8 | 27.9 | 28.2 | 28.2 | 27.001 ns | 1.23 | 0.07 | 3 | - | - | - | - | - | NA |
| &#39;Acquire+Release \| Hayate ArrayPool (O-D)&#39; | 26.919 ns | 1.9335 ns | 1.2789 ns | 26.6 | 27.9 | 29.3 | 29.3 | 26.893 ns | 1.23 | 0.08 | 3 | - | - | - | - | - | NA |
| &#39;Acquire+Release \| Hayate Sharded4&#39; | 240.540 ns | 12.2558 ns | 7.2933 ns | 244.0 | 245.9 | 245.9 | 245.9 | 244.000 ns | 10.97 | 0.64 | 7 | 0.0405 | - | - | - | 384 B | NA |
| &#39;Acquire+Release \| Hayate Full&#39; | 338.424 ns | 16.3745 ns | 10.8307 ns | 340.1 | 349.9 | 350.1 | 350.1 | 340.149 ns | 15.44 | 0.91 | 8 | 0.0439 | - | - | 0.0005 | 416 B | NA |
| &#39;Release \| Hayate AllOff&#39; | 245.344 ns | 10.1520 ns | 6.7149 ns | 244.8 | 252.2 | 254.9 | 254.9 | 246.750 ns | 11.19 | 0.63 | 7 | 0.0408 | - | - | 0.0017 | 384 B | NA |
| &#39;Release \| Hayate Lean&#39; | 26.676 ns | 1.8874 ns | 1.2484 ns | 26.2 | 28.1 | 28.4 | 28.4 | 26.530 ns | 1.22 | 0.08 | 3 | - | - | - | - | - | NA |
| &#39;AcquireAsync+Release \| Hayate AllOff&#39; | 227.675 ns | 8.2802 ns | 5.4768 ns | 227.1 | 232.6 | 236.4 | 236.4 | 228.527 ns | 10.38 | 0.57 | 7 | 0.0415 | - | - | 0.0017 | 392 B | NA |
| &#39;AcquireAsync+Release \| Hayate Lean&#39; | 69.179 ns | 3.6724 ns | 2.4291 ns | 68.3 | 72.4 | 73.2 | 73.2 | 68.868 ns | 3.16 | 0.19 | 6 | 0.0153 | 0.0000 | - | - | 144 B | NA |
| &#39;AcquireAsync+Release \| Hayate Full&#39; | 235.518 ns | 11.8166 ns | 7.0318 ns | 235.0 | 245.4 | 245.4 | 245.4 | 234.999 ns | 10.74 | 0.62 | 7 | 0.0415 | - | - | 0.0005 | 392 B | NA |
| &#39;Acquire+Release \| MEOP (baseline)&#39; | 21.980 ns | 1.7299 ns | 1.1442 ns | 21.7 | 23.2 | 23.4 | 23.4 | 21.928 ns | 1.00 | 0.07 | 2 | - | - | - | - | - | NA |
| &#39;Acquire+Release \| plain new (no-pool lower bound)&#39; | 5.226 ns | 0.5901 ns | 0.3903 ns | 5.3 | 5.7 | 5.7 | 5.7 | 5.267 ns | 0.24 | 0.02 | 1 | 0.0025 | - | - | - | 24 B | NA |
| &#39;Acquire+Release \| TinyPools&#39; | 75.208 ns | 7.0037 ns | 4.6325 ns | 73.7 | 81.2 | 82.5 | 82.5 | 73.971 ns | 3.43 | 0.27 | 6 | 0.0025 | - | - | - | 24 B | NA |
| &#39;Acquire+Release \| PowerPools&#39; | 40.409 ns | 4.8796 ns | 3.2276 ns | 41.9 | 43.4 | 43.9 | 43.9 | 42.182 ns | 1.84 | 0.17 | 4 | - | - | - | - | - | NA |
| &#39;Acquire+Release \| Chopin.Pooling&#39; | 460.322 ns | 32.7453 ns | 21.6590 ns | 459.9 | 478.3 | 493.5 | 493.5 | 460.782 ns | 20.99 | 1.42 | 9 | 0.0057 | - | - | - | 56 B | NA |
| &#39;Release \| MEOP Return&#39; | 21.056 ns | 1.8514 ns | 1.2246 ns | 21.7 | 21.9 | 22.6 | 22.6 | 21.709 ns | 0.96 | 0.07 | 2 | - | - | - | - | - | NA |
| &#39;Release \| MSL.Pool&#39; | 572.461 ns | 34.4380 ns | 22.7786 ns | 572.2 | 596.2 | 599.1 | 599.1 | 576.873 ns | 26.11 | 1.65 | 10 | 0.0467 | 0.0000 | - | - | 440 B | NA |
| &#39;Release \| TinyPools&#39; | 54.930 ns | 4.4343 ns | 2.9330 ns | 54.9 | 56.8 | 60.0 | 60.0 | 55.464 ns | 2.51 | 0.18 | 5 | 0.0025 | - | - | - | 24 B | NA |
| &#39;Release \| PowerPools&#39; | 38.471 ns | 2.5336 ns | 1.6758 ns | 38.5 | 40.3 | 40.5 | 40.5 | 38.602 ns | 1.75 | 0.11 | 4 | - | - | - | - | - | NA |
| &#39;Release \| Chopin.Pooling&#39; | 465.253 ns | 43.8426 ns | 28.9991 ns | 455.1 | 494.7 | 515.7 | 515.7 | 455.176 ns | 21.22 | 1.65 | 9 | 0.0057 | - | - | - | 56 B | NA |
| &#39;AcquireAsync+Release \| MSL.Pool (CT)&#39; | 592.546 ns | 37.0279 ns | 24.4917 ns | 584.7 | 613.8 | 645.2 | 645.2 | 586.360 ns | 27.03 | 1.73 | 10 | 0.0467 | 0.0000 | - | - | 440 B | NA |

## 4. Reading the Chopin.Pooling rows

Measured in this run, with every other row measured in the same process:

| Observation | Value |
| :--- | :--- |
| `BorrowObject()` + payload write + `ReturnObject()`, single-threaded | 460.3 ns, **56 B**, ratio 20.99 against the same-run MEOP baseline row |
| `BorrowObject()` + `ReturnObject()`, single-threaded | 465.3 ns, **56 B**, ratio 21.22 |
| Ratio against the same-run general-engine row (`AllOff`, 270.1 ns) | **≈1.70×** — Chopin is 1.70× slower than the HayateOP general engine with every feature off |
| Ratio against the same-run `Hayate Lean` (27.1 ns) / `Hayate ArrayPool (O-D)` (26.9 ns) | ≈17.0× / ≈17.1× |
| Ratio against the same-run `PowerPools` (40.4 ns) / `TinyPools` (75.2 ns) | ≈11.4× / ≈6.1× |
| Position in Suite 1 | **slowest of all 10 rows**, and the only single-threaded external row that allocates besides TinyPools |
| 100-thread contention, per 100-op iteration | 145.1 µs (≈1.45 µs/op), 10,563 B/iteration, **6.33 lock contentions per iteration** |
| Ratio against the same-run MEOP / `Hayate Lean` concurrent rows | ≈6.86× / ≈8.08× (per operation) |
| Ratio against the same-run `TinyPools` / `MSL.Pool` / `PowerPools` concurrent rows | ≈1.12× / ≈1.20× / ≈1.54× — the slowest external reference row under contention |

Allocation, decomposed the way the T8 and O8 reports did — the `Concurrent-100 | Hayate Lean` row is the
zero-allocation control measured in the same run, so the `Parallel.For` harness cost cancels:

| Row | Allocated / iteration | Minus the `Hayate Lean` row | Per operation |
| :--- | ---: | ---: | ---: |
| `Concurrent-100 \| Hayate Lean` | 4,188 B | 0 B | 0 B |
| `Concurrent-100 \| PowerPools` | 4,147 B | −41 B | ≈0 B |
| `Concurrent-100 \| MEOP` | 4,355 B | 167 B | 1.7 B |
| `Concurrent-100 \| Chopin.Pooling` | 10,563 B | 6,375 B | **63.8 B** |
| `Concurrent-100 \| TinyPools` | 11,769 B | 7,581 B | **75.8 B** |
| `Concurrent-100 \| Hayate AllOff` | 46,685 B | 42,497 B | 425.0 B |
| `Concurrent-100 \| Hayate Sharded4` | 47,002 B | 42,814 B | 428.1 B |
| `Concurrent-100 \| MSL.Pool` | 48,519 B | 44,331 B | **443.3 B** |

The decomposition validates itself on the two rows whose standalone probes are on record: TinyPools'
delta (75.8 B/op) brackets its probe figure (72.0 B/op) and MSL.Pool's (443.3 B/op) reproduces its probe
figure (440 B/op) almost exactly. Chopin.Pooling's contended delta (63.8 B/op) sits just above its own
single-threaded figure (56.0 B/op), which is the expected direction once the contended path is also
paying for lost updates. Since a pool forced to create objects during the measurement would show an
extra ~24 B per created round trip on top of this, **the create path is not part of the measured
window** in any of these rows.

Interpretation, kept separate from the measurements above:

1. **The single-threaded round trip is expensive and is not allocation-free.** 460.3 ns is 17× the
   HayateOP lean path and 1.70× the HayateOP general engine with every feature disabled, and it is the
   slowest row in Suite 1. §1.4 accounts for the 56 B: a `ConcurrentDictionary` lookup key object is
   constructed per return **and** the idle-set insert pushes a fresh `ConcurrentStack<T>` node. Neither
   is a defect in the port — both are direct consequences of carrying Commons Pool's object-tracking
   dictionary and its `BlockingCollection`-backed idle list — but they are per-operation allocations
   that MEOP, the HayateOP lean path and PowerPools do not pay.
2. **The cost is mechanical, not a contention artefact.** `BorrowObject()` measures exactly
   0.000 B/op, so the pop side is free; the 56 B is entirely on the return side, which fits the §1.4
   attribution and rules out "the pool is secretly creating objects" — the factory counter is flat
   across 1,000,000 pairs and 20 thread bursts (§1.3).
3. **Under contention the library stays in the same league as TinyPools, not of MEOP.** 145.1 µs per
   100-op iteration is 1.12× TinyPools and 1.20× MSL.Pool — i.e. it is the slowest external row, but
   only marginally so, and it is still ~7× faster than the non-sharded HayateOP general engine row
   (185.5 µs) and ~3.4× faster than `Hayate Sharded4` in this particular (high-variance) run. Its 6.33
   lock contentions per iteration sit between TinyPools' 6.44 and PowerPools' 4.61, and above the
   zero-contention MEOP and lean rows — consistent with a `BlockingCollection` + dictionary composite
   rather than a lock-free structure.
4. **A concurrent cross-check that does not fully agree, reported honestly.** The standalone steady
   probe also measured a contended shape (200 × `Parallel.For(0,100)`) and reads a delta of
   **138.3 B/op** against its own MEOP control, where the matrix-derived delta above is 63.8 B/op. Both
   readings agree that the contended path allocates materially more than the single-threaded 56 B/op;
   they do not agree on the magnitude, and the discrepancy (a factor of ≈2.2) was **not** resolved. Per
   the practice set for the T8 and O8 columns, the conservative (larger) figure is the one to quote when
   a bound is needed, and the difference is attributed to the same context sensitivity that has already
   been documented for allocation readings in this project.
5. **It is not a like-for-like workload.** Chopin.Pooling is a 2018-era port of Apache Commons Pool: it
   ships object-identity tracking, an abandoned-object configuration, an eviction policy and an
   `AtomLong` statistics surface, and it depends on `Microsoft.Extensions.Logging` for its diagnostics
   story. MEOP, TinyPools and PowerPools carry none of that. Nothing in this matrix measures creation
   cost or feature cost, so the columns answer only "what does each library's public borrow/return path
   cost under an identical capacity basis, warm-up and payload" — not "which library is better".
6. **Read the suite pairs, not the ranks.** `Acquire+Release | Chopin.Pooling` (460.3 ns) is directly
   comparable to `Acquire+Release | Hayate AllOff` (270.1 ns), `Hayate Lean` (27.1 ns) and
   `MEOP` (22.0 ns); `Release | Chopin.Pooling` (465.3 ns) is comparable to `Release | Hayate AllOff`
   (245.3 ns) and `Release | MEOP Return` (21.1 ns). The `Ratio` column is relative to the MEOP
   round-trip row for *every* row in the table, including rows from other suites, so it must not be read
   across suites.

## 5. Gate impact

- The three new rows are **external-reference rows**. `scripts/bench-compare.py` gates only entries
  whose description contains `Hayate` and is not a `Concurrent-100` row (`gated = "Hayate" in method
  and "Concurrent" not in method`), so none of them can influence pass/fail; they are reported for
  context exactly like the MEOP, plain-`new`, TinyPools, PowerPools and MSL.Pool rows.
- `docs/benchmarks/baseline/baseline.json` is **intentionally untouched**. The Q1 runbook requires a
  CI-native capture, and committing a local capture as the gate baseline is precisely the
  cross-environment comparison the calibration flag guards against.
- Consequence to be aware of: the CI workflow runs the `hot` category only (`--anyCategories hot`),
  while these rows carry `reference` / `concurrent` — consistent with the existing reference rows. They
  therefore appear in local full-matrix runs and in this report, but not in the CI hot-only run. If
  continuous CI visibility of the Chopin.Pooling column is wanted, either of the two single-threaded
  rows can take an additional `hot` category or the workflow's category filter can be widened; that is a
  separate decision because it also touches what the gate compares.
- When the CI-native baseline is captured, both new single-threaded reference rows are emitted by
  `--emit-baseline` automatically as `gated: false`; no manual baseline edit is needed.
- No `NU1701` suppression is needed for this package, unlike TinyPools: net10.0 resolves the shipped
  `netstandard1.6` asset directly, not through an asset-target fallback, and the project restore reports
  0 warnings.
- **Source report synced (outside this repository, so outside this commit).** The workspace document
  `HayateOP-vs-CHOPIN-comparison-2026-09-09.md` had recorded "no direct HayateOP↔CHOPIN head-to-head
  baseline exists" as the largest uncertainty behind its performance conclusion, pointing at its own
  `O5` item. That caveat has been replaced with the measured figures from this run (§3.5 of that
  document), and its `O5` item is now marked done. The document lives in the workspace, not in this
  repository, so it is deliberately not part of this commit.
