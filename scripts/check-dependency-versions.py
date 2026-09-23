#!/usr/bin/env python3
"""Check that every dependency version the packages ship has a single source, and that the
logging line stays on one generation per target framework.

Usage
-----
    python scripts/check-dependency-versions.py [--repo DIR] [--quiet]

Defaults
--------
    --repo   the repository root, inferred from this script's location

Exit codes
----------
    0  all four assertions hold
    1  at least one assertion failed
    2  input error (a props file is missing or unreadable)

What it asserts
---------------
    A. Single source     No version literal survives in the six props files that decide the
                         nupkgs' dependency groups; every ``Version=`` is a ``$(...)``
                         variable.
    B. No orphans        Every variable the versions file defines is referenced at least once,
                         and every ``$(Hayate...)`` a props file references is defined.
                         (MSBuild/SDK built-ins such as ``$(TargetFramework)`` are outside this
                         file's remit, so the check keys on the ``Hayate`` prefix rather than
                         maintaining a whitelist.)
    C. One generation    For every target framework, the DependencyInjection package's
                         ``Microsoft.Extensions.Logging`` and the Configuration package's
                         ``Microsoft.Extensions.Logging.Abstractions`` share a major.minor.
    D. Wiring            Those two package references actually consume those two variables.
                         Without this, C would be checking variables nobody reads.

Why C is a generation check and not an equality check
-----------------------------------------------------
The 3.0 plan asked for the two packages' versions to be *aligned*. That is not reachable as
written: the two packages do not ship the same patches. Per the nuget.org flat container,
``Microsoft.Extensions.Logging`` ships 6.0.0/6.0.1, 7.0.0 and 8.0.0/8.0.1 in those bands --
there is no 6.0.4, 7.0.1 or 8.0.3 to align to -- while ``.Logging.Abstractions`` ships
6.0.0-6.0.4, 7.0.0-7.0.1 and 8.0.0-8.0.3. Only the 9.x and 10.x bands carry a common patch,
and there both sides already sit at 9.0.13 and 10.0.3.

Same generation per target framework, with each package at its own band maximum, is therefore
the strongest alignment that exists -- and it is what this script enforces, so a future bump
cannot quietly break it.

Scope
-----
The versions that ship: the six props files that decide each nupkg's dependency groups. Test
projects are out of scope (``IsPackable=false``; each holds its own dev-toolchain versions),
as is the build-time SourceLink package in ``asset/props/sourcelink.env.props``.
"""
import argparse
import os
import re
import sys

VERSIONS_REL = "asset/props/dependency.versions.props"
CONSUMERS = [
    "src/HayateOP.Extensions.DependencyInjection/dependency.props",
    "src/HayateOP.Extensions.Configuration/dependency.props",
    "src/HayateOP.Extensions.HealthCheck/dependency.props",
    "src/HayateOP.Extensions.Endpoints/dependency.props",
    "src/HayateOP.Extensions.Specialized/dependency.props",
    "src/HayateOP.Extensions.ObjectPoolCompat/references.props",
]

DI_LOGGING = "HayateMelLoggingVersion"
CFG_LOGGING = "HayateMelLoggingAbstractionsVersion"
DI_LOGGING_PKG = "Microsoft.Extensions.Logging"
CFG_LOGGING_PKG = "Microsoft.Extensions.Logging.Abstractions"

# Variables this file owns, by naming convention.
OWN = re.compile(r"^Hayate")

PAT_COND = re.compile(
    r"<(?P<n>[A-Za-z_]\w*)\s+Condition=\"\s*'\$\(TargetFramework\)'\s*==\s*'(?P<t>[^']+)'\s*\"\s*>"
    r"(?P<v>[^<]+)</(?P=n)>"
)
PAT_PLAIN = re.compile(r"<(?P<n>[A-Za-z_]\w*)\s*>(?P<v>[^<]+)</(?P=n)>")


def mm(version):
    return ".".join(version.split(".")[:2])


def main():
    ap = argparse.ArgumentParser(add_help=True)
    ap.add_argument("--repo", default=None,
                    help="repository root (default: inferred from this script's location)")
    ap.add_argument("--quiet", action="store_true", help="only report failures")
    args = ap.parse_args()

    repo = args.repo or os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

    def read(rel):
        path = os.path.join(repo, rel.replace("/", os.sep))
        if not os.path.isfile(path):
            print("input error: %s not found (is --repo right?)" % rel)
            sys.exit(2)
        with open(path, encoding="utf-8-sig") as fh:
            return fh.read()

    problems = []
    versions_text = read(VERSIONS_REL)

    defined = {}
    for m in PAT_COND.finditer(versions_text):
        defined.setdefault(m.group("n"), {})[m.group("t")] = m.group("v").strip()
    for m in PAT_PLAIN.finditer(versions_text):
        defined.setdefault(m.group("n"), {})[""] = m.group("v").strip()
    if not defined:
        print("input error: no variables parsed out of %s" % VERSIONS_REL)
        sys.exit(2)

    # A + B
    used = set()
    for rel in CONSUMERS:
        text = read(rel)
        literals = re.findall(r'Version="(?!\$)[^"]*"', text)
        if literals:
            problems.append("A single source: %s still carries version literals %s" % (rel, literals))
        for var in re.findall(r"\$\(([A-Za-z_]\w*)\)", text):
            if not OWN.match(var):
                continue
            used.add(var)
            if var not in defined:
                problems.append("B dangling: %s references undefined $(%s)" % (rel, var))
    for var in sorted(defined):
        if var not in used:
            problems.append("B orphan: $(%s) is defined but no props file uses it" % var)

    # C
    for name in (DI_LOGGING, CFG_LOGGING):
        if name not in defined:
            problems.append("C: $(%s) is not defined in %s" % (name, VERSIONS_REL))
    if DI_LOGGING in defined and CFG_LOGGING in defined:
        a, b = defined[DI_LOGGING], defined[CFG_LOGGING]
        tfms = sorted((set(a) | set(b)) - {""})
        if not tfms:
            problems.append("C: neither log-line variable is scoped to a target framework")
        for tfm in tfms:
            x, y = a.get(tfm), b.get(tfm)
            if x is None or y is None:
                problems.append("C: %s has %s=%s but %s=%s"
                                % (tfm, DI_LOGGING, x or "-", CFG_LOGGING, y or "-"))
            elif mm(x) != mm(y):
                problems.append("C different generations on %s: %s=%s (%s) vs %s=%s (%s)"
                                % (tfm, DI_LOGGING, x, mm(x), CFG_LOGGING, y, mm(y)))
            elif not args.quiet:
                print("  ok %-9s %s=%-8s %s=%-8s generation %s"
                      % (tfm, DI_LOGGING, x, CFG_LOGGING, y, mm(x)))

    # D
    checks = [
        (CONSUMERS[0], DI_LOGGING_PKG, DI_LOGGING),
        (CONSUMERS[1], CFG_LOGGING_PKG, CFG_LOGGING),
    ]
    for rel, pkg, expected in checks:
        text = read(rel)
        m = re.search(r'Include="%s"\s+Version="\$\(([^)]+)\)"' % re.escape(pkg), text)
        if not m:
            problems.append("D wiring: %s does not take %s from a variable" % (rel, pkg))
        elif m.group(1) != expected:
            problems.append("D wiring: %s takes %s from $(%s), expected $(%s)"
                            % (rel, pkg, m.group(1), expected))

    if problems:
        print("\nFAILED, %d problem(s):" % len(problems))
        for p in problems:
            print("  -", p)
        return 1
    if not args.quiet:
        print("\nOK: A single source / B no orphans or dangling refs / C one logging generation "
              "per TFM / D wiring")
    return 0


if __name__ == "__main__":
    sys.exit(main())
