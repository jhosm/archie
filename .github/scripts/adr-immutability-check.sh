#!/usr/bin/env bash
# Authoritative §D5 gate (ADR-PC-000 §D5 / ADR-PC-020 §D3,§P1): an Accepted ADR's
# `## Decision` must not change unless the SAME PR carries a dated amendment
# ('Revised'/'Amendment') or a supersession ('Superseded by', or a new ADR). Heuristic
# but matches the §D5 convention; the conformance agent (archie-bhq.5) judges the rest.
set -euo pipefail

base="origin/${BASE_REF:-main}"
git fetch --no-tags origin "${BASE_REF:-main}" >/dev/null 2>&1 || true

ADR_RE='/adrs/ADR-(PC|IC)-[0-9]+.*\.md$'   # live dir only; relocated (retired/) ADRs are pass (1)

extract_decision() {  # stdin -> the `## Decision`..next `## ` block
  awk '/^## / { if (inblock && $0 !~ /^## Decision/) exit } /^## Decision/ { inblock=1 } inblock { print }'
}

# Relocating a referenced ADR (e.g. into adrs/retired/) rewrites link *hrefs* but not the
# Decision prose. Strip link targets so an href-only change is not read as a Decision edit;
# link text and the surrounding prose are still compared. (ADR-PC-000 §D5 retired-subfolder rule.)
strip_links() { sed -E 's/\]\([^)]*\)/]/g'; }

status=0

# Compare an Accepted ADR's base `## Decision` (from $1 = "base:OLDPATH") against its
# working-tree file ($2); a prose change (link hrefs normalised) must carry an
# amendment/supersession in the diff of the remaining path args ($3…, for `git diff -- …`).
check_decision() {
  local base_show="$1" wf="$2"; shift 2
  local base_content base_status
  base_content="$(git show "$base_show" 2>/dev/null || true)"
  [ -n "$base_content" ] || { echo "skip (no base): $wf"; return 0; }
  base_status="$(printf '%s\n' "$base_content" | grep -m1 '^| *Status *|' \
    | awk -F'|' '{gsub(/^[[:space:]]+|[[:space:]]+$/,"",$3); print $3}' || true)"
  case "$base_status" in Accepted) ;; *) echo "skip (base status='$base_status'): $wf"; return 0 ;; esac
  if [ "$(printf '%s\n' "$base_content" | extract_decision | strip_links)" = "$(extract_decision < "$wf" | strip_links)" ]; then
    echo "ok (Decision prose unchanged; link hrefs normalised): $wf"; return 0
  fi
  if git diff "${base}...HEAD" -- "$@" | grep '^+' | grep -qiE 'revised|amendment|amended|superseded by'; then
    echo "ok (Decision changed WITH amendment/supersession): $wf"; return 0
  fi
  echo "::error file=${wf}::ADR-PC-000 §D5: '$wf' is Accepted and its '## Decision' changed with no dated amendment or supersession in this PR. Append a '*Revised YYYY-MM-DD: …*' amendment, or supersede with a new ADR."
  status=1
}

found=0

# (1) Relocated ADRs (the §D5 retired-subfolder move): for each ADR now under adrs/retired/,
# compare the base `## Decision` at its PRE-MOVE live path (adrs/X) against the moved file,
# so a relocation that ALSO edits an Accepted Decision is caught. Deterministic on the path
# (not git's by-similarity rename pairing, which can mis-pair a near-identical reissue); a
# plain `git mv` would otherwise ride through pass (2) as add+delete and skip. (bd babelstone-vxm1.)
while IFS= read -r new; do
  [ -n "$new" ] || continue
  old="${new/\/retired\//\/}"                              # docs/.../adrs/retired/X -> docs/.../adrs/X
  git show "${base}:${old}" >/dev/null 2>&1 || continue    # only a relocation FROM the live dir
  [ -f "$new" ] || continue
  found=1
  check_decision "${base}:${old}" "$new" "$old" "$new"
done < <(git diff --name-only "${base}...HEAD" | grep -E '/adrs/retired/ADR-(PC|IC)-[0-9]+.*\.md$' || true)

# (2) In-place edits (`--no-renames` so a rename surfaces as add+delete — both skipped here
# and handled by (1) above).
while IFS= read -r f; do
  [ -n "$f" ] || continue
  [ -f "$f" ] || { echo "skip (deleted / old-rename path): $f"; continue; }
  git show "${base}:${f}" >/dev/null 2>&1 || { echo "skip (new file): $f"; continue; }
  found=1
  check_decision "${base}:${f}" "$f" "$f"
done < <(git diff --no-renames --name-only "${base}...HEAD" | grep -E "$ADR_RE" || true)

[ "$found" -eq 1 ] || echo "No ADR files changed."
exit "$status"
