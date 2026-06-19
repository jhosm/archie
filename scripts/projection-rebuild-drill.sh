#!/usr/bin/env bash
# scripts/projection-rebuild-drill.sh — the monthly projection-rebuild drill
# (L.4 / bd babelstone-j67l). Run on a monthly cron by
# .github/workflows/projection-rebuild-drill.yml, on demand via that workflow's
# workflow_dispatch, and locally via `make projection-rebuild-drill`.
#
# In plain English: once a month we deliberately throw away the engine's derived
# views and rebuild them from scratch out of the raw event log, then check the
# rebuilt views match what was running. A match PROVES — not assumes — that the
# event log really is the single source of truth and that no slow, quiet bug has
# crept into how the views are computed. A mismatch caught one before it reached
# a customer or a regulator. This script is the drill; the runbook
# (infra/runbooks/projection-rebuild-drill.md) is how an operator reads its
# result and responds to a failure.
#
# What it does, formally: it drives the existing event-store §7.2 full-rebuild
# drill path — ProjectionReconciler.FullRebuildDrillAsync (supersede-all +
# checkpoint reset + cold re-fold from sequence 0, then a byte-for-byte
# before/after compare) — by running the reconciler's Testcontainers-backed
# integration tests in Babelstone.EventStore.Tests. Those tests spin a real
# PostgreSQL, seed a stream, drain a projection, then call FullRebuildDrillAsync
# and assert (a) a clean rebuild reproduces the running belief byte-for-byte
# (RebuildReconciliation.Identical) and (b) the rebuild REPAIRS a drifted belief
# back to the cold-fold hash. The drill WRAPS and INVOKES that path; it does not
# change the reconciler or the EventStore signatures (the engine core is owned by
# another lane). A clean run is also snapshot-correctness evidence (§8.3): the
# rebuild re-folds cold, so matching the running state proves the snapshot
# acceleration is faithful.
#
# Exit code: 0 = drill PASSED (the §7.2 invariant held this cycle); non-zero =
# a divergence or an infra failure — the process incident the runbook §4 covers.
#
# Requirements: Docker (Testcontainers brings up PostgreSQL — no `make up`
# needed) and the mise-pinned .NET SDK. The .NET invocation is prefixed with
# `mise exec --` so it builds against the same SDK as CI and every dev machine.

set -euo pipefail

# ---------------------------------------------------------------------------
# pretty output (same palette as scripts/demo-lib.sh; this script is standalone
# so it does not source that file — it is a single-purpose drill, not a demo
# launcher).
# ---------------------------------------------------------------------------
say()  { printf '\n\033[1;36m▶ %s\033[0m\n' "$*"; }
ok()   { printf '  \033[32m✓ %s\033[0m\n' "$*"; }
info() { printf '  \033[2m%s\033[0m\n' "$*"; }
warn() { printf '  \033[1;33m! %s\033[0m\n' "$*"; }
die()  { printf '\n\033[1;31m✗ %s\033[0m\n' "$*" >&2; exit 1; }

# Resolve the repo root from this script's location, so the drill runs the same
# whether invoked via `make`, the workflow, or directly from any cwd.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

# The reconciler's full-rebuild-drill path lives in this test project, tagged
# Category=Integration (real PostgreSQL via Testcontainers).
TEST_PROJECT="engine/tests/Babelstone.EventStore.Tests/Babelstone.EventStore.Tests.csproj"

# Narrow the run to the drill cases that invoke FullRebuildDrillAsync, AND keep
# the Integration trait so the Testcontainers PostgreSQL is actually used (the
# unit lane excludes it). The filter is an AND of the two traits/name match.
TEST_FILTER='FullyQualifiedName~ProjectionReconcilerIntegrationTests&FullyQualifiedName~FullRebuildDrill&Category=Integration'

say "Projection-rebuild drill (event-store §7.2 / bd babelstone-j67l)"
info "repo root: ${ROOT}"
info "drill path: ProjectionReconciler.FullRebuildDrillAsync (wrapped via the Integration tests)"

# Fail fast and legibly if Docker is unavailable — Testcontainers needs it, and
# a cryptic mid-test container error is a worse operator experience than this.
if ! docker info >/dev/null 2>&1; then
  die "Docker is not available — the drill needs Testcontainers (a real PostgreSQL). Start Docker and retry. (runbook §2)"
fi
ok "Docker reachable"

# mise must be present (pinned .NET). In a fresh worktree run `mise trust --yes`
# first; the workflow uses jdx/mise-action to provide it.
if ! command -v mise >/dev/null 2>&1; then
  die "mise not found — the pinned .NET SDK is resolved via mise (see mise.toml / CLAUDE.md)."
fi

say "Running the §7.2 full-rebuild drill cases (Testcontainers PostgreSQL)"
info "filter: ${TEST_FILTER}"

# Run from the repo root so the relative project path resolves regardless of cwd.
cd "${ROOT}"

# `mise exec --` resolves the pinned dotnet; the test run brings up PostgreSQL
# via Testcontainers and exercises FullRebuildDrillAsync end-to-end. We do NOT
# pass --no-build: a clean checkout (CI, a fresh worktree) needs the build.
if mise exec -- dotnet test "${TEST_PROJECT}" \
      --configuration Release \
      --nologo \
      --filter "${TEST_FILTER}"; then
  ok "Drill PASSED — cold rebuild reproduced the running projection byte-for-byte (§7.2 invariant held)"
  ok "Snapshot correctness (§8.3): the cold re-fold matched, so the snapshot acceleration is faithful"
  say "DRILL RESULT: PASS"
  info "Record the run as resilience-testing evidence (runbook §5)."
  info "To wire the freshness alert, push reconciliation_drill_last_success_timestamp_seconds=\$(date +%s) (runbook §4b)."
  exit 0
else
  status=$?
  warn "One or more FullRebuildDrill_* cases FAILED — a §7.2 divergence."
  warn "The cold re-fold did NOT reproduce the running projection. This is the slow-drift"
  warn "bug class the drill exists to catch (accumulated rounding / state-dependent handler logic)."
  say "DRILL RESULT: FAIL (divergence)"
  die "Investigate per the runbook §4a: capture the divergent kind + before/after hashes, hand to the family/engine owner, do NOT close until a re-run drill is clean. A divergence is a process incident (ADR-PC-005 §P5). (dotnet test exit ${status})"
fi
