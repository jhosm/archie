#!/usr/bin/env bash
#
# demo-saga.sh — one-command bring-up of the constitution-SAGA path for Mission Control's
# LIVE·saga mode (bd babelstone-f0ic.11). Where demo-mcp.sh stands up the engine kernel in
# isolation (Postgres-only walking skeleton, engine-DIRECT), this script stands up the
# INTENDED command-plane topology END TO END: a client hits the orchestrator's EDGE front door,
# which STARTS the constitution saga (ADR-IC-003 / Document 05), the saga decides its commands, the
# dispatcher (ADR-PC-029) delivers the reversible settlement leg to the Core-ACL stub over idempotent
# HTTP, and ActivateDeposit lands a REAL deposit in the engine — whose DepositConstituted event flows
# back over the bus to carry the saga to its terminal success state. Nothing rides the bus but events.
#
#   1. start Postgres + Redpanda + the Core-ACL settlement stub (infra/compose.yaml)
#   2. stand up the ENGINE: apply the event-store schema, seed the rate sheet, start the engine host
#      on :8080 pointed at the SAME Redpanda (so its outbox relay publishes DepositConstituted)
#   3. build + start the orchestrator host (edge + consume loop + dispatcher); it applies its
#      own saga schema on boot (SagaMigrationHostedService)
#   4. drive POST /api/v1/deposits/constitute → assert 202 + process_id + stream_url
#   5. read the SSE stream + assert the saga walked to terminal COMPLETED, and that the engine
#      recorded the REAL deposit (GET the engine by the process_id = deposit_id correlation key)
#   6. confirm BOTH settlement legs hit the Core-ACL stub (reversible reserve + irreversible debit)
#   7. demo the refusal branch: an "insufficient" account → terminal DEPOSIT_CONSTITUTION_FAILED
#
# For the WHOLE UI (this saga path AND LIVE·engine AND Operator=CLAUDE, all in one bring-up so you can
# flip modes in the browser), see scripts/demo-all.sh / `make demo`.
#
# WHAT THIS SHOWS (with the engine→saga completion bridge now in main, bd babelstone-t7o3.11 / PR #200):
# the saga STARTS, WALKS the full reversible→irreversible flow — ReserveAccountBalance (→ BalanceReserved),
# the product-limit auto-pass (→ LimitsValidated), auto-approval (→ ConstitutionApproved), the IRREVERSIBLE
# ConfirmDebit (→ DebitConfirmed) — reaching APPROVED, where it dispatches ActivateDeposit to the engine.
# The engine appends a de-settled DepositConstituted (ADR-PC-029) and its catalog-gated outbox relay
# publishes that fact onto the `term_deposit` family topic; the orchestrator's consume loop reads it,
# correlates ce_subject → process_id, and advances (APPROVED, DepositConstituted) → COMPLETED. The
# saga advances on the ENGINE'S EVENT, never on the ActivateDeposit HTTP 2xx (the slot-2 contract).
# Both settlement legs hit the Core-ACL stub (/v1/reservations + /v1/debits); the engine ends up holding
# a real, queryable deposit.
#
# The REFUSAL branch reaches a terminal state without ever touching the engine: a source account flagged
# "insufficient" makes the Core-ACL stub 422 the reservation → PreconditionRefused →
# DEPOSIT_CONSTITUTION_FAILED, fail-closed BEFORE approval (so ActivateDeposit is never dispatched and no
# deposit is appended). Step 7 demonstrates it.
#
# Usage:
#   scripts/demo-saga.sh [up]    # bring up the full saga path, leave engine + orchestrator running
#   scripts/demo-saga.sh down    # stop the engine + orchestrator hosts this script started
#
# Overridable env: PG_PORT REDPANDA_KAFKA_PORT CORE_ACL_STUB_PORT ORCH_PORT ENGINE_PORT RATESHEET_PORT
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
# shellcheck source=scripts/demo-lib.sh
. "$ROOT/scripts/demo-lib.sh"

# --- configuration (overridable; defaults match infra/compose.yaml + Program.cs defaults) ---
PG_PORT="${PG_PORT:-5432}"
REDPANDA_KAFKA_PORT="${REDPANDA_KAFKA_PORT:-19092}"   # Redpanda external listener (from host)
CORE_ACL_STUB_PORT="${CORE_ACL_STUB_PORT:-8089}"      # WireMock settlement stub (from host)
ORCH_PORT="${ORCH_PORT:-8090}"                        # orchestrator edge (Kestrel); avoids :8080 engine
ENGINE_PORT="${ENGINE_PORT:-8080}"                    # engine command/query host — the ActivateDeposit target
RATESHEET_PORT="${RATESHEET_PORT:-8086}"              # transient RateSheets.Api deploy host (reaped after seeding)

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

# The engine owns the `babelstone` application database (created by the compose postgres init,
# POSTGRES_DB=babelstone) — distinct from the orchestrator's `babelstone_orchestrator` above. The
# saga's ActivateDeposit lands here as a REAL deposit; the engine's outbox relay then publishes the
# resulting DepositConstituted onto the `term_deposit` FAMILY topic (topic = aggregate_type), which
# the orchestrator's consume loop reads to drive APPROVED → COMPLETED on the engine's real event —
# the ADR-PC-029 slot-2 contract (advance on the EVENT, never the ActivateDeposit HTTP 2xx).
ENGINE_DB="${ENGINE_DB:-babelstone}"
ENGINE_CONN="Host=localhost;Port=${PG_PORT};Database=${ENGINE_DB};Username=babelstone;Password=babelstone"
ENGINE_URL="http://localhost:${ENGINE_PORT}"
RATESHEET_URL="http://localhost:${RATESHEET_PORT}"
MIGRATIONS_DIR="engine/src/Babelstone.EventStore.Migrations/Sql"
PRODUCT="${PRODUCT:-dpz_pt_12m_juros_venc}"           # the product the saga constitutes (1:1 with demo-mcp.sh)
RATE_SHEET_VERSION="${RATE_SHEET_VERSION:-pt-deposits-2026.1}"
# The committed rate-sheet YAML is the SINGLE SOURCE we deploy (bd babelstone-alfy): the same file a
# treasury author edits, serialised 1:1 to JSON at deploy time (ADR-PC-008 §P1), not an inline JSON
# heredoc that could drift. It prices dpz_pt_12m_juros_venc at 300 bps — what the in-tx resolve needs.
RATE_SHEET_YAML="${RATE_SHEET_YAML:-rate-sheets/term_deposit/${RATE_SHEET_VERSION}.yaml}"

RUNDIR="$ROOT/.demo-saga"                              # logs + pidfiles (gitignored)

# --- the demo's gateway-attested caller (the X-Client-Id Kong would propagate, ADR-IC-006 §P4).
# An OPAQUE business reference, never PII. serve.py injects this same value so the start and the
# SSE-read ownership checks agree (EdgeAuth). ---
DEMO_CLIENT_ID="${DEMO_CLIENT_ID:-CLI-DEMO-0001}"

# --- saga/ACL inspection helpers (specific to this path; the shared helpers live in demo-lib.sh) ---

# The current state of a saga, read from the orchestrator DB (the self-advance + consume loop mutate
# saga_state). Echoes the SCREAMING_SNAKE state name, or empty if the row isn't there yet.
saga_state_of() { # public_process_id
  docker exec "$PG_CONTAINER" psql -U babelstone -d "$PG_ORCH_DB" -tAc \
    "SELECT state FROM saga_state WHERE public_process_id='$1';" 2>/dev/null | tr -d '[:space:]'
}

# The INTERNAL saga process_id (a UUID) for a public PROC-… reference. This is the engine's deposit
# stream id too: the saga POSTs deposit_id = process_id, so the engine's aggregate_id == process_id
# (== the DepositConstituted ce_subject the consume loop correlates on). We GET the engine by it.
saga_uuid_of() { # public_process_id
  docker exec "$PG_CONTAINER" psql -U babelstone -d "$PG_ORCH_DB" -tAc \
    "SELECT process_id FROM saga_state WHERE public_process_id='$1';" 2>/dev/null | tr -d '[:space:]'
}

# How many POSTs the Core-ACL stub received on a given path (its request journal is the proof).
acl_count() { # urlPath
  curl -sS -X POST "${ACL_URL}/__admin/requests/count" -H 'Content-Type: application/json' \
    -d "{\"method\":\"POST\",\"urlPath\":\"$1\"}" 2>/dev/null \
    | py -c "import json,sys;print(json.load(sys.stdin).get('count',0))" 2>/dev/null || echo 0
}

teardown() {
  say "Stopping the saga demo's engine + orchestrator hosts (Postgres/Redpanda/ACL-stub are left running — use 'make down' for the stack)"
  stop_pidfile "$RUNDIR/orchestrator.pid" "orchestrator host"
  pkill -f 'Babelstone.Orchestrator.dll' 2>/dev/null && ok "swept stray orchestrator process(es)" || true
  stop_pidfile "$RUNDIR/engine.pid" "engine host"
  pkill -f 'Babelstone.Engine.Api.dll' 2>/dev/null && ok "swept stray engine process(es)" || true
  # The rate-sheet deploy host is transient (reaped by the EXIT trap on a normal run), but sweep any
  # orphan a hard kill in the narrow pre-trap window could have left behind.
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

# Tracks whether every demo assertion held. A failed assertion `warn`s (it does NOT `die` — we leave
# the stack up for inspection), but it flips this to false so the closing banner reports the truth
# instead of unconditionally claiming end-to-end success.
ALL_GREEN=true

# ---------------------------------------------------------------------------
# 0. preflight
# ---------------------------------------------------------------------------
say "Preflight"
require_demo_tools
if port_busy "$ORCH_PORT"; then
  die "port $ORCH_PORT is busy (orchestrator edge). Stop whatever holds it, or set ORCH_PORT to a free port."
fi
if port_busy "$ENGINE_PORT"; then
  die "port $ENGINE_PORT is busy (engine). Likely a 'demo-mcp.sh' engine or Redpanda Console from 'make up' — stop it, or set ENGINE_PORT to a free port."
fi
# The rate-sheet deploy host is transient, but guard its port too: if something else holds it, wait_up
# would return on the squatter's response and the deploy POST would land on the wrong server, masking
# the real port clash behind a confusing "expected 201 or 200, got X" die.
if port_busy "$RATESHEET_PORT"; then
  die "port $RATESHEET_PORT is busy (transient rate-sheet deploy host). Stop whatever holds it, or set RATESHEET_PORT to a free port."
fi
ok "docker, mise, lsof present; ports $ORCH_PORT (orchestrator), $ENGINE_PORT (engine), $RATESHEET_PORT (rate-sheet) are free"

# ---------------------------------------------------------------------------
# 1. infra: Postgres + Redpanda + Core-ACL settlement stub
# ---------------------------------------------------------------------------
say "1/7 Starting Postgres + Redpanda + the Core-ACL settlement stub"
$COMPOSE up -d --wait postgres redpanda core-acl-stub
wait_postgres "$PG_CONTAINER"
ok "Postgres on :${PG_PORT}, Redpanda on :${REDPANDA_KAFKA_PORT}, Core-ACL stub on :${CORE_ACL_STUB_PORT}"

create_orchestrator_db "$PG_CONTAINER" "$PG_ORCH_DB"

# ---------------------------------------------------------------------------
# 2. stand up the ENGINE: event-store schema → rate sheet → engine host on Redpanda
#
# The engine is the ActivateDeposit target AND the source of the terminal DepositConstituted event.
# It needs (a) the event-store schema applied to the `babelstone` DB — the engine does NOT apply this
# on boot, only its family read-model migration (which itself REQUIRES the `babelstone_engine` role
# that 0002 creates, so a full apply is load-bearing, not optional); (b) a rate sheet so the in-tx
# rate resolve succeeds (ADR-PC-008); (c) the SAME Redpanda the orchestrator consumes, so its outbox
# relay publishes DepositConstituted onto `term_deposit`.
# ---------------------------------------------------------------------------
say "2/7 Standing up the engine (event-store schema → rate sheet → host on Redpanda)"

# (a) event-store schema — shared ledger applier: applies only the migrations a `schema_migrations`
# ledger hasn't recorded (backfilling the ledger from artifacts on a legacy volume first), so a
# pre-existing volume gets the genuinely-missing newer migrations rather than a re-run of 0001.
apply_event_store_schema "$PG_CONTAINER" "$ENGINE_DB" "$MIGRATIONS_DIR"

# build the engine + rate-sheet hosts up front (first run restores NuGet — be patient).
mise exec -- dotnet build engine/src/Babelstone.RateSheets.Api/Babelstone.RateSheets.Api.csproj --nologo -v q \
  || die "RateSheets.Api build failed"
mise exec -- dotnet build engine/src/Babelstone.Engine.Api/Babelstone.Engine.Api.csproj --nologo -v q \
  || die "Engine.Api build failed"
RATESHEET_DLL="$(dll_for engine/src/Babelstone.RateSheets.Api Babelstone.RateSheets.Api)"
ENGINE_DLL="$(dll_for engine/src/Babelstone.Engine.Api Babelstone.Engine.Api)"
[ -n "$RATESHEET_DLL" ] && [ -n "$ENGINE_DLL" ] || die "built engine/rate-sheet DLLs not found under bin/Debug/net*/"
ok "engine + rate-sheet hosts built"

# (b) seed the rate sheet through the C.6 deploy API (the validated seam, not a raw INSERT). The
# transient deploy host is reaped immediately after — the engine reads the rate_sheets table directly.
# We deploy FROM the committed YAML source (bd babelstone-alfy), serialised 1:1 to JSON (ADR-PC-008 §P1).
info "deploying rate sheet ${RATE_SHEET_VERSION} from ${RATE_SHEET_YAML} (prices ${PRODUCT} at 300 bps; the in-tx resolve needs it)"
[ -f "$RATE_SHEET_YAML" ] || die "rate-sheet YAML source not found: $RATE_SHEET_YAML"
saga_deploy() { # base_url
  local url="$1" code
  code="$(ratesheet_post_yaml "$url" demo-saga "$RATE_SHEET_YAML" "$RUNDIR/deploy-resp.json")"
  case "$code" in
    201) ok "rate sheet ${RATE_SHEET_VERSION} deployed (201 Created)" ;;
    200) ok "rate sheet ${RATE_SHEET_VERSION} already present, identical (200 OK)" ;;
    *)   die "rate-sheet deploy expected 201 or 200, got $code  ($(cat "$RUNDIR/deploy-resp.json"))" ;;
  esac
}
with_ratesheet_host "$RATESHEET_DLL" "$ENGINE_CONN" "$RATESHEET_URL" \
  "$RUNDIR/ratesheet-api.log" saga_deploy

# (c) start the engine host on :ENGINE_PORT pointed at the SAME Redpanda + the `babelstone` DB. The
# Kafka bootstrap is set EXPLICITLY (the saga path depends on the outbox relay actually reaching this
# broker — don't rely on the Program.cs default coinciding). The engine applies its family read-model
# migration on boot (needs the babelstone_engine role from step (a)).
say "Starting the engine host on ${ENGINE_URL} (Kafka → the shared Redpanda; outbox relay publishes DepositConstituted)"
start_engine_host "$ENGINE_DLL" "$ENGINE_CONN" "$ENGINE_URL" "$ROOT/packs" \
  "$RUNDIR/engine.pid" "$RUNDIR/engine.log" "localhost:${REDPANDA_KAFKA_PORT}"

# ---------------------------------------------------------------------------
# 3. build + start the orchestrator host (edge + consume loop + dispatcher)
# ---------------------------------------------------------------------------
say "3/7 Building the orchestrator host (first run restores NuGet — be patient)"
mise exec -- dotnet build orchestrator/src/Babelstone.Orchestrator/Babelstone.Orchestrator.csproj --nologo -v q \
  || die "orchestrator build failed"
ORCH_DLL="$(dll_for orchestrator/src/Babelstone.Orchestrator Babelstone.Orchestrator)"
[ -n "$ORCH_DLL" ] || die "built orchestrator DLL not found under bin/Debug/net*/"
ok "built"

say "Starting the orchestrator host on ${ORCH_URL} (it applies its own saga schema on boot)"
# For the demo the bootstrap `babelstone` user serves BOTH the migration (DDL) and runtime roles; the
# least-privilege babelstone_orchestrator runtime role + its envelope are asserted by the orchestrator's
# own tests, not the demo. Engine__BaseUrl points the dispatcher's ActivateDeposit at the engine above.
start_orchestrator_host "$ORCH_DLL" "$ORCH_CONN" "localhost:${REDPANDA_KAFKA_PORT}" \
  "$ACL_URL" "$ENGINE_URL" "$ORCH_URL" "$RUNDIR/orchestrator.pid" "$RUNDIR/orchestrator.log"

# ---------------------------------------------------------------------------
# 4. drive the edge front door → assert 202 + process_id + stream_url
# ---------------------------------------------------------------------------
say "4/7 Opening a deposit through the EDGE (POST /api/v1/deposits/constitute)"
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
# 5. read the SSE stream → wait for terminal COMPLETED → verify the engine recorded the deposit
# ---------------------------------------------------------------------------
say "5/7 Reading the SSE stream + waiting for the saga to walk to terminal COMPLETED"
# The stream is long-lived; the saga self-advances fast as each leg is delivered, then rests at
# APPROVED until the engine's DepositConstituted event arrives over the bus and carries it to
# COMPLETED (the slot-2 advance is on the EVENT, so this last hop crosses Redpanda — allow for it).
curl -sS --max-time 4 "${ORCH_URL}${STREAM}" -H "X-Client-Id: ${DEMO_CLIENT_ID}" \
  > "$RUNDIR/stream.txt" 2>/dev/null || true
grep -q '^event: state' "$RUNDIR/stream.txt" \
  && ok "SSE state frames received" \
  || warn "no SSE state frame captured in the read window (see $RUNDIR/stream.txt)"
FINAL=""
for _ in $(seq 1 30); do
  FINAL="$(saga_state_of "$PROC")"
  [ "$FINAL" = "COMPLETED" ] && break
  sleep 1
done
if [ "$FINAL" = "COMPLETED" ]; then
  ok "saga walked STARTED → PARALLEL_VALIDATION → VALIDATIONS_COMPLETE → APPROVED → COMPLETED"
  info "reserve + limits + approval + the irreversible debit all fired; ActivateDeposit landed a real"
  info "deposit, and the engine's DepositConstituted carried the saga to its terminal success state"
elif [ "$FINAL" = "APPROVED" ]; then
  ALL_GREEN=false
  warn "saga rests at APPROVED — the engine's DepositConstituted did not carry it to COMPLETED."
  warn "Check that the engine is publishing to the shared Redpanda (.demo-saga/engine.log) and that"
  warn "the orchestrator consume loop is reading the term_deposit topic (.demo-saga/orchestrator.log)."
else
  ALL_GREEN=false
  warn "saga at '${FINAL:-?}' (expected COMPLETED — check $RUNDIR/orchestrator.log + $RUNDIR/engine.log)"
fi

# The terminal COMPLETED is, by the slot-2 contract, only reachable on a REAL engine DepositConstituted.
# Confirm the bank actually holds the deposit: GET the engine by the deposit_id (== the saga's internal
# process_id, the ce_subject correlation key). A 200 with an ACTIVE status is "the bank opened it".
SAGA_UUID="$(saga_uuid_of "$PROC")"
if [ -n "$SAGA_UUID" ]; then
  DEP_CODE="$(curl -sS -o "$RUNDIR/engine-deposit.json" -w '%{http_code}' "${ENGINE_URL}/v1/deposits/${SAGA_UUID}" 2>/dev/null || echo 000)"
  if [ "$DEP_CODE" = 200 ]; then
    DSTATUS="$(py -c "import json;print(json.load(open('$RUNDIR/engine-deposit.json')).get('lifecycle','?'))" 2>/dev/null || echo '?')"
    ok "engine holds deposit ${SAGA_UUID} (HTTP 200, lifecycle ${DSTATUS}) — the bank actually opened it"
  else
    ALL_GREEN=false
    warn "engine GET /v1/deposits/${SAGA_UUID} → HTTP ${DEP_CODE} (expected 200 once COMPLETED — check $RUNDIR/engine.log)"
  fi
fi

# ---------------------------------------------------------------------------
# 6. confirm BOTH settlement legs hit the Core-ACL stub (reserve + irreversible debit)
# ---------------------------------------------------------------------------
say "6/7 Confirming BOTH settlement legs hit the Core-ACL stub"
RES="$(acl_count /v1/reservations)"
DEB="$(acl_count /v1/debits)"
if [ "${RES:-0}" -ge 1 ]; then
  ok "ReserveAccountBalance delivered (POST /v1/reservations ×${RES}) — the reversible hold"
else
  ALL_GREEN=false
  warn "no reservation seen at the ACL stub (check $RUNDIR/orchestrator.log)"
fi
if [ "${DEB:-0}" -ge 1 ]; then
  ok "ConfirmDebit delivered (POST /v1/debits ×${DEB}) — the IRREVERSIBLE money leg"
else
  ALL_GREEN=false
  warn "no debit seen at the ACL stub yet (dispatcher may still be draining — check $RUNDIR/orchestrator.log)"
fi

# ---------------------------------------------------------------------------
# 7. demo the refusal branch — fail-closed terminal, no money moved, engine never touched
# ---------------------------------------------------------------------------
say "7/7 Demonstrating the refusal branch (fail-closed terminal)"
# A source account flagged "insufficient" makes the Core-ACL stub 422 the reservation, so the saga
# fails CLOSED before approval — and therefore before ActivateDeposit — so the engine is never asked
# to constitute and no deposit is appended: PreconditionRefused → DEPOSIT_CONSTITUTION_FAILED.
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
  ok "refusal saga ${RPROC} reached terminal DEPOSIT_CONSTITUTION_FAILED (fail-closed — nothing committed, engine never touched)"
else
  ALL_GREEN=false
  warn "refusal saga at '${RFINAL:-?}' (expected DEPOSIT_CONSTITUTION_FAILED — check $RUNDIR/orchestrator.log)"
fi

# ---------------------------------------------------------------------------
# done — the banner reports what was actually observed, not an unconditional success
# ---------------------------------------------------------------------------
if [ "$ALL_GREEN" = true ]; then
  cat <<DONE

$(printf '\033[1;32m✓ Constitution-saga path is up, end to end.\033[0m')

  engine        ${ENGINE_URL}   (command/query + outbox relay → Redpanda; logs: .demo-saga/engine.log)
  orchestrator  ${ORCH_URL}   (edge + consume loop + dispatcher; logs: .demo-saga/orchestrator.log)
  Core-ACL stub ${ACL_URL}    (settlement; WireMock)
  happy-path saga: ${PROC} (→ ${FINAL})   refusal saga: ${RPROC:-—} (→ ${RFINAL:-?})

The happy-path saga walked all the way to COMPLETED: the reversible reserve AND the irreversible debit
both fired against the Core-ACL stub, ActivateDeposit landed a REAL deposit in the engine, and the
engine's DepositConstituted event flowed back over Redpanda to carry the saga to its terminal success
state (the ADR-PC-029 slot-2 advance — on the EVENT, not the HTTP 2xx). The refusal saga fails closed
before approval, so the engine is never touched.

Drive it from Mission Control's LIVE·saga mode:

  python3 docs/demo/mission-control/serve.py     # serves the UI + proxies /api/v1/* + /v1/* here
  open http://localhost:9000                      # flip Mode to LIVE·saga → Constitute deposit

Stop the engine + orchestrator hosts when you're done (infra is left up — use 'make down' for the stack):

  scripts/demo-saga.sh down
DONE
else
  cat <<DONE

$(printf '\033[1;33m! Saga path is up, but NOT every step reached its expected terminal — see the warnings above.\033[0m')

  engine        ${ENGINE_URL}   (logs: .demo-saga/engine.log)
  orchestrator  ${ORCH_URL}   (logs: .demo-saga/orchestrator.log)
  Core-ACL stub ${ACL_URL}    (settlement; WireMock)
  happy-path saga: ${PROC} (→ ${FINAL:-?})   refusal saga: ${RPROC:-—} (→ ${RFINAL:-?})

The hosts are LEFT RUNNING for inspection (the script warns, it does not tear down on a failed
assertion). The most common cause of a happy path stuck at APPROVED is the engine→saga bus hop:
confirm the engine is publishing DepositConstituted to the shared Redpanda (.demo-saga/engine.log)
and the orchestrator consume loop is reading the term_deposit topic (.demo-saga/orchestrator.log).

Stop the engine + orchestrator hosts when you're done (infra is left up — use 'make down' for the stack):

  scripts/demo-saga.sh down
DONE
fi
