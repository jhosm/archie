#!/usr/bin/env bash
# CodeQL fail-on gate — ADR-IC-014 (Q.7, bd archie-j72w).
#
# The official `github/codeql-action/analyze` is report-only: it uploads SARIF to
# the Security tab and the job stays green even when it finds alerts. GitHub's
# native PR-blocking lives in a repository UI setting ("Code scanning → Check
# failures"), which is NOT version-controlled and so does not fit this repo's
# "source of record in git" posture (see .github/rulesets/README.md). This script
# is the version-controlled realisation of ADR-IC-014's "flip CodeQL to fail-on":
# it reads the SARIF the analyze step wrote and finds results at/above GitHub's
# *default* check-failure bar, so PR gating matches what the Security tab would flag —
# deterministically, in CI, with no hidden UI dependency. Blocking is gated on
# CODEQL_FAILON_ENFORCE=1 (a deliberate two-step — see the exit logic below); without
# it the script reports and passes, so it can ship before the report-only backlog is
# triaged (ADR-IC-014 residual risk "Baseline noise").
#
# Default bar (overridable via env for tuning during baseline settling):
#   - any result whose effective level is "error", OR
#   - any result whose rule carries security-severity >= 7.0 (GitHub "high"/"critical").
# This mirrors GitHub's "Errors and high/critical security severities" default.
#
# FAIL-CLOSED stance (it is a security gate, so "unknown" is never "safe"):
#   - a present-but-unparseable security-severity is forced ABOVE the bar (not 0);
#   - a result whose rule metadata cannot be resolved (and that carries no own level)
#     is treated as blocking and counted as "unresolved";
#   - a missing SARIF directory is a misconfiguration and fails;
#   - an empty SARIF directory fails when enforcing (a gate that evaluates nothing
#     must not wave a PR through).
# An *absent* security-severity is a non-security rule → 0, governed by level instead.
#
# Usage: codeql-failon.sh <sarif-dir>
set -euo pipefail

dir="${1:?usage: codeql-failon.sh <sarif-dir>}"
# Numeric threshold for rule security-severity (GitHub: high starts at 7.0).
threshold="${CODEQL_SECURITY_SEVERITY_THRESHOLD:-7.0}"
enforce="${CODEQL_FAILON_ENFORCE:-0}"

# Validate the threshold up front — a non-numeric value would otherwise abort jq with
# an opaque "--argjson" error.
case "$threshold" in
  '' | *[!0-9.]* | *.*.*) echo "::error::CODEQL_SECURITY_SEVERITY_THRESHOLD='$threshold' is not numeric"; exit 1 ;;
esac

emit() { printf '%s\n' "$@" >> "${GITHUB_OUTPUT:-/dev/null}"; }

# A missing directory means the analyze step's output path changed or this script is
# pointed at the wrong place — never a clean pass.
if [ ! -d "$dir" ]; then
  echo "::error::codeql-failon: SARIF directory '$dir' does not exist — misconfiguration, not a skipped leg"
  exit 1
fi

shopt -s nullglob
files=("$dir"/*.sarif)
if [ "${#files[@]}" -eq 0 ]; then
  # The directory exists but holds no SARIF. analyze writes SARIF whenever it runs, so
  # this means there was nothing to evaluate here. Report-only: warn + pass; enforcing:
  # fail closed (the always-run `CodeQL gate` job separately enforces "analysis ran").
  if [ "$enforce" = "1" ]; then
    echo "::error::codeql-failon: no *.sarif in '$dir' (enforcing — failing closed)"; exit 1
  fi
  echo "::warning::codeql-failon: no *.sarif files in '$dir' — nothing to evaluate (report-only)"
  emit "findings=0" "unresolved=0" "enforced=false"
  exit 0
fi

blocking=0
unresolved=0
for f in "${files[@]}"; do
  # Per SARIF run: build a rule-id -> {level, security-severity} table from the driver +
  # extension rules, then classify each result. Emits two integers: "<blocking> <unresolved>".
  read -r count unres < <(jq -r --argjson th "$threshold" '
    [ .runs[]?
      | ( ([ (.tool.driver.rules // [])[], (.tool.extensions[]?.rules // [])[] ])
          | reduce .[] as $r ({};
              .[$r.id] = {
                level: (($r.defaultConfiguration.level // "warning") | ascii_downcase),
                sev: ( $r.properties["security-severity"] as $raw
                       | if   $raw == null then 0
                         elif ($raw | tostring | test("^[0-9]+(\\.[0-9]+)?$")) then ($raw | tonumber)
                         else  100 end )   # present-but-unparseable: force above any threshold
              }) ) as $byid
      | .results[]?
      | ($byid[(.ruleId // .rule.id // "")]) as $rule
      | ((.level // $rule.level // "__unresolved__") | ascii_downcase) as $lvl
      | ($rule.sev // 0) as $sev
      | { blk: ($lvl == "error" or $lvl == "__unresolved__" or $sev >= $th),
          unr: ($lvl == "__unresolved__") }
    ] as $rows
    | "\($rows | map(select(.blk)) | length) \($rows | map(select(.unr)) | length)"
  ' "$f")

  if [ "$unres" -gt 0 ]; then
    echo "::warning::$(basename "$f"): $unres result(s) with unresolvable rule metadata — treated as blocking (fail-closed)"
    unresolved=$((unresolved + unres))
  fi
  if [ "$count" -gt 0 ]; then
    echo "::error::$(basename "$f"): $count CodeQL finding(s) at/above the fail-on bar (level=error/unresolved or security-severity>=$threshold)"
    blocking=$((blocking + count))
  else
    echo "$(basename "$f"): clear (no findings at/above the fail-on bar)"
  fi
done

if [ "$blocking" -gt 0 ]; then
  # Enforcement is a deliberate two-step (ADR-IC-014 residual risk "Baseline noise":
  # triage the pre-existing report-only backlog BEFORE flipping CodeQL to blocking).
  emit "findings=$blocking" "unresolved=$unresolved"
  if [ "$enforce" = "1" ]; then
    emit "enforced=true"
    echo "::error::CodeQL fail-on: $blocking blocking finding(s) — see the Security tab. (ADR-IC-014)"
    exit 1
  fi
  emit "enforced=false"
  echo "::notice title=CodeQL report-only::$blocking finding(s) at/above the bar are NOT blocking this PR (CODEQL_FAILON_ENFORCE unset). Triage then enforce (ADR-IC-014)."
  exit 0
fi
emit "findings=0" "unresolved=0" "enforced=$([ "$enforce" = "1" ] && echo true || echo false)"
echo "CodeQL fail-on: no blocking findings across ${#files[@]} SARIF file(s)"
