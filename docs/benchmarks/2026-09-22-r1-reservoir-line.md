# HayateOP — R-1: `Reservoir` reference column and the 1/4/8/16-worker concurrency ladder (2026-09-22)

> Purpose: add the `thomhurst/Reservoir` head-to-head line (plan item **G-3**, research batch
> **`33-R-1`**) and, alongside it, a worker-count ladder that shows how each pool's per-operation
> cost behaves as concurrency rises. Both are **reference rows, not gate rows** — see §2. The ladder
> exists because the question "can a `Concurrent-N` row be gated?" cannot be answered from a single
> 100-thread figure: it needs the *shape* of the curve and the *spread* at each point. 3.1 reads
> this report to make that decision.
>
> **Scope discipline**: the `Reservoir` package reference is added to `tests/HayateOP.Benchmarks`
> **only**. It is not a dependency of any shipped package — the core package's zero-dependency
> design is deliberate, and this reference must never reach it. This mirrors how PowerPools, Chopin,
> TinyPools, MSL.Pool and PoolingLib are referenced.
>
> **Comparability**: these numbers were taken on a **local 16-logical-core host**, not on a GitHub
> Actions runner. Absolute nanoseconds are **not** comparable with `baseline/baseline.json` or with
> the figures in `docs/hot-path-costs.md` §1. Only ratios *within* this report should be read.

## 1. What was added

| Row (BDN description) | Suite | What it measures |
| :--- | :--- | :--- |
| `Acquire+Release \| Reservoir` | Suite 1 — single-threaded synchronous | `Rent()`, a payload write, then `Return()` |
| `Concurrent-100 \| Reservoir` | Suite 4 — contention | 100-thread `Parallel.For` rent/write/return round trip |
| `Concurrent-N \| MEOP` | Suite 5 — ladder | 100,000 pairs split across 1/4/8/16 workers |
| `Concurrent-N \| Hayate AllOff` | Suite 5 — ladder | same |
| `Concurrent-N \| Hayate Lean` | Suite 5 — ladder | same |
| `Concurrent-N \| Hayate Sharded4` | Suite 5 — ladder | same |
| `Concurrent-N \| Reservoir` | Suite 5 — ladder | same |

`Reservoir` cannot appear in Suite 3 (asynchronous `AcquireAsync+Release`): the library exposes **no
asynchronous API** — `Rent()` is the only acquire path. Suite 3 is therefore N/A for this line, which
mirrors how MEOP, plain `new`, TinyPools and PowerPools are N/A there.

**Package**: `Reservoir` **1.9.1** (MIT, Tom Longhurst, `github.com/thomhurst/Reservoir`), zero
dependencies, shipping `netstandard2.0` / `net8.0` / `net10.0` assets. Because a `net10.0` asset
exists, the benchmarks project resolves `lib/net10.0/Reservoir.dll` directly: no asset-target
fallback, no `NU1701`, and therefore **no `NoWarn`** — the same situation as the PowerPools and
Chopin references, and unlike TinyPools (which needs `NoWarn="NU1701"`).

**API choice**: the two-parameter `ObjectPool<T, TPolicy>` is used with a `struct ReservoirPolicy`
rather than the one-parameter `ObjectPool<T>`. The package's own documentation states that
`ObjectPool<T>` **boxes** a struct policy and that performance-sensitive callers should use the
two-parameter form; since this row exists to be a fair head-to-head line, the cheaper form is the
only honest one. `Return()` resets through the policy, so this is the one row in Suite 1 that pays a
user-defined reset on every return — the same shape as the other policy-driven columns.

## 2. Why none of this is a gate row

Two independent mechanisms keep these rows out of the gate, and either one alone would be enough:

1. **Category.** The performance gate runs `--anyCategories hot`. Suite 5 is
   `[BenchmarkCategory("concurrent")]`; the two Reservoir rows are `reference` / `concurrent`. The
   `hot` category was re-listed after this change and still contains **exactly the same 10 rows** as
   before it, so no baseline re-capture is triggered and **no gate number moves**.
2. **Name.** `scripts/bench-compare.py` marks a row `excluded` when its rendered method name contains
   `Concurrent` (`bench-compare.py:192-193`), independently of the category. So even if a future
   change added these rows to `hot`, they would stay out of the comparison.

## 3. How the ladder is measured

Each case performs a **fixed total of 100,000 borrow/return pairs** (`TierTotalOps`) split evenly
across `threads` workers, with `OperationsPerInvoke = TierTotalOps` so the reported `Mean` is
**wall-clock nanoseconds per borrow/return pair** and is directly comparable with Suite 1's
single-threaded figures. `MaxDegreeOfParallelism` is pinned to the tier and the `ParallelOptions`
instances are hoisted, so the harness does not re-plan the loop per invocation.

**The first design was wrong and was discarded.** It ran one borrow/return pair per `Parallel.For`
iteration, which measured **771.8 ns at a single worker** against a real pair cost of roughly 10 ns —
about 98% `Parallel.For` scheduling. Amortising a fixed work quantity per iteration is what makes the
ladder readable at all. The discarded figures are still in the working log and are cited here only as
the reason for the redesign; do not quote them.

`Description` is rendered **verbatim** by BDN — there is no `{threads}` placeholder support (verified:
it prints literally). The tier is therefore carried by BDN's auto-generated `threads` column, and all
five rows share one description, `Concurrent-N | <impl>`.

## 4. Results

Job `Short` (IterationCount=10, WarmupCount=3, Server GC, concurrent), .NET 10.0.11, X64 RyuJIT
x86-64-v3, 16 logical processors. `Mean` and `StdDev` in ns; `Allocated` per operation.

| Row | 1 worker | 4 workers | 8 workers | 16 workers | Allocated |
| :--- | ---: | ---: | ---: | ---: | :--- |
| `Concurrent-N \| MEOP` | 25.5 (1.00) | 144.5 (1.30) | 167.5 (1.46) | 193.7 (1.90) | — |
| `Concurrent-N \| Hayate AllOff` | 297.5 (16.69) | 616.4 (46.50) | 821.8 (49.41) | 1,120.4 (55.30) | 384 B |
| `Concurrent-N \| Hayate Lean` | 29.2 (0.90) | 114.3 (1.20) | 141.7 (2.80) | 165.9 (5.11) | — |
| `Concurrent-N \| Hayate Sharded4` | 271.4 (7.42) | 638.0 (45.90) | 872.2 (71.94) | 1,394.1 (164.44) | 384 B |
| `Concurrent-N \| Reservoir` | 26.1 (0.61) | **9.0** (0.16) | 13.6 (0.29) | 16.3 (1.04) | — |

Ratio of the 16-worker figure to the same row's own 1-worker figure: MEOP **7.59×**, AllOff **3.77×**,
Lean **5.69×**, Sharded4 **5.14×**, Reservoir **0.63×**.

The `Allocated` column is **indicative only and must not be gated on**. Under `Parallel.For` the
benchmark body runs on thread-pool workers, so the memory diagnoser does not attribute cleanly — note
that `Hayate AllOff` reports `Gen0` collections at 1 worker yet `-` allocated. The 384 B figures do
match the known per-operation allocation of the AllOff and Sharded4 paths in Suite 1, which is the
only reason they are reproduced here.

## 5. Reading the results

The metric is wall-clock time **per operation**, so a row that scales should get *cheaper* as workers
are added: perfect scaling would put the 16-worker figure at roughly 1/16 of the 1-worker figure.
Nothing reaches that, and it is **not reachable in practice** on a 16-logical-core host — SMT siblings,
memory bandwidth and `Parallel.For` scheduling all intervene. The value of the column is the
**relative ordering**, not the distance from the theoretical bound.

With that caveat:

- **Reservoir is the only row whose per-operation cost ever falls below its own single-worker cost.**
  It drops 26.1 → 9.0 ns going to 4 workers, then rises gently to 13.6 and 16.3 ns but stays below
  where it started. Its striped store converts added workers into throughput.
- **Every HayateOP configuration and MEOP degrade monotonically**, and by 16 workers they are 3.8× to
  7.6× *worse* than their own serial figure — added workers buy contention, not throughput. Sharded4
  degrades as much as AllOff does despite carrying four shards, so at these worker counts the sharding
  is not converting parallelism into throughput.
- The consequence for anyone sizing a pool: HayateOP's per-pair cost is roughly **6–19 ns of serial
  work plus a contention term that grows with worker count**, while Reservoir's is roughly **26 ns of
  work that mostly parallelises away**.

This is the architectural difference the research predicted, now measured rather than extrapolated.
It is not a verdict on the two designs, because the two are buying different things: HayateOP carries
validation, generation eviction, diagnostics and the ownership registry on the borrow/return path,
and Reservoir carries none of them.

## 6. What 3.1 should read from this

The gating question is not "which is fastest" but "is the spread small enough that a regression
threshold means something". Relative standard deviation (`StdDev / Mean`) per tier:

| Row | 1 | 4 | 8 | 16 |
| :--- | ---: | ---: | ---: | ---: |
| MEOP | 3.9% | 0.9% | 0.9% | 1.0% |
| Hayate AllOff | 5.6% | 7.5% | 6.0% | 4.9% |
| Hayate Lean | 3.1% | 1.1% | 2.0% | 3.1% |
| Hayate Sharded4 | 2.7% | 7.2% | 8.3% | 11.8% |
| Reservoir | 2.4% | 1.8% | 2.1% | 6.4% |

- **Lean and MEOP stay under ~4% at every tier** — a `Concurrent-N` row for those two would be
  gateable on spread alone.
- **Sharded4 is the worst and gets worse with workers** (2.7% → 11.8%). At 16 workers a mean
  regression threshold would be swamped by its own noise.
- **AllOff sits at 5–7.5% throughout**, which is marginal.

**⚠️ Before any `Concurrent-N` row is gated, the description collision must be fixed.**
`bench-compare.py` keys rows by the rendered benchmark description (`bench-compare.py:145`), and the
four tiers of each implementation share **one** description (`Concurrent-N | <impl>`). Gating them as
they stand would let the four tiers overwrite each other in the comparison — last write wins, with no
error. The description has to become unique per tier first. This is recorded in
`HayateOP-3.1-plan-2026-09-22.md` §一.1.1 and in the Suite 5 comment block in
`tests/HayateOP.Benchmarks/Program.cs`.

## 7. Reproducing

From the repository root, with the build environment sourced:

```
dotnet build tests/HayateOP.Benchmarks/HayateOP.Benchmarks.csproj -c Release
tests/HayateOP.Benchmarks/bin/Release/net10.0/HayateOP.Benchmarks.exe \
    --filter '*ConcurrentTier*' --job Short
```

The full 20-case ladder (5 rows × 4 tiers) takes about five minutes on the host used here. BDN writes
its own `BenchmarkDotNet.Artifacts/results/HayateOpBenchmarks-report.csv`; the run behind this report
is committed as `2026-09-22-r1-reservoir-line.csv`.

## See also

- `docs/benchmarks/baseline/baseline.json` — the gated numbers. Nothing in this report touches them.
- `docs/benchmarks/baseline/README.md` — how the gate thresholds and the re-capture flow work.
- `docs/hot-path-costs.md` §5 — the pooling break-even line, and why a cheap-to-create object is a
  net loss regardless of which pool wins here.
- The other reference columns: `2026-09-18-t8-tinypools-line.md`, `2026-09-18-o8-powerpools-line.md`,
  `2026-09-18-o5-chopin-line.md`, `2026-09-18-n5-marklauter-line.md`,
  `2026-09-18-cb-poolinglib-line.md`.
