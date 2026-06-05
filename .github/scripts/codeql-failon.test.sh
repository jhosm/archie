#!/usr/bin/env bash
# Unit tests for codeql-failon.sh — the CodeQL PR-gate severity logic (ADR-IC-014, bd
# babelstone-2t16.10 / Q.7b). The Q.7 commit (babelstone-j72w) claimed local fixture
# tests but committed none; this file is the durable, version-controlled realisation, so
# the "a seeded high-severity finding blocks the PR" guarantee (Q.7b step 3) is a
# reproducible regression gate rather than a one-off manual experiment.
#
# Each case crafts a SARIF document, runs the script against it under a chosen
# CODEQL_FAILON_ENFORCE / threshold, and asserts the exit code AND the values emitted to
# GITHUB_OUTPUT. Pure bash + jq (both on the ubuntu-latest runner and bash-3.2-compatible
# for local macOS runs). No network, no CodeQL — the SARIF *is* the seeded finding.
#
# Run: bash .github/scripts/codeql-failon.test.sh   (exit 0 = all green)
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT="$HERE/codeql-failon.sh"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

TESTS=0
FAILS=0
pass() { TESTS=$((TESTS + 1)); printf '  ok   %s\n' "$1"; }
fail() { TESTS=$((TESTS + 1)); FAILS=$((FAILS + 1)); printf '  FAIL %s\n         %s\n' "$1" "$2"; }

# run <enforce> <threshold> <sarif-dir> — sets RC, OUT, OUTFILE. An empty threshold lets
# the script's own default (7.0) apply; all env is set on the invocation line so nothing
# leaks between cases.
run() {
  OUTFILE="$WORK/gh_output"; : > "$OUTFILE"
  OUT="$(CODEQL_FAILON_ENFORCE="$1" CODEQL_SECURITY_SEVERITY_THRESHOLD="$2" \
         GITHUB_OUTPUT="$OUTFILE" bash "$SCRIPT" "$3" 2>&1)"; RC=$?
}
check_rc()   { if [ "$RC" = "$1" ]; then pass "$2"; else fail "$2" "expected rc=$1, got rc=$RC :: $OUT"; fi; }
check_out()  { case "$OUT" in *"$1"*) pass "$2";; *) fail "$2" "output missing [$1] :: $OUT";; esac; }
check_emit() { if grep -qF "$1" "$OUTFILE"; then pass "$2"; else fail "$2" "GITHUB_OUTPUT missing [$1] :: $(tr '\n' ' ' < "$OUTFILE")"; fi; }

# new_dir <name> — a fresh SARIF dir under $WORK.
new_dir() { d="$WORK/$1"; mkdir -p "$d"; printf '%s' "$d"; }

# sarif <rule_level> <rule_secsev> <result_level> — emit one-run SARIF on stdout. Empty
# secsev/result_level omit the field (the absent-severity and no-own-level paths). rule id
# is "r1"; the result points at "r1" so its metadata resolves.
sarif() {
  jq -n --arg rl "$1" --arg sv "$2" --arg el "$3" '
    { runs: [ {
        tool: { driver: { rules: [
          ( { id: "r1", defaultConfiguration: { level: $rl } }
            + (if $sv == "" then {} else { properties: { "security-severity": $sv } } end) )
        ] } },
        results: [ ( { ruleId: "r1", message: { text: "seeded finding" } }
                     + (if $el == "" then {} else { level: $el } end) ) ]
      } ] }'
}

printf 'codeql-failon.sh unit tests\n'

# 1 — a HIGH security-severity finding BLOCKS under enforce (Q.7b step 3: the seeded high).
d="$(new_dir high)"; sarif warning 9.8 "" > "$d/csharp.sarif"
run 1 "" "$d"
check_rc 1 "high security-severity (9.8) blocks when CODEQL_FAILON_ENFORCE=1"
check_out "at/above the fail-on bar" "high finding is reported at/above the bar"
check_emit "findings=1" "high finding emits findings=1"
check_emit "enforced=true" "enforcing run emits enforced=true"

# 2 — the SAME high finding is REPORT-ONLY (passes) when enforce is unset.
run 0 "" "$d"
check_rc 0 "high security-severity does NOT block in report-only mode"
check_out "NOT blocking this PR" "report-only emits the not-blocking notice"
check_emit "enforced=false" "report-only run emits enforced=false"

# 3 — an error-LEVEL finding with no security-severity blocks under enforce.
d="$(new_dir errlvl)"; sarif error "" "" > "$d/csharp.sarif"
run 1 "" "$d"
check_rc 1 "error-level rule (no security-severity) blocks under enforce"

# 4 — a clean finding (warning level, security-severity below the bar) passes both ways.
d="$(new_dir clean)"; sarif warning 3.1 "" > "$d/csharp.sarif"
run 1 "" "$d"
check_rc 0 "warning + security-severity 3.1 passes even when enforcing"
check_emit "findings=0" "clean run emits findings=0"
run 0 "" "$d"
check_rc 0 "warning + security-severity 3.1 passes in report-only too"

# 5 — boundary: exactly 7.0 is at the bar (>=) and blocks.
d="$(new_dir bound_eq)"; sarif warning 7.0 "" > "$d/csharp.sarif"
run 1 "" "$d"; check_rc 1 "security-severity exactly 7.0 blocks (>= threshold)"

# 6 — just below the bar (6.9) passes.
d="$(new_dir bound_lo)"; sarif warning 6.9 "" > "$d/csharp.sarif"
run 1 "" "$d"; check_rc 0 "security-severity 6.9 is below the bar and passes"

# 7 — a result-level error overrides a warning-level rule and blocks.
d="$(new_dir reslvl)"; sarif warning 2.0 error > "$d/csharp.sarif"
run 1 "" "$d"; check_rc 1 "result-level=error overrides a warning rule and blocks"

# 8 — FAIL-CLOSED: a result whose rule metadata cannot be resolved (and no own level) is
# treated as blocking and counted unresolved.
d="$(new_dir unresolved)"
jq -n '{ runs: [ { tool: { driver: { rules: [] } },
         results: [ { ruleId: "ghost", message: { text: "orphan" } } ] } ] }' > "$d/csharp.sarif"
run 1 "" "$d"
check_rc 1 "unresolvable rule metadata blocks (fail-closed) under enforce"
check_out "unresolvable rule metadata" "unresolved result warns about fail-closed treatment"
check_emit "unresolved=1" "unresolved result emits unresolved=1"

# 9 — FAIL-CLOSED: a present-but-unparseable security-severity is forced above any threshold.
d="$(new_dir badsev)"; sarif warning high "" > "$d/csharp.sarif"
run 1 "" "$d"; check_rc 1 "unparseable security-severity ('high') is forced above the bar"

# 10 — a missing SARIF directory is a misconfiguration: always fails, both modes.
miss="$WORK/no-such-dir"
run 1 "" "$miss"; check_rc 1 "missing SARIF dir fails when enforcing"
check_out "does not exist" "missing dir reports the misconfiguration"
run 0 "" "$miss"; check_rc 1 "missing SARIF dir fails even in report-only (misconfiguration)"

# 11 — an empty SARIF dir: fail-closed when enforcing, warn-and-pass when report-only.
d="$(new_dir empty)"
run 1 "" "$d"; check_rc 1 "empty SARIF dir fails closed when enforcing"
run 0 "" "$d"; check_rc 0 "empty SARIF dir warns and passes in report-only"
check_emit "findings=0" "empty report-only run emits findings=0"

# 12 — a non-numeric threshold is rejected before jq runs.
d="$(new_dir thr)"; sarif warning 1.0 "" > "$d/csharp.sarif"
run 1 "not-a-number" "$d"; check_rc 1 "non-numeric threshold is rejected"
check_out "is not numeric" "bad threshold reports the validation error"

# 13 — a custom (raised) threshold reshapes the bar: 8.0 now passes, 9.5 still blocks.
d="$(new_dir custom_lo)"; sarif warning 8.0 "" > "$d/csharp.sarif"
run 1 9.0 "$d"; check_rc 0 "8.0 passes under a raised 9.0 threshold"
d="$(new_dir custom_hi)"; sarif warning 9.5 "" > "$d/csharp.sarif"
run 1 9.0 "$d"; check_rc 1 "9.5 blocks under a raised 9.0 threshold"

# 14 — multiple SARIF files aggregate: one clean leg + one high leg → blocking.
d="$(new_dir multi)"
sarif warning 1.0 "" > "$d/go.sarif"
sarif warning 8.4 "" > "$d/csharp.sarif"
run 1 "" "$d"; check_rc 1 "a high finding in any SARIF file blocks across the set"

# 15 — severity carried on an EXTENSION rule (not the driver) still resolves and blocks.
d="$(new_dir ext)"
jq -n '{ runs: [ {
    tool: { driver: { rules: [] },
            extensions: [ { rules: [ { id: "x1",
              defaultConfiguration: { level: "warning" },
              properties: { "security-severity": "8.1" } } ] } ] },
    results: [ { ruleId: "x1", message: { text: "from extension pack" } } ] } ] }' > "$d/csharp.sarif"
run 1 "" "$d"; check_rc 1 "security-severity on a tool.extensions rule resolves and blocks"

# 16 — a SARIF file with zero results passes even under enforce (present, evaluated, clean).
d="$(new_dir noresults)"
jq -n '{ runs: [ { tool: { driver: { rules: [] } }, results: [] } ] }' > "$d/csharp.sarif"
run 1 "" "$d"; check_rc 0 "a present SARIF with zero results passes under enforce"

printf '\n%d test(s), %d failure(s)\n' "$TESTS" "$FAILS"
[ "$FAILS" -eq 0 ]
