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
5. Reference rows (`MEOP`, plain `new`) are reported for context and never gated.
6. Results are written to the job summary; the full BenchmarkDotNet report is uploaded
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
>   renamed comes back without its overrides; rename and re-capture in the same change.

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

Thresholds live in `baseline.json`. The `concurrent` category is never gated (only the `hot`
category is run in CI) because its run-to-run variance exceeds the thresholds; the sub-100 ns
lean rows carry 30/60 overrides per the PG4 noise measurements. The first CI-native capture
(2026-09-21) confirmed the delivered picture — all three asynchronous rows came in below the
previous local figures — but it does not yet carry enough repeats to justify tightening the
overrides; keep 30/60 until CI variance data allows it. The 2026-09-22 re-capture keeps 30/60
for the same reason: its own `Release | Hayate Lean` came in 14% above the previous capture,
inside the range those overrides exist to absorb and a point under the global warning bar.
