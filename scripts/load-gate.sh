#!/usr/bin/env bash
# scripts/load-gate.sh — the composite v1 RC load-acceptance gate
# (L.3f / bd babelstone-2e6q.6; ADR-PC-011 Open Action #4).
#
# In plain English: this is the single green-or-red verdict for a release
# candidate. It runs the load-test host over each acceptance dimension in turn —
# the §8.3 sync-latency bands, sustained and burst throughput, the §8.2 cold
# replay budget + the no-rebuild-divergence invariant, the L.5 snapshot-
# accelerated replay parity, the L.6 discard-rebuild drill on populated
# snapshots, and the ADR-PC-005 §P1 synchronous-replication append-latency cost —
# and exits 0 only if EVERY dimension that ran passed. Any red dimension makes
# the whole gate red, so the RC pipeline can block on this one command. The
# runner's own exit code IS each dimension's verdict (0 = PASS), and this script
# ANDs them into one.
#
# What it does, formally: it composes the accreted RunArtefact verdicts that the
# load-test host (engine/load/Babelstone.LoadHarness.Runner) folds — latency,
# sustained/burst throughput, replay budget, no-divergence, snapshot-replay
# parity, discard-rebuild, and the §P1 repl-latency delta — into the single
# binary 2e6q acceptance gate. It does NOT re-implement any verdict; each `dotnet
# run … --measure <mode>` returns the artefact's Passed as its process exit code,
# and the gate is the logical AND.
#
# Cadence: the RC pipeline runs this every release candidate
# (.github/workflows/load-gate.yml). Locally: `make load-gate`.
#
# Scale note: the dimensions run at a SHORT, fast profile by default (a CI-time
# smoke of each band) so the gate is runnable on every RC without a 24h soak.
# The full §8.3 soak (250 TPS / 24h sustained, 1000 TPS / 15 min burst) is the
# same host with bigger --tps/--duration (LOAD_GATE_SUSTAINED_ARGS /
# LOAD_GATE_BURST_ARGS), reserved for the periodic full pass, not the per-RC gate
# (ADR-PC-011 §8.4 cadence). The §P1 repl-latency dimension is ADVISORY here
# (single-node stack, no named standby); against the HA overlay it gates — pass
# LOAD_GATE_REPL_ARGS="--standby-confirmed" with the overlay's write endpoint.
#
# Exit code: 0 = the whole gate PASSED (every dimension green); non-zero = at
# least one dimension is red — the RC is blocked.
#
# Requirements: the live dev stack up (`make up`) with the event-store migrations
# applied to the `babelstone` DB (this script applies them via demo-lib.sh, the
# same way the demos do — CLAUDE.md gotcha), and the mise-pinned .NET SDK.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

# Reuse the demo library's pretty-output + migration applier (the single home for
# the event-store-schema apply, so the gate and the demos never drift on it).
# shellcheck source=scripts/demo-lib.sh
source "${SCRIPT_DIR}/demo-lib.sh"

# ---------------------------------------------------------------------------
# Config (overridable so the same gate drives the per-RC smoke and the periodic
# full soak without code change — ADR-PC-011 §8.1 "size is config, not code").
# ---------------------------------------------------------------------------
PG_CONN="${LOAD_GATE_PG:-Host=localhost;Port=5432;Database=babelstone;Username=babelstone;Password=babelstone}"
PG_CONTAINER="${LOAD_GATE_PG_CONTAINER:-babelstone-postgres}"
PG_DB="${LOAD_GATE_PG_DB:-babelstone}"
MIGRATIONS_DIR="${ROOT}/engine/src/Babelstone.EventStore.Migrations/Sql"
RUNNER_PROJECT="engine/load/Babelstone.LoadHarness.Runner"

# Per-dimension argument overrides (the per-RC defaults are a fast smoke of each
# band; the full §8.3 soak substitutes bigger numbers via these).
LOAD_GATE_LATENCY_ARGS="${LOAD_GATE_LATENCY_ARGS:---profile smoke --tps 50 --duration 5s --no-bus}"
LOAD_GATE_SUSTAINED_ARGS="${LOAD_GATE_SUSTAINED_ARGS:---profile sustained --tps 100 --duration 5s --no-bus}"
LOAD_GATE_BURST_ARGS="${LOAD_GATE_BURST_ARGS:---profile burst --tps 100 --burst-tps 300 --duration 3s --burst-duration 3s --no-bus}"
LOAD_GATE_REPLAY_ARGS="${LOAD_GATE_REPLAY_ARGS:---measure replay --tps 50 --duration 3s --no-bus}"
LOAD_GATE_SNAPSHOT_ARGS="${LOAD_GATE_SNAPSHOT_ARGS:---measure snapshot-replay --tps 50 --duration 3s --depth 64 --no-bus}"
LOAD_GATE_DISCARD_ARGS="${LOAD_GATE_DISCARD_ARGS:---measure discard-rebuild --tps 50 --duration 3s --depth 64 --no-bus}"
# §P1 repl-latency: ADVISORY by default (no --standby-confirmed). Against the HA
# overlay pass LOAD_GATE_REPL_ARGS="… --standby-confirmed --pg <overlay-write-endpoint>".
LOAD_GATE_REPL_ARGS="${LOAD_GATE_REPL_ARGS:---measure repl-latency --tps 50 --duration 3s --repl-samples 20 --no-bus}"

say "Composite load-acceptance gate (L.3f / bd babelstone-2e6q.6; ADR-PC-011 Open Action #4)"
info "repo root: ${ROOT}"

# Fail fast if the stack/DB is unreachable.
if ! command -v mise >/dev/null 2>&1; then
  die "mise not found — the pinned .NET SDK is resolved via mise (see mise.toml / CLAUDE.md)."
fi

# Apply the event-store migrations (the engine does not on boot — CLAUDE.md
# gotcha). Skipped if the Postgres container is not the demo container (e.g. a
# k8s/HA target supplies its own migrated DB) — then the operator must have
# applied them; the runner fails loud on a missing table if not.
if docker ps --format '{{.Names}}' 2>/dev/null | grep -qx "${PG_CONTAINER}"; then
  say "Applying event-store migrations to '${PG_DB}' (idempotent)"
  apply_event_store_schema "${PG_CONTAINER}" "${PG_DB}" "${MIGRATIONS_DIR}"
else
  warn "Postgres container '${PG_CONTAINER}' not found — assuming the target DB is already migrated."
fi

# ---------------------------------------------------------------------------
# Run each dimension; the runner's exit code is the dimension's verdict. We DO
# NOT set -e around the runs (a red dimension must be recorded, not abort the
# gate before the others run) — every dimension runs, then the gate is the AND.
# ---------------------------------------------------------------------------
FAILED=""
PASSED=""

run_dimension() { # label  args…
  local label="$1"; shift
  say "Dimension: ${label}"
  info "args: $*"
  if mise exec -- dotnet run --project "${RUNNER_PROJECT}" -c Release -- \
        --pg "${PG_CONN}" "$@"; then
    ok "${label}: PASS"
    PASSED="${PASSED:+${PASSED} }${label}"
  else
    warn "${label}: FAIL (runner exit non-zero)"
    FAILED="${FAILED:+${FAILED} }${label}"
  fi
}

cd "${ROOT}"

# shellcheck disable=SC2086  # word-splitting the *_ARGS strings into flags is intended.
run_dimension "latency (§8.3 sync bands)"          ${LOAD_GATE_LATENCY_ARGS}
# shellcheck disable=SC2086
run_dimension "sustained throughput (§8.3)"        ${LOAD_GATE_SUSTAINED_ARGS}
# shellcheck disable=SC2086
run_dimension "burst throughput (§8.3)"            ${LOAD_GATE_BURST_ARGS}
# shellcheck disable=SC2086
run_dimension "replay budget + no-divergence (§8.2/§8.3)" ${LOAD_GATE_REPLAY_ARGS}
# shellcheck disable=SC2086
run_dimension "snapshot-accelerated replay (L.5/§P3)"     ${LOAD_GATE_SNAPSHOT_ARGS}
# shellcheck disable=SC2086
run_dimension "discard-rebuild drill (L.6/§8.3)"   ${LOAD_GATE_DISCARD_ARGS}
# shellcheck disable=SC2086
run_dimension "sync-replication append cost (§P1)" ${LOAD_GATE_REPL_ARGS}

# ---------------------------------------------------------------------------
# The single binary verdict: PASS iff no dimension failed.
# ---------------------------------------------------------------------------
echo
say "Composite gate result"
info "passed: ${PASSED:-none}"
if [ -n "${FAILED}" ]; then
  warn "failed: ${FAILED}"
  say "LOAD GATE: FAIL"
  die "At least one acceptance dimension is red — the v1 RC is blocked (bd babelstone-2e6q)."
fi

ok "Every acceptance dimension passed."
say "LOAD GATE: PASS"
echo "The v1 RC clears the composite load-acceptance gate (bd babelstone-2e6q)."
