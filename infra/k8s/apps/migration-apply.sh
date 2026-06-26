#!/bin/sh
# Event-store schema migrator for the staging cluster (bd babelstone-zla1.5.1).
#
# The engine does NOT self-migrate the event store (it only self-applies its family
# read-model on boot, which needs the babelstone_engine role created by migration
# 0002). So this script applies engine/src/Babelstone.EventStore.Migrations/Sql/0*.sql
# to the babelstone DB before the engine boots — mirroring apply_event_store_schema()
# in scripts/demo-lib.sh: a forward-only, ledger-guarded apply where each SQL file and
# its schema_migrations ledger row commit in ONE transaction (ON_ERROR_STOP=1), so a
# failure leaves the DB at the last fully-applied version. The SQL lands here as the
# /sql ConfigMap mount.
#
# Run by BOTH the one-shot migration Job and (idempotently — already-applied files are
# skipped via the ledger) re-checked by the engine pod's init wait. At the end it writes
# a sentinel table the engine init waits on, so engine readiness is decoupled from any
# hardcoded migration-count/version ceiling.
set -eu

: "${PGHOST:=postgres}"
: "${PGPORT:=5432}"
: "${PGDATABASE:=babelstone}"
: "${PGUSER:=babelstone}"
export PGHOST PGPORT PGDATABASE PGUSER PGPASSWORD

run()   { psql -v ON_ERROR_STOP=1 --no-psqlrc "$@"; }
query() { psql -v ON_ERROR_STOP=1 --no-psqlrc -tAc "$1"; }

echo "waiting for postgres at ${PGHOST}:${PGPORT}/${PGDATABASE} ..."
until pg_isready -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$PGDATABASE" >/dev/null 2>&1; do
  sleep 2
done

# Ledger — the SAME DDL the engine's MigrationRunner uses, so the two are interchangeable.
run -c "CREATE TABLE IF NOT EXISTS schema_migrations (
          version    BIGINT      PRIMARY KEY,
          name       TEXT        NOT NULL,
          applied_at TIMESTAMPTZ NOT NULL DEFAULT now());"

for f in /sql/0*.sql; do
  base=$(basename "$f")
  stem=${base%.sql}            # 0014_bitemporal_read_index
  vernum=${stem%%_*}           # 0014
  name=${stem#*_}              # bitemporal_read_index
  ver=$(printf '%s' "$vernum" | sed 's/^0*//'); [ -n "$ver" ] || ver=0   # 14 (no octal traps)

  if [ "$(query "SELECT 1 FROM schema_migrations WHERE version = ${ver}")" = "1" ]; then
    echo "skip   ${base} (version ${ver} already applied)"
    continue
  fi
  echo "apply  ${base} (version ${ver})"
  # File + its ledger insert in ONE transaction (mirrors demo-lib.sh).
  run --single-transaction -f "$f" \
      -c "INSERT INTO schema_migrations (version, name) VALUES (${ver}, '${name}');"
done

# Sentinel the engine init waits on (decouples readiness from any hardcoded ceiling).
run -c "CREATE TABLE IF NOT EXISTS _event_store_apply_complete (
          completed_at TIMESTAMPTZ NOT NULL DEFAULT now());"
run -c "INSERT INTO _event_store_apply_complete DEFAULT VALUES;"
echo "event-store migration complete."
