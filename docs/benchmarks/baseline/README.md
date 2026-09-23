# Performance baseline

Machine-readable performance baseline consumed by the `perf-regression` GitHub Actions
workflow (`.github/workflows/perf-regression.yml`) through `scripts/bench-compare.py`.

## Files

| File | Purpose |
| :--- | :--- |
| `baseline.json` | Committed baseline: per-benchmark mean / stddev / percentiles / allocated bytes plus the gate thresholds. |

## How the gate works

1. The workflow builds `tests/HayateOP.Benchmarks` in `Release` and runs the `hot`
   benchmark category (single-threaded `Acquire+Release`, `Release`, `AcquireAsync+Release`
   for the Lean / Sharded4 / Full configurations). The `concurrent` category is never gated.
2. `scripts/bench-compare.py` reads the produced `*-report.csv`, matches rows to
   `baseline.json` by benchmark description, and compares the mean against the baseline.
3. Entries with `gated: true` are checked:
   - mean regression `>= warnMeanPercent` → warning
   - mean regression `>= failMeanPercent` → failure
   - allocation growth `>= allocWarnBytes` → warning
   - allocation growth `>= allocFailBytes` → failure
4. A target may carry `warnMeanPercentOverride` / `failMeanPercentOverride`, which replace
   the global thresholds for that benchmark alone (PG4's convergence outcome: the sub-100 ns
   lean rows are measured across a ~1.9× run-to-run range locally, so they run 30/60). A
   benchmark report cannot express them, so `--emit-baseline-file` inherits them from the
   baseline it replaces, matched by method name.
5. A target may carry `allocGateExempt: true` when its allocated byte count is not
   reproducible across runs or hosts. The allocation delta is then reported as an advisory
   notice instead of a pass/fail signal; the **mean-time gate still applies** to that row.
   `ALLOC_GATE_EXEMPT_METHODS` in `scripts/bench-compare.py` is the single source of truth
   (each entry carries the measurement that justifies it) and `allocGateExemptReason` is
   written into `baseline.json` for readers. Like `gated` / `excluded`, the flag is derived
   from the method name, so a re-capture cannot drop it. Three rows carry it — see
   [Allocation gate exemptions](#allocation-gate-exemptions).
6. Reference rows (`MEOP`, plain `new`) are reported for context and never gated.
7. Results are written to the job summary; the full BenchmarkDotNet report is uploaded
   as a workflow artifact.

## Gate mode: calibration vs enforcing

The workflow reads `baseline.json`'s `calibration` flag to pick its mode:

| `calibration` | Behaviour |
| :--- | :--- |
| `true` | Advisory: `failMeanPercent` / `allocFailBytes` regressions are **reported** (`--no-fail`). This was the seeded state while the committed baseline came from the maintainer's local machine, so comparing a GitHub runner against it was a cross-environment comparison (measured up to 2.4× run-to-run spread on the sub-50 ns rows). |
| `false` (**current**) | Enforcing: mean and allocation regressions **fail the job**, like any required check. |

The calibration flag is therefore the switch: committing a CI-native baseline removes it
and hardens the gate in the same commit. No workflow change is needed.

The gate has been enforcing since the first CI-native capture (2026-09-21); the current file is
the 2026-09-22 re-capture. That first capture predated the emit path carrying per-target
thresholds, so the three sub-100 ns lean rows had to have their 30/60 overrides re-stamped by
hand after it. The emit path inherits them now — see the note in the runbook below.

## Capturing the CI-native baseline (runbook)

1. On GitHub: **Actions → Performance regression → Run workflow → `update_baseline` = true.**
   The job runs the hot-path benchmarks on the runner and writes a complete, committable
   `baseline.json` (schema, thresholds, `calibration: false`, capture metadata) to the
   **`ci-baseline`** artifact. The per-target thresholds are inherited from the `baseline.json`
   the capture replaces, so the artifact carries them too.
2. Download the artifact, replace `docs/benchmarks/baseline/baseline.json` with it, and commit.
3. The next workflow run reports `calibration=false` and enforces the gate.

> **The emit path carries the per-target thresholds forward.** `--emit-baseline-file` builds the
> file through `build_baseline_json()`, which reads the baseline named by `--baseline` (the
> committed one by default) and copies `warnMeanPercentOverride` / `failMeanPercentOverride`
> onto the emitted entry with the same method name. A re-capture therefore keeps the three
> sub-100 ns lean rows at 30/60 instead of reverting them to the global 15/30 — a fallback whose
> own run-to-run spread those rows exceed, so ordinary noise would fail the gate. Two caveats:
>
> - **Capture with the committed `baseline.json` in place.** If `--baseline` does not resolve, the
>   emit path prints a `::warning::` and writes a file without the overrides. Re-stamp them by
>   hand before committing — that is what `Acquire+Release | Hayate Lean`, `Release | Hayate Lean`
>   and `AcquireAsync+Release | Hayate Lean` need (warn 30 / fail 60).
> - **A renamed benchmark counts as a new row.** Inheritance is by method name, so a row that is
>   renamed comes back without its overrides; rename and re-capture in the same change. The
>   allocation-gate exemption below is matched by method name too, so a rename drops it as well
>   — and `ALLOC_GATE_EXEMPT_METHODS` has to be updated alongside.

## Allocation gate exemptions

Three rows measure an allocated byte count that is not a property of the code, so the
allocation gate cannot be a pass/fail signal for them. Reproduced on 2026-09-23 while
preparing the 3.0 re-capture:

| Row | Observation |
| :--- | :--- |
| `Acquire+Release \| Hayate Full` | 0 B on the CI runner, **416 B** on a workstation in 3 out of 3 runs. |
| `AcquireAsync+Release \| Hayate Lean` | **0 B / 113 B / 439 B** for the same binary, depending on which benchmarks ran earlier in the process. The committed baseline reads 0 B and the 2026-09-23 CI run reads 113 B — CI against CI, so the spread is not merely cross-environment. |
| `AcquireAsync+Release \| Hayate Full` | 392 B when run alone, **928 B** after the full `hot` suite, 0 B on CI. The committed baseline (captured on CI) reads 392 B while a later CI run reads 0 B. |

Both async rows allocate through continuations, and their byte count tracks which benchmarks
ran earlier in the same process rather than the code under test; the synchronous `Full` row's
absolute byte count is host-specific. The precise mechanism (BenchmarkDotNet's per-benchmark
allocation accounting versus pool state left behind by earlier benchmarks) is not pinned down,
so the exemption records the observation, not a causal claim.

Exempting these rows costs only the allocation signal on them. Their mean-time gate still
enforces — verified by a counter-run in which a +40% mean on an exempt row still fails the job
— and the allocation delta still appears in the job summary, as a notice.

Local captures stay useful for spot checks, but never commit a local capture as the gate
baseline — that is exactly the cross-environment comparison the calibration flag guards
against:

```bash
# local spot check only
dotnet build tests/HayateOP.Benchmarks -c Release
./tests/HayateOP.Benchmarks/bin/Release/net10.0/HayateOP.Benchmarks.exe --anyCategories hot
python scripts/bench-compare.py            # compare against the committed baseline
python scripts/bench-compare.py --emit-baseline-file candidate.json   # never commit this
```

## Thresholds

| Threshold | Global default | Meaning |
| :--- | :--- | :--- |
| `warnMeanPercent` | 15 | mean slowdown that raises a warning |
| `failMeanPercent` | 30 | mean slowdown that fails the gate |
| `warnMeanPercentOverride` | – | per-target warning threshold (replaces the global for that row) |
| `failMeanPercentOverride` | – | per-target failure threshold (replaces the global for that row) |
| `allocWarnBytes` | 0 | allocated-bytes growth that raises a warning |
| `allocFailBytes` | 64 | allocated-bytes growth that fails the gate |
| `allocGateExempt` | – | per-target: the row's allocation delta is advisory only, never a gate |

Thresholds live in `baseline.json`. The `concurrent` category is never gated (only the `hot`
category is run in CI) because its run-to-run variance exceeds the thresholds; the sub-100 ns
lean rows carry 30/60 overrides per the PG4 noise measurements. The first CI-native capture
(2026-09-21) confirmed the delivered picture — all three asynchronous rows came in below the
previous local figures — but it does not yet carry enough repeats to justify tightening the
overrides; keep 30/60 until CI variance data allows it. The 2026-09-22 re-capture keeps 30/60
for the same reason: its own `Release | Hayate Lean` came in 14% above the previous capture,
inside the range those overrides exist to absorb and a point under the global warning bar.
