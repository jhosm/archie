#!/usr/bin/env bash
#
# demo-mcp.sh — one-command walking-skeleton demo for Epic E (bd babelstone-7puj):
# a thin term-deposit slice driven through the dev MCP server (ADR-IC-010 / E.5).
#
# This is the MINIMAL live path — Postgres ONLY, no Redpanda, no orchestrator — so it boots fast and
# fails in few ways (ideal on a stage). It chains the manual runbook into one command:
#   1. start PostgreSQL only (the engine's sole dependency)
#   2. apply the forward-only event-store schema (shared apply_event_store_schema)
#   3. deploy the rate sheet via the REAL C.6 deploy API (ADR-PC-008 §P2), asserting
#      201-deploy / 200-idempotent-replay / 409-forward-only-conflict — the validated seam
#   4. start the engine command/query host (Babelstone.Engine.Api, ADR-PC-021 §D5)
#   5. drive constitute -> read -> mature over HTTP and assert the canonical AT_MATURITY numbers
#   6. start the Python MCP server (Streamable HTTP) in front of the engine
# then print the `claude mcp add` wiring. The engine + MCP are left RUNNING; stop with: down.
#
# For the WHOLE UI (every mode + Operator=CLAUDE) in one bring-up, see scripts/demo-all.sh / `make demo`.
#
# Usage:
#   scripts/demo-mcp.sh [up]    # run the demo, leave engine + MCP up   (make demo-mcp)
#   scripts/demo-mcp.sh down    # stop the engine + MCP this demo started (make demo-mcp-down)
#
# Overridable env: PG_PORT RATESHEET_PORT ENGINE_PORT
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
# shellcheck source=scripts/demo-lib.sh
. "$ROOT/scripts/demo-lib.sh"

# --- configuration (overridable; defaults match the MCP/FastMCP/dev-stack defaults) ---
PG_PORT="${PG_PORT:-5432}"
RATESHEET_PORT="${RATESHEET_PORT:-5080}"   # the transient deploy host; any free port
ENGINE_PORT="${ENGINE_PORT:-8080}"         # MUST match the MCP's BABELSTONE_ENGINE_URL default
# The MCP server's listen port. Its __main__.py reads MCP_BIND_PORT and DEFAULTS to 8080 (the
# in-container port Kong dials), so start_mcp_server pins MCP_BIND_PORT to this value — both to match
# the URL we poll and to keep the MCP off the engine's :8080. (Engine J makes the host configurable.)
MCP_PORT=8000

COMPOSE="docker compose -f infra/compose.yaml"
PG_CONTAINER="babelstone-postgres"
PG_CONN="Host=localhost;Port=${PG_PORT};Database=babelstone;Username=babelstone;Password=babelstone"
ENGINE_URL="http://localhost:${ENGINE_PORT}"
MCP_URL="http://127.0.0.1:${MCP_PORT}/mcp"
RUNDIR="$ROOT/.demo-mcp"                    # logs + pidfiles (gitignored)
MIGRATIONS_DIR="engine/src/Babelstone.EventStore.Migrations/Sql"

# --- canonical scenario (1:1 with DepositsApiIntegrationTests) ---
PRODUCT="dpz_pt_12m_juros_venc"
RATE_SHEET_VERSION="pt-deposits-2026.1"

teardown() {
  say "Stopping the demo's engine + MCP (Postgres is left running — use 'make down' for the stack)"
  stop_pidfile "$RUNDIR/mcp.pid" "MCP server"
  stop_pidfile "$RUNDIR/engine.pid" "engine host"
  # Belt-and-braces: the recorded pid may be a launcher wrapper, so also match the assembly/module.
  pkill -f 'Babelstone.Engine.Api.dll' 2>/dev/null && ok "swept stray engine process(es)" || true
  pkill -f 'babelstone_mcp' 2>/dev/null && ok "swept stray MCP process(es)" || true
  ok "done"
}

# ---------------------------------------------------------------------------
# down
# ---------------------------------------------------------------------------
if [ "${1:-up}" = "down" ]; then
  teardown
  exit 0
fi

[ "${1:-up}" = "up" ] || die "usage: $0 [up|down]"

mkdir -p "$RUNDIR"

# ---------------------------------------------------------------------------
# 0. preflight
# ---------------------------------------------------------------------------
say "Preflight"
require_demo_tools
# The engine (8080) and MCP (8000) ports collide with the full stack's Redpanda Console / Kong proxy
# AND with a sibling demo's engine. This demo needs Postgres ONLY, so those ports must be free.
if port_busy "$ENGINE_PORT"; then
  die "port $ENGINE_PORT is busy (engine) — a sibling demo's engine (demo-saga/demo-all) or Redpanda Console from 'make up'. Stop it (the matching '*-down', or 'make down'), or set ENGINE_PORT=8088."
fi
if port_busy "$MCP_PORT"; then
  die "port $MCP_PORT is busy (MCP) — the Kong proxy from 'make up' or a sibling demo's MCP server. Stop it ('make down' / the matching '*-down'), or set MCP_PORT to a free port."
fi
ok "docker, mise, lsof present; ports $ENGINE_PORT (engine) and $MCP_PORT (MCP) are free"

# ---------------------------------------------------------------------------
# 1. Postgres only
# ---------------------------------------------------------------------------
say "1/6 Starting PostgreSQL (the only dependency the engine host needs)"
$COMPOSE up -d --wait postgres
wait_postgres "$PG_CONTAINER"
ok "PostgreSQL accepting connections on localhost:${PG_PORT}"

# ---------------------------------------------------------------------------
# 2. event-store schema (forward-only SQL; shared ledger applier — applies only
#    the migrations a `schema_migrations` ledger hasn't recorded, so a pre-existing
#    volume gets the genuinely-missing newer migrations, never a re-run of 0001)
# ---------------------------------------------------------------------------
say "2/6 Applying the event-store schema"
apply_event_store_schema "$PG_CONTAINER" babelstone "$MIGRATIONS_DIR"

# ---------------------------------------------------------------------------
# pre-build the two hosts (so backgrounded launches start fast with clean PIDs)
# ---------------------------------------------------------------------------
say "Building the engine + rate-sheet hosts (first run restores NuGet — be patient)"
mise exec -- dotnet build engine/src/Babelstone.RateSheets.Api/Babelstone.RateSheets.Api.csproj --nologo -v q \
  || die "RateSheets.Api build failed"
mise exec -- dotnet build engine/src/Babelstone.Engine.Api/Babelstone.Engine.Api.csproj --nologo -v q \
  || die "Engine.Api build failed"
RATESHEET_DLL="$(dll_for engine/src/Babelstone.RateSheets.Api Babelstone.RateSheets.Api)"
ENGINE_DLL="$(dll_for engine/src/Babelstone.Engine.Api Babelstone.Engine.Api)"
[ -n "$RATESHEET_DLL" ] && [ -n "$ENGINE_DLL" ] || die "built DLLs not found under bin/Debug/net*/"
ok "built"

# ---------------------------------------------------------------------------
# 3. deploy the rate sheet via the C.6 API — assert 201 / 200 / 409
# ---------------------------------------------------------------------------
say "3/6 Deploying the rate sheet via the C.6 deploy API (validated seam, not a raw INSERT)"
cat > "$RUNDIR/rate-sheet.json" <<JSON
{"rate_sheet_version_id":"${RATE_SHEET_VERSION}","product_family":"term_deposit","pack_version":"pt.2026.1","effective_from":"2026-01-01T00:00:00+00:00","approved_by":"treasury.alm@bank.internal","approval_ref":"ALM-2026-019","products":{"dpz_pt_12m_juros_venc":{"standard":{"bands":[{"principal_cents":[0,null],"tan_basis_points":300}]}},"dpz_pt_12m_juros_mensal":{"standard":{"bands":[{"principal_cents":[0,null],"tan_basis_points":325}]}},"dpz_pt_12m_juros_antecip":{"standard":{"bands":[{"principal_cents":[0,null],"tan_basis_points":300}]}}}}
JSON
# Same version id, a DIFFERENT rate — must be refused (forward-only immutability, §P5).
cat > "$RUNDIR/rate-sheet-conflict.json" <<JSON
{"rate_sheet_version_id":"${RATE_SHEET_VERSION}","product_family":"term_deposit","pack_version":"pt.2026.1","effective_from":"2026-01-01T00:00:00+00:00","approved_by":"treasury.alm@bank.internal","approval_ref":"ALM-2026-019","products":{"${PRODUCT}":{"standard":{"bands":[{"principal_cents":[0,null],"tan_basis_points":350}]}}}}
JSON

# Runs inside with_ratesheet_host: asserts the deploy / idempotent-replay / forward-only-conflict trio.
mcp_deploy() { # base_url
  local url="$1" code
  code="$(ratesheet_post "$url" demo-mcp "$RUNDIR/rate-sheet.json" "$RUNDIR/deploy-resp.json")"
  case "$code" in
    201) ok "deploy → 201 Created (new rate sheet ${RATE_SHEET_VERSION})" ;;
    200) ok "deploy → 200 OK (rate sheet ${RATE_SHEET_VERSION} already present, identical)" ;;
    *)   die "deploy expected 201 or 200, got $code  ($(cat "$RUNDIR/deploy-resp.json"))" ;;
  esac
  code="$(ratesheet_post "$url" demo-mcp "$RUNDIR/rate-sheet.json" "$RUNDIR/deploy-resp.json")"
  [ "$code" = 200 ] || die "idempotent re-POST expected 200, got $code  ($(cat "$RUNDIR/deploy-resp.json"))"
  ok "idempotent re-POST → 200 OK (replayed, no second write — ADR-PC-008 §P2)"
  code="$(ratesheet_post "$url" demo-mcp "$RUNDIR/rate-sheet-conflict.json" "$RUNDIR/deploy-resp.json")"
  [ "$code" = 409 ] || die "conflicting re-POST (different rate) expected 409, got $code  ($(cat "$RUNDIR/deploy-resp.json"))"
  ok "conflicting re-POST (350 bps) → 409 Conflict (forward-only immutability — ADR-PC-008 §P5)"
}
with_ratesheet_host "$RATESHEET_DLL" "$PG_CONN" "http://localhost:${RATESHEET_PORT}" \
  "$RUNDIR/ratesheet-api.log" mcp_deploy

# ---------------------------------------------------------------------------
# 4. start the engine command/query host on :ENGINE_PORT (Postgres-only — no Kafka)
# ---------------------------------------------------------------------------
say "4/6 Starting the engine host on ${ENGINE_URL}"
start_engine_host "$ENGINE_DLL" "$PG_CONN" "$ENGINE_URL" "$ROOT/packs" \
  "$RUNDIR/engine.pid" "$RUNDIR/engine.log"

# ---------------------------------------------------------------------------
# 5. drive constitute -> read -> mature and assert the canonical numbers
# ---------------------------------------------------------------------------
say "5/6 Driving constitute → read → mature and asserting the canonical AT_MATURITY numbers"
cat > "$RUNDIR/constitute-req.json" <<JSON
{"principal_cents":1000000,"product_id":"${PRODUCT}","role":"standard","term_days":365,"start_date":"2026-01-15","interest_variant":"AT_MATURITY","auto_renewal_policy":"NONE","funding_account":"PT50-DDA-001"}
JSON

code="$(curl -sS -o "$RUNDIR/constitute-resp.json" -w '%{http_code}' \
  -X POST "${ENGINE_URL}/v1/deposits" -H 'Content-Type: application/json' \
  -H "Idempotency-Key: $(uuidgen)" \
  --data-binary @"$RUNDIR/constitute-req.json")"
[ "$code" = 201 ] || die "constitute expected 201, got $code  ($(cat "$RUNDIR/constitute-resp.json"))"
DID="$(py -c "import json;print(json.load(open('$RUNDIR/constitute-resp.json'))['deposit_id'])")"
ok "constituted deposit $DID (HTTP 201)"

curl -sS -o "$RUNDIR/active.json" "${ENGINE_URL}/v1/deposits/${DID}" || die "read position failed"
info "active position:"
assert_json "$RUNDIR/active.json" tan_basis_points 300
assert_json "$RUNDIR/active.json" rate_sheet_version_id "$RATE_SHEET_VERSION"
assert_json "$RUNDIR/active.json" lifecycle Active

code="$(curl -sS -o "$RUNDIR/matured.json" -w '%{http_code}' \
  -X POST "${ENGINE_URL}/v1/deposits/${DID}/maturity" -H 'Content-Type: application/json' -d '{}')"
[ "$code" = 200 ] || die "maturity expected 200, got $code  ($(cat "$RUNDIR/matured.json"))"
info "matured position:"
assert_json "$RUNDIR/matured.json" accrued_gross_interest_cents 30417
assert_json "$RUNDIR/matured.json" withholding_to_date_cents 8517
assert_json "$RUNDIR/matured.json" net_interest_cents 21900
assert_json "$RUNDIR/matured.json" total_payout_cents 1021900
assert_json "$RUNDIR/matured.json" lifecycle Matured
ok "canonical numbers reproduced end-to-end through the engine's HTTP boundary"

# ---------------------------------------------------------------------------
# 6. start the Python MCP server in front of the engine
# ---------------------------------------------------------------------------
say "6/6 Setting up + starting the Python MCP server"
setup_mcp_venv dev
(cd mcp-server && "$ROOT/mcp-server/.venv/bin/python" -m pytest -q) || die "MCP contract tests failed"
ok "MCP package installed; contract tests green"
start_mcp_server "$ENGINE_URL" "$MCP_PORT" "$RUNDIR/mcp.pid" "$RUNDIR/mcp.log" "$MCP_URL"

# ---------------------------------------------------------------------------
# done — print the Claude Code wiring
# ---------------------------------------------------------------------------
cat <<DONE

$(printf '\033[1;32m✓ Walking skeleton is up.\033[0m')

  engine  ${ENGINE_URL}            (logs: .demo-mcp/engine.log)
  MCP     ${MCP_URL}   (logs: .demo-mcp/mcp.log)
  a deposit was already constituted + matured as a smoke test: ${DID}

Wire it into Claude Code (the MCP server must stay running, which it is):

  claude mcp add --transport http babelstone-deposits ${MCP_URL}
  claude mcp list        # babelstone-deposits should show ✓ connected

Then, in a Claude Code session, exercise it:

  • "Using the babelstone-deposits MCP, call constitute_deposit with product_id
     ${PRODUCT}, role standard, principal_cents 1000000, term_days 365,
     start_date 2026-01-15, funding_account PT50-DDA-001 — then call get_deposit
     with that deposit_id and show the position."

  • "Then call mature_deposit with that deposit_id and show the matured payout"
     — the whole constitute → read → mature loop runs through MCP tools, no curl.

Stop the engine + MCP when you're done (Postgres is left up):

  make demo-mcp-down
DONE
