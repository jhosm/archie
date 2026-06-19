# Reconciliation alerts — ops runbook (M.5, bd `babelstone-irfl`)

**In plain English.** The engine keeps an independent way of checking that every
downstream "projection" (a derived view of the data, like a deposit balance)
still agrees with the raw event log it was built from. When it stops agreeing,
that is *drift* — a quiet bug that, left alone, eventually shows a regulator or
auditor a wrong number. This runbook is what an operator does when one of those
drift alarms goes off: what each alarm means, how urgent it is, and how to fix
it (the fix is almost always "rebuild the affected view from the log").

The formal frame: this is the operational layer over the three
[event-store §7.1](../../docs/product-management/product_concepts/feature-design-event-store-projections.md)
reconciliation patterns computed by
[`ProjectionReconciler`](../../engine/src/Babelstone.Engine/ProjectionReconciler.cs).
The alert rules these runbooks pair with live in
[`infra/grafana/prometheus/alert-rules.yaml`](../grafana/prometheus/alert-rules.yaml)
(the `projection-reconciliation` group). Mismatch thresholds and escalation are
the M.5 deliverable.

> **Emit status (read this first).** The reconciler returns its verdicts as
> *records* (`ChecksumReconciliation`, `EventCountReconciliation`,
> `RebuildReconciliation`), not yet as Prometheus metrics. The checksum / skip /
> divergence alert rules are therefore present in `alert-rules.yaml` as
> **guarded, commented** rules — each names the pending emit, owner, threshold
> and severity, the [ADR-PC-020 §P5](../../docs/product-management/product_concepts/adrs/ADR-PC-020-llm-toolchain-and-conformance-governance.md)
> deliberate-visible-hole discipline. This runbook documents the operator
> response so it is ready the day the metric lands and the rule is uncommented.
> The one rule that is **live today** is the drill-freshness alert — see the
> companion [projection-rebuild-drill runbook](./projection-rebuild-drill.md).

---

## 0 · The three signals at a glance

| Alert | §7.1 pattern | Reconciler verdict | Severity | Meaning |
|---|---|---|---|---|
| `ReconciliationChecksumMismatch` | (a) daily checksum | `ChecksumReconciliation.Match == false` | **critical** | A projection belief disagrees byte-for-byte with a cold fold of the log — consumer drift. |
| `ReconciliationEventCountSkip` | (b) event-count | `EventCountStatus.Skip` | **critical** | The consumer advanced its sequence past events it never folded — events lost/dropped. |
| `ReconciliationRebuildDivergence` | (c) §7.2 rebuild | `RebuildReconciliation.Identical == false` | **critical** | The monthly cold rebuild did not reproduce the running state — a slow-drift bug. |

A benign **`Gap`** (`EventCountStatus.Gap` — the consumer is merely behind the
head, in order) is **not** an alert: it is acceptable async lag that closes on
the next drain ([§7.1](../../docs/product-management/product_concepts/feature-design-event-store-projections.md)).
Only a `Skip` fires. Keep that distinction front of mind — paging on lag is
alert fatigue; paging on a skip is correct.

---

## 1 · Thresholds and escalation (the M.5 decision)

Thresholds are deployment-time decisions
([ADR-IC-004 §P4](../../docs/product-management/integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md)
posture, applied here); the values below are the POC defaults baked into
`alert-rules.yaml`.

| Signal | Threshold | `for:` | Severity | Escalation |
|---|---|---|---|---|
| Checksum mismatch | any in 1 day | 0m | critical | Page on-call. A regulator/auditor must never be first to see a drifted projection. |
| Event-count skip | any in 5 min | 0m | critical | Page on-call. The projection is wrong until rebuilt. |
| Rebuild divergence | any in 35 days | 0m | critical | Page on-call; open an investigation issue on the divergent kind before the next settlement cycle. |
| Drill stale / un-run | >35 days or never | 1h | warning | See [projection-rebuild-drill runbook](./projection-rebuild-drill.md) §4. A missed drill is a process incident (ADR-PC-005 §P5). |

Severity routes via the `severity:` label the Alertmanager/Grafana contact-point
keys off (same convention as the SLI rules in this file). `critical` pages;
`warning` notifies.

---

## 2 · `ReconciliationChecksumMismatch` — consumer drift (§7.1 pattern (a))

**What it means.** The daily per-instance checksum cold-folded the stream from
the event log alone, hashed that state, and it did **not** match a hash of the
projection's current belief. The materialised belief has drifted since the last
reconciliation — accumulated handler drift, a partial write, or a bug in a
conditional that depends on consumer state.

**Triage.**

1. Read the alert labels: `consumer` (the `ReconciliationContract.Consumer`
   reference — `engine` / `acl` / `notification` / an analytics consumer; never
   PII) and `projection_kind` (the family-prefixed discriminator, e.g.
   `term_deposit.deposit_position`). These name *which* view drifted and *whose*.
2. Confirm it is genuine drift, not a transient mid-write read: re-run the
   checksum for that `(streamId, projectionKind)` — if it now matches, a write
   was in flight; note it and stand down. If it persists, it is real drift.

**Repair.** Run the §7.2 full-rebuild drill on the affected kind — the rebuild
*is* the repair path (supersede-all + checkpoint reset + cold re-fold from 0
discards the drifted belief and rewrites it from the log). See the
[projection-rebuild-drill runbook](./projection-rebuild-drill.md) §3. After the
rebuild, the checksum matches by construction; then **root-cause the handler
drift** so it does not recur — a rebuild that has to be repeated is a finding.

**Do not** hand-edit the projection row. The only legitimate way to correct a
belief is to re-fold it from the log.

---

## 3 · `ReconciliationEventCountSkip` — events lost (§7.1 pattern (b))

**What it means.** Event-count reconciliation found the consumer reporting
**fewer** folded events than genuinely exist at or below the sequence it claims
to have processed. It advanced its checkpoint past events it never applied — a
plumbing failure (a dropped delivery, a partition reset, a checkpoint written
ahead of the fold). This is **not** a benign `Gap` (behind the head, in order):
a `Gap` does not fire.

**Triage.**

1. Labels again: `consumer` + `projection_kind` name the affected consumer.
2. Establish the blast radius: is it one stream or many? A single-stream skip is
   often a poisoned record stepped past (cross-check `inbox_poison_total` for the
   same topic); a fleet-wide skip points at a checkpoint/relay fault.
3. Check the inbox/relay health for the consumer's topic — a skip frequently
   pairs with an `InboxPoisonRecordsAppearing` or an outbox-lag alert.

**Repair.** Rebuild the affected kind from the log (same path as §2). The
rebuild re-folds *every* event, so the skipped events are reapplied. Then fix the
plumbing that let the checkpoint run ahead of the fold — a recurring skip is a
delivery/idempotency defect, not a projection defect.

---

## 4 · `ReconciliationRebuildDivergence` — slow drift the cheap checks missed (§7.2)

**What it means.** The monthly full-rebuild drill cold-re-folded the log and the
result did **not** match the running projection byte-for-byte. This is the bug
class the daily checksum cannot catch: accumulated rounding error, or logic whose
output depends on the *order/history* of consumer state rather than the events
alone. It is the reason the drill exists.

**Triage and repair.**

1. The drill output names the divergent `projection_kind` and the before/after
   hashes. Capture both — they are the evidence.
2. **The rebuild already repaired the running state** (the drill's after-state is
   the correct cold fold). The urgent operational risk is contained the moment the
   drill completes.
3. The real work is the **root cause**: a divergence proves a handler is not a
   pure, replayable fold of the events. Hand it to the family/engine owner with
   the before/after hashes and the kind. Do not close the alert until the handler
   is fixed and a re-run drill is clean — otherwise the divergence returns next
   month.

See the [projection-rebuild-drill runbook](./projection-rebuild-drill.md) for how
the drill is run and what a divergence looks like in its output.

---

## 5 · No-PII discipline (do not break it while triaging)

Reconciliation signals carry only **structural references** — the `consumer`
name, the `projection_kind` discriminator, a `streamId`, and hashes
([ADR-PC-004 §P2](../../docs/product-management/product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md)
/ the no-PII-on-the-durable-bus rule). When you escalate or attach evidence to a
ticket, carry the **same** references — a stream id and a kind, never a depositor
name, NIF, or IBAN. The hashes are SHA-256 digests, safe to paste. Resist the
urge to "make it readable" by resolving the subject; that is exactly the boundary
the design keeps closed.

---

## 6 · Cross-references

- [event-store §7.1 / §7.2 / §7.3](../../docs/product-management/product_concepts/feature-design-event-store-projections.md)
  — the three reconciliation patterns, the rebuild drill, and the per-consumer
  reconciliation contract.
- [`ProjectionReconciler.cs`](../../engine/src/Babelstone.Engine/ProjectionReconciler.cs)
  — the engine code that computes these verdicts (read-only reference).
- [`alert-rules.yaml`](../grafana/prometheus/alert-rules.yaml) — the
  `projection-reconciliation` rule group these runbooks pair with.
- [projection-rebuild-drill runbook](./projection-rebuild-drill.md) — running the
  §7.2 drill, the freshness alert, and snapshot-correctness verification (bd
  `babelstone-j67l`).
- [DR recovery drill runbook](./dr-recovery-drill.md) — the monthly failover
  rehearsal rides the same monthly cadence as the rebuild drill (§1 there).
- [ADR-PC-002](../../docs/product-management/product_concepts/adrs/ADR-PC-002-application-level-bitemporality.md)
  — projections are derived, rebuildable bitemporal folds over the event log
  (the source of truth the reconciler re-folds against).
- [ADR-PC-020 §P5](../../docs/product-management/product_concepts/adrs/ADR-PC-020-llm-toolchain-and-conformance-governance.md)
  — the deliberate-visible-hole discipline the guarded alert rules follow.
