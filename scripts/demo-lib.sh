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
# step-up SCA headers — the Kong-less dev bypass for the engine money-mover gate
# ---------------------------------------------------------------------------
# In plain English: maturing/paying-interest on a deposit is irreversible, so the engine refuses it
# (422 SCA_REQUIRED) unless it sees a FRESH, bank-signed strong-authentication proof — the gateway-
# attested X-SCA-Acr / X-SCA-Auth-Time headers (ADR-IC-010 §A8 / bd babelstone-ziu3.5). In production
# Kong mints those from the customer's refreshed step-up token. These minimal Postgres-only demos run
# WITHOUT Kong, so — exactly mirroring the documented supply-your-own-X-Client-Id dev bypass — we mint
# a fresh step-up token with the stub authorization server (infra/stub-as/mint-stepup-token.sh) and
# inject its acr/auth_time as the X-SCA-* headers DIRECTLY onto the curl. The token is now also RFC 8705
# sender-constrained (a cnf.x5t#S256 binding, bd babelstone-26rb); the binding is enforced at Kong, so
# this Kong-less bypass simply sources the same minted proof and presents the freshness claims the
# engine reads. POC-only — the same throwaway-key caveat as every mint-edge-token.sh path.
#
# Echoes the two header arguments (each as a separate token) ready to splice into a curl invocation:
#   eval set -- "$(stepup_sca_headers)"  # not needed; use the array form below
# Prefer the array form in the caller (bash-3.2 safe):
#   STEPUP=(); while IFS= read -r line; do STEPUP+=("$line"); done < <(stepup_sca_headers)
# then pass  "${STEPUP[@]}"  to curl. Each printed line is one curl arg (-H then the header value).
stepup_sca_headers() {
  local token acr auth_time
  token="$(bash "$ROOT/infra/stub-as/mint-stepup-token.sh")" \
    || die "could not mint a step-up SCA token (infra/stub-as/mint-stepup-token.sh)"
  # Decode the token payload and read the AS-signed acr / auth_time — exactly the claims Kong would
  # attest as X-SCA-Acr / X-SCA-Auth-Time. py() resolves the mise-pinned Python.
  acr="$(printf '%s' "$token" | py -c '
import sys, json, base64
seg = sys.stdin.read().strip().split(".")[1]
seg += "=" * (-len(seg) % 4)
print(json.loads(base64.urlsafe_b64decode(seg))["acr"])
')" || die "could not read acr from the minted step-up token"
  auth_time="$(printf '%s' "$token" | py -c '
import sys, json, base64
seg = sys.stdin.read().strip().split(".")[1]
seg += "=" * (-len(seg) % 4)
print(json.loads(base64.urlsafe_b64decode(seg))["auth_time"])
')" || die "could not read auth_time from the minted step-up token"
  # One curl arg per line: -H, header, -H, header (newline-delimited so the caller reads them into an
  # array without word-splitting on the header values).
  printf '%s\n' "-H" "X-SCA-Acr: $acr" "-H" "X-SCA-Auth-Time: $auth_time"
}

# ---------------------------------------------------------------------------
# demo customer conta à ordem — open + seed a starting balance on the engine-owned CA
# ---------------------------------------------------------------------------
# In plain English: the products in these demos need a REAL customer current account to settle against —
# the deposit's principal debit and its maturity credit, a loan's disbursement credit and installment
# debit. This helper stands one up on the engine: it opens a demand account (POST /v1/accounts) and seeds
# a non-zero starting balance (POST /v1/accounts/{id}/credit), then echoes the opened account id so the
# caller can thread it as the funding / settlement account. That id IS the account's opaque account_ref
# (AccountRef == AccountId.ToString(), ADR-PC-033), so a ce_settlementtarget=engine-ca leg naming it as
# account_ref lands on this very account (ADR-PC-043).
#
# The seed credit goes through the SETTLEMENT-facing credit ingress, whose exactly-once key is the
# BODY's economic-intent reference — NOT the HTTP Idempotency-Key (the scoped ADR-PC-029 carve-out,
# ADR-PC-043) — so a demo re-run with the SAME intent reference collapses to one append at command_dedup
# and the balance is seeded exactly once. The seed money-mover carries the step-up SCA headers too
# (stepup_sca_headers): the account money-movers get the same Kong-less dev bypass the deposit maturity
# mover uses, so the whole engine-CA money path is uniform under the demo.
#
# Echoes the opened account id (a UUID) on STDOUT — and ONLY the id: the human-readable progress lines are
# routed to STDERR (info … >&2), so `ACCT_ID="$(open_and_seed_demo_ca …)"` captures the bare id, not the
# chatter. (info()/ok()/say() print to stdout, so a helper meant for `$(…)` capture must send its own
# progress to stderr; die() already goes to stderr.)
#   ACCT_ID="$(open_and_seed_demo_ca "$ENGINE_URL" 200000000 "$RUNDIR")"
open_and_seed_demo_ca() { # engine_url seed_cents rundir [product_code] [currency]
  local url="$1" seed_cents="$2" rundir="$3" product="${4:-ca_pt_standard}" currency="${5:-EUR}"
  local account_id value_date code seed_ref
  local STEPUP=()

  # A fresh account stream id — also this account's opaque account_ref (ADR-PC-033). Lowercased so the
  # engine-CA settlement ingress (which Guid.Parses the account_ref) and any string compare agree.
  account_id="$(uuidgen | tr '[:upper:]' '[:lower:]')"
  value_date="$(py -c 'import datetime; print(datetime.date.today().isoformat())')"

  # (a) open the demand account. The Idempotency-Key is OPTIONAL on open (a new-stream append is a
  # one-shot), but we supply one so a demo re-run of the SAME account_id dedupes to a 200 replay.
  code="$(curl -sS -o "$rundir/ca-open.json" -w '%{http_code}' \
    -X POST "$url/v1/accounts" -H 'Content-Type: application/json' \
    -H "Idempotency-Key: $account_id" \
    -d "{\"account_id\":\"$account_id\",\"product_code\":\"$product\",\"currency\":\"$currency\"}")" \
    || die "could not POST /v1/accounts to open the demo customer CA"
  case "$code" in
    201) info "opened demo customer CA $account_id ($product / $currency)" >&2 ;;
    200) info "demo customer CA $account_id already open ($product / $currency) — replayed" >&2 ;;
    *)   die "open demo CA expected 201 or 200, got $code  ($(cat "$rundir/ca-open.json"))" ;;
  esac

  # (b) seed a non-zero starting balance via the settlement CREDIT ingress. The intent_reference is the
  # exactly-once key (ADR-PC-043 slot 4), NOT the HTTP Idempotency-Key — so a re-run with the SAME
  # reference seeds exactly once. We tie it to the account id so distinct demo accounts get distinct keys.
  # The money-mover carries the step-up SCA headers, uniform with the deposit maturity mover.
  seed_ref="DEMO-CA-SEED-${account_id}"
  while IFS= read -r _hdr; do STEPUP+=("$_hdr"); done < <(stepup_sca_headers)
  code="$(curl -sS -o "$rundir/ca-credit.json" -w '%{http_code}' \
    -X POST "$url/v1/accounts/$account_id/credit" -H 'Content-Type: application/json' \
    "${STEPUP[@]}" \
    -d "{\"amount_cents\":$seed_cents,\"value_date\":\"$value_date\",\"intent_reference\":\"$seed_ref\"}")" \
    || die "could not POST /v1/accounts/$account_id/credit to seed the starting balance"
  [ "$code" = 200 ] || die "seed credit expected 200, got $code  ($(cat "$rundir/ca-credit.json"))"
  info "seeded starting balance ${seed_cents} cents on demo CA $account_id (settlement credit, intent ${seed_ref})" >&2

  printf '%s\n' "$account_id"
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

# The tool checks every launcher shares: docker present + running, mise present, lsof present, and
# npx (Node) for the YAML→JSON rate-sheet deploy bridge (the same pinned-Node path the CI scripts use).
require_demo_tools() {
  command -v docker >/dev/null 2>&1 || die "docker not found on PATH"
  docker info >/dev/null 2>&1 || die "docker is not running — start Docker Desktop and retry"
  command -v mise >/dev/null 2>&1 || die "mise not found — run 'make bootstrap' first"
  command -v lsof >/dev/null 2>&1 || die "lsof not found (needed for the port-clash guard)"
  command -v npx >/dev/null 2>&1 || die "npx (Node.js) not found — needed to serialise the committed rate-sheet YAML to JSON at deploy (brew install node)"
}

# Block until Postgres accepts connections on the `babelstone` DB inside the compose container.
wait_postgres() { # pg_container
  until docker exec "$1" pg_isready -U babelstone -d babelstone >/dev/null 2>&1; do sleep 1; done
}

# Apply the forward-only event-store schema (0001..NNNN) to a database, idempotently across re-runs.
#
# In plain English: the engine doesn't apply its own event-store migrations on boot, so the demo host
# has to. The old applier was naive — it skipped EVERY migration the moment `command_dedup` existed,
# which left newer tables absent on a volume created before a later migration, and the runtime then
# 500'd on the missing table. This applier instead keeps a ledger (a `schema_migrations` row per
# applied file) and applies only the migrations the ledger hasn't recorded — exactly the per-file
# tracking the engine's own MigrationRunner.cs does, so the demo and the engine agree on "applied".
#
# The migrations are NOT individually idempotent (0001 does a bare `CREATE TABLE events`), so we MUST
# never re-run an applied one. The ledger is the source of truth:
#   • table `schema_migrations` (version BIGINT PK, name TEXT, applied_at TIMESTAMPTZ) — same shape as
#     MigrationRunner.LedgerDdl. `version` is the file's leading digits (`0015_…` → 15; long.Parse-style
#     leading-zero strip), so the demo ledger and the engine's runtime ledger are interchangeable.
#   • each pending file is applied inside ONE transaction together with its ledger insert, so a failure
#     leaves the DB at the last fully-applied version (no half-applied phantom).
#
# Legacy volumes (provisioned by the OLD naive applier, which left NO ledger): if `events` exists but
# `schema_migrations` does not, we BACKFILL the ledger from the on-disk artifacts before applying any
# pending file — probing each table/role/column-creating migration's signature. That way an old volume
# is brought current (only the genuinely-missing newer migrations run) instead of re-running 0001 (which
# would fail "relation events already exists") or skipping into a runtime 500.
apply_event_store_schema() { # pg_container db_name migrations_dir
  local c="$1" db="$2" dir="$3" f base version name applied

  # The ledger — same DDL/shape as MigrationRunner.LedgerDdl. CREATE TABLE IF NOT EXISTS is idempotent.
  docker exec -i "$c" psql -U babelstone -d "$db" -v ON_ERROR_STOP=1 -q >/dev/null <<'SQL' \
    || die "could not create the schema_migrations ledger in '$db'"
CREATE TABLE IF NOT EXISTS schema_migrations (
    version    BIGINT      NOT NULL PRIMARY KEY,
    name       TEXT        NOT NULL,
    applied_at TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp()
);
SQL

  # Legacy backfill: a volume from the OLD applier has the tables but no ledger rows. If `events` exists
  # yet the ledger is empty, record the migrations whose on-disk artifact is present so we don't re-run
  # them. Probes are the signature each table/role/column-creating migration leaves behind.
  if _ledger_is_empty "$c" "$db" && _regclass_exists "$c" "$db" public.events; then
    warn "ledger empty but tables present — backfilling schema_migrations from on-disk artifacts (legacy volume)"
    _backfill_ledger "$c" "$db"
  fi

  # Apply every file the ledger hasn't recorded, in version order, each in its own transaction with its
  # ledger insert. \i is NOT used — we pipe the file in so the demo works without bind-mounting the repo.
  for f in "$dir"/0*.sql; do
    base="$(basename "$f")"
    # leading digits → version (strip leading zeros so it matches long.Parse: 0015 → 15)
    version="$(printf '%s' "$base" | sed -E 's/^0*([0-9]+)_.*/\1/')"
    name="$(printf '%s' "$base" | sed -E 's/^[0-9]+_(.*)\.sql$/\1/')"
    if _ledger_has_version "$c" "$db" "$version"; then
      continue
    fi
    info "applying $base (version $version)"
    {
      printf 'BEGIN;\n'
      cat "$f"
      printf "\nINSERT INTO schema_migrations (version, name) VALUES (%s, '%s');\n" "$version" "$name"
      printf 'COMMIT;\n'
    } | docker exec -i "$c" psql -U babelstone -d "$db" -v ON_ERROR_STOP=1 -q \
      || die "migration $base failed (rolled back; DB left at the last applied version)"
    applied="${applied:+$applied }$version"
  done

  if [ -n "${applied:-}" ]; then
    ok "applied event-store migrations to '$db' (versions: $applied)"
  else
    ok "event-store schema already current in '$db' (ledger up to date — nothing to apply)"
  fi
}

# True (rc 0) when the schema_migrations ledger has no rows.
_ledger_is_empty() { # pg_container db_name
  [ "$(docker exec "$1" psql -U babelstone -d "$2" -tAc \
        'SELECT count(*) FROM schema_migrations;' 2>/dev/null | tr -d '[:space:]')" = "0" ]
}

# True when the ledger already records this migration version.
_ledger_has_version() { # pg_container db_name version
  docker exec "$1" psql -U babelstone -d "$2" -tAc \
    "SELECT 1 FROM schema_migrations WHERE version = $3;" 2>/dev/null | grep -q 1
}

# True when a relation (table/index) exists.
_regclass_exists() { # pg_container db_name qualified_name
  docker exec "$1" psql -U babelstone -d "$2" -tAc \
    "SELECT to_regclass('$3') IS NOT NULL;" 2>/dev/null | grep -q t
}

# Record `version`/`name` in the ledger iff the gate command proves the migration's artifact is present.
# ON CONFLICT DO NOTHING keeps backfill idempotent across re-runs.
_backfill_if() { # pg_container db_name version name gate_sql
  if docker exec "$1" psql -U babelstone -d "$2" -tAc "$5" 2>/dev/null | grep -q t; then
    docker exec "$1" psql -U babelstone -d "$2" -v ON_ERROR_STOP=1 -q -c \
      "INSERT INTO schema_migrations (version, name) VALUES ($3, '$4') ON CONFLICT (version) DO NOTHING;" \
      >/dev/null || die "ledger backfill for version $3 failed"
    info "backfilled migration $3 ($4) — artifact present"
  fi
}

# Backfill the ledger from on-disk artifacts. Each gate is the signature a table/role/column-creating
# migration leaves; column-only ALTERs (0010, 0016) gate on the added column, the index-only migration
# (0014) on its index. A migration whose artifact is absent stays unrecorded → the apply loop runs it.
_backfill_ledger() { # pg_container db_name
  local c="$1" db="$2"
  _backfill_if "$c" "$db" 1  events_and_outbox        "SELECT to_regclass('public.events')               IS NOT NULL;"
  _backfill_if "$c" "$db" 2  append_only_role         "SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='babelstone_engine');"
  _backfill_if "$c" "$db" 3  snapshots                "SELECT to_regclass('public.snapshots')            IS NOT NULL;"
  _backfill_if "$c" "$db" 4  rate_sheets              "SELECT to_regclass('public.rate_sheets')          IS NOT NULL;"
  _backfill_if "$c" "$db" 5  projections              "SELECT to_regclass('public.projections')          IS NOT NULL;"
  _backfill_if "$c" "$db" 6  pack_versions            "SELECT to_regclass('public.pack_versions')        IS NOT NULL;"
  _backfill_if "$c" "$db" 10 projection_runtime       "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='projections' AND column_name='projection_kind');"
  _backfill_if "$c" "$db" 11 projection_checkpoints   "SELECT to_regclass('public.projection_checkpoints') IS NOT NULL;"
  _backfill_if "$c" "$db" 12 inbox                    "SELECT to_regclass('public.inbox')                IS NOT NULL;"
  _backfill_if "$c" "$db" 14 bitemporal_read_index    "SELECT to_regclass('public.projections_belief_history_idx') IS NOT NULL;"
  _backfill_if "$c" "$db" 15 command_dedup            "SELECT to_regclass('public.command_dedup')        IS NOT NULL;"
  _backfill_if "$c" "$db" 16 outbox_integration_headers "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='outbox' AND column_name='integration_headers');"
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

# Serialise a committed rate-sheet YAML source to JSON on stdout. The deploy wire format is JSON
# (ADR-PC-008 §P2) but the YAML file is the source of truth (§P1: the stored JSONB body is 1:1 with
# the deployed YAML), so this is the one bridging step. We use js-yaml (pinned via npx) rather than
# the unpinned `yq` the manual how-to suggests — the same pinned-Node path the CI scripts already
# take (scripts/asyncapi-catalog-validate.sh), so the demo bring-up adds no new unpinned dependency.
RATESHEET_JS_YAML="${RATESHEET_JS_YAML:-js-yaml@4.1.0}"
ratesheet_yaml_to_json() { # yaml_file
  command -v npx >/dev/null 2>&1 || die "npx (Node.js) is required to serialise the rate-sheet YAML to JSON (brew install node)"
  npx --yes "$RATESHEET_JS_YAML" "$1" || die "could not serialise rate-sheet YAML '$1' to JSON"
}

# Serialise a committed rate-sheet YAML source to JSON, then POST it; echo the HTTP status. This is
# the YAML-native deploy seam (bd babelstone-alfy): the demo deploys the SAME committed file an author
# edits, so the stored row cannot drift from /rate-sheets. Used inside a deploy_fn exactly like
# ratesheet_post, but reading a .yaml source instead of a pre-serialised .json body.
ratesheet_post_yaml() { # base_url actor yaml_file respfile
  ratesheet_yaml_to_json "$3" | curl -sS -o "$4" -w '%{http_code}' \
    -X POST "$1/v1/rate-sheets" \
    -H 'Content-Type: application/json' -H "X-Deploy-Actor: $2" \
    --data-binary @-
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
  # DEPLOYMENT_ENVIRONMENT is REQUIRED here, not optional: the MCP server's telemetry
  # (telemetry.py resolve_environment) fails fast — refuses to boot — when no deployment
  # environment is set, rather than guess one (ADR-IC-007 §P1). We set it inline here, mirroring
  # the .NET hosts' inline ASPNETCORE_ENVIRONMENT=Development, so the demo boots without the
  # operator having to export it in their shell.
  DEPLOYMENT_ENVIRONMENT=Development \
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
#
# Settlement counterparty routing (ADR-PC-043): the dispatcher picks the settlement counterparty from the
# leg's promoted ce_settlementtarget header ALONE, and only the BASE URL flips. $4 (Settlement__BaseUrl)
# is the LEGACY-DDA home — the default an absent/legacy-dda target routes to (the WireMock Core-ACL stub);
# it stays the fallback so the legacy-DDA demo path is preserved. The OPTIONAL 9th arg
# (Settlement__EngineCaBaseUrl) is the engine-owned CA target: pass the engine's own base URL and a
# ce_settlementtarget=engine-ca leg routes HOME to the engine's authorize/capture/credit ingress. Omit it
# (or pass empty) and the router fails an engine-ca leg closed — never a silent settle on the legacy core.
start_orchestrator_host() { # orch_dll orch_conn kafka_bootstrap acl_url engine_url orch_url pidfile logfile [engine_ca_settlement_url]
  Settlement__EngineCaBaseUrl="${9:-}" \
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
  # DEPLOYMENT_ENVIRONMENT=Development for the same reason as start_mcp_server: any telemetry the
  # agent host wires (now or later) fails fast without a deployment environment (ADR-IC-007 §P1).
  # The agent subpackage does not call configure_tracing today, so this is harmless when unused;
  # we set it anyway for correct env attribution and to keep the two Python launchers symmetric.
  DEPLOYMENT_ENVIRONMENT=Development \
  BABELSTONE_AGENT_MCP_URL="$1" BABELSTONE_MCP_SERVER_URI="$1" \
  AGENT_BIND_HOST=127.0.0.1 AGENT_BIND_PORT="$2" \
    nohup "$ROOT/mcp-server/.venv/bin/python" -m babelstone_mcp.agent > "$4" 2>&1 &
  echo $! > "$3"
  wait_up "http://localhost:$2/" 30 "agent host" "$4"
}
