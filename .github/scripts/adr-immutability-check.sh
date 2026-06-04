#!/usr/bin/env bash
# Authoritative §D5 gate (ADR-PC-000 §D5 / ADR-PC-020 §D3,§P1): an Accepted ADR's
# `## Decision` must not change in place unless the SAME PR carries a dated amendment
# ('Revised'/'Amendment') or a supersession ('Superseded by', or a new ADR). Heuristic
# but matches the §D5 convention; the conformance agent (archie-bhq.5) judges the rest.
set -euo pipefail

base="origin/${BASE_REF:-main}"
git fetch --no-tags origin "${BASE_REF:-main}" >/dev/null 2>&1 || true

extract_decision() {  # stdin -> the `## Decision`..next `## ` block
  awk '/^## / { if (inblock && $0 !~ /^## Decision/) exit } /^## Decision/ { inblock=1 } inblock { print }'
}

# Relocating a referenced ADR (e.g. into adrs/retired/) rewrites link *hrefs* but not the
# Decision prose. Strip link targets so an href-only change is not read as a Decision edit;
# link text and the surrounding prose are still compared. (ADR-PC-000 §D5 retired-subfolder rule.)
strip_links() { sed -E 's/\]\([^)]*\)/]/g'; }

status=0
changed="$(git diff --name-only "${base}...HEAD" | grep -E '/adrs/(retired/)?ADR-(PC|IC)-[0-9]+.*\.md$' || true)"
[ -n "$changed" ] || { echo "No ADR files changed."; exit 0; }

while IFS= read -r f; do
  [ -n "$f" ] || continue
  [ -f "$f" ] || { echo "skip (moved/deleted from working tree): $f"; continue; }   # e.g. moved into adrs/retired/
  base_content="$(git show "${base}:${f}" 2>/dev/null || true)"
  [ -n "$base_content" ] || { echo "skip (new file): $f"; continue; }
  base_status="$(printf '%s\n' "$base_content" | grep -m1 '^| *Status *|' \
    | awk -F'|' '{gsub(/^[[:space:]]+|[[:space:]]+$/,"",$3); print $3}' || true)"
  case "$base_status" in Accepted) ;; *) echo "skip (base status='$base_status'): $f"; continue ;; esac

  if [ "$(printf '%s\n' "$base_content" | extract_decision | strip_links)" = "$(extract_decision < "$f" | strip_links)" ]; then
    echo "ok (Decision prose unchanged; link hrefs normalised): $f"; continue
  fi
  if git diff "${base}...HEAD" -- "$f" | grep '^+' | grep -qiE 'revised|amendment|amended|superseded by'; then
    echo "ok (Decision changed WITH amendment/supersession): $f"; continue
  fi
  echo "::error file=${f}::ADR-PC-000 §D5: '$f' is Accepted and its '## Decision' changed in place with no dated amendment or supersession in this PR. Append a '*Revised YYYY-MM-DD: …*' amendment, or supersede with a new ADR."
  status=1
done <<EOF
$changed
EOF
exit "$status"
