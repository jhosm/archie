#!/usr/bin/env bash
#
# spec-coverage-check.sh — the authoritative per-push half of the ADR-PC-020 §P6
# coverage checker: ADR <-> catalogue <-> code/test traceability.
#
# ADR-PC-020 §P6 asserts:
#   - every Verifiable commitment resolves to >=1 test that exists (and runs in CI),
#     and (where applicable) >=1 code anchor;
#   - every ADR anchor in code points to a LIVE (non-superseded) ADR.
# The periodic "decided-but-unbuilt / no-commitment-ADR" sweep (§P3) is the separate
# spec-coverage-audit.sh, run nightly, not per-push.
#
# The catalogue (commitment-catalogue.md) is the single source of truth for each
# commitment's claim, gate, Test ID, and status; each governing ADR references its
# rows by Test ID. This checker enforces that contract in both directions.
#
# Portable to bash 3.2 (macOS): the markdown table is parsed in awk into a TSV and
# orchestrated with temp files — no associative arrays.
#
# Exit 0 = clean; exit 1 = at least one violation (printed as a ::error:: annotation
# so it surfaces in the GitHub Actions log and inline).
set -euo pipefail

root="$(git rev-parse --show-toplevel)"
cd "$root"

PC_ADRS="docs/product-management/product_concepts/adrs"
IC_ADRS="docs/product-management/integration_concepts/adrs"
CATALOGUE="$PC_ADRS/commitment-catalogue.md"
# Buildable subtrees that may carry `// ADR-PC-NNN` code anchors (ADR-PC-019 §P1).
#
# Derived from the repo's tracked top-level directories minus an explicit exclusion
# set, rather than hand-maintained: a new top-level estate dir (a future family or
# boundary service) is auto-in-scope, so no Live commitment fails coverage merely
# because its dir was forgotten (bd babelstone-64uw.4 — lifecycle-driver + cadence
# previously had to be hand-added in PR #404 for a commitment to resolve).
#
# Excluded (everything that is NOT a compiled-source subtree carrying ADR anchors):
#   - dot-dirs (.github, .beads, .claude, .config, .githooks, …) — tooling/CI/config;
#   - docs / docfx — prose and generated reference, no code anchors;
#   - infra — Docker/ops config, no compiled source;
#   - scripts — the CI *.sh gates, resolved SEPARATELY below (see the scripts/*.sh
#     Live-resolution branch), so it must stay out of CODE_DIRS;
#   - packs / product-configs / rate-sheets / plugins / research — asset, config,
#     regulatory-pack and plugin trees with no ADR-anchored compiled source.
# Deriving (not hardcoding) means the exclusion list is the thing to maintain, and a
# forgotten *new* code dir now fails open (in scope) instead of failing silently.
CODE_EXCLUDE_DIRS="docs docfx infra scripts packs plugins product-configs rate-sheets research"
CODE_DIRS=""
while IFS= read -r d; do
  case "$d" in .*) continue ;; esac                # dot-dirs are never code subtrees
  skip=""
  for x in $CODE_EXCLUDE_DIRS; do [ "$d" = "$x" ] && { skip=1; break; }; done
  [ -n "$skip" ] && continue
  CODE_DIRS="${CODE_DIRS:+$CODE_DIRS }$d"
done < <(git ls-tree -d --name-only HEAD)
# Only real source files carry anchors — not the scaffold's README.md / Dockerfile.
CODE_INCLUDES=(--include='*.cs' --include='*.go' --include='*.py' --include='*.ts' --include='*.tsx' --include='*.sql' --include='*.fs')

status=0
err()  { echo "::error file=${1}::${2}"; status=1; }
note() { echo "  $*"; }

[ -f "$CATALOGUE" ] || { err "$CATALOGUE" "ADR-PC-020 §P6: commitment catalogue not found."; exit 1; }

tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
rows="$tmp/rows.tsv"        # tid \t status \t gate \t srcpath  (one per catalogue row)

# --- Parse the "## The seed" table into TSV (fields counted from the END, so a
#     pipe in the commitment prose cannot shift the trailing columns). ---
awk '
  /^## The seed/      { inseed=1; next }
  inseed && /^## /     { inseed=0 }
  inseed && /^\|/ {
    if ($0 ~ /^\|[-| :]+\|$/) next                 # separator row
    n = split($0, c, "|")                          # c[n]="" (trailing), c[n-1]=Status ...
    num = c[2]; gsub(/^[ \t]+|[ \t]+$/, "", num)
    if (num == "#") next                           # header row
    st  = c[n-1]; tid = c[n-2]; gate = c[n-3]; src = c[n-4]
    gsub(/^[ \t]+|[ \t]+$/, "", st)
    gsub(/[` \t]/, "", tid)
    gsub(/^[ \t]+|[ \t]+$/, "", gate)
    # extract the link target from "[label](path)" in the Governing-source cell
    if (match(src, /\]\([^)]+\)/)) { link = substr(src, RSTART+2, RLENGTH-3) } else { link = "" }
    print tid "\t" st "\t" gate "\t" link
  }
' "$CATALOGUE" > "$rows"

[ -s "$rows" ] || { err "$CATALOGUE" "ADR-PC-020 §P6: no commitment rows parsed from the '## The seed' table."; exit 1; }

echo "== Catalogue integrity =="
# Valid status vocabulary + Test-ID shape + resolvable governing-source link.
while IFS=$'\t' read -r tid st gate link; do
  case "$st" in Live|Planned|Gap) ;; *) err "$CATALOGUE" "Row '$tid': status '$st' is not Live/Planned/Gap." ;; esac
  case "$tid" in ''|*[!A-Z0-9_]*) err "$CATALOGUE" "Test ID '$tid' is not UPPER_SNAKE_CASE." ;; esac
  [ -n "$gate" ] || err "$CATALOGUE" "Row '$tid': empty gate (pyramid level)."
  if [ -n "$link" ]; then
    target="$PC_ADRS/${link#./}"                   # links in the catalogue are relative to the adrs/ dir
    [ -f "$target" ] || err "$CATALOGUE" "Row '$tid': governing-source link '$link' does not resolve ($target)."
  else
    err "$CATALOGUE" "Row '$tid': no governing-source link."
  fi
done < "$rows"

# Unique Test IDs.
dupes="$(cut -f1 "$rows" | sort | uniq -d || true)"
[ -z "$dupes" ] || err "$CATALOGUE" "Duplicate Test ID(s): $(echo "$dupes" | tr '\n' ' ')"

cut -f1 "$rows" | sort -u > "$tmp/catalogue_tids"

# --- Helper: print the `## Verifiable commitments` section of an ADR file. ---
vc_section() { awk '/^## Verifiable commitments/{f=1;next} f&&/^## /{f=0} f&&/^---$/{f=0} f' "$1"; }
# --- Helper: the Test IDs a section *references* — the leading `TID` of each
#     "- `TID` — gloss" bullet (the catalogued reference form, ADR-PC-000 §A1
#     amendment). Deliberately ignores `UPPER_SNAKE` tokens later in the gloss,
#     which are enum values (EVENT_DRIVEN, SCHEDULED, …), not Test IDs. ---
referenced_tids() { grep -oE '^[[:space:]]*-[[:space:]]+`[A-Z][A-Z0-9_]+`' | grep -oE '[A-Z][A-Z0-9_]+' | sort -u; }

echo "== ADR -> catalogue: every referenced Test ID exists =="
# Any Test ID an ADR's Verifiable-commitments section cites must be a real catalogue row.
for dir in "$PC_ADRS" "$IC_ADRS"; do
  [ -d "$dir" ] || continue
  for adr in "$dir"/ADR-*.md; do
    [ -f "$adr" ] || continue
    # Conventions ADRs (e.g. ADR-PC-000, which only *illustrates* the section in a
    # code fence) are exempt and never carry real commitments — ADR-PC-000 §A2.
    shape="$(grep -m1 '^| *Shape *|' "$adr" | awk -F'|' '{gsub(/^[[:space:]]+|[[:space:]]+$/,"",$3); print $3}' || true)"
    case "$shape" in *Conventions*) continue ;; esac
    section="$(vc_section "$adr")"
    [ -n "$section" ] || continue
    echo "$section" | referenced_tids > "$tmp/ref_tids" || true
    while IFS= read -r tid; do
      [ -n "$tid" ] || continue
      grep -Fxq "$tid" "$tmp/catalogue_tids" || err "$adr" "References Test ID '$tid' with no row in the commitment catalogue."
    done < "$tmp/ref_tids"
  done
done

echo "== catalogue -> ADR: every ADR-governed row is referenced back =="
# A row whose governing source is an ADR file must be referenced (by Test ID) in that
# ADR's Verifiable-commitments section — the no-duplication / no-orphan invariant.
while IFS=$'\t' read -r tid st gate link; do
  case "$link" in *ADR-PC-*.md|*ADR-IC-*.md) ;; *) continue ;; esac   # skip non-ADR sources (feature-design / concept docs)
  target="$PC_ADRS/${link#./}"
  [ -f "$target" ] || continue                                       # already reported above
  if ! vc_section "$target" | grep -q "\`$tid\`"; then
    err "$target" "Catalogue row '$tid' names this ADR as its governing source, but the ADR's '## Verifiable commitments' section does not reference '$tid'."
  fi
done < "$rows"

echo "== Live commitments resolve to a test (and code anchor) =="
# Only Live rows are required to resolve; Planned/Gap are deliberately unbuilt.
live=0
while IFS=$'\t' read -r tid st gate link; do
  [ "$st" = "Live" ] || continue
  live=$((live+1))
  found_test=""
  for d in $CODE_DIRS; do
    [ -d "$d" ] || continue
    if grep -rqF "${CODE_INCLUDES[@]}" -e "$tid" "$d" 2>/dev/null; then found_test="yes"; break; fi
  done
  # A commitment may instead be realised by a CI shell-script gate under scripts/ — e.g.
  # kong-config-check.sh (ADR-IC-006) or grafana-rbac-check.sh (the observability-plane
  # RBAC enforcement, OBS_PLANE_RBAC / catalogue SEC-2). These run in ci.yml's path-scoped
  # jobs, so they ARE "a test that exists and runs in CI" (ADR-PC-020 §P6) for a commitment
  # whose realisation is ops/infra config with NO compiled-code home (the engine/contract
  # subtrees carry no Grafana/Kong source). Accept a scripts/*.sh gate naming the Test ID as
  # Live-resolution evidence alongside the code-dir tests. Purely additive — it can only let
  # MORE rows resolve, never fewer, so it cannot mask a regression in an existing Live row.
  if [ -z "$found_test" ] && [ -d scripts ] && grep -rqF --include='*.sh' -e "$tid" scripts 2>/dev/null; then
    found_test="yes"
  fi
  [ -n "$found_test" ] || err "$CATALOGUE" "Row '$tid' is Live but no test/code under {$CODE_DIRS} (nor a CI gate under scripts/*.sh) references the Test ID."
done < "$rows"
[ "$live" -gt 0 ] || note "no Live commitments yet — engine is a skeleton; test-resolution checks are dormant (all rows Planned)."

echo "== Code anchors point to a live (non-superseded, non-withdrawn) ADR =="
# Scan code for `ADR-PC-NNN` / `ADR-IC-NNN` anchors; each must resolve to an ADR file
# whose Status is not 'Superseded' or 'Withdrawn' (both bind nothing — ADR-PC-000 §D5).
anchors=0
for d in $CODE_DIRS; do
  [ -d "$d" ] || continue
  while IFS= read -r ref; do
    [ -n "$ref" ] || continue
    anchors=$((anchors+1))
    f="$(ls "$PC_ADRS/$ref"-*.md "$PC_ADRS/retired/$ref"-*.md "$IC_ADRS/$ref"-*.md "$IC_ADRS/retired/$ref"-*.md 2>/dev/null | head -1 || true)"
    if [ -z "$f" ]; then
      err "$d" "Code anchor '$ref' resolves to no ADR file."
    elif grep -qiE '^\| *Status *\|.*(Superseded|Withdrawn)' "$f"; then
      err "$f" "Code anchor '$ref' points to a SUPERSEDED or WITHDRAWN ADR (ADR-PC-020 §P6)."
    fi
    # -a (--binary-files=text): a fuzz-corpus test (e.g. EngineApiJsonEnvelopeFuzzTests.cs)
    # embeds non-text bytes, so grep's binary heuristic (notably BSD/macOS grep) can emit a
    # 'Binary file <path> matches' line in place of the -o matches — the loop then ingests it
    # as a bogus anchor that resolves to no ADR. -a forces text so the file's real `// ADR-NNN`
    # anchors are extracted and checked instead (bd babelstone-2t16.23).
  done < <(grep -rahoE "${CODE_INCLUDES[@]}" 'ADR-(PC|IC)-[0-9]{3}' "$d" 2>/dev/null | sort -u)
done
[ "$anchors" -gt 0 ] || note "no code anchors yet — no engine source committed."

if [ "$status" -eq 0 ]; then
  echo "spec-coverage: OK ($(wc -l < "$rows" | tr -d ' ') catalogue rows; $live Live, $anchors code anchors)."
else
  echo "spec-coverage: FAILED — see ::error:: annotations above."
fi
exit "$status"
