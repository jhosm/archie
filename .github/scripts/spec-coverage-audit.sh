#!/usr/bin/env bash
#
# spec-coverage-audit.sh — the periodic ADR-PC-020 §P3 / §P6 sweep (nightly, NOT
# per-push). Surfaces coverage gaps as *findings*, not build breaks:
#
#   - buildable-decision ADRs (Shape != Conventions; ADR-PC-000 §A2 exempts
#     Conventions) with no `## Verifiable commitments` section — the incremental
#     backfill backlog (ADR-PC-020 Open Action #7 / archie-bhq.10);
#   - ADRs whose section is present but names no Test ID and does not declare
#     "no executable commitments" (the §A1 must-say-so-explicitly rule);
#   - [activates once engine source lands] code paths anchored to an ADR that has
#     no commitment test, and ADRs with Live commitments but no implementing code.
#
# Report-only by design: the per-push gate (spec-coverage-check.sh) fails CI on
# real inconsistencies; this sweep gives visibility so a no-commitment ADR is a
# tracked finding, never a silent hole. Exit 0 always (use --strict to exit 1 on
# findings). Writes a summary to $GITHUB_STEP_SUMMARY when present.
set -euo pipefail

strict=0; [ "${1:-}" = "--strict" ] && strict=1

root="$(git rev-parse --show-toplevel)"; cd "$root"
PC_ADRS="docs/product-management/product_concepts/adrs"
IC_ADRS="docs/product-management/integration_concepts/adrs"

vc_section() { awk '/^## Verifiable commitments/{f=1;next} f&&/^## /{f=0} f&&/^---$/{f=0} f' "$1"; }
summary() { [ -n "${GITHUB_STEP_SUMMARY:-}" ] && printf '%s\n' "$*" >> "$GITHUB_STEP_SUMMARY" || true; }

findings=0
backlog=""

summary "## Spec-coverage audit (ADR-PC-020 §P3)"
summary ""

for dir in "$PC_ADRS" "$IC_ADRS"; do
  [ -d "$dir" ] || continue
  for adr in "$dir"/ADR-*.md; do
    [ -f "$adr" ] || continue
    rel="${adr#$root/}"

    # Shape (PC only); ADR-IC entries carry no Shape field — treat as buildable.
    shape="$(grep -m1 '^| *Shape *|' "$adr" | awk -F'|' '{gsub(/^[[:space:]]+|[[:space:]]+$/,"",$3); print $3}' || true)"
    case "$shape" in *Conventions*) continue ;; esac   # §A2 exemption

    section="$(vc_section "$adr")"
    if [ -z "$section" ]; then
      echo "::warning file=${adr}::No '## Verifiable commitments' section — backfill backlog (ADR-PC-020 Open Action #7)."
      backlog="${backlog}  - ${adr}\n"
      findings=$((findings+1))
      continue
    fi
    # Section present: it must name a Test ID or explicitly declare none.
    if ! echo "$section" | grep -qE '`[A-Z][A-Z0-9_]+`'; then
      if ! echo "$section" | grep -qiE 'no executable commitments'; then
        echo "::warning file=${adr}::'## Verifiable commitments' present but names no Test ID and does not declare 'no executable commitments' (ADR-PC-000 §A1)."
        findings=$((findings+1))
      fi
    fi
  done
done

# --- Code-based sweeps activate once engine source exists (none committed yet). ---
have_src=""
for d in engine families orchestrator acl mcp-server notification pack-validate contracts; do
  [ -d "$d" ] && ls "$d"/**/*.cs "$d"/*.cs "$d"/**/*.go "$d"/*.go "$d"/**/*.py "$d"/*.py >/dev/null 2>&1 && { have_src="yes"; break; }
done
[ -n "$have_src" ] || echo "note: no engine source yet — decided-but-unbuilt and governed-code-without-test sweeps are dormant."

echo
if [ "$findings" -eq 0 ]; then
  echo "spec-coverage-audit: no findings."
  summary "No findings."
else
  echo "spec-coverage-audit: ${findings} finding(s) (report-only)."
  summary "**${findings} finding(s).** Backfill backlog (ADR-PC-020 Open Action #7 / archie-bhq.10):"
  summary ""
  [ -n "$backlog" ] && printf "%b" "$backlog" | while IFS= read -r l; do [ -n "$l" ] && summary "$l"; done
fi

[ "$strict" -eq 1 ] && exit "$([ "$findings" -eq 0 ] && echo 0 || echo 1)"
exit 0
