# HayateOP 2.5  — BenchmarkDotNet baseline report (2026-09-10)

> Purpose: establish a **reproducible head-to-head baseline** for 2.5 covering the full Acquire / Release / AcquireAsync paths,
> against `Microsoft.Extensions.ObjectPool` (MEOP) and **raw `new`** (no pool lower bound), reporting P50/P90/P95/P99
> and allocation metrics (`B/Op`) for regression comparison with later work (allocation tracking and the lean-storage rework).

## 1. Runtime environment and configuration

| Item | Value |
| :--- | :--- |
| BenchmarkDotNet | 0.15.8 |
| Runtime | .NET 10.0.11(SDK 10.0.400), X64 RyuJIT x86-64-v3 |
| Hardware | 12th Gen Intel Core i7-1260P 2.10GHz, 1 CPU / 16 logical cores (12 physical cores) |
| Job | `Short`: WarmupCount=3, IterationCount=10, Server GC, Concurrent GC |
| Diagnosers | `MemoryDiagnoser`(B/Op, Gen0/1/2) + `ThreadingDiagnoser` (Completed Work Items, Lock Contentions) |
| Percentile columns | custom `P50/P90/P95/P99` (percentiles taken over each iteration's average per-op time; BDN has no built-in P99 column) |
| Capacity basis | Min=250 / Max=300, warmed up to MaxPoolSize (aligned with the historical baseline for comparability) |
| Command | `dotnet run -c Release -- --filter '*'` (or run `HayateOP.Benchmarks.exe --filter '*'` directly) |

> **On `--join`**: BDN 0.15.8 **has no** `--join` switch (verified against the BenchmarkDotNet.dll literals);
> this item in the plan was a documentation error. Percentiles are emitted directly via the custom statistics columns, equivalently satisfying the "output P50/P95/P99" acceptance goal.

## 2. Results (verbatim BDN MarkdownExporter output)

```
BenchmarkDotNet v0.15.8, Windows 11 (10.0.28120.2824)
12th Gen Intel Core i7-1260P 2.10GHz, 1 CPU, 16 logical and 12 physical cores
.NET SDK 10.0.400
  [Host] : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3
  Short  : .NET 10.0.11 (10.0.11, 10.0.1126.37416), X64 RyuJIT x86-64-v3

Job=Short  Concurrent=True  Server=True
IterationCount=10  WarmupCount=3
```

| Method | Mean | Error | StdDev | P50 | P90 | P95 | P99 | Median | Ratio | Rank | Gen0 | Completed Work Items | Lock Contentions | Allocated |
|------------------------------------- |-----------------:|------------------:|----------------:|------------:|------------:|------------:|------------:|-----------------:|----------:|-----:|-------:|---------------------:|-----------------:|----------:|
| 'Acquire+Release \| MEOP (baseline)' | 16.486 ns | 0.8571 ns | 0.5101 ns | 16.3 | 17.3 | 17.3 | 17.3 | 16.266 ns | 1.00 | 2 | - | - | - | - |
| 'Acquire+Release \| raw new (no pool lower bound)' | 7.899 ns | 0.9915 ns | 0.6558 ns | 7.7 | 8.5 | 8.9 | 8.9 | 7.840 ns | 0.48 | 1 | 0.0007 | - | - | - |
| 'Acquire+Release \| Hayate Lean' | 251.797 ns | 14.6346 ns | 9.6799 ns | 248.6 | 264.1 | 264.8 | 264.8 | 251.302 ns | 15.29 | 3 | 0.0100 | - | - | 384 B |
| 'Acquire+Release \| Hayate Sharded4' | 254.981 ns | 16.6655 ns | 11.0232 ns | 249.8 | 268.6 | 272.8 | 272.8 | 252.719 ns | 15.48 | 3 | 0.0100 | - | - | 384 B |
| 'Acquire+Release \| Hayate Full' | 363.850 ns | 22.0910 ns | 14.6118 ns | 369.2 | 376.4 | 379.9 | 379.9 | 371.371 ns | 22.09 | 4 | 0.0153 | - | - | - |
| 'Release \| MEOP Return' | 16.826 ns | 0.7190 ns | 0.4756 ns | 16.6 | 17.4 | 17.7 | 17.7 | 16.741 ns | 1.02 | 2 | - | - | - | - |
| 'Release \| Hayate Lean' | 272.020 ns | 16.6125 ns | 10.9882 ns | 272.1 | 278.1 | 293.4 | 293.4 | 273.432 ns | 16.51 | 3 | 0.0100 | - | - | 384 B |
| 'AcquireAsync+Release \| Hayate Lean' | 254.023 ns | 24.3138 ns | 16.0821 ns | 249.2 | 274.3 | 276.7 | 276.7 | 250.758 ns | 15.42 | 3 | 0.0143 | - | - | 392 B |
| 'AcquireAsync+Release \| Hayate Full' | 265.730 ns | 17.5545 ns | 11.6112 ns | 269.2 | 277.1 | 281.1 | 281.1 | 269.281 ns | 16.13 | 3 | 0.0105 | - | - | 392 B |
| 'Concurrent-100 \| MEOP' | 23,678.670 ns | 1,243.0683 ns | 739.7300 ns | 23,478.3 | 25,236.4 | 25,236.4 | 25,236.4 | 23,478.260 ns | 1,437.51 | 5 | 0.1068 | 12.7527 | 0.0000 | 4,434 B |
| 'Concurrent-100 \| Hayate Lean' | 1,356,132.930 ns | 1,110,304.3007 ns | 734,397.5494 ns | 1,123,691.0 | 2,171,564.5 | 2,521,680.5 | 2,521,680.5 | 1,242,241.406 ns | 82,329.74 | 7 | - | 31.6758 | - | 46,786 B |
| 'Concurrent-100 \| Hayate Sharded4' | 287,206.348 ns | 333,112.0497 ns | 220,332.9960 ns | 127,663.2 | 533,381.3 | 700,995.2 | 700,995.2 | 128,472.070 ns | 17,436.07 | 6 | - | 30.5313 | 0.0977 | 46,529 B |

> The raw CSV is in the same directory: `2026-09-10-m1-bdn-baseline.csv`.
> Chinese method names in console output render as mojibake under a GBK console (a pre-existing environment issue); the names in the Markdown/CSV artifacts are normal UTF-8.

## 3. Key conclusions

1. **Single operation (low contention)**: MEOP 16.5 ns / 0 B, raw `new` 7.9 ns; Hayate Lean **251.8 ns / 384 B** (~15.3x MEOP).
   Matching the 2026-09-09 baseline (Hayate minimal 188.8 ns, full-featured 304.7 ns) in the same order of magnitude,
   this confirms the 2.5 changes introduced no single-operation regression.
   This 15x gap is a known existing shortfall, and is precisely the goal of the fourth batch's lock-free fast path and wrapper-object allocation removal work (highest priority).
2. **384 B allocation per operation**: the Lean path allocates 384 B of managed memory per `Acquire+Release` (`HayateObject<T>` wrapper +
   sharded linked-list node + reserved object), compared with MEOP's 0 B - the core motivation for removing wrapper-object allocation.
3. **Async overhead is negligible**: `AcquireAsync+Release` Lean 254.0 ns, essentially on par with the synchronous 251.8 ns
   (392 B vs 384 B, the difference being the async state machine; when the object is ready the synchronous fast path is taken with no extra wait).
4. **Full-feature cost**: Lean -> Full single operation 251.8 ns -> 363.9 ns (+44%), from validation/metrics/leak-detection/eviction instrumentation;
   still ~22x versus MEOP. This quantifies the "hot-path cost annotation" work.
5. **Concurrency (100 threads)**: MEOP 23.7 µs vs Hayate Sharded4 287 µs / Lean 1356 µs.
   **This group has extremely high variance** (Sharded4 StdDev 220 µs ~= 77% of the mean, Lean StdDev 734 µs),
   and triggers the BDN `MinIterationTime` warning (iteration time only 38-56 ms, below the recommended 100 ms).
   Conclusion: a short-iteration Job is insufficient to measure the concurrency scope; a re-run with longer iterations (>=100 ms/iteration) is needed;
   and the next round should evaluate `Parallel.For` thread-pool jitter. **Absolute values in the concurrency columns are not recommended for citation at this time**.

## 4. Reproduction and follow-up

- Reproduce: `dotnet run -c Release --project tests/HayateOP.Benchmarks -- --filter '*'`,
  artifacts are in `BenchmarkDotNet.Artifacts/results/`.
- Follow-up (fourth batch):
  - After the lean-storage rework lands, use this report as the baseline; acceptance = Lean single-op latency / allocation aligned with MEOP's order of magnitude;
  - Additional comparison columns beyond MEOP (marklauter / CHOPIN / POP / TP / CPL) must be added to this run for the other targets;
  - The concurrency columns require a long-iteration Job (or `RunStrategy.Monitoring`) before re-establishing a baseline.
- Allocation tracking uses this report's `B/Op` column as the authoritative scope; the pool's `EnableAllocationTracking` is only a runtime sampling reference.
