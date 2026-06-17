#!/usr/bin/env bash
#
# demo-lib.sh — shared helpers for the Mission Control demo launchers.
#
# In plain English: the four demo scripts (demo-mcp, demo-saga, demo-agent, demo-all) used to each
# carry their own copy of the same bash — the pretty-printers, the "wait until a port answers" loop,
# the migration applier, the engine launch. That drift is what let the two scripts disagree on the
# migration guard (one had a bug the other had already fixed). This file is the single home for those
# shared steps; every launcher SOURCES it and then just wires its own config + the mode-specific bits.
#
# It is SOURCED, never executed directly. The sourcing script owns `set -euo pipefail` and exports its
# config as globals (RUNDIR, ROOT, …); the functions here read documented args (and a few well-known
# globals like $ROOT). Targets macOS system bash 3.2 — no associative arrays, no ${var,,}, no mapfile.

# ---------------------------------------------------------------------------
# pretty output (one set, shared by every launcher)
# ---------------------------------------------------------------------------
say()  { printf '\n\033[1;36m▶ %s\033[0m\n' "$*"; }
ok()   { printf '  \033[32m✓ %s\033[0m\n' "$*"; }
info() { printf '  \033[2m%s\033[0m\n' "$*"; }
warn() { printf '  \033[1;33m! %s\033[0m\n' "$*"; }
die()  { printf '\n\033[1;31m✗ %s\033[0m\n' "$*" >&2; exit 1; }

# Pinned interpreter, for JSON assertions (resolves the mise-pinned Python, not the system one).
py() { mise exec -- python "$@"; }

# Read a field out of a saved JSON response and assert it equals an expected value.
assert_json() { # file field expected
  local got
  got="$(py -c "import json;print(json.load(open('$1')).get('$2'))")" \
    || die "could not parse $2 from $1"
  [ "$got" = "$3" ] || die "expected $2=$3 but got '$got'  (see $1)"
  ok "$2 = $got"
}

# ---------------------------------------------------------------------------
# probes & process lifecycle
# ---------------------------------------------------------------------------

# Wait until an HTTP endpoint answers at all (any status != 000 means the port is live). A POST-only
# host answering a GET with 404/405 still counts — it proves the listener is up, which is the point.
wait_up() { # url timeout_seconds name [logfile]
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

# ---------------------------------------------------------------------------
# preflight & infra
# ---------------------------------------------------------------------------

# The tool checks every launcher shares: docker present + running, mise present, lsof present.
require_demo_tools() {
  command -v docker >/dev/null 2>&1 || die "docker not found on PATH"
  docker info >/dev/null 2>&1 || die "docker is not running — start Docker Desktop and retry"
  command -v mise >/dev/null 2>&1 || die "mise not found — run 'make bootstrap' first"
  command -v lsof >/dev/null 2>&1 || die "lsof not found (needed for the port-clash guard)"
}

# Block until Postgres accepts connections on the `babelstone` DB inside the compose container.
wait_postgres() { # pg_container
  until docker exec "$1" pg_isready -U babelstone -d babelstone >/dev/null 2>&1; do sleep 1; done
}

# Apply the forward-only event-store schema (0001..NNNN) to a database, idempotently across re-runs.
#
# The migrations are NOT individually idempotent (0001 does a bare `CREATE TABLE events`), and there
# is no migration ledger, so we guard on the last TABLE-creating migration's artifact (`command_dedup`,
# 0015; the trailing 0016 only adds a column via an idempotent ALTER, so a volume the full loop has
# migrated always has command_dedup):
#   • command_dedup present            → schema fully applied, skip (re-running 0001 would error).
#   • neither events nor command_dedup → clean DB, apply 0001..NNNN in order.
#   • events present but not command_dedup → a PARTIALLY/older-migrated volume. Re-running 0001 would
#     fail loudly ("relation events already exists") and silently skipping would 500 at runtime, so
#     we stop with an actionable message instead of doing either. (This is the unified, honest guard;
#     demo-mcp used to skip-into-a-500 here, demo-saga used to die confusingly on the re-run.)
apply_event_store_schema() { # pg_container db_name migrations_dir
  local c="$1" db="$2" dir="$3" f
  if docker exec "$c" psql -U babelstone -d "$db" -tAc \
       "SELECT to_regclass('public.command_dedup') IS NOT NULL;" 2>/dev/null | grep -q t; then
    ok "event-store schema already present in '$db' (command_dedup exists) — skipping migrations"
    return 0
  fi
  if docker exec "$c" psql -U babelstone -d "$db" -tAc \
       "SELECT to_regclass('public.events') IS NOT NULL;" 2>/dev/null | grep -q t; then
    die "database '$db' is partially migrated (has 'events' but not 'command_dedup'). The forward-only
   migrations aren't re-runnable, so wipe the volume and let them apply fresh, then re-run this demo:
       docker compose -f infra/compose.yaml down -v"
  fi
  for f in "$dir"/0*.sql; do
    info "applying $(basename "$f")"
    docker exec -i "$c" psql -U babelstone -d "$db" -v ON_ERROR_STOP=1 -q < "$f" \
      || die "migration $(basename "$f") failed"
  done
  ok "applied event-store schema to '$db' (events, outbox, snapshots, rate_sheets, command_dedup, + babelstone_engine role)"
}

# ---------------------------------------------------------------------------
# rate sheet (the C.6 validated deploy seam, ADR-PC-008 §P2 — not a raw INSERT)
# ---------------------------------------------------------------------------

# Bring up the transient RateSheets.Api, run a caller-supplied deploy function against it, then reap
# the host (even on a failed assertion — the EXIT trap guarantees it). The deploy host is throwaway:
# the engine reads the rate_sheets table directly afterward.
#
# The deploy_fn is a shell function name; it receives the deploy base URL as $1 and does whatever
# POST + status assertions that launcher needs (one product vs three, with/without the 409 check).
with_ratesheet_host() { # ratesheet_dll connstring base_url logfile deploy_fn
  local dll="$1" conn="$2" url="$3" log="$4" fn="$5" pid
  ConnectionStrings__RateSheets="$conn" ASPNETCORE_URLS="$url" ASPNETCORE_ENVIRONMENT=Development \
    mise exec -- dotnet "$dll" > "$log" 2>&1 &
  pid=$!
  trap "kill $pid 2>/dev/null || true" EXIT
  wait_up "${url}/" 60 "RateSheets.Api" "$log"
  "$fn" "$url"
  kill "$pid" 2>/dev/null || true
  trap - EXIT
  ok "stopped the transient deploy host (the engine reads rate_sheets directly)"
}

# POST a rate-sheet body and echo the HTTP status. Used inside a deploy_fn.
ratesheet_post() { # base_url actor bodyfile respfile
  curl -sS -o "$4" -w '%{http_code}' \
    -X POST "$1/v1/rate-sheets" \
    -H 'Content-Type: application/json' -H "X-Deploy-Actor: $2" \
    --data-binary @"$3"
}

# ---------------------------------------------------------------------------
# long-lived hosts (engine / MCP) — nohup so they outlive this script's shell
# ---------------------------------------------------------------------------

# Start the engine command/query host on its port. Kafka is OPTIONAL: pass a bootstrap (saga/all) to
# wire the outbox relay to a broker so DepositConstituted is published; omit it (mcp) for the
# Postgres-only walking skeleton. The probe hits an unknown deposit id — a 404 proves the surface is
# live without needing a real deposit.
start_engine_host() { # engine_dll connstring engine_url packs_dir pidfile logfile [kafka_bootstrap] [timeout]
  local dll="$1" conn="$2" url="$3" packs="$4" pidfile="$5" log="$6" kafka="${7:-}" timeout="${8:-90}"
  if [ -n "$kafka" ]; then
    ConnectionStrings__Engine="$conn" Engine__PacksDir="$packs" Engine__PackVersion=pt.2026.1 \
      Kafka__BootstrapServers="$kafka" \
      ASPNETCORE_URLS="$url" ASPNETCORE_ENVIRONMENT=Development \
      nohup mise exec -- dotnet "$dll" > "$log" 2>&1 &
  else
    ConnectionStrings__Engine="$conn" Engine__PacksDir="$packs" Engine__PackVersion=pt.2026.1 \
      ASPNETCORE_URLS="$url" ASPNETCORE_ENVIRONMENT=Development \
      nohup mise exec -- dotnet "$dll" > "$log" 2>&1 &
  fi
  echo $! > "$pidfile"
  wait_up "${url}/v1/deposits/00000000-0000-0000-0000-000000000000" "$timeout" "engine host" "$log"
}

# Create (once) and populate the MCP server's venv with the requested extras (e.g. "dev" or "agent").
setup_mcp_venv() { # extras
  if [ ! -d mcp-server/.venv ]; then
    (cd mcp-server && mise exec -- python -m venv .venv) || die "venv creation failed"
  fi
  (cd mcp-server && "$ROOT/mcp-server/.venv/bin/python" -m pip install -q -e ".[$1]") \
    || die "pip install '.[$1]' failed"
}

# Start the Python MCP server (Streamable HTTP) in front of the engine.
#
# The server (babelstone_mcp/__main__.py) reads MCP_BIND_HOST/MCP_BIND_PORT and DEFAULTS the port to
# 8080 — the in-container port Kong dials. For the host-process demo we MUST pin it to the demo's MCP
# port (8000), both so the readiness probe + the agent host's BABELSTONE_AGENT_MCP_URL find it and so
# it doesn't collide with the engine on :8080. (We leave MCP_BIND_HOST at its 0.0.0.0 default.)
start_mcp_server() { # engine_url mcp_port pidfile logfile mcp_url
  BABELSTONE_ENGINE_URL="$1" MCP_BIND_PORT="$2" \
    nohup "$ROOT/mcp-server/.venv/bin/python" -m babelstone_mcp > "$4" 2>&1 &
  echo $! > "$3"
  wait_up "$5" 30 "MCP server" "$4"
}

# ---------------------------------------------------------------------------
# orchestrator (saga path) — used by demo-saga.sh and demo-all.sh
# ---------------------------------------------------------------------------

# Create the orchestrator's dedicated application DB (ADR-IC-003 §S2) if absent. CREATE DATABASE can't
# run in a transaction and errors if it exists, so guard on pg_database — idempotent across re-runs.
# The saga SCHEMA itself is applied by the orchestrator host on boot (SagaMigrationHostedService).
create_orchestrator_db() { # pg_container orch_db
  if docker exec "$1" psql -U babelstone -d babelstone -tAc \
       "SELECT 1 FROM pg_database WHERE datname='$2'" 2>/dev/null | grep -q 1; then
    ok "orchestrator database '$2' already present"
  else
    docker exec "$1" psql -U babelstone -d babelstone -c "CREATE DATABASE $2" >/dev/null \
      || die "could not create orchestrator database '$2'"
    ok "created orchestrator database '$2' (the saga schema is applied by the host on boot)"
  fi
}

# Start the orchestrator host (edge + consume loop + dispatcher). Connection strings resolve at the
# composition root (ADR-PC-004 Amendment A1); the Kafka/Engine/Settlement targets are ENDPOINTS, not
# credentials. The probe hits an unknown process id — a 404 proves the HTTP surface is live.
start_orchestrator_host() { # orch_dll orch_conn kafka_bootstrap acl_url engine_url orch_url pidfile logfile
  ConnectionStrings__OrchestratorMigration="$2" \
    ConnectionStrings__Orchestrator="$2" \
    Kafka__BootstrapServers="$3" \
    Settlement__BaseUrl="$4" \
    Engine__BaseUrl="$5" \
    ASPNETCORE_URLS="$6" ASPNETCORE_ENVIRONMENT=Development \
    nohup mise exec -- dotnet "$1" > "$8" 2>&1 &
  echo $! > "$7"
  wait_up "${6}/api/v1/processes/PROC-UNKNOWN/stream" 60 "orchestrator host" "$8"
}

# ---------------------------------------------------------------------------
# real-Claude agent host — used by demo-agent.sh and demo-all.sh
# ---------------------------------------------------------------------------

# Start the real-Claude agent host. It holds its OWN identity + Anthropic key and connects to the MCP
# server itself (ADR-IC-010 §P3/§P4); the caller must ensure the `agent` extra is installed and that
# ANTHROPIC_API_KEY is exported. The probe GETs the POST-only host (404 → live). mcp_url is the same
# value for both the tool-call URL and the audience/server-uri.
start_agent_host() { # mcp_url agent_port pidfile logfile
  BABELSTONE_AGENT_MCP_URL="$1" BABELSTONE_MCP_SERVER_URI="$1" \
  AGENT_BIND_HOST=127.0.0.1 AGENT_BIND_PORT="$2" \
    nohup "$ROOT/mcp-server/.venv/bin/python" -m babelstone_mcp.agent > "$4" 2>&1 &
  echo $! > "$3"
  wait_up "http://localhost:$2/" 30 "agent host" "$4"
}
