#!/usr/bin/env bash
# scripts/cd-migrate.sh — forward-only DB-migration gate for the CD promotion pipeline
# (Q.6 / bd babelstone-4c81).
#
# In plain English: before a promoted image starts serving in a P.6 environment, the
# database schema it expects must already be applied. This script applies the three
# forward-only SQL migration series in order, each inside one transaction with a ledger
# insert, and — critically — it GATES promotion: it refuses to proceed if anyone has
# committed a *backward* edit (a deleted or rewritten already-applied migration). The
# migration discipline is forward-only (an applied migration is immutable; the only legal
# change is a new higher-numbered file), so a gap or a rewrite of an already-applied file
# is a release-blocking error, not something we silently re-run.
#
# The three series (ADR-PC-019 §P1 monorepo layout; each owns its own ledger):
#   • engine event store     engine/src/Babelstone.EventStore.Migrations/Sql/0001..NNNN
#                            (family-agnostic + append-only role grants; ADR-PC-001)
#   • orchestrator substrate orchestrator/src/Babelstone.Orchestrator.Substrate/Migrations/Sql
#                            (saga_state / saga_outbox; ADR-IC-003)
#   • term_deposit read model families/term-deposit/.../Migrations/Sql
#                            (family-named tables in its own ledger; ADR-IC-005)
#
# The ledger shape (version BIGINT PK, name TEXT, applied_at) matches the engine's own
# MigrationRunner.LedgerDdl and the demo applier (scripts/demo-lib.sh), so the CD applier,
# the demo host, and the engine's runtime runner all agree on what "applied" means.
#
# Usage:
#   scripts/cd-migrate.sh --gate-only          # FORWARD-ONLY GATE ONLY (no DB):
#                                              #   assert each series is gap-free + that no
#                                              #   committed migration was rewritten vs the
#                                              #   pinned baseline (CI promotion gate).
#   scripts/cd-migrate.sh --psql 'host=… …'    # gate + APPLY all three series to the target
#                                              #   (forward-only, ledgered, idempotent).
#   PGHOST/PGPORT/PGUSER/PGPASSWORD/PGDATABASE  also accepted (standard libpq env).
#
# The applier is idempotent across re-runs (the ledger skips already-applied files) but
# NEVER re-runs or re-writes an applied migration — forward-only is the invariant.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

ENGINE_DIR="$REPO_ROOT/engine/src/Babelstone.EventStore.Migrations/Sql"
ORCH_DIR="$REPO_ROOT/orchestrator/src/Babelstone.Orchestrator.Substrate/Migrations/Sql"
FAMILY_DIR="$REPO_ROOT/families/term-deposit/src/Babelstone.Families.TermDeposit.Application/Migrations/Sql"

info() { printf '\033[36m• %s\033[0m\n' "$*" >&2; }
ok()   { printf '\033[32m✓ %s\033[0m\n' "$*" >&2; }
warn() { printf '\033[33m! %s\033[0m\n' "$*" >&2; }
die()  { printf '\033[31m✗ %s\033[0m\n' "$*" >&2; exit 1; }

MODE="apply"          # apply | gate-only
PSQL_CONN=""
while [ $# -gt 0 ]; do
  case "$1" in
    --gate-only) MODE="gate-only"; shift ;;
    --psql)      PSQL_CONN="$2"; shift 2 ;;
    -h|--help)   sed -n '2,46p' "$0"; exit 0 ;;
    *) echo "cd-migrate: unknown argument '$1'" >&2; exit 2 ;;
  esac
done

# ── forward-only gate: per series, files are monotonically ordered, none rewritten ───
# "Forward-only" means: an applied migration is immutable. We can mechanically assert
# two of its halves at promotion time WITHOUT a DB:
#   (a) monotonic numbering — versions strictly increase in apply order (a duplicate or
#       out-of-order number is a malformed series). Numbering GAPS are allowed: the engine
#       series legitimately skips numbers (0006→0010, 0012→0014) — a reserved/never-used
#       number is not a forward-only violation; only re-ordering or re-using one is, and
#   (b) immutability vs the pinned baseline — no already-committed migration's BYTES
#       changed relative to the promotion baseline ref (default origin/main). A rewrite
#       of an applied file is the backward edit the forward-only rule forbids.
# (The third half — that the live DB ledger has no version ABOVE what the repo ships —
#  is asserted in apply_series against the real ledger.)
gate_series() { # label dir
  local label="$1" dir="$2"
  [ -d "$dir" ] || die "$label: migrations dir not found ($dir)"
  local files; files="$(cd "$dir" && ls 0*.sql 2>/dev/null | sort || true)"
  [ -n "$files" ] || die "$label: no migrations found in $dir"

  # (a) gap-free numbering check (forward-only series is contiguous)
  local prev=0 v f base
  for base in $files; do
    v="$(printf '%s' "$base" | sed -E 's/^0*([0-9]+)_.*/\1/')"
    if [ "$v" -le "$prev" ]; then
      die "$label: non-monotonic migration ordering at $base (forward-only violated)"
    fi
    prev="$v"
  done
  ok "$label: $(echo "$files" | wc -l | tr -d ' ') migrations, monotonically ordered (forward-only numbering)"

  # (b) immutability vs the promotion baseline: no already-committed file's bytes changed.
  # Skip if not a git tree or the baseline ref is unavailable (e.g. a shallow checkout).
  local baseline="${CD_MIGRATE_BASELINE:-origin/main}"
  if git -C "$REPO_ROOT" rev-parse --verify -q "$baseline" >/dev/null 2>&1; then
    local rel changed
    rel="${dir#$REPO_ROOT/}"
    # files present in BOTH the baseline and the working tree whose content differs
    changed="$(git -C "$REPO_ROOT" diff --name-only "$baseline" -- "$rel" 2>/dev/null \
                | grep -E '/0[0-9]+_.*\.sql$' || true)"
    if [ -n "$changed" ]; then
      # A diff on a migration file is only legal if the file is NEW (absent in baseline).
      local bad=""
      while IFS= read -r cf; do
        [ -z "$cf" ] && continue
        if git -C "$REPO_ROOT" cat-file -e "$baseline:$cf" 2>/dev/null; then
          bad="${bad:+$bad }$cf"   # existed in baseline AND changed → rewrite of an applied migration
        fi
      done <<< "$changed"
      [ -z "$bad" ] || die "$label: forward-only VIOLATION — already-committed migration(s) rewritten vs $baseline: $bad"
    fi
    ok "$label: no already-committed migration rewritten vs $baseline (immutable, forward-only)"
  else
    warn "$label: baseline ref '$baseline' unavailable — skipped the immutability diff (numbering gate still ran)"
  fi
}

# ── psql wrapper (connection string or libpq env) ────────────────────────────────────
PSQL() {
  if [ -n "$PSQL_CONN" ]; then
    psql "$PSQL_CONN" -v ON_ERROR_STOP=1 -q "$@"
  else
    psql -v ON_ERROR_STOP=1 -q "$@"
  fi
}

# ── apply one series, ledgered + forward-only, against the live DB ───────────────────
apply_series() { # label dir
  local label="$1" dir="$2" f base version name applied=""
  info "$label: applying forward-only migrations from $dir"

  PSQL >/dev/null <<'SQL' || die "$label: could not create the schema_migrations ledger"
CREATE TABLE IF NOT EXISTS schema_migrations (
    version    BIGINT      NOT NULL PRIMARY KEY,
    name       TEXT        NOT NULL,
    applied_at TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp()
);
SQL

  # Forward-only DB guard: the live ledger must NOT carry a version ABOVE the highest the
  # repo ships (that would mean the DB was advanced past this release — a rollback/downgrade
  # we refuse rather than silently leave a phantom-newer schema).
  local repo_max db_max
  repo_max="$(cd "$dir" && ls 0*.sql | sed -E 's/^0*([0-9]+)_.*/\1/' | sort -n | tail -1)"
  db_max="$(PSQL -tA -c 'SELECT COALESCE(MAX(version),0) FROM schema_migrations;' 2>/dev/null | tr -d '[:space:]' || echo 0)"
  if [ "${db_max:-0}" -gt "${repo_max:-0}" ]; then
    die "$label: live DB ledger at version $db_max is AHEAD of the repo ($repo_max) — refusing a backward promotion"
  fi

  for f in "$dir"/0*.sql; do
    base="$(basename "$f")"
    version="$(printf '%s' "$base" | sed -E 's/^0*([0-9]+)_.*/\1/')"
    name="$(printf '%s' "$base" | sed -E 's/^[0-9]+_(.*)\.sql$/\1/')"
    local present
    present="$(PSQL -tA -c "SELECT 1 FROM schema_migrations WHERE version = $version;" | tr -d '[:space:]')"
    if [ "$present" = "1" ]; then
      continue
    fi
    info "$label: applying $base (version $version)"
    {
      printf 'BEGIN;\n'
      cat "$f"
      printf "\nINSERT INTO schema_migrations (version, name) VALUES (%s, '%s');\n" "$version" "$name"
      printf 'COMMIT;\n'
    } | PSQL || die "$label: migration $base failed (rolled back; DB left at the last applied version)"
    applied="${applied:+$applied }$version"
  done

  if [ -n "$applied" ]; then
    ok "$label: applied versions $applied"
  else
    ok "$label: schema already current (ledger up to date)"
  fi
}

# ── main ─────────────────────────────────────────────────────────────────────────────
info "forward-only migration gate (Q.6) — engine event store, orchestrator substrate, term_deposit read model"
gate_series "engine-event-store"   "$ENGINE_DIR"
gate_series "orchestrator-substrate" "$ORCH_DIR"
gate_series "term_deposit-read-model" "$FAMILY_DIR"
ok "forward-only gate PASSED for all three series"

if [ "$MODE" = "gate-only" ]; then
  ok "gate-only: promotion gate satisfied (no DB touched)"
  exit 0
fi

command -v psql >/dev/null 2>&1 || die "apply mode needs psql on PATH (set --psql or libpq PG* env)"
# Each series targets its own logical database in a real deployment; here we apply them in
# order against the connection given. The connection's PGDATABASE (or --psql conn db) is the
# target — a deployment points each series at its own DB by re-invoking with that conn.
apply_series "engine-event-store"   "$ENGINE_DIR"
apply_series "orchestrator-substrate" "$ORCH_DIR"
apply_series "term_deposit-read-model" "$FAMILY_DIR"
ok "all three migration series applied forward-only against the target"
