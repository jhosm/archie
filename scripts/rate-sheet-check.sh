#!/usr/bin/env bash
# scripts/rate-sheet-check.sh — rate-sheet YAML shape validation (bd babelstone-alfy, ADR-PC-008).
# Run by the `rate-sheets` path-scoped CI job and locally via `make rate-sheet-check`.
#
# In plain English: a rate sheet is now a committed YAML file under /rate-sheets/, and the README
# promises "rate-sheet schema validation" on PRs that touch it. This is that gate. It checks every
# committed sheet has the shape the deploy endpoint (POST /v1/rate-sheets) would accept BEFORE it is
# ever deployed — so a band gap, an overlap, a non-open top band, a missing envelope field, or a TAN
# above the pack ceiling fails in CI on the PR, not at first constitution.
#
# What it asserts per file, mirroring the engine's own checks so CI and the deploy boundary agree:
#   • Envelope (ADR-PC-008 §P1, the columns on the stored row): rate_sheet_version_id, product_family,
#     pack_version, effective_from, approved_by, approval_ref are all present and non-empty; and the
#     filename (sans .yaml) equals rate_sheet_version_id (README: "the filename is the version id").
#   • Body (RateSheetBody / RateBandJsonConverter): products -> role -> bands, each band a well-shaped
#     half-open [from, to) range — non-negative integer lower, upper either null (open-ended top band)
#     or a strictly-greater integer.
#   • Cross-band invariants (RateSheetValidator): the bands for one (product, role) are contiguous,
#     non-overlapping, exhaustive, with EXACTLY the highest band open-ended (to == null).
#   • Pack bound (ADR-PC-008 §P2): every tan_basis_points is within [0, max_consumer_rate_bps], the
#     ceiling read from the verified pack the sheet names (packs/<pack_version>/parameters/constants.yaml)
#     when that pack is present; if it is absent the bound check is skipped with a note (the deploy
#     host enforces it against the loaded pack regardless).
#
# This is a SHAPE gate on the committed source. The authoritative validator is the C# RateSheetValidator
# the deploy endpoint runs (engine/src/Babelstone.RateSheets/RateSheetValidator.cs); this script
# re-expresses the same rules in a hermetic, pinned-Node check so a malformed sheet never reaches main.
# It is intentionally NOT a silent pass: a sheet with no parseable envelope, or a /rate-sheets tree with
# no committed sheets, is reported explicitly.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

SHEETS_DIR="${1:-rate-sheets}"
JS_YAML="${RATESHEET_JS_YAML:-js-yaml@4.1.0}"

command -v node >/dev/null 2>&1 || { echo "FATAL: node is required (brew install node)"; exit 2; }
command -v npx  >/dev/null 2>&1 || { echo "FATAL: npx is required (ships with Node.js)"; exit 2; }

# Collect committed sheets anywhere under the tree (files live at <family>/<version>.yaml, so a flat
# glob misses them — recurse). README.md is data-free prose; skip it. macOS system bash is 3.2 (no
# `mapfile`), so read the NUL-delimited find output into an array the portable way.
sheets=()
while IFS= read -r -d '' f; do
  sheets+=("$f")
done < <(find "$SHEETS_DIR" -type f \( -name '*.yaml' -o -name '*.yml' \) -print0 | LC_ALL=C sort -z)

if [ "${#sheets[@]}" -eq 0 ]; then
  echo "no committed rate-sheet YAML under $SHEETS_DIR/ — nothing to validate (not a failure)"
  exit 0
fi

# The validator is a single Node program fed (file, json-body, max-bps) per sheet. We pre-serialise
# each YAML with the pinned js-yaml CLI (the same pinned-Node path scripts/asyncapi-catalog-validate.sh
# uses — no unpinned yq), read the pack ceiling here in bash, and hand both to node for the rule checks.
fail=0
for sheet in "${sheets[@]}"; do
  echo "== $sheet =="

  json="$(npx --yes "$JS_YAML" "$sheet" 2>/tmp/rs-yaml-err)" || {
    echo "  FAIL  not valid YAML:"; sed 's/^/        /' /tmp/rs-yaml-err; fail=1; continue
  }

  # Read the pack ceiling for THIS sheet's pack_version (best-effort: skip if the pack is absent).
  pack_version="$(printf '%s' "$json" | node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{try{process.stdout.write(String(JSON.parse(s).pack_version||""))}catch(e){}})')"
  max_bps=""
  pack_constants="packs/${pack_version}/parameters/constants.yaml"
  if [ -n "$pack_version" ] && [ -f "$pack_constants" ]; then
    max_bps="$(npx --yes "$JS_YAML" "$pack_constants" 2>/dev/null \
      | node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{try{const v=JSON.parse(s).max_consumer_rate_bps;process.stdout.write(v==null?"":String(v))}catch(e){}})')"
  fi
  [ -n "$max_bps" ] || echo "  note  pack '$pack_version' not present locally — skipping the TAN ≤ max_consumer_rate_bps bound check (the deploy host enforces it against the loaded pack)"

  base="$(basename "$sheet")"; expected_id="${base%.*}"
  if printf '%s' "$json" | RS_FILE="$sheet" RS_EXPECTED_ID="$expected_id" RS_MAX_BPS="$max_bps" node "$ROOT/scripts/rate-sheet-check.mjs"; then
    echo "  ok    $sheet"
  else
    fail=1
  fi
done

if [ "$fail" -ne 0 ]; then
  echo "rate-sheet shape validation FAILED — fix the YAML above (ADR-PC-008 §P1/§P2)"
  exit 1
fi
echo "rate-sheet shape validation passed (${#sheets[@]} sheet(s))"
