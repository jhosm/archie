#!/usr/bin/env python3
"""Fail-closed line/branch coverage floor for a cobertura report.

Q.y Phase 2 (bd babelstone-2t16.13). Used by the `engine` CI job to gate the
MERGED .NET cobertura against a floor. Because that job already feeds the
always-run `CI gate` aggregator (ADR-PC-019 §P1), a breach fails the job → fails
the gate → blocks the PR, with NO new required check added to main-protect.json.

It reads the ROOT <coverage> element's lines-covered / lines-valid and
branches-covered / branches-valid COUNTS — the authoritative, de-duplicated
totals. (Do NOT sum the <line> elements: cobertura emits each line twice, once
under <class>/<lines> and once under <method>/<lines>, so a naive count nearly
doubles the denominator.)

An empty or unreadable report fails CLOSED rather than passing silently — in
particular the `dotnet-coverage merge` empty-trap (a 178-byte report with
line-rate=0 and no count attributes), which is what you get when the only
measured assembly was excluded from instrumentation.

Usage:
    coverage-floor-cobertura.py <report.xml> <line-floor> <branch-floor>
    # floors are fractions in [0,1], e.g. 0.85 0.75

Exit status: 0 = at or above floor, 1 = below floor / unreadable / empty,
2 = bad arguments.
"""
import sys
import xml.etree.ElementTree as ET


def main(argv):
    if len(argv) != 4:
        print(f"usage: {argv[0]} <report.xml> <line-floor> <branch-floor>", file=sys.stderr)
        return 2
    path, line_floor, branch_floor = argv[1], float(argv[2]), float(argv[3])

    try:
        raw = open(path, "rb").read()
    except OSError as exc:
        print(f"::error::cannot read cobertura report {path}: {exc}")
        return 1

    # The report is a trusted artifact this same CI job just generated, but harden
    # the stdlib parser anyway: a DTD or entity declaration is the prerequisite for
    # both XXE and billion-laughs expansion, and cobertura output legitimately has
    # neither — so reject outright rather than depend on defusedxml in a .NET job.
    if b"<!DOCTYPE" in raw or b"<!ENTITY" in raw:
        print(f"::error::{path} contains a DTD/entity declaration — refusing to parse")
        return 1

    try:
        root = ET.fromstring(raw)
    except ET.ParseError as exc:
        print(f"::error::cannot parse cobertura report {path}: {exc}")
        return 1

    def count(attr):
        value = root.get(attr)
        return int(value) if value not in (None, "") else None

    lines_covered, lines_valid = count("lines-covered"), count("lines-valid")
    branches_covered, branches_valid = count("branches-covered"), count("branches-valid")

    if not lines_valid:  # None or 0 — the empty-trap; coverage was never measured
        print(f"::error::{path} reports no lines (lines-valid={lines_valid}); "
              "coverage was not measured — failing closed")
        return 1

    line_rate = lines_covered / lines_valid
    # Require BOTH branch counts before dividing: a root with branches-valid but no
    # branches-covered is malformed (standard tooling emits the pair together), and
    # None / int would raise a raw traceback instead of the clean ::error:: contract.
    # No branches in the measured surface → the branch floor is vacuously satisfied.
    has_branches = bool(branches_valid) and branches_covered is not None
    branch_rate = (branches_covered / branches_valid) if has_branches else 1.0

    print(f"line   coverage: {lines_covered}/{lines_valid} = {line_rate:.2%}  (floor {line_floor:.0%})")
    if has_branches:
        print(f"branch coverage: {branches_covered}/{branches_valid} = {branch_rate:.2%}  (floor {branch_floor:.0%})")
    else:
        print("branch coverage: n/a (no branches in the measured surface)")

    breaches = []
    if line_rate < line_floor:
        breaches.append(f"line {line_rate:.2%} < floor {line_floor:.0%}")
    if has_branches and branch_rate < branch_floor:
        breaches.append(f"branch {branch_rate:.2%} < floor {branch_floor:.0%}")

    if breaches:
        print("::error::.NET coverage below floor — " + "; ".join(breaches))
        return 1

    print("OK: .NET coverage floor satisfied")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
