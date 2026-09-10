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
   - any increase in allocated bytes → warning
4. Reference rows (`MEOP`, plain `new`) are reported for context and never gated.
5. Results are written to the job summary; the full BenchmarkDotNet report is uploaded
   as a workflow artifact.

## Environment caveat (`calibration`)

Absolute benchmark numbers are machine dependent. The seeded `baseline.json` was captured
on the maintainer's local Windows machine, so comparing it against a GitHub-hosted runner
is a **cross-environment** comparison. While `"calibration": true`:

- the workflow runs in advisory mode (`continue-on-error`), and
- the job summary states that the thresholds are still being calibrated.

To remove the caveat, capture a CI-native baseline once and commit it:

```bash
# locally, from the repository root
dotnet build tests/HayateOP.Benchmarks -c Release
./tests/HayateOP.Benchmarks/bin/Release/net10.0/HayateOP.Benchmarks.exe --anyCategories hot
python scripts/bench-compare.py --emit-baseline > docs/benchmarks/baseline/baseline.json
```

The workflow also exposes an `update_baseline` manual input that runs the emit step on the
runner; copy the printed JSON into `baseline.json` and open a pull request.

## Thresholds

| Threshold | Default | Meaning |
| :--- | :--- | :--- |
| `warnMeanPercent` | 15 | mean slowdown that raises a warning |
| `failMeanPercent` | 30 | mean slowdown that fails the gate |
| `allocWarnBytes` | 0 | allocated-bytes delta that raises a warning |
| `allocFailBytes` | 64 | allocated-bytes delta that fails the gate |

Thresholds live in `baseline.json` and are tuned in the calibration phase; the concurrent
suite stays excluded because its run-to-run variance exceeds the thresholds.
