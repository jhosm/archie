#!/usr/bin/env bash
#
# demo-mcp.sh — one-command walking-skeleton demo for Epic E (bd babelstone-7puj):
# a thin term-deposit slice driven through the dev MCP server (ADR-IC-010 / E.5).
#
# It chains the manual runbook so a future run is one command:
#   1. start PostgreSQL only (the engine's sole dependency — no Redpanda needed)
#   2. apply the hand-rolled forward-only migrations (no migration runner exists)
#   3. deploy the rate sheet via the REAL C.6 deploy API (Babelstone.RateSheets.Api,
#      ADR-PC-008 §P2), asserting the 201-deploy / 200-idempotent-replay /
#      409-forward-only-conflict semantics — the validated seam, not a raw INSERT
#   4. start the engine command/query host (Babelstone.Engine.Api, ADR-PC-021 §D5)
#   5. drive constitute -> read -> mature over HTTP and assert the canonical
#      AT_MATURITY numbers (gross 30417 / tax 8517 / net 21900 / payout 1021900)
#   6. start the Python MCP server (Streamable HTTP) in front of the engine
# then print the `claude mcp add` wiring + the prompts to exercise it from Claude Code.
# The engine + MCP are left RUNNING; stop them with:  scripts/demo-mcp.sh down
#
# Usage:
#   scripts/demo-mcp.sh [up]    # run the demo, leave engine + MCP up   (make demo-mcp)
#   scripts/demo-mcp.sh down    # stop the engine + MCP this demo started (make demo-mcp-down)
#
# Overridable env: PG_PORT RATESHEET_PORT ENGINE_PORT
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

# --- configuration (overridable; defaults match the MCP/FastMCP/dev-stack defaults) ---
PG_PORT="${PG_PORT:-5432}"
RATESHEET_PORT="${RATESHEET_PORT:-5080}"   # the transient deploy host; any free port
ENGINE_PORT="${ENGINE_PORT:-8080}"         # MUST match the MCP's BABELSTONE_ENGINE_URL default
# FastMCP binds its default 8000 from code (server.py has no host/port override; configurable
# binding is Epic J). It is NOT a knob here — overriding would only move the URL we poll, not
# the listener. The engine URL the MCP targets IS wired (BABELSTONE_ENGINE_URL, set at launch).
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

# --- pretty output ---
say()  { printf '\n\033[1;36m▶ %s\033[0m\n' "$*"; }
ok()   { printf '  \033[32m✓ %s\033[0m\n' "$*"; }
info() { printf '  \033[2m%s\033[0m\n' "$*"; }
die()  { printf '\n\033[1;31m✗ %s\033[0m\n' "$*" >&2; exit 1; }

py() { mise exec -- python "$@"; }   # pinned interpreter, for JSON assertions

# Read a field out of a saved JSON response and assert it equals an expected value.
assert_json() { # file field expected
  local got
  got="$(py -c "import json;print(json.load(open('$1')).get('$2'))")" \
    || die "could not parse $2 from $1"
  [ "$got" = "$3" ] || die "expected $2=$3 but got '$got'  (see $1)"
  ok "$2 = $got"
}

# Wait until an HTTP endpoint answers at all (any status != 000 means the port is live).
wait_up() { # url timeout_seconds name logfile
  local url="$1" timeout="$2" name="$3" log="${4:-}" i=0 code
  while :; do
    code="$(curl -s -o /dev/null -w '%{http_code}' --max-time 2 "$url" 2>/dev/null || true)"
    [ -n "$code" ] && [ "$code" != "000" ] && { ok "$name is up ($url → HTTP $code)"; return 0; }
    i=$((i + 1))
    if [ "$i" -ge "$timeout" ]; then
      [ -n "$log" ] && { printf '\n--- last 30 lines of %s ---\n' "$log"; tail -n 30 "$log" 2>/dev/null || true; }
      die "$name did not come up at $url within ${timeout}s"
    fi
    sleep 1
  done
}

port_busy() { lsof -nP -iTCP:"$1" -sTCP:LISTEN >/dev/null 2>&1; }

# Resolve the built DLL for a project (deterministic path → clean kill semantics).
dll_for() { ls "$1"/bin/Debug/net*/"$2".dll 2>/dev/null | head -1; }

stop_pidfile() { # pidfile name
  local pidfile="$1" name="$2" pid
  if [ -f "$pidfile" ]; then
    pid="$(cat "$pidfile" 2>/dev/null || true)"
    if [ -n "$pid" ] && kill -0 "$pid" 2>/dev/null; then
      kill "$pid" 2>/dev/null || true
      ok "stopped $name (pid $pid)"
    fi
    rm -f "$pidfile"
  fi
}

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
command -v docker >/dev/null 2>&1 || die "docker not found on PATH"
docker info >/dev/null 2>&1 || die "docker is not running — start Docker Desktop and retry"
command -v mise >/dev/null 2>&1 || die "mise not found — run 'make bootstrap' first"
command -v lsof >/dev/null 2>&1 || die "lsof not found (needed for the port-clash guard)"
# The engine (8080) and MCP (8000) ports collide with the full stack's Redpanda Console
# and Kong proxy. This demo needs Postgres ONLY, so those ports must be free.
if port_busy "$ENGINE_PORT"; then
  die "port $ENGINE_PORT is busy — likely Redpanda Console from 'make up'. Run 'make down' (the demo needs Postgres only), or set ENGINE_PORT=8088."
fi
if port_busy "$MCP_PORT"; then
  die "port $MCP_PORT is busy — likely the Kong proxy from 'make up'. Run 'make down', or set MCP_PORT to a free port."
fi
ok "docker, mise, lsof present; ports $ENGINE_PORT (engine) and $MCP_PORT (MCP) are free"

# ---------------------------------------------------------------------------
# 1. Postgres only
# ---------------------------------------------------------------------------
say "1/6 Starting PostgreSQL (the only dependency the engine host needs)"
$COMPOSE up -d --wait postgres
until docker exec "$PG_CONTAINER" pg_isready -U babelstone -d babelstone >/dev/null 2>&1; do sleep 1; done
ok "PostgreSQL accepting connections on localhost:${PG_PORT}"

# ---------------------------------------------------------------------------
# 2. migrations (forward-only SQL; no runner exists — apply in 0001..0004 order)
# ---------------------------------------------------------------------------
say "2/6 Applying the event-store schema"
if docker exec "$PG_CONTAINER" psql -U babelstone -d babelstone -tAc \
     "SELECT to_regclass('public.events') IS NOT NULL;" 2>/dev/null | grep -q t; then
  ok "schema already present (events table exists) — skipping migrations"
else
  for f in "$MIGRATIONS_DIR"/0*.sql; do
    info "applying $(basename "$f")"
    docker exec -i "$PG_CONTAINER" psql -U babelstone -d babelstone -v ON_ERROR_STOP=1 -q < "$f" \
      || die "migration $(basename "$f") failed"
  done
  ok "applied: events, outbox, snapshots, rate_sheets (+ append-only role)"
fi

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
ConnectionStrings__RateSheets="$PG_CONN" ASPNETCORE_URLS="http://localhost:${RATESHEET_PORT}" \
  mise exec -- dotnet "$RATESHEET_DLL" > "$RUNDIR/ratesheet-api.log" 2>&1 &
RATESHEET_PID=$!
# The deploy host is transient: always reap it, even on a failed assertion.
trap 'kill "$RATESHEET_PID" 2>/dev/null || true' EXIT
wait_up "http://localhost:${RATESHEET_PORT}/" 60 "RateSheets.Api" "$RUNDIR/ratesheet-api.log"

cat > "$RUNDIR/rate-sheet.json" <<JSON
{"rate_sheet_version_id":"${RATE_SHEET_VERSION}","product_family":"term_deposit","pack_version":"pt.2026.1","effective_from":"2026-01-01T00:00:00+00:00","approved_by":"treasury.alm@bank.internal","approval_ref":"ALM-2026-019","products":{"${PRODUCT}":{"standard":{"bands":[{"principal_cents":[0,null],"tan_basis_points":300}]}}}}
JSON
# Same version id, a DIFFERENT rate — must be refused (forward-only immutability, §P5).
cat > "$RUNDIR/rate-sheet-conflict.json" <<JSON
{"rate_sheet_version_id":"${RATE_SHEET_VERSION}","product_family":"term_deposit","pack_version":"pt.2026.1","effective_from":"2026-01-01T00:00:00+00:00","approved_by":"treasury.alm@bank.internal","approval_ref":"ALM-2026-019","products":{"${PRODUCT}":{"standard":{"bands":[{"principal_cents":[0,null],"tan_basis_points":350}]}}}}
JSON

deploy() { # bodyfile  -> echoes the HTTP status
  curl -sS -o "$RUNDIR/deploy-resp.json" -w '%{http_code}' \
    -X POST "http://localhost:${RATESHEET_PORT}/v1/rate-sheets" \
    -H 'Content-Type: application/json' -H 'X-Deploy-Actor: demo-mcp' \
    --data-binary @"$1"
}

code="$(deploy "$RUNDIR/rate-sheet.json")"
case "$code" in
  201) ok "deploy → 201 Created (new rate sheet ${RATE_SHEET_VERSION})" ;;
  200) ok "deploy → 200 OK (rate sheet ${RATE_SHEET_VERSION} already present, identical)" ;;
  *)   die "deploy expected 201 or 200, got $code  ($(cat "$RUNDIR/deploy-resp.json"))" ;;
esac

code="$(deploy "$RUNDIR/rate-sheet.json")"
[ "$code" = 200 ] || die "idempotent re-POST expected 200, got $code  ($(cat "$RUNDIR/deploy-resp.json"))"
ok "idempotent re-POST → 200 OK (replayed, no second write — ADR-PC-008 §P2)"

code="$(deploy "$RUNDIR/rate-sheet-conflict.json")"
[ "$code" = 409 ] || die "conflicting re-POST (different rate) expected 409, got $code  ($(cat "$RUNDIR/deploy-resp.json"))"
ok "conflicting re-POST (350 bps) → 409 Conflict (forward-only immutability — ADR-PC-008 §P5)"

kill "$RATESHEET_PID" 2>/dev/null || true
trap - EXIT
ok "stopped the transient deploy host (the engine reads rate_sheets directly)"

# ---------------------------------------------------------------------------
# 4. start the engine command/query host on :ENGINE_PORT
# ---------------------------------------------------------------------------
say "4/6 Starting the engine host on ${ENGINE_URL}"
ConnectionStrings__Engine="$PG_CONN" Engine__PacksDir="$ROOT/packs" Engine__PackVersion=pt.2026.1 \
  ASPNETCORE_URLS="$ENGINE_URL" \
  nohup mise exec -- dotnet "$ENGINE_DLL" > "$RUNDIR/engine.log" 2>&1 &
echo $! > "$RUNDIR/engine.pid"
wait_up "${ENGINE_URL}/v1/deposits/00000000-0000-0000-0000-000000000000" 60 "engine host" "$RUNDIR/engine.log"

# ---------------------------------------------------------------------------
# 5. drive constitute -> read -> mature and assert the canonical numbers
# ---------------------------------------------------------------------------
say "5/6 Driving constitute → read → mature and asserting the canonical AT_MATURITY numbers"
cat > "$RUNDIR/constitute-req.json" <<JSON
{"principal_cents":1000000,"product_id":"${PRODUCT}","role":"standard","term_days":365,"start_date":"2026-01-15","interest_variant":"AT_MATURITY","auto_renewal_policy":"NONE","funding_account":"PT50-DDA-001"}
JSON

code="$(curl -sS -o "$RUNDIR/constitute-resp.json" -w '%{http_code}' \
  -X POST "${ENGINE_URL}/v1/deposits" -H 'Content-Type: application/json' \
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
if [ ! -d mcp-server/.venv ]; then
  (cd mcp-server && mise exec -- python -m venv .venv) || die "venv creation failed"
fi
VENV_PY="$ROOT/mcp-server/.venv/bin/python"
(cd mcp-server && "$VENV_PY" -m pip install -q -e '.[dev]') || die "pip install failed"
(cd mcp-server && "$VENV_PY" -m pytest -q) || die "MCP contract tests failed"
ok "MCP package installed; contract tests green"

BABELSTONE_ENGINE_URL="$ENGINE_URL" \
  nohup "$VENV_PY" -m babelstone_mcp > "$RUNDIR/mcp.log" 2>&1 &
echo $! > "$RUNDIR/mcp.pid"
wait_up "$MCP_URL" 30 "MCP server" "$RUNDIR/mcp.log"

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
     start_date 2026-01-15, funding_account PT50-DDA-001 — then read the
     bank://deposits/{deposit_id} resource and show the position."

  • Mature it (engine-only; the MCP has no maturity tool), then re-read the resource:
     curl -sS -X POST ${ENGINE_URL}/v1/deposits/<deposit_id>/maturity -d '{}'

Stop the engine + MCP when you're done (Postgres is left up):

  make demo-mcp-down
DONE
