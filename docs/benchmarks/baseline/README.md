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
   lean rows are measured across a ~1.9× run-to-run range locally, so they run 30/60).
5. Reference rows (`MEOP`, plain `new`) are reported for context and never gated.
6. Results are written to the job summary; the full BenchmarkDotNet report is uploaded
   as a workflow artifact.

## Gate mode: calibration vs enforcing

The workflow reads `baseline.json`'s `calibration` flag to pick its mode:

| `calibration` | Behaviour |
| :--- | :--- |
| `true` (current) | Advisory: `failMeanPercent` / `allocFailBytes` regressions are **reported** (`--no-fail`), because the committed baseline was captured on the maintainer's local machine and comparing a GitHub runner against it is a cross-environment comparison (measured up to 2.4× run-to-run spread on the sub-50 ns rows). |
| `false` | Enforcing: mean and allocation regressions **fail the job**, like any required check. |

The calibration flag is therefore the switch: committing a CI-native baseline removes it
and hardens the gate in the same commit. No workflow change is needed.

## Capturing the CI-native baseline (one-time runbook)

1. On GitHub: **Actions → Performance regression → Run workflow → `update_baseline` = true.**
   The job runs the hot-path benchmarks on the runner and writes a complete, committable
   `baseline.json` (schema, thresholds, `calibration: false`, capture metadata) to the
   **`ci-baseline`** artifact.
2. Download the artifact, replace `docs/benchmarks/baseline/baseline.json` with it, and commit.
3. The next workflow run reports `calibration=false` and enforces the gate.

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

Thresholds live in `baseline.json`. The concurrent suite stays excluded because its
run-to-run variance exceeds the thresholds; the sub-100 ns lean rows carry 30/60 overrides
per the PG4 noise measurements. After the first CI-native capture lands, tighten the
overrides with real CI variance data if it allows.
