#!/usr/bin/env bash
#
# demo-all.sh — one-command bring-up of the WHOLE Mission Control backend (bd babelstone-dvsv).
#
# In plain English: the other three demo scripts each stand up one slice and prove one thing. This one
# stands up EVERYTHING behind Mission Control at once, so you open the UI and flip freely between
# DEMO, LIVE·engine and LIVE·saga — and toggle Operator YOU/CLAUDE — without juggling scripts or
# hitting a port clash. It's the "just show me the whole thing" launcher.
#
# The trick that makes one bring-up serve every mode: the saga's Redpanda-wired engine is a strict
# SUPERSET of the walking-skeleton's Postgres-only engine. LIVE·engine just calls /v1 directly and
# doesn't care that the outbox is also publishing; the MCP server and the agent host likewise just
# need an engine on :8080. So a SINGLE engine serves LIVE·engine, LIVE·saga AND the agent path:
#
#   1. infra: Postgres + Redpanda + Core-ACL stub  (+ the orchestrator's dedicated DB)
#   2. apply the event-store schema to the `babelstone` DB
#   3. build the engine, rate-sheet and orchestrator hosts
#   4. deploy the 3-product rate sheet (so the LIVE·engine variants all price)
#   5. start the engine on :8080, Redpanda-wired (the superset that serves every mode)
#   6. start the orchestrator on :8090 (the LIVE·saga edge + saga + dispatcher)
#   7. start the MCP server on :8000 (+ the real-Claude agent host on :8091, if the key is set)
#   8. start Mission Control (serve.py) on :9000, proxying /v1 + /api/v1 + /agent same-origin
#
# ANTHROPIC_API_KEY is OPTIONAL and lives SERVER-SIDE ONLY in the agent host (ADR-IC-014). Without it
# the agent host is skipped and Operator=CLAUDE degrades to an illustrative narration; every other
# mode still works. For the minimal single-slice paths, see demo-mcp.sh / demo-saga.sh / demo-agent.sh.
#
# Usage:
#   ANTHROPIC_API_KEY=sk-ant-… scripts/demo-all.sh [up]   # bring it all up, leave it running  (make demo)
#   scripts/demo-all.sh down                              # stop every host (infra is left up)  (make demo-down)
#
# Overridable env: PG_PORT REDPANDA_KAFKA_PORT CORE_ACL_STUB_PORT ENGINE_PORT ORCH_PORT MCP_PORT
#                  AGENT_BIND_PORT RATESHEET_PORT MC_PORT
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
# shellcheck source=scripts/demo-lib.sh
. "$ROOT/scripts/demo-lib.sh"

# --- configuration (overridable; defaults match the per-slice scripts + serve.py + compose) ---
PG_PORT="${PG_PORT:-5432}"
REDPANDA_KAFKA_PORT="${REDPANDA_KAFKA_PORT:-19092}"
CORE_ACL_STUB_PORT="${CORE_ACL_STUB_PORT:-8089}"
ENGINE_PORT="${ENGINE_PORT:-8080}"        # engine command/query host — serves LIVE·engine + LIVE·saga + MCP/agent
ORCH_PORT="${ORCH_PORT:-8090}"            # orchestrator edge (LIVE·saga)
MCP_PORT="${MCP_PORT:-8000}"              # MCP server (FastMCP binds 8000 from code — not a real knob, see demo-mcp.sh)
AGENT_PORT="${AGENT_BIND_PORT:-8091}"     # real-Claude agent host
RATESHEET_PORT="${RATESHEET_PORT:-8086}"  # transient RateSheets.Api deploy host (reaped after seeding)
MC_PORT="${MC_PORT:-9000}"                # Mission Control UI / proxy

COMPOSE="docker compose -f infra/compose.yaml"
PG_CONTAINER="babelstone-postgres"
ENGINE_DB="${ENGINE_DB:-babelstone}"
PG_ORCH_DB="${PG_ORCH_DB:-babelstone_orchestrator}"
ENGINE_CONN="Host=localhost;Port=${PG_PORT};Database=${ENGINE_DB};Username=babelstone;Password=babelstone"
ORCH_CONN="Host=localhost;Port=${PG_PORT};Database=${PG_ORCH_DB};Username=babelstone;Password=babelstone"
ENGINE_URL="http://localhost:${ENGINE_PORT}"
ORCH_URL="http://localhost:${ORCH_PORT}"
ACL_URL="http://localhost:${CORE_ACL_STUB_PORT}"
RATESHEET_URL="http://localhost:${RATESHEET_PORT}"
MCP_URL="http://127.0.0.1:${MCP_PORT}/mcp"
MIGRATIONS_DIR="engine/src/Babelstone.EventStore.Migrations/Sql"
RUNDIR="$ROOT/.demo-all"                   # logs + pidfiles (gitignored)
VENV_PY="$ROOT/mcp-server/.venv/bin/python"

PRODUCT="dpz_pt_12m_juros_venc"
RATE_SHEET_VERSION="pt-deposits-2026.1"
DEMO_CLIENT_ID="${DEMO_CLIENT_ID:-CLI-DEMO-0001}"

teardown() {
  say "Stopping every demo host (Postgres/Redpanda/ACL-stub are left running — use 'make down' for the stack)"
  stop_pidfile "$RUNDIR/serve.pid" "Mission Control proxy"
  stop_pidfile "$RUNDIR/agent.pid" "agent host"
  stop_pidfile "$RUNDIR/mcp.pid" "MCP server"
  stop_pidfile "$RUNDIR/orchestrator.pid" "orchestrator host"
  stop_pidfile "$RUNDIR/engine.pid" "engine host"
  # Belt-and-braces: the recorded pid may be a launcher wrapper, so also match the assembly/module.
  pkill -f 'mission-control/serve.py' 2>/dev/null && ok "swept stray Mission Control process(es)" || true
  pkill -f 'babelstone_mcp.agent' 2>/dev/null && ok "swept stray agent host process(es)" || true
  pkill -f 'babelstone_mcp' 2>/dev/null && ok "swept stray MCP process(es)" || true
  pkill -f 'Babelstone.Orchestrator.dll' 2>/dev/null && ok "swept stray orchestrator process(es)" || true
  pkill -f 'Babelstone.Engine.Api.dll' 2>/dev/null && ok "swept stray engine process(es)" || true
  pkill -f 'Babelstone.RateSheets.Api.dll' 2>/dev/null && ok "swept stray rate-sheet deploy host process(es)" || true
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
# 0. preflight — every host port must be free (this is the full backend)
# ---------------------------------------------------------------------------
say "Preflight"
require_demo_tools
for spec in "ENGINE_PORT:$ENGINE_PORT:engine" "ORCH_PORT:$ORCH_PORT:orchestrator" \
            "MCP_PORT:$MCP_PORT:MCP server" "AGENT_PORT:$AGENT_PORT:agent host" \
            "RATESHEET_PORT:$RATESHEET_PORT:transient rate-sheet host" "MC_PORT:$MC_PORT:Mission Control"; do
  var="${spec%%:*}"; rest="${spec#*:}"; port="${rest%%:*}"; label="${rest#*:}"
  if port_busy "$port"; then
    die "port $port is busy ($label) — a sibling demo or 'make up' is holding it. Stop it (the matching '*-down', or 'make down'), or override $var."
  fi
done
ok "docker, mise, lsof present; ports $ENGINE_PORT/$ORCH_PORT/$MCP_PORT/$AGENT_PORT/$RATESHEET_PORT/$MC_PORT are free"

# ---------------------------------------------------------------------------
# 1. infra: Postgres + Redpanda + Core-ACL stub (+ orchestrator DB)
# ---------------------------------------------------------------------------
say "1/8 Starting Postgres + Redpanda + the Core-ACL settlement stub"
$COMPOSE up -d --wait postgres redpanda core-acl-stub
wait_postgres "$PG_CONTAINER"
ok "Postgres on :${PG_PORT}, Redpanda on :${REDPANDA_KAFKA_PORT}, Core-ACL stub on :${CORE_ACL_STUB_PORT}"
create_orchestrator_db "$PG_CONTAINER" "$PG_ORCH_DB"

# ---------------------------------------------------------------------------
# 2. engine event-store schema
# ---------------------------------------------------------------------------
say "2/8 Applying the event-store schema"
apply_event_store_schema "$PG_CONTAINER" "$ENGINE_DB" "$MIGRATIONS_DIR"

# ---------------------------------------------------------------------------
# 3. build the engine, rate-sheet and orchestrator hosts
# ---------------------------------------------------------------------------
say "3/8 Building the engine, rate-sheet and orchestrator hosts (first run restores NuGet — be patient)"
mise exec -- dotnet build engine/src/Babelstone.RateSheets.Api/Babelstone.RateSheets.Api.csproj --nologo -v q \
  || die "RateSheets.Api build failed"
mise exec -- dotnet build engine/src/Babelstone.Engine.Api/Babelstone.Engine.Api.csproj --nologo -v q \
  || die "Engine.Api build failed"
mise exec -- dotnet build orchestrator/src/Babelstone.Orchestrator/Babelstone.Orchestrator.csproj --nologo -v q \
  || die "orchestrator build failed"
RATESHEET_DLL="$(dll_for engine/src/Babelstone.RateSheets.Api Babelstone.RateSheets.Api)"
ENGINE_DLL="$(dll_for engine/src/Babelstone.Engine.Api Babelstone.Engine.Api)"
ORCH_DLL="$(dll_for orchestrator/src/Babelstone.Orchestrator Babelstone.Orchestrator)"
[ -n "$RATESHEET_DLL" ] && [ -n "$ENGINE_DLL" ] && [ -n "$ORCH_DLL" ] \
  || die "built DLLs not found under bin/Debug/net*/"
ok "built"

# ---------------------------------------------------------------------------
# 4. deploy the 3-product rate sheet (so every LIVE·engine variant prices)
# ---------------------------------------------------------------------------
say "4/8 Deploying the rate sheet via the C.6 deploy API (all 3 products, validated seam)"
cat > "$RUNDIR/rate-sheet.json" <<JSON
{"rate_sheet_version_id":"${RATE_SHEET_VERSION}","product_family":"term_deposit","pack_version":"pt.2026.1","effective_from":"2026-01-01T00:00:00+00:00","approved_by":"treasury.alm@bank.internal","approval_ref":"ALM-2026-019","products":{"dpz_pt_12m_juros_venc":{"standard":{"bands":[{"principal_cents":[0,null],"tan_basis_points":300}]}},"dpz_pt_12m_juros_mensal":{"standard":{"bands":[{"principal_cents":[0,null],"tan_basis_points":325}]}},"dpz_pt_12m_juros_antecip":{"standard":{"bands":[{"principal_cents":[0,null],"tan_basis_points":300}]}}}}
JSON
all_deploy() { # base_url
  local url="$1" code
  code="$(ratesheet_post "$url" demo-all "$RUNDIR/rate-sheet.json" "$RUNDIR/deploy-resp.json")"
  case "$code" in
    201) ok "rate sheet ${RATE_SHEET_VERSION} deployed (201 Created)" ;;
    200) ok "rate sheet ${RATE_SHEET_VERSION} already present, identical (200 OK)" ;;
    *)   die "rate-sheet deploy expected 201 or 200, got $code  ($(cat "$RUNDIR/deploy-resp.json"))" ;;
  esac
}
with_ratesheet_host "$RATESHEET_DLL" "$ENGINE_CONN" "$RATESHEET_URL" \
  "$RUNDIR/ratesheet-api.log" all_deploy

# ---------------------------------------------------------------------------
# 5. start the engine — Redpanda-wired (the superset that serves EVERY mode)
# ---------------------------------------------------------------------------
say "5/8 Starting the engine host on ${ENGINE_URL} (Redpanda-wired — serves LIVE·engine, LIVE·saga and the agent path)"
start_engine_host "$ENGINE_DLL" "$ENGINE_CONN" "$ENGINE_URL" "$ROOT/packs" \
  "$RUNDIR/engine.pid" "$RUNDIR/engine.log" "localhost:${REDPANDA_KAFKA_PORT}"

# ---------------------------------------------------------------------------
# 6. start the orchestrator (LIVE·saga edge + saga + dispatcher)
# ---------------------------------------------------------------------------
say "6/8 Starting the orchestrator host on ${ORCH_URL} (it applies its own saga schema on boot)"
start_orchestrator_host "$ORCH_DLL" "$ORCH_CONN" "localhost:${REDPANDA_KAFKA_PORT}" \
  "$ACL_URL" "$ENGINE_URL" "$ORCH_URL" "$RUNDIR/orchestrator.pid" "$RUNDIR/orchestrator.log"

# ---------------------------------------------------------------------------
# 7. start the MCP server (+ the real-Claude agent host, if the key is set)
# ---------------------------------------------------------------------------
say "7/8 Starting the MCP server (+ the real-Claude agent host if ANTHROPIC_API_KEY is set)"
setup_mcp_venv agent
start_mcp_server "$ENGINE_URL" "$RUNDIR/mcp.pid" "$RUNDIR/mcp.log" "$MCP_URL"
if [ -n "${ANTHROPIC_API_KEY:-}" ]; then
  start_agent_host "http://localhost:${MCP_PORT}/mcp" "${AGENT_PORT}" "$RUNDIR/agent.pid" "$RUNDIR/agent.log"
  AGENT_NOTE="real model — Operator=CLAUDE runs Claude through the MCP tools"
else
  warn "ANTHROPIC_API_KEY not set — skipping the agent host; Operator=CLAUDE will degrade to an illustrative narration"
  AGENT_NOTE="illustrative — set ANTHROPIC_API_KEY and re-run for a real model"
fi

# ---------------------------------------------------------------------------
# 8. start Mission Control (serve.py) — proxies /v1 + /api/v1 + /agent same-origin
# ---------------------------------------------------------------------------
say "8/8 Starting Mission Control on http://localhost:${MC_PORT}"
ENGINE_URL="$ENGINE_URL" ORCHESTRATOR_URL="$ORCH_URL" AGENT_URL="http://localhost:${AGENT_PORT}" \
  DEMO_CLIENT_ID="$DEMO_CLIENT_ID" MC_PORT="${MC_PORT}" \
  nohup python3 docs/demo/mission-control/serve.py > "$RUNDIR/serve.log" 2>&1 &
echo $! > "$RUNDIR/serve.pid"
wait_up "http://localhost:${MC_PORT}/" 20 "Mission Control" "$RUNDIR/serve.log"

cat <<DONE

$(printf '\033[1;32m✓ The whole Mission Control backend is up.\033[0m')

  UI            http://localhost:${MC_PORT}        (logs: .demo-all/serve.log)
  engine        ${ENGINE_URL}        (LIVE·engine + the saga's ActivateDeposit target; .demo-all/engine.log)
  orchestrator  ${ORCH_URL}        (LIVE·saga edge + saga + dispatcher; .demo-all/orchestrator.log)
  MCP           ${MCP_URL}   (.demo-all/mcp.log)   agent  http://localhost:${AGENT_PORT}  (.demo-all/agent.log)
  Core-ACL stub ${ACL_URL}        (settlement; WireMock)
  ${AGENT_NOTE}

Open the UI and flip freely — one backend serves every mode:
  • open http://localhost:${MC_PORT}
  • Mode toggle → DEMO / LIVE·engine / LIVE·saga
  • Operator toggle → YOU / CLAUDE   (CLAUDE drives the engine-direct MCP tools, in any Mode)
  • Telemetry toggle → ON   (LIVE·engine pulls the real Tempo trace if the LGTM stack is up)

Stop every host when you're done (infra is left up — use 'make down' for the stack):

  make demo-down
DONE
