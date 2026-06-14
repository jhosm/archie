#!/usr/bin/env bash
#
# demo-saga.sh — one-command bring-up of the constitution-SAGA path for Mission Control's
# LIVE·saga mode (bd babelstone-f0ic.11). Where demo-mcp.sh stands up the engine kernel in
# isolation (Postgres-only walking skeleton, engine-DIRECT), this script stands up the
# INTENDED command-plane topology: a client hits the orchestrator's EDGE front door, which
# STARTS the constitution saga (ADR-IC-003 / Document 05), the saga decides its commands, and
# the dispatcher (ADR-PC-029) delivers the reversible settlement leg to the Core-ACL stub over
# idempotent HTTP. Nothing rides the bus but events.
#
#   1. start Postgres + Redpanda + the Core-ACL settlement stub (infra/compose.yaml)
#   2. build + start the orchestrator host (edge + consume loop + dispatcher); it applies its
#      own saga schema on boot (SagaMigrationHostedService)
#   3. drive POST /api/v1/deposits/constitute → assert 202 + process_id + stream_url
#   4. read the SSE stream + assert the saga walked to APPROVED
#   5. confirm BOTH settlement legs hit the Core-ACL stub (reversible reserve + irreversible debit)
#   6. demo the refusal branch: an "insufficient" account → terminal DEPOSIT_CONSTITUTION_FAILED
#
# WHAT THIS SHOWS (with the t7o3.8 result-event bridge now in main): the saga STARTS, then WALKS the
# full reversible→irreversible flow — ReserveAccountBalance (→ BalanceReserved), the product-limit
# auto-pass (→ LimitsValidated), auto-approval (→ ConstitutionApproved), and the IRREVERSIBLE
# ConfirmDebit (→ DebitConfirmed) — landing at APPROVED. Both settlement legs hit the Core-ACL stub
# (/v1/reservations + /v1/debits). At APPROVED it dispatches ActivateDeposit to the engine on :8080.
#
# The engine is NOT started here (lean bring-up). So ActivateDeposit has no target and the saga rests
# at APPROVED — and the final APPROVED→COMPLETED step needs the engine's real DepositConstituted to
# correlate back as ProcessConstituted, a SEPARATE bridge t7o3.8 deliberately excluded (ADR-PC-029
# slot 2). Start the engine (scripts/demo-mcp.sh up) for ActivateDeposit to land a real deposit.
#
# The REFUSAL branch DOES reach a terminal state on its own: a source account flagged "insufficient"
# makes the Core-ACL stub 422 the reservation → PreconditionRefused → DEPOSIT_CONSTITUTION_FAILED
# (fail-closed, nothing committed). Step 6 demonstrates it.
#
# Usage:
#   scripts/demo-saga.sh [up]    # bring up the saga path, leave the orchestrator running
#   scripts/demo-saga.sh down    # stop the orchestrator host this script started
#
# Overridable env: PG_PORT REDPANDA_KAFKA_PORT CORE_ACL_STUB_PORT ORCH_PORT
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

# --- configuration (overridable; defaults match infra/compose.yaml + Program.cs defaults) ---
PG_PORT="${PG_PORT:-5432}"
REDPANDA_KAFKA_PORT="${REDPANDA_KAFKA_PORT:-19092}"   # Redpanda external listener (from host)
CORE_ACL_STUB_PORT="${CORE_ACL_STUB_PORT:-8089}"      # WireMock settlement stub (from host)
ORCH_PORT="${ORCH_PORT:-8090}"                        # orchestrator edge (Kestrel); avoids :8080 engine

COMPOSE="docker compose -f infra/compose.yaml"
PG_CONTAINER="babelstone-postgres"
# The orchestrator owns its OWN application database (ADR-IC-003 §S2) — distinct from the engine's
# `babelstone` DB. They share table NAMES in `public` (both carry an `inbox` dedup table, the
# orchestrator's lifted from the engine's 0012_inbox.sql), so co-locating them in one database
# collides; a dedicated DB is both correct and conflict-free. The orchestrator's own tests isolate
# via per-test Testcontainers; the demo isolates via this dedicated database.
PG_ORCH_DB="${PG_ORCH_DB:-babelstone_orchestrator}"
ORCH_CONN="Host=localhost;Port=${PG_PORT};Database=${PG_ORCH_DB};Username=babelstone;Password=babelstone"
ORCH_URL="http://localhost:${ORCH_PORT}"
ACL_URL="http://localhost:${CORE_ACL_STUB_PORT}"
RUNDIR="$ROOT/.demo-saga"                              # logs + pidfiles (gitignored)

# --- the demo's gateway-attested caller (the X-Client-Id Kong would propagate, ADR-IC-006 §P4).
# An OPAQUE business reference, never PII. serve.py injects this same value so the start and the
# SSE-read ownership checks agree (EdgeAuth). ---
DEMO_CLIENT_ID="${DEMO_CLIENT_ID:-CLI-DEMO-0001}"

# --- pretty output (mirrors demo-mcp.sh) ---
say()  { printf '\n\033[1;36m▶ %s\033[0m\n' "$*"; }
ok()   { printf '  \033[32m✓ %s\033[0m\n' "$*"; }
info() { printf '  \033[2m%s\033[0m\n' "$*"; }
warn() { printf '  \033[33m! %s\033[0m\n' "$*"; }
die()  { printf '\n\033[1;31m✗ %s\033[0m\n' "$*" >&2; exit 1; }

py() { mise exec -- python "$@"; }   # pinned interpreter, for JSON assertions

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
dll_for()   { ls "$1"/bin/Debug/net*/"$2".dll 2>/dev/null | head -1; }

# The current state of a saga, read from the orchestrator DB (the self-advance + consume loop mutate
# saga_state). Echoes the SCREAMING_SNAKE state name, or empty if the row isn't there yet.
saga_state_of() { # public_process_id
  docker exec "$PG_CONTAINER" psql -U babelstone -d "$PG_ORCH_DB" -tAc \
    "SELECT state FROM saga_state WHERE public_process_id='$1';" 2>/dev/null | tr -d '[:space:]'
}

# How many POSTs the Core-ACL stub received on a given path (its request journal is the proof).
acl_count() { # urlPath
  curl -sS -X POST "${ACL_URL}/__admin/requests/count" -H 'Content-Type: application/json' \
    -d "{\"method\":\"POST\",\"urlPath\":\"$1\"}" 2>/dev/null \
    | py -c "import json,sys;print(json.load(sys.stdin).get('count',0))" 2>/dev/null || echo 0
}

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
  say "Stopping the saga demo's orchestrator host (Postgres/Redpanda/ACL-stub are left running — use 'make down' for the stack)"
  stop_pidfile "$RUNDIR/orchestrator.pid" "orchestrator host"
  pkill -f 'Babelstone.Orchestrator.dll' 2>/dev/null && ok "swept stray orchestrator process(es)" || true
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
if port_busy "$ORCH_PORT"; then
  die "port $ORCH_PORT is busy (orchestrator edge). Stop whatever holds it, or set ORCH_PORT to a free port."
fi
ok "docker, mise, lsof present; orchestrator port $ORCH_PORT is free"

# ---------------------------------------------------------------------------
# 1. infra: Postgres + Redpanda + Core-ACL settlement stub
# ---------------------------------------------------------------------------
say "1/5 Starting Postgres + Redpanda + the Core-ACL settlement stub"
$COMPOSE up -d --wait postgres redpanda core-acl-stub
until docker exec "$PG_CONTAINER" pg_isready -U babelstone -d babelstone >/dev/null 2>&1; do sleep 1; done
ok "Postgres on :${PG_PORT}, Redpanda on :${REDPANDA_KAFKA_PORT}, Core-ACL stub on :${CORE_ACL_STUB_PORT}"

# The orchestrator's dedicated application database (ADR-IC-003 §S2). CREATE DATABASE cannot run in a
# transaction and errors if it exists, so guard on pg_database. Idempotent across re-runs.
if docker exec "$PG_CONTAINER" psql -U babelstone -d babelstone -tAc \
     "SELECT 1 FROM pg_database WHERE datname='${PG_ORCH_DB}'" 2>/dev/null | grep -q 1; then
  ok "orchestrator database '${PG_ORCH_DB}' already present"
else
  docker exec "$PG_CONTAINER" psql -U babelstone -d babelstone -c "CREATE DATABASE ${PG_ORCH_DB}" >/dev/null \
    || die "could not create orchestrator database '${PG_ORCH_DB}'"
  ok "created orchestrator database '${PG_ORCH_DB}' (the saga schema is applied by the host on boot)"
fi

# ---------------------------------------------------------------------------
# 2. build + start the orchestrator host (edge + consume loop + dispatcher)
# ---------------------------------------------------------------------------
say "2/5 Building the orchestrator host (first run restores NuGet — be patient)"
mise exec -- dotnet build orchestrator/src/Babelstone.Orchestrator/Babelstone.Orchestrator.csproj --nologo -v q \
  || die "orchestrator build failed"
ORCH_DLL="$(dll_for orchestrator/src/Babelstone.Orchestrator Babelstone.Orchestrator)"
[ -n "$ORCH_DLL" ] || die "built orchestrator DLL not found under bin/Debug/net*/"
ok "built"

say "Starting the orchestrator host on ${ORCH_URL} (it applies its own saga schema on boot)"
# Connection strings resolve at the composition root (ADR-PC-004 Amendment A1). For the demo the
# bootstrap `babelstone` user serves BOTH the migration (DDL) and runtime roles; the least-privilege
# babelstone_orchestrator runtime role + its envelope are asserted by the orchestrator's own tests,
# not the demo. The Kafka/Engine/Settlement targets are ENDPOINTS, not credentials.
ConnectionStrings__OrchestratorMigration="$ORCH_CONN" \
  ConnectionStrings__Orchestrator="$ORCH_CONN" \
  Kafka__BootstrapServers="localhost:${REDPANDA_KAFKA_PORT}" \
  Settlement__BaseUrl="$ACL_URL" \
  Engine__BaseUrl="http://localhost:8080" \
  ASPNETCORE_URLS="$ORCH_URL" ASPNETCORE_ENVIRONMENT=Development \
  nohup mise exec -- dotnet "$ORCH_DLL" > "$RUNDIR/orchestrator.log" 2>&1 &
echo $! > "$RUNDIR/orchestrator.pid"
# The edge 404s an unknown process id — a clean "the HTTP surface is live" probe.
wait_up "${ORCH_URL}/api/v1/processes/PROC-UNKNOWN/stream" 60 "orchestrator host" "$RUNDIR/orchestrator.log"

# ---------------------------------------------------------------------------
# 3. drive the edge front door → assert 202 + process_id + stream_url
# ---------------------------------------------------------------------------
say "3/5 Opening a deposit through the EDGE (POST /api/v1/deposits/constitute)"
cat > "$RUNDIR/constitute-req.json" <<JSON
{"product_code":"dpz_pt_12m_juros_venc","amount":1000000,"source_account_ref":"ACCT-REF-DEMO-001","interest_account_ref":"ACCT-REF-DEMO-002"}
JSON

code="$(curl -sS -o "$RUNDIR/constitute-resp.json" -w '%{http_code}' \
  -X POST "${ORCH_URL}/api/v1/deposits/constitute" \
  -H 'Content-Type: application/json' -H "X-Client-Id: ${DEMO_CLIENT_ID}" \
  --data-binary @"$RUNDIR/constitute-req.json")"
[ "$code" = 202 ] || die "constitute expected 202 Accepted, got $code  ($(cat "$RUNDIR/constitute-resp.json"))"
PROC="$(py -c "import json;print(json.load(open('$RUNDIR/constitute-resp.json'))['process_id'])")"
STREAM="$(py -c "import json;print(json.load(open('$RUNDIR/constitute-resp.json'))['stream_url'])")"
ok "saga STARTED → 202 Accepted (process ${PROC})"
info "stream_url: ${STREAM}"

# ---------------------------------------------------------------------------
# 4. read the SSE stream → assert a structural state frame is emitted
# ---------------------------------------------------------------------------
say "4/6 Reading the SSE stream + waiting for the saga to walk to APPROVED"
# The stream is long-lived; the saga self-advances fast as each leg is delivered. Capture a few
# frames, then poll the persisted state until it reaches APPROVED (or time out).
curl -sS --max-time 4 "${ORCH_URL}${STREAM}" -H "X-Client-Id: ${DEMO_CLIENT_ID}" \
  > "$RUNDIR/stream.txt" 2>/dev/null || true
grep -q '^event: state' "$RUNDIR/stream.txt" \
  && ok "SSE state frames received" \
  || warn "no SSE state frame captured in the read window (see $RUNDIR/stream.txt)"
FINAL=""
for _ in $(seq 1 12); do
  FINAL="$(saga_state_of "$PROC")"
  [ "$FINAL" = "APPROVED" ] && break
  sleep 1
done
if [ "$FINAL" = "APPROVED" ]; then
  ok "saga walked STARTED → PARALLEL_VALIDATION → VALIDATIONS_COMPLETE → APPROVED"
  info "reserve + limits + approval + the irreversible debit all fired; ActivateDeposit now dispatched to the engine"
else
  warn "saga at '${FINAL:-?}' (expected APPROVED — check $RUNDIR/orchestrator.log)"
fi

# ---------------------------------------------------------------------------
# 5. confirm BOTH settlement legs hit the Core-ACL stub (reserve + irreversible debit)
# ---------------------------------------------------------------------------
say "5/6 Confirming BOTH settlement legs hit the Core-ACL stub"
RES="$(acl_count /v1/reservations)"
DEB="$(acl_count /v1/debits)"
[ "${RES:-0}" -ge 1 ] \
  && ok "ReserveAccountBalance delivered (POST /v1/reservations ×${RES}) — the reversible hold" \
  || warn "no reservation seen at the ACL stub (check $RUNDIR/orchestrator.log)"
[ "${DEB:-0}" -ge 1 ] \
  && ok "ConfirmDebit delivered (POST /v1/debits ×${DEB}) — the IRREVERSIBLE money leg" \
  || warn "no debit seen at the ACL stub yet (dispatcher may still be draining — check $RUNDIR/orchestrator.log)"

# ---------------------------------------------------------------------------
# 6. demo the refusal branch — fail-closed terminal, no money moved
# ---------------------------------------------------------------------------
say "6/6 Demonstrating the refusal branch (fail-closed terminal)"
# A source account flagged "insufficient" makes the Core-ACL stub 422 the reservation, so the saga
# fails CLOSED before any irreversible effect: PreconditionRefused → DEPOSIT_CONSTITUTION_FAILED.
cat > "$RUNDIR/refusal-req.json" <<JSON
{"product_code":"dpz_pt_12m_juros_venc","amount":1000000,"source_account_ref":"ACCT-insufficient-001","interest_account_ref":"ACCT-REF-DEMO-002"}
JSON
RPROC="$(curl -sS -X POST "${ORCH_URL}/api/v1/deposits/constitute" \
  -H 'Content-Type: application/json' -H "X-Client-Id: ${DEMO_CLIENT_ID}" \
  --data-binary @"$RUNDIR/refusal-req.json" \
  | py -c "import json,sys;print(json.load(sys.stdin)['process_id'])" 2>/dev/null || echo '')"
RFINAL=""
for _ in $(seq 1 12); do
  RFINAL="$(saga_state_of "$RPROC")"
  [ "$RFINAL" = "DEPOSIT_CONSTITUTION_FAILED" ] && break
  sleep 1
done
if [ "$RFINAL" = "DEPOSIT_CONSTITUTION_FAILED" ]; then
  ok "refusal saga ${RPROC} reached terminal DEPOSIT_CONSTITUTION_FAILED (fail-closed — nothing committed)"
else
  warn "refusal saga at '${RFINAL:-?}' (expected DEPOSIT_CONSTITUTION_FAILED — check $RUNDIR/orchestrator.log)"
fi

# ---------------------------------------------------------------------------
# done
# ---------------------------------------------------------------------------
cat <<DONE

$(printf '\033[1;32m✓ Constitution-saga path is up.\033[0m')

  orchestrator  ${ORCH_URL}   (edge + consume loop + dispatcher; logs: .demo-saga/orchestrator.log)
  Core-ACL stub ${ACL_URL}    (settlement; WireMock)
  happy-path saga: ${PROC} (→ APPROVED)   refusal saga: ${RPROC:-—} (→ DEPOSIT_CONSTITUTION_FAILED)

The happy-path saga walked to APPROVED — the reversible reserve AND the irreversible debit both
fired against the Core-ACL stub. At APPROVED it dispatches ActivateDeposit to the engine on :8080;
start the engine (scripts/demo-mcp.sh up) for it to land a REAL deposit. The final APPROVED→COMPLETED
step needs the engine→saga completion bridge (ADR-PC-029 slot 2), a separate piece not built yet.

Drive it from Mission Control's LIVE·saga mode:

  python3 docs/demo/mission-control/serve.py     # serves the UI + proxies /api/v1/* here
  open http://localhost:9000                      # flip Mode to LIVE·saga

Stop the orchestrator host when you're done (infra is left up — use 'make down' for the stack):

  scripts/demo-saga.sh down
DONE
