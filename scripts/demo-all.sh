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
#      + the OTel Collector → Grafana/Tempo LGTM stack (so LIVE·engine Telemetry shows real spans)
#   2. apply the event-store schema to the `babelstone` DB
#   3. build the engine, rate-sheet and orchestrator hosts
#   4. deploy the 3-product rate sheet (so the LIVE·engine variants all price)
#   5. start the engine on :8080, Redpanda-wired (the superset that serves every mode), then open +
#      credit-seed a customer conta à ordem on it — the account products settle against (engine-CA)
#   6. start the orchestrator on :8090 (the LIVE·saga edge + saga + dispatcher), with the engine-CA
#      settlement target pointed at the engine so a ce_settlementtarget=engine-ca leg routes home (ADR-PC-043)
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
#                  AGENT_BIND_PORT RATESHEET_PORT MC_PORT OTLP_GRPC_PORT GRAFANA_PORT TEMPO_PORT
#                  ENGINE_CA_SETTLEMENT_URL (engine-CA settlement target; default the engine — set empty for legacy-DDA only)
#                  DEMO_CA_SEED_CENTS (starting balance seeded on the customer conta à ordem)
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
MCP_PORT="${MCP_PORT:-8000}"              # MCP server listen port (start_mcp_server pins MCP_BIND_PORT to it; the server defaults to 8080 otherwise)
AGENT_PORT="${AGENT_BIND_PORT:-8091}"     # real-Claude agent host
RATESHEET_PORT="${RATESHEET_PORT:-8086}"  # transient RateSheets.Api deploy host (reaped after seeding)
MC_PORT="${MC_PORT:-9000}"                # Mission Control UI / proxy
OTLP_GRPC_PORT="${OTLP_GRPC_PORT:-4317}"  # OTel Collector OTLP/gRPC ingest (the engine exports here, ADR-IC-007 §P1)
GRAFANA_PORT="${GRAFANA_PORT:-3000}"      # Grafana UI (open a real trace here)
TEMPO_PORT="${TEMPO_PORT:-3200}"          # Tempo query API (Mission Control Telemetry tab reads real spans by trace id)
LOKI_PORT="${LOKI_PORT:-3100}"            # Loki query API (Mission Control Logs lens; compose binds :3100)
PROMETHEUS_PORT="${PROMETHEUS_PORT:-9090}" # Prometheus query API (Mission Control Metrics lens; compose binds :9090)

COMPOSE="docker compose -f infra/compose.yaml"
PG_CONTAINER="babelstone-postgres"
ENGINE_DB="${ENGINE_DB:-babelstone}"
PG_ORCH_DB="${PG_ORCH_DB:-babelstone_orchestrator}"
ENGINE_CONN="Host=localhost;Port=${PG_PORT};Database=${ENGINE_DB};Username=babelstone;Password=babelstone"
ORCH_CONN="Host=localhost;Port=${PG_PORT};Database=${PG_ORCH_DB};Username=babelstone;Password=babelstone"
ENGINE_URL="http://localhost:${ENGINE_PORT}"
ORCH_URL="http://localhost:${ORCH_PORT}"
ACL_URL="http://localhost:${CORE_ACL_STUB_PORT}"
# The engine-owned CA settlement target (ADR-PC-043). Defaults to the engine's own base URL so a
# ce_settlementtarget=engine-ca leg routes HOME to the engine's authorize/capture/credit ingress and lands
# on the seeded conta à ordem. Set ENGINE_CA_SETTLEMENT_URL= (empty) to keep the legacy-DDA-only path.
ENGINE_CA_SETTLEMENT_URL="${ENGINE_CA_SETTLEMENT_URL-http://localhost:${ENGINE_PORT}}"
RATESHEET_URL="http://localhost:${RATESHEET_PORT}"
MCP_URL="http://127.0.0.1:${MCP_PORT}/mcp"
MIGRATIONS_DIR="engine/src/Babelstone.EventStore.Migrations/Sql"
RUNDIR="$ROOT/.demo-all"                   # logs + pidfiles (gitignored)
VENV_PY="$ROOT/mcp-server/.venv/bin/python"

PRODUCT="dpz_pt_12m_juros_venc"
RATE_SHEET_VERSION="pt-deposits-2026.1"
# The committed rate-sheet YAML is the SINGLE SOURCE we deploy (bd babelstone-alfy): the same file a
# treasury author edits, serialised 1:1 to JSON at deploy time (ADR-PC-008 §P1). It already carries all
# three LIVE·engine products (venc 300 / mensal 325 / antecip 300 bps), so there is no inline JSON to drift.
RATE_SHEET_YAML="rate-sheets/term_deposit/${RATE_SHEET_VERSION}.yaml"
# The personal-loan (crédito pessoal) fixed-rate sheet, deployed alongside the deposit sheet so a loan can
# disburse LIVE·engine (not DEMO-only). Same YAML-native deploy path, same host — one more POST. It prices
# the cp_pt_* loan variants under product-configs/personal-loan/ at role `standard` (ADR-PC-030 / ADR-PC-008).
LOAN_RATE_SHEET_VERSION="pt-loans-2026.1"
LOAN_RATE_SHEET_YAML="rate-sheets/personal_loan/${LOAN_RATE_SHEET_VERSION}.yaml"
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
# `make up` binds Kong (:8000) + Redpanda Console (:8080) — the exact ports the demo's MCP (:8000) and
# engine (:8080) need. serve.py is the demo's gateway stand-in, so Kong/Console aren't needed here: stop
# just those two named containers if they're what's holding the ports (bd babelstone-3xtq). Any OTHER
# listener is left for the port checks below to flag.
free_makeup_demo_clashes "$MCP_PORT" "$ENGINE_PORT"
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
#    + the OTel Collector → Grafana/Tempo LGTM stack, so the Telemetry tab in
#    LIVE·engine mode pulls the REAL trace (with the Npgsql db.client query spans,
#    bd scd2.3) instead of silently degrading to the illustrative waterfall. The
#    engine exports OTLP to the collector on :4317 (ADR-IC-007 §P1); without these
#    two services up, every span the engine emits is dropped on the floor.
# ---------------------------------------------------------------------------
say "1/8 Starting Postgres + Redpanda + the Core-ACL stub + the OTel/Tempo trace backend"
$COMPOSE up -d --wait postgres redpanda core-acl-stub otel-collector grafana-lgtm
wait_postgres "$PG_CONTAINER"
ok "Postgres on :${PG_PORT}, Redpanda on :${REDPANDA_KAFKA_PORT}, Core-ACL stub on :${CORE_ACL_STUB_PORT}, OTel Collector on :${OTLP_GRPC_PORT} → Grafana/Tempo on :${GRAFANA_PORT}/:${TEMPO_PORT}"
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
# 4. deploy the rate sheets (so every LIVE·engine variant prices): the 3-product
#    term-deposit sheet AND the personal-loan sheet, from the same host.
# ---------------------------------------------------------------------------
say "4/8 Deploying the rate sheets via the C.6 deploy API (deposit + loan, validated seam)"
info "deploying FROM the committed YAML sources ${RATE_SHEET_YAML} and ${LOAN_RATE_SHEET_YAML} (serialised 1:1 to JSON — ADR-PC-008 §P1)"
[ -f "$RATE_SHEET_YAML" ] || die "rate-sheet YAML source not found: $RATE_SHEET_YAML"
[ -f "$LOAN_RATE_SHEET_YAML" ] || die "loan rate-sheet YAML source not found: $LOAN_RATE_SHEET_YAML"
# Deploy one committed sheet against a running ratesheet host; die on anything but 200/201 (idempotent).
deploy_one_sheet() { # url version yaml_file
  local url="$1" version="$2" yaml="$3" code
  code="$(ratesheet_post_yaml "$url" demo-all "$yaml" "$RUNDIR/deploy-resp.json")"
  case "$code" in
    201) ok "rate sheet ${version} deployed (201 Created)" ;;
    200) ok "rate sheet ${version} already present, identical (200 OK)" ;;
    *)   die "rate-sheet deploy expected 201 or 200, got $code  ($(cat "$RUNDIR/deploy-resp.json"))" ;;
  esac
}
all_deploy() { # base_url
  local url="$1"
  deploy_one_sheet "$url" "$RATE_SHEET_VERSION" "$RATE_SHEET_YAML"
  deploy_one_sheet "$url" "$LOAN_RATE_SHEET_VERSION" "$LOAN_RATE_SHEET_YAML"
}
with_ratesheet_host "$RATESHEET_DLL" "$ENGINE_CONN" "$RATESHEET_URL" \
  "$RUNDIR/ratesheet-api.log" all_deploy

# ---------------------------------------------------------------------------
# 5. start the engine — Redpanda-wired (the superset that serves EVERY mode)
# ---------------------------------------------------------------------------
say "5/8 Starting the engine host on ${ENGINE_URL} (Redpanda-wired — serves LIVE·engine, LIVE·saga and the agent path)"
# Point the engine's OTLP exporter at the collector's HOST-published gRPC ingest (:${OTLP_GRPC_PORT}), so
# its deposit.*/Npgsql spans reach the collector → grafana-lgtm and land in Tempo — the trace the Mission
# Control Telemetry tab then queries by id (bd w5hx). The SDK default is already http://localhost:4317, but
# setting it explicitly keeps the export path correct when OTLP_GRPC_PORT is overridden. start_engine_host's
# inline env-prefix inherits this exported value for the dotnet process (it sets no OTEL_* of its own).
export OTEL_EXPORTER_OTLP_ENDPOINT="http://localhost:${OTLP_GRPC_PORT}"
start_engine_host "$ENGINE_DLL" "$ENGINE_CONN" "$ENGINE_URL" "$ROOT/packs" \
  "$RUNDIR/engine.pid" "$RUNDIR/engine.log" "localhost:${REDPANDA_KAFKA_PORT}"

# Open + seed a customer conta à ordem on the engine — the account products SETTLE against. The seeded id
# IS the account_ref (AccountRef == AccountId.ToString(), ADR-PC-033); paste it as the source/interest
# account when constituting a deposit LIVE·saga in the UI, and a ce_settlementtarget=engine-ca leg lands on
# THIS account (ADR-PC-043) — funding debits it (hold → capture), maturity credits it. The starting balance
# covers a demo deposit's principal with headroom.
say "Opening + seeding a customer conta à ordem on the engine (products settle against it)"
DEMO_CA_ID="$(open_and_seed_demo_ca "$ENGINE_URL" "${DEMO_CA_SEED_CENTS:-200000000}" "$RUNDIR")"
ok "demo customer CA ${DEMO_CA_ID} open + seeded on the engine (the engine-CA settlement target)"

# ---------------------------------------------------------------------------
# 6. start the orchestrator (LIVE·saga edge + saga + dispatcher)
# ---------------------------------------------------------------------------
say "6/8 Starting the orchestrator host on ${ORCH_URL} (it applies its own saga schema on boot)"
# Settlement counterparty routing (ADR-PC-043): $ACL_URL is the LEGACY-DDA home (Settlement__BaseUrl,
# the WireMock Core-ACL stub) — kept as the fallback so the legacy-DDA path stays available. The
# ENGINE_CA_SETTLEMENT_URL (default: the engine's base URL) is the engine-owned CA target
# (Settlement__EngineCaBaseUrl): a ce_settlementtarget=engine-ca leg routes HOME to the engine's
# authorize/capture/credit ingress, landing on the seeded conta à ordem. Set ENGINE_CA_SETTLEMENT_URL=
# (empty) for the pre-ADR-PC-043 legacy-only behaviour (engine-ca legs then fail closed).
start_orchestrator_host "$ORCH_DLL" "$ORCH_CONN" "localhost:${REDPANDA_KAFKA_PORT}" \
  "$ACL_URL" "$ENGINE_URL" "$ORCH_URL" "$RUNDIR/orchestrator.pid" "$RUNDIR/orchestrator.log" \
  "$ENGINE_CA_SETTLEMENT_URL"

# ---------------------------------------------------------------------------
# 7. start the MCP server (+ the real-Claude agent host, if the key is set)
# ---------------------------------------------------------------------------
say "7/8 Starting the MCP server (+ the real-Claude agent host if ANTHROPIC_API_KEY is set)"
setup_mcp_venv agent
start_mcp_server "$ENGINE_URL" "$MCP_PORT" "$RUNDIR/mcp.pid" "$RUNDIR/mcp.log" "$MCP_URL"
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
# Point serve.py's Telemetry/Logs/Metrics proxy arms at the LGTM appliance's HOST-published query APIs
# (compose binds Tempo :3200, Loki :3100, Prometheus :9090 — overridable via TEMPO_PORT/LOKI_PORT/
# PROMETHEUS_PORT). serve.py defaults to those same loopback ports, but passing them explicitly keeps the
# Telemetry tab wired to the REAL Grafana Tempo trace even when a port is overridden (bd w5hx) — otherwise
# the tab degrades to its illustrative fallback because :3200 didn't answer. The engine already exports its
# OTLP traces to the collector (:${OTLP_GRPC_PORT}) → grafana-lgtm, so Tempo holds the trace this queries.
ENGINE_URL="$ENGINE_URL" ORCHESTRATOR_URL="$ORCH_URL" AGENT_URL="http://localhost:${AGENT_PORT}" \
  DEMO_CLIENT_ID="$DEMO_CLIENT_ID" MC_PORT="${MC_PORT}" \
  TEMPO_URL="http://localhost:${TEMPO_PORT}" \
  LOKI_URL="http://localhost:${LOKI_PORT}" \
  PROM_URL="http://localhost:${PROMETHEUS_PORT}" \
  nohup python3 docs/demo/mission-control/serve.py > "$RUNDIR/serve.log" 2>&1 &
echo $! > "$RUNDIR/serve.pid"
wait_up "http://localhost:${MC_PORT}/" 20 "Mission Control" "$RUNDIR/serve.log"

cat <<DONE

$(printf '\033[1;32m✓ The whole Mission Control backend is up.\033[0m')

  UI            http://localhost:${MC_PORT}        (logs: .demo-all/serve.log)
  engine        ${ENGINE_URL}        (LIVE·engine + the saga's ActivateDeposit target + engine-CA settlement ingress; .demo-all/engine.log)
  orchestrator  ${ORCH_URL}        (LIVE·saga edge + saga + dispatcher; .demo-all/orchestrator.log)
  MCP           ${MCP_URL}   (.demo-all/mcp.log)   agent  http://localhost:${AGENT_PORT}  (.demo-all/agent.log)
  Core-ACL stub ${ACL_URL}        (legacy-DDA settlement; WireMock)
  customer CA   ${DEMO_CA_ID:-—}   (engine-owned conta à ordem — the engine-CA settlement target; GET ${ENGINE_URL}/v1/accounts/${DEMO_CA_ID:-<id>})
  ${AGENT_NOTE}

Open the UI and flip freely — one backend serves every mode:
  • open http://localhost:${MC_PORT}
  • Mode toggle → DEMO / LIVE·engine / LIVE·saga
  • Operator toggle → YOU / CLAUDE   (CLAUDE drives the engine-direct MCP tools, in any Mode)
  • Telemetry toggle → ON   (LIVE·engine pulls the real Tempo trace — the LGTM stack is up; open it in Grafana on http://localhost:${GRAFANA_PORT})
  • LIVE·saga engine-CA settlement → constitute with the seeded customer CA id above as the source/interest account
    (${DEMO_CA_ID:-—}); a GUID funding ref routes ce_settlementtarget=engine-ca (ADR-PC-043), so the funding debit +
    maturity credit land on that real account. A legacy (non-GUID) ref keeps the legacy-DDA path to the Core-ACL stub.

Stop every host when you're done (infra is left up — use 'make down' for the stack):

  make demo-down
DONE
