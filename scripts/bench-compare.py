#!/usr/bin/env python3
"""Compare a BenchmarkDotNet CSV report against the committed performance baseline.

Usage
-----
    python scripts/bench-compare.py [report.csv] [--baseline FILE]
    python scripts/bench-compare.py [--emit-baseline | --emit-baseline-file PATH] [--no-fail]

Defaults
--------
    report.csv   first match of BenchmarkDotNet.Artifacts/results/*-report.csv
    --baseline   docs/benchmarks/baseline/baseline.json

Exit codes
----------
    0  no regression beyond the failure threshold (or --no-fail)
    1  at least one gated benchmark regressed beyond the failure threshold
    2  input error (report or baseline missing / unreadable)

The script only gates entries flagged ``gated: true`` and never gates entries flagged
``excluded: true`` (e.g. the high-variance concurrent suite). Reference rows are printed
for context.

Per-target overrides (PG4 calibration outcome): a target may carry optional
``warnMeanPercentOverride`` / ``failMeanPercentOverride`` values that replace the global
thresholds for that benchmark alone. Use them for rows whose measured run-to-run variance
exceeds the global threshold (e.g. the sub-100 ns lean rows). A benchmark report cannot
express them, so ``--emit-baseline-file`` inherits them from the baseline it is replacing
(matched by method name) instead of silently reverting those rows to the global defaults.
"""

from __future__ import annotations

import argparse
import csv
import datetime
import glob
import io
import json
import os
import re
import sys

TIME_UNITS_TO_NS = {
    "ns": 1.0,
    "us": 1_000.0,
    "\u00b5s": 1_000.0,   # µs
    "\u03bcs": 1_000.0,   # μs (greek mu)
    "ms": 1_000_000.0,
    "s": 1_000_000_000.0,
}

SIZE_UNITS_TO_BYTES = {
    "B": 1.0,
    "KB": 1024.0,
    "MB": 1024.0 * 1024.0,
    "GB": 1024.0 * 1024.0 * 1024.0,
}

# Per-target keys that carry a hand-tuned decision rather than a measurement. A benchmark
# report cannot express them, so a fresh capture has to inherit them from the baseline it is
# replacing; `gated` / `excluded` are deliberately absent because they are derived from the
# method name in `build_baseline_json`. Dropping these is not cosmetic: the PG4 30/60
# overrides on the three sub-100 ns lean rows came back as the global 15/30 on every
# re-capture and failed the gate on ordinary run-to-run noise.
INHERITED_TARGET_KEYS = ("warnMeanPercentOverride", "failMeanPercentOverride")

_NUMBER_RE = re.compile(r"^([+-]?[0-9][0-9,\.]*)")


def _to_number(text: str):
    """Parse a leading number, tolerating thousands separators, or return None."""
    if text is None:
        return None
    text = text.strip().strip("'\"")
    if text in ("", "NA", "?", "-"):
        return None
    match = _NUMBER_RE.match(text)
    if not match:
        return None
    try:
        return float(match.group(1).replace(",", ""))
    except ValueError:
        return None


def parse_duration_ns(text: str):
    if text is None:
        return None
    body = text.strip().strip("'\"")
    if body in ("", "NA", "?"):
        return None
    parts = body.split()
    value = _to_number(parts[0])
    if value is None:
        return None
    unit = parts[1] if len(parts) > 1 else "ns"
    return value * TIME_UNITS_TO_NS.get(unit, 1.0)


def parse_bytes(text: str):
    if text is None:
        return None
    body = text.strip().strip("'\"")
    if body in ("", "NA", "?"):
        return None
    parts = body.split()
    value = _to_number(parts[0])
    if value is None:
        return None
    unit = parts[1] if len(parts) > 1 else "B"
    return value * SIZE_UNITS_TO_BYTES.get(unit, 1.0)


def normalize_method(name: str) -> str:
    return (name or "").strip().strip("'\"").strip()


def escape_cell(text: str) -> str:
    """Escape characters that would break a Markdown table cell."""
    return text.replace("|", "\\|")


def find_report(explicit: str | None) -> str | None:
    if explicit:
        return explicit if os.path.isfile(explicit) else None
    candidates = sorted(glob.glob("BenchmarkDotNet.Artifacts/results/*-report.csv"))
    if not candidates:
        candidates = sorted(glob.glob("BenchmarkDotNet.Artifacts/results/*.csv"))
    return candidates[0] if candidates else None


def read_report(path: str):
    """Return {method: {'meanNs': float|None, 'allocatedBytes': float|None, 'row': dict}}."""
    with io.open(path, "r", encoding="utf-8-sig", newline="") as handle:
        reader = csv.DictReader(handle)
        fieldnames = [f.strip() for f in (reader.fieldnames or [])]
        if "Method" not in fieldnames or "Mean" not in fieldnames:
            raise ValueError(
                "Unexpected CSV layout; expected 'Method' and 'Mean' columns, got: %s" % fieldnames
            )
        results = {}
        for row in reader:
            normalized = { (k or "").strip(): v for k, v in row.items() if k is not None }
            method = normalize_method(normalized.get("Method", ""))
            if not method:
                continue
            results[method] = {
                "meanNs": parse_duration_ns(normalized.get("Mean")),
                "allocatedBytes": parse_bytes(normalized.get("Allocated")),
                "row": normalized,
            }
    return results


def load_baseline(path: str):
    with io.open(path, "r", encoding="utf-8") as handle:
        return json.load(handle)


def format_delta(actual, reference):
    if actual is None or reference is None or reference == 0:
        return "n/a"
    return "%+.1f%%" % ((actual - reference) / reference * 100.0)


def build_baseline_json(report_path: str, results, previous=None):
    """Build a complete, committable baseline JSON from a report (Q1: CI-native capture).

    The emitted file keeps the full schema the comparator consumes - thresholds, the
    calibration flag (fresh CI captures start enforced), capture metadata and per-target
    percentiles when the report carries them - so it can replace ``baseline.json`` as-is.

    ``previous`` is the baseline being replaced. Every per-target key in
    ``INHERITED_TARGET_KEYS`` is carried over from the entry with the same method name,
    because a report cannot express a hand-tuned threshold; without that, re-capturing
    silently reverts those rows to the global defaults. Returns ``(json_text, inherited)``
    where ``inherited`` maps each method to the keys it received.
    """
    inheritable = {}
    for entry in (previous or {}).get("targets", []):
        carried = {key: entry[key] for key in INHERITED_TARGET_KEYS if key in entry}
        if carried:
            inheritable[normalize_method(entry.get("method", ""))] = carried

    targets = []
    inherited = {}
    for method, data in results.items():
        if data["meanNs"] is None:
            continue
        row = data["row"]
        gated = "Hayate" in method and "Concurrent" not in method
        excluded = "Concurrent" in method
        target = {
            "method": method,
            "gated": gated,
            "meanNs": round(data["meanNs"], 3),
            "allocatedBytes": int(data["allocatedBytes"]) if data["allocatedBytes"] is not None else None,
        }
        std_dev = parse_duration_ns(row.get("StdDev"))
        if std_dev is not None:
            target["stdDevNs"] = round(std_dev, 4)
        p50 = parse_duration_ns(row.get("P50"))
        p90 = parse_duration_ns(row.get("P90"))
        p95 = parse_duration_ns(row.get("P95"))
        p99 = parse_duration_ns(row.get("P99"))
        if p50 is not None:
            target["p50Ns"] = round(p50, 1)
        if p90 is not None:
            target["p90Ns"] = round(p90, 1)
        if p95 is not None:
            target["p95Ns"] = round(p95, 1)
        if p99 is not None:
            target["p99Ns"] = round(p99, 1)
        if excluded:
            target["excluded"] = True
            target["exclusionReason"] = "High run-to-run variance; recorded for context only."
        carried = inheritable.get(method)
        if carried:
            target.update(carried)
            inherited[method] = tuple(carried)
        targets.append(target)

    notes = [
        "Captured on a GitHub-hosted runner through the perf-regression workflow's",
        "`update_baseline` input, so the gate compares CI against CI and the cross-environment",
        "calibration caveat is lifted: `failMeanPercent` (and the allocation gate) now enforce.",
        "Only entries with gated = true participate in the pass/fail gate; reference rows are",
        "reported for context. The Concurrent-100 suite is recorded but never gated (variance).",
    ]
    if inherited:
        notes.append(
            "Per-target thresholds were inherited from the previous baseline by method name:"
        )
        notes.append(
            "a benchmark report cannot express them, so `build_baseline_json` carries %s"
            % " / ".join(INHERITED_TARGET_KEYS)
        )
        notes.append(
            "forward and a re-capture no longer reverts those rows to the global defaults."
        )
        notes.append("Inherited on: %s." % ", ".join(sorted(inherited)))

    payload = {
        "schemaVersion": 1,
        "environment": "github-actions",
        "calibration": False,
        "capturedAt": datetime.date.today().isoformat(),
        "runtime": "GitHub Actions runner, .NET 10.0",
        "job": "Short (Warmup 3 / Iteration 10, Server GC + Concurrent GC)",
        "source": report_path.replace("\\", "/"),
        "notes": notes,
        "thresholds": {
            "warnMeanPercent": 15.0,
            "failMeanPercent": 30.0,
            "allocWarnBytes": 0,
            "allocFailBytes": 64,
        },
        "targets": targets,
    }
    return json.dumps(payload, indent=2, ensure_ascii=False), inherited


def main() -> int:
    parser = argparse.ArgumentParser(description="Benchmark regression gate")
    parser.add_argument("report", nargs="?", default=None, help="BenchmarkDotNet CSV report")
    parser.add_argument("--baseline", default="docs/benchmarks/baseline/baseline.json")
    parser.add_argument("--emit-baseline", action="store_true",
                        help="Print a fresh baseline JSON built from the report and exit.")
    parser.add_argument("--emit-baseline-file", default=None, metavar="PATH",
                        help="Write the fresh baseline JSON to PATH (a full, committable baseline).")
    parser.add_argument("--no-fail", action="store_true",
                        help="Always exit 0 (advisory mode).")
    args = parser.parse_args()

    if args.emit_baseline or args.emit_baseline_file:
        report_path = find_report(args.report)
        if not report_path:
            print("::error::No BenchmarkDotNet CSV report found.", file=sys.stderr)
            return 2
        results = read_report(report_path)

        # A report carries measurements only, so the hand-tuned per-target thresholds have to
        # come from the baseline this capture replaces - which is what `--baseline` points at.
        previous = None
        if os.path.isfile(args.baseline):
            try:
                previous = load_baseline(args.baseline)
            except Exception as exc:  # noqa: BLE001
                print("::warning::Could not read the previous baseline %s (%s); the emitted "
                      "baseline carries no inherited per-target thresholds."
                      % (args.baseline, exc), file=sys.stderr)
        else:
            print("::warning::No previous baseline at %s; the emitted baseline carries no "
                  "inherited per-target thresholds. Re-stamp them before committing."
                  % args.baseline, file=sys.stderr)

        payload, inherited = build_baseline_json(report_path, results, previous)
        if args.emit_baseline_file:
            parent = os.path.dirname(os.path.abspath(args.emit_baseline_file))
            if parent:
                os.makedirs(parent, exist_ok=True)
            with io.open(args.emit_baseline_file, "w", encoding="utf-8", newline="\n") as handle:
                handle.write(payload + "\n")
            print("Baseline written to %s" % args.emit_baseline_file)
            for method in sorted(inherited):
                print("  inherited %s on %s" % (" / ".join(inherited[method]), method))
        else:
            print(payload)
        return 0

    report_path = find_report(args.report)
    if not report_path:
        print("::error::No BenchmarkDotNet CSV report found.", file=sys.stderr)
        return 2
    print("Report   : %s" % report_path)

    try:
        results = read_report(report_path)
    except Exception as exc:  # noqa: BLE001
        print("::error::Failed to read report: %s" % exc, file=sys.stderr)
        return 2

    if not os.path.isfile(args.baseline):
        print("::error::Baseline file not found: %s" % args.baseline, file=sys.stderr)
        return 2
    baseline = load_baseline(args.baseline)
    thresholds = baseline.get("thresholds", {})
    warn_pct = float(thresholds.get("warnMeanPercent", 15.0))
    fail_pct = float(thresholds.get("failMeanPercent", 30.0))
    alloc_warn = thresholds.get("allocWarnBytes")
    alloc_warn = float("inf") if alloc_warn is None else float(alloc_warn)
    alloc_fail = thresholds.get("allocFailBytes")
    alloc_fail = float("inf") if alloc_fail is None else float(alloc_fail)
    calibration = bool(baseline.get("calibration", False))

    print("Baseline : %s (environment=%s%s)"
          % (args.baseline, baseline.get("environment", "unknown"),
             ", calibration mode" if calibration else ""))
    print("Thresholds: warn +%.0f%% / fail +%.0f%% / alloc warn +%s B / alloc fail +%s B"
          % (warn_pct, fail_pct,
             "-" if alloc_warn == float("inf") else "%.0f" % alloc_warn,
             "-" if alloc_fail == float("inf") else "%.0f" % alloc_fail))
    print("")

    lines = []
    lines.append("## Performance regression report")
    lines.append("")
    if calibration:
        lines.append("> **Calibration mode**: the committed baseline was captured on a different machine "
                     "than this runner, so absolute numbers are advisory and the job does not block "
                     "(`continue-on-error`). A CI-captured baseline "
                     "(see `docs/benchmarks/baseline/README.md`) removes this caveat and enforces the gate.")
        lines.append("")
    lines.append("| Benchmark | Baseline mean | Current mean | Delta | Allocated | Status |")
    lines.append("| :--- | ---: | ---: | ---: | ---: | :--- |")

    failures = []
    warnings = []
    notices = []
    for target in baseline.get("targets", []):
        method = normalize_method(target.get("method", ""))
        actual = results.get(method)
        base_mean = target.get("meanNs")
        base_alloc = target.get("allocatedBytes")

        # Per-target overrides (PG4): wider thresholds for rows whose measured variance
        # exceeds the global threshold (e.g. the sub-100 ns lean rows).
        target_warn = float(target.get("warnMeanPercentOverride", warn_pct))
        target_fail = float(target.get("failMeanPercentOverride", fail_pct))

        if actual is None or actual["meanNs"] is None:
            if not target.get("gated"):
                # Reference / excluded rows are reported for context; silence them when absent
                # (the CI job intentionally runs only the `hot` category).
                continue
            failures.append("%s: no result in report" % method)
            lines.append("| %s | %s | - | n/a | %s | **MISSING** |"
                         % (escape_cell(method), _fmt_ns(base_mean), _fmt_bytes(base_alloc)))
            continue

        mean = actual["meanNs"]
        alloc = actual["allocatedBytes"]
        delta_pct = ((mean - base_mean) / base_mean * 100.0) if base_mean else 0.0

        status = "ok"
        if target.get("excluded"):
            status = "excluded"
        elif not target.get("gated"):
            status = "reference"
        else:
            if delta_pct >= target_fail:
                status = "**FAIL**"
                failures.append("%s: mean +%.1f%% (>= +%.0f%%)" % (method, delta_pct, target_fail))
            elif delta_pct >= target_warn:
                status = "warn"
                warnings.append("%s: mean +%.1f%% (>= +%.0f%%)" % (method, delta_pct, target_warn))
            if base_alloc is not None and alloc is not None and alloc > base_alloc:
                delta_bytes = alloc - base_alloc
                message = "%s: allocation %s -> %s (+%s)" % (
                    method, _fmt_bytes(base_alloc), _fmt_bytes(alloc), _fmt_bytes(delta_bytes))
                # The allocation gate runs on CI-native baselines; while the baseline is
                # cross-environment, allocation deltas are advisory only.
                if delta_bytes >= alloc_fail and not calibration:
                    status = "**FAIL**"
                    failures.append(message + " (>= alloc fail threshold)")
                elif delta_bytes >= alloc_warn and not calibration:
                    warnings.append(message + " (>= alloc warn threshold)")
                else:
                    notices.append(message)

        lines.append("| %s | %s | %s | %s | %s | %s |"
                     % (escape_cell(method), _fmt_ns(base_mean), _fmt_ns(mean),
                        _fmt_delta_pct(delta_pct) if target.get("gated") else format_delta(mean, base_mean),
                        "%s (%s)" % (_fmt_bytes(alloc), _fmt_bytes(base_alloc)),
                        status))

    lines.append("")
    if failures:
        lines.append("### Failures")
        lines.extend("- %s" % item for item in failures)
    if warnings:
        lines.append("### Warnings")
        lines.extend("- %s" % item for item in warnings)
    if notices:
        lines.append("### Notices (advisory)")
        lines.extend("- %s" % item for item in notices)
    if not failures and not warnings and not notices:
        lines.append("No regressions detected.")

    markdown = "\n".join(lines)
    summary_path = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary_path:
        with io.open(summary_path, "a", encoding="utf-8") as handle:
            handle.write(markdown + "\n")
    print(markdown)

    if failures and not args.no_fail:
        return 1
    return 0


def _fmt_ns(value):
    if value is None:
        return "-"
    return "%.1f ns" % value


def _fmt_bytes(value):
    if value is None:
        return "-"
    return "%d B" % int(value)


def _fmt_delta_pct(value):
    return "%+.1f%%" % value


if __name__ == "__main__":
    sys.exit(main())
