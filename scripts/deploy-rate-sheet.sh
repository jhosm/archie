#!/usr/bin/env bash
# scripts/deploy-rate-sheet.sh — YAML-native rate-sheet deploy tool (bd babelstone-alfy).
# Run by `make deploy-rate-sheet`, by the demo scripts (via demo-lib.sh's ratesheet_post_yaml),
# and by hand against a running RateSheets.Api / engine.
#
# In plain English: a rate sheet's source of truth is a committed YAML file under /rate-sheets/
# (treasury-owned config-as-code). The deploy endpoint POST /v1/rate-sheets only accepts JSON, so
# this tool reads the YAML, serialises it to JSON 1:1, and POSTs it with the gateway-attested
# X-Deploy-Actor header. You author pure YAML; this closes the loop to the wire — no hand-written
# JSON heredoc that could drift from the committed file.
#
# What it does, against the file you name:
#
#   1. Serialise the YAML source to JSON with js-yaml (pinned via npx) — NOT the unpinned `yq` the
#      manual how-to suggests; this is the same pinned-Node path the CI scripts already take
#      (scripts/asyncapi-catalog-validate.sh), so deploying adds no new unpinned dependency. The
#      stored JSONB body is then 1:1 with the deployed YAML (ADR-PC-008 §P1).
#   2. POST it to {BASE_URL}/v1/rate-sheets with the required X-Deploy-Actor header (ADR-PC-008 §P4
#      / Amendment §A3: the deploying principal is the gateway-authenticated identity, recorded as
#      published_by — never a payload field).
#   3. Report the status per the §P2 contract: 201 Created (new) / 200 OK (idempotent identical
#      re-deploy) / 409 Conflict (different body or claimed effective_from under an existing id) /
#      400 (validation) / 401 (missing actor). Exit non-zero on anything but 201/200.
#
# Usage:
#   scripts/deploy-rate-sheet.sh <rate-sheet.yaml> [--base-url URL] [--actor PRINCIPAL]
#   make deploy-rate-sheet SHEET=rate-sheets/term_deposit/pt-deposits-2026.1.yaml
#
# Env (overridden by the flags): RATESHEET_BASE_URL (default http://localhost:8080),
#   RATESHEET_ACTOR (default treasury.analyst@bank.internal), RATESHEET_JS_YAML (default js-yaml@4.1.0).
set -euo pipefail

BASE_URL="${RATESHEET_BASE_URL:-http://localhost:8080}"
ACTOR="${RATESHEET_ACTOR:-treasury.analyst@bank.internal}"
SHEET=""

while [ $# -gt 0 ]; do
  case "$1" in
    --base-url) BASE_URL="$2"; shift 2 ;;
    --actor)    ACTOR="$2"; shift 2 ;;
    -h|--help)
      sed -n '2,30p' "$0"; exit 0 ;;
    -*) echo "unknown option: $1" >&2; exit 2 ;;
    *)
      [ -z "$SHEET" ] || { echo "only one rate-sheet file may be given (got '$SHEET' and '$1')" >&2; exit 2; }
      SHEET="$1"; shift ;;
  esac
done

[ -n "$SHEET" ] || { echo "usage: $0 <rate-sheet.yaml> [--base-url URL] [--actor PRINCIPAL]" >&2; exit 2; }
[ -f "$SHEET" ] || { echo "rate-sheet file not found: $SHEET" >&2; exit 2; }
command -v npx >/dev/null 2>&1 || { echo "npx (Node.js) is required to serialise YAML to JSON (brew install node)" >&2; exit 2; }
command -v curl >/dev/null 2>&1 || { echo "curl is required" >&2; exit 2; }

JS_YAML="${RATESHEET_JS_YAML:-js-yaml@4.1.0}"

# 1. YAML -> JSON (pinned js-yaml). The body the endpoint stores is then 1:1 with this file.
json="$(npx --yes "$JS_YAML" "$SHEET")" || { echo "could not serialise '$SHEET' to JSON" >&2; exit 1; }

# 2. POST with the X-Deploy-Actor header.
resp="$(mktemp)"
trap 'rm -f "$resp"' EXIT
code="$(printf '%s' "$json" | curl -sS -o "$resp" -w '%{http_code}' \
  -X POST "${BASE_URL%/}/v1/rate-sheets" \
  -H 'Content-Type: application/json' -H "X-Deploy-Actor: $ACTOR" \
  --data-binary @-)" || { echo "POST to ${BASE_URL%/}/v1/rate-sheets failed (is the deploy host up?)" >&2; exit 1; }

# 3. Report per the ADR-PC-008 §P2 status contract.
case "$code" in
  201) echo "201 Created — new rate sheet deployed from $SHEET";              cat "$resp"; echo ;;
  200) echo "200 OK — identical body already deployed (idempotent replay)";   cat "$resp"; echo ;;
  409) echo "409 Conflict — version id exists with a different body, or its effective_from is claimed." >&2
       echo "Ship a NEW rate_sheet_version_id (a new file) — a published sheet is forward-only (ADR-PC-008 §P5)." >&2
       cat "$resp" >&2; echo >&2; exit 1 ;;
  400) echo "400 Bad Request — the body failed validation (band gap/overlap, non-open top band, TAN out of bound, or unknown pack_version)." >&2
       cat "$resp" >&2; echo >&2; exit 1 ;;
  401) echo "401 Unauthorized — the X-Deploy-Actor header was missing or blank." >&2; exit 1 ;;
  *)   echo "unexpected HTTP $code deploying $SHEET" >&2; cat "$resp" >&2; echo >&2; exit 1 ;;
esac
