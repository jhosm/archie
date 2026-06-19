# Projection-rebuild drill — ops runbook (L.4, bd `babelstone-j67l`)

**In plain English.** Once a month we deliberately throw away the engine's
derived views and rebuild them from scratch out of the raw event log, then check
the rebuilt views match what was running. If they match, we have *proved* — not
assumed — that the event log really is the single source of truth and that no
slow, quiet bug has crept into how the views are computed. If they don't match,
we caught a bug before it reached a customer or a regulator. This runbook
describes the automated drill (a script + a scheduled CI job), how to read its
result, and what to do when it fails.

The formal frame: this automates the
[event-store §7.2](../../docs/product-management/product_concepts/feature-design-event-store-projections.md)
**projection-rebuild drill** — the periodic full rebuild that catches the
slow-drift bugs the daily checksum (§7.1 pattern (a)) and event-count
reconciliation (§7.1 pattern (b)) miss. It drives the existing
[`ProjectionReconciler.FullRebuildDrillAsync`](../../engine/src/Babelstone.Engine/ProjectionReconciler.cs)
path (supersede-all + checkpoint reset + cold re-fold from sequence 0, then a
byte-for-byte before/after compare). It also exercises **snapshot correctness**
(§8.3): the rebuild discards all snapshots and re-folds cold, so a clean drill is
also evidence the snapshot acceleration is faithful.

> **Scope.** This drill is the projection half of resilience testing. The DR
> half — restore-from-backup, failover, key-store recovery — is the companion
> [DR recovery drill runbook](./dr-recovery-drill.md). That runbook's monthly
> failover rehearsal explicitly *rides this drill's monthly cadence* (§1 there),
> so the two are run together.

---

## 1 · What the drill proves (event-store §7.2 / §8.3)

| Claim | How the drill proves it |
|---|---|
| **The event log is the source of truth** | A cold re-fold from the log alone reproduces the running projection byte-for-byte (`RebuildReconciliation.Identical`). |
| **Handlers are pure, replayable folds** | A divergence reveals state-dependent or non-deterministic handler logic — exactly the slow-drift class (§7.1) the cheap checks miss. |
| **Snapshots are correct (§8.3)** | The rebuild discards snapshots and re-folds cold; matching the snapshot-accelerated state proves the snapshot infrastructure is faithful, not assumed. |
| **The cold-replay budget holds** | The drill records wall-clock; the rebuild stays within the published replay budget (gated independently by the `REPLAY_BUDGET_5S_30S` fitness function — the drill exercises it end-to-end, it does not re-assert it). |

A divergence is an **engineering bug, not a budget overrun** (§8.2 framing) — it
is raised, never buried.

---

## 2 · How the automation works

Two pieces, both in this lane's footprint:

- **`scripts/projection-rebuild-drill.sh`** — the drill itself. It invokes the
  existing `FullRebuildDrillAsync` path by running the reconciler's
  Testcontainers-backed integration tests
  (`Babelstone.EventStore.Tests`, the `FullRebuildDrill_*` cases under
  `Category=Integration`). Those tests spin a real PostgreSQL, seed a stream,
  drain a projection, then call `FullRebuildDrillAsync` and assert the cold
  rebuild reproduces the running belief (`Identical`) and repairs a drifted one.
  The drill **wraps and invokes** that path — it does not change the reconciler
  or the EventStore signatures (the engine core is owned elsewhere). A green run
  means the §7.2 invariant held; a red run is a divergence to investigate.
- **`.github/workflows/projection-rebuild-drill.yml`** — runs the script on a
  **monthly cron** (and on `workflow_dispatch` for an on-demand drill). The
  `ubuntu-latest` runner ships Docker, so the Testcontainers PostgreSQL comes up
  with no `make up`. The job's outcome is the drill record; a red job is the
  process incident.

Run it locally:

```bash
make projection-rebuild-drill
# or directly:
scripts/projection-rebuild-drill.sh
```

The script targets the mise-pinned .NET (`mise exec -- dotnet …`) so it builds
against the same SDK as CI, and it prints a clear PASS/FAIL banner with the
divergence detail on failure.

---

## 3 · Running a drill and reading the result

1. **Trigger.** Either wait for the monthly cron, dispatch the workflow manually
   (`Actions → projection-rebuild-drill → Run workflow`), or run
   `make projection-rebuild-drill` locally / in a non-production environment with
   production-shaped data (§7.2 runs the drill **off** production).
2. **PASS** — every `FullRebuildDrill_*` case is green: the cold rebuild
   reproduced the running state (`Identical == true`) and the repair case
   re-folded a drifted belief back to the cold-fold hash. The source-of-truth
   invariant held this cycle. Record it (§5).
3. **FAIL** — a `FullRebuildDrill_*` case is red. The test output names the
   divergence (a before-hash ≠ after-hash where they were expected equal, or an
   `EventsRefolded` count that is off). This is a **divergence finding** — go to
   §4.

---

## 4 · When the drill fails or goes stale

### 4a · Divergence (the drill RAN and FAILED)

A red drill means a cold re-fold did not reproduce the running projection — the
slow-drift bug the drill exists to catch.

1. Capture the divergent `projection_kind` and the before/after hashes from the
   test output — that is the evidence.
2. **The running state, if rebuilt, is now correct** (the cold fold is the
   source of truth). The urgent risk is contained once a rebuild completes; the
   real work is the root cause.
3. A divergence proves a handler is not a pure, deterministic fold of its events.
   Hand it to the family/engine owner with the kind and the hashes. **Do not
   close** until the handler is fixed and a re-run drill is clean — otherwise the
   divergence returns next cycle.
4. If/when the reconciler emits a `reconciliation_rebuild_divergence_total`
   metric, the `ReconciliationRebuildDivergence` rule
   ([alert-rules.yaml](../grafana/prometheus/alert-rules.yaml), guarded today)
   fires this automatically — see the
   [reconciliation-alerts runbook](./reconciliation-alerts.md) §4.

### 4b · Staleness (the drill did NOT run) — `ProjectionRebuildDrillStale`

This is the **live** alert (the only reconciliation rule active today). It fires
when no successful drill has been recorded in >35 days, **or** when the freshness
metric was never pushed at all — an un-run drill and a long-stale drill are the
same finding: the invariant is unproven.

To wire freshness (recommended), have a green drill publish
`reconciliation_drill_last_success_timestamp_seconds = <unix-now>` to a
Prometheus Pushgateway, or write it to a node_exporter textfile collector path,
e.g.:

```bash
# on a successful drill, record the moment it passed:
printf 'reconciliation_drill_last_success_timestamp_seconds %s\n' "$(date +%s)" \
  > "${TEXTFILE_DIR:-/var/lib/node_exporter/textfile_collector}/projection_rebuild_drill.prom"
```

Until that is wired, the `absent(...)` clause makes the alert read an un-pushed
metric as "no recent drill" — the safe interpretation. **A missed scheduled
drill is a process incident** (ADR-PC-005 §P5): raise it, do not silence the
alert.

---

## 5 · Record the drill — resilience-testing evidence

Like the DR drill, a projection-rebuild drill produces a dated record (the
resilience-testing evidence artefact). The CI run *is* the primary record; keep a
human-readable summary where the ops/audit trail lives (not in this repo — it is
operational data, not config):

```
Projection-rebuild drill — <YYYY-MM-DD>
Trigger: [monthly cron | manual dispatch | local]
Result:  PASS / FAIL
Kinds rebuilt: <list or "all">   Divergences: <none | kind+hashes>
Cold-replay wall-clock: <…>  (budget held: Y/N)
Snapshot correctness (§8.3): PASS/FAIL
Freshness metric pushed: Y/N
Findings: <…>
```

A FAIL, a missed cadence, or a divergence left un-root-caused is a finding — it
is raised, not buried.

---

## 6 · Cross-references

- [event-store §7.2](../../docs/product-management/product_concepts/feature-design-event-store-projections.md)
  — the projection-rebuild drill on a calendar schedule, run off production.
- [event-store §8.2 / §8.3](../../docs/product-management/product_concepts/feature-design-event-store-projections.md)
  — replay-must-work-cold; the drill's snapshot-correctness verification.
- [`ProjectionReconciler.FullRebuildDrillAsync`](../../engine/src/Babelstone.Engine/ProjectionReconciler.cs)
  — the rebuild path the drill drives (read-only reference; the drill wraps it,
  does not change it).
- [`scripts/projection-rebuild-drill.sh`](../../scripts/projection-rebuild-drill.sh)
  / [`.github/workflows/projection-rebuild-drill.yml`](../../.github/workflows/projection-rebuild-drill.yml)
  — the script + scheduled workflow this runbook documents.
- [reconciliation-alerts runbook](./reconciliation-alerts.md) — the daily
  checksum / event-count signals (§7.1) and their alerts (bd `babelstone-irfl`).
- [DR recovery drill runbook](./dr-recovery-drill.md) — the monthly failover
  rehearsal rides this drill's cadence.
- [ADR-PC-003](../../docs/product-management/product_concepts/adrs/ADR-PC-003-postgresql-snapshots.md)
  — snapshots accelerate but are not required for cold replay; the drill verifies
  their correctness.
- [ADR-PC-005 §P5](../../docs/product-management/product_concepts/adrs/ADR-PC-005-dr-rto-rpo.md)
  — drills as resilience-testing evidence; a missed drill is a process incident.
