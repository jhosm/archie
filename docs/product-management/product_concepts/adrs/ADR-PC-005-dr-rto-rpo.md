# ADR-PC-005: DR / RTO / RPO Posture for the Event Store

| Field | Value |
|---|---|
| Status | Accepted (production-blocking at cutover; RTO/RPO numbers are POC defaults, operating-bank sign-off not required for the POC) |
| Date | 2026-05-23 |
| Deciders | jhosm |
| Shape | Tool-selection (operational-discipline ADR per [ADR-PC-000](./ADR-PC-000-namespace-and-contract-shape-framework.md) D4; default to tool-selection) |
| Common criteria | [ADR-IC-000](../../integration_concepts/adrs/ADR-IC-000-common-evaluation-criteria.md) (reused per [ADR-PC-000](./ADR-PC-000-namespace-and-contract-shape-framework.md) D2) |
| Depends on | [ADR-PC-001](./ADR-PC-001-event-store-technology.md) (PostgreSQL event store — the volume to protect), [ADR-PC-003](./ADR-PC-003-postgresql-snapshots.md) (snapshots accelerate recovery replay), [ADR-PC-004](./ADR-PC-004-pii-crypto-shredding.md) (OpenBao key-store DR; the crypto-shred-vs-backup tension), [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) (hand-rolled engine) |
| Resolves | bd `archie-10r.6` (ADR-PC-005: DR / RTO / RPO posture for the event store) |

> **Source-citation note:** the bd issue cites "Q-AT"; the DR/RTO/RPO open question is actually **[Q-AY](../04-open-questions.md)** (Q-AT is cross-moratorium handling). This ADR resolves Q-AY.

---

## Context

The event store is the engine's source of truth ([01 §2](../01-product-architecture.md)); projections are derived and rebuildable, but **an event-store-volume loss is unrecoverable from projections alone** ([Q-AY](../04-open-questions.md), bd issue). [event-store](../feature-design-event-store-projections.md) makes replay routine and treats it as the *integrity* story, but never names a recovery posture for losing the events volume. This ADR does — it is **production-blocking for v1 cutover**: the engine cannot enter production without a named recovery position.

Four decisions ([bd archie-10r.6](../04-open-questions.md), [Q-AY](../04-open-questions.md)): (1) backup cadence; (2) off-site replication topology; (3) named RTO/RPO targets (numbers, not "fast"/"small"); (4) the cold-replay budget under a *recovery* scenario — distinct from the [Q-Z](../04-open-questions.md) normal-operations cold-replay benchmark.

**What must be protected** (the recovery scope is broader than the `events` table alone):

- `events`, `outbox`, `saga_state` — the source of truth and in-flight orchestration ([ADR-PC-001](./ADR-PC-001-event-store-technology.md), [ADR-PC-010 §P3–§P4](./ADR-PC-010-dotnet-hand-rolled-engine.md)). **Catastrophic if lost.**
- The **OpenBao key store** ([ADR-PC-004](./ADR-PC-004-pii-crypto-shredding.md)). Key loss = irreversible loss of *all* PII (mass de-facto erasure) — as critical to protect as the events volume.
- Projections and snapshots — **recoverable by rebuild from the log**; not separately protected (rebuild is the recovery, snapshots merely accelerate it).

**Candidates evaluated** (backup + replication topology, all PostgreSQL-native per [ADR-PC-001](./ADR-PC-001-event-store-technology.md)):

| # | Candidate | Notes |
|---|---|---|
| A | **Continuous WAL streaming + synchronous off-site warm standby + PITR base backups** | Streaming replication to a warm standby in a separate site/AZ; WAL archiving + periodic base backups (pgBackRest/Barman) for point-in-time recovery. |
| B | **Periodic snapshot backups only** (hourly / daily) | Base backups at intervals; no continuous replication. |
| C | **Multi-region active-active** | Bidirectional replication across regions. |

---

## Evaluation

### Hard filter results

#### F1 · Cost / licensing

| Candidate | Licence / cost | Verdict |
|---|---|---|
| A · WAL streaming + warm standby + PITR | PostgreSQL-native streaming replication + WAL archiving; pgBackRest / Barman are OSS. Cost = a second PG node + off-site storage. | **Pass** |
| B · Periodic snapshots only | PG base backups; cheapest. | **Pass** |
| C · Multi-region active-active | PG multi-master is hard/limited (BDR-class tooling is largely commercial); operationally heavy. | **Pass (conditional)** — multi-master PG tooling tends toward paid/commercial and high operational burden; flagged per F1. |

#### F2 · Regulatory fit (GDPR / DORA / PSD2)

**DORA explicitly requires documentable RTO/RPO and resilience testing** ([ADR-IC-000 F2](../../integration_concepts/adrs/ADR-IC-000-common-evaluation-criteria.md)). This ADR is the artefact that satisfies that obligation; the projection-rebuild drill ([event-store §7.2](../feature-design-event-store-projections.md)) doubles as the resilience-test evidence. GDPR interaction: backups retain PII ciphertext and (for the key store) keys, so the backup-retention horizon bounds true crypto-shred completion (see §P4, [ADR-PC-004 §P5](./ADR-PC-004-pii-crypto-shredding.md)).

| Candidate | Verdict |
|---|---|
| A | **Pass** — named RTO/RPO, drillable failover and PITR; standard DORA-shaped story. |
| B | **Pass (conditional)** — RPO bounded by the snapshot interval (data-loss window); acceptable only if that window meets the named RPO. |
| C | **Pass** — strongest resilience, but disproportionate for v1 single-region scope. |

---

### Soft criteria

#### A · WAL streaming + synchronous off-site warm standby + PITR — **CHOSEN**

**S1 · Operational complexity.** Moderate and standard for a team already operating PostgreSQL ([ADR-PC-001](./ADR-PC-001-event-store-technology.md)): one warm-standby node and a backup tool (pgBackRest/Barman) the team can run without specialist knowledge. Failover and PITR are well-documented PG operations.

**S2 · Ecosystem coherence.** Native PG primitives; the standby is the same PG the engine already speaks; observability ([ADR-IC-007](../../integration_concepts/adrs/ADR-IC-007-observability-stack.md)) covers replication lag.

**S3 · Exit cost.** Low — WAL/PITR/streaming replication are PG-standard, not vendor-specific.

**S4 · Longevity.** PostgreSQL's; pgBackRest/Barman are mature OSS.

#### B · Periodic snapshots only

Cheapest and simplest, but RPO is bounded by the snapshot interval — an hourly cadence risks up to an hour of committed events lost on a primary failure, which is unacceptable for a banking source of truth. **Decisive reason for not choosing:** RPO is too coarse for the source of truth; continuous WAL streaming (A) drives RPO toward zero at modest extra cost.

#### C · Multi-region active-active

Strongest resilience, but PG multi-master is hard, the tooling skews commercial (F1), and v1 is explicitly single-region ([01 §6](../01-product-architecture.md), [ADR-PC-001 §Consequences](./ADR-PC-001-event-store-technology.md)). **Decisive reason for not choosing:** disproportionate for v1; named as the future path, deferred.

---

## Decision

**Chosen: continuous WAL streaming to a synchronous off-site warm standby, plus WAL archiving and periodic base backups (pgBackRest/Barman) for point-in-time recovery — single-region for v1, with multi-region named and deferred.** The OpenBao key store gets an equivalent DR posture (HA + backup), because losing it loses all PII.

This is the lowest-risk PG-native posture that drives RPO toward zero for the source of truth at a cost a 1–2 person team operating PostgreSQL can carry, and it produces the documentable RTO/RPO and drillable failover/PITR that DORA requires.

**Rejected: periodic snapshots only** — RPO too coarse for the source of truth. **Rejected: multi-region active-active** — disproportionate for v1's single-region scope; the future scale path, not the v1 posture.

### Named RTO / RPO targets (v1 POC defaults — pending operating-bank sign-off)

These are concrete numbers per [Q-AY](../04-open-questions.md)'s "named, not vague" requirement; the operating bank confirms or tightens them at cutover.

| Target | v1 default | Rationale |
|---|---|---|
| **RPO — committed events** | **≈ 0** | Synchronous streaming replication: a committed event exists on the standby before commit acknowledgement, so primary loss loses no acknowledged event. |
| **RPO — base-backup floor** (standby + primary both lost) | **≤ 60 s** | WAL archived continuously; PITR replays to within the last archived segment. |
| **RTO — failover to warm standby** | **≤ 15 min** | Promote the standby; engine reconnects. |
| **RTO — full restore from backup** (both nodes lost) | **≤ 4 h (v1 book)** | Restore base backup + replay WAL, then rebuild projections (snapshot-accelerated). |
| **Recovery cold-replay budget** | full-book projection rebuild within the published window (**≤ 24 h** at v4 scale per [two-modes §6.1](../feature-design-two-modes-asymmetry.md)) | Distinct from the [Q-Z](../04-open-questions.md) per-instance benchmark — this is whole-book rebuild after a restore. Snapshots ([ADR-PC-003](./ADR-PC-003-postgresql-snapshots.md)) keep it inside the window. |

*Revised 2026-06-11 (additive, does not change the Decision — M.4 / bd `babelstone-f0ui`): the §P2 PITR leg is implemented with **pgBackRest** (chosen from the §Decision-named "pgBackRest/Barman" candidate set for its first-class incremental + immutable-retention support on object stores) — config [`infra/pgbackrest/pgbackrest.conf`](../../../../infra/pgbackrest/pgbackrest.conf), wired in [`infra/k8s/overlays/ha/`](../../../../infra/k8s/README.md). Two POC-default operational numbers are fixed here, both refinements WITHIN the §Decision targets (not changes to them): **event-store PITR retention = 14 days** (time-based full retention; the immutable §P2 window) and **OpenBao key-store raft-snapshot retention = 14 days, daily cadence** (§P4 "at least as strong as the event store"). The two retention windows are deliberately **aligned**, because their maximum is the true crypto-shred-completion horizon (§P4 / [ADR-PC-004 §P5](./ADR-PC-004-pii-crypto-shredding.md)); the recovery drill ([`infra/runbooks/dr-recovery-drill.md`](../../../../infra/runbooks/dr-recovery-drill.md) §6) asserts they stay aligned. WAL archiving uses `archive-async` so archive-push stays OFF the §P1 synchronous-commit path. CI validates the manifests' SHAPE (kustomize build + kubeconform); the actual restore/failover is the §P5 drill, not a CI step.*

---

## Implementation Principles

### P1 — Protect the source of truth with synchronous replication

`events`, `outbox`, and `saga_state` are replicated synchronously to an off-site warm standby so a committed event is durable on two nodes before acknowledgement (RPO ≈ 0 for committed events). The synchronous-replication latency cost is on the append path and **must be included in the Q-AK load test** ([two-modes §8](../feature-design-two-modes-asymmetry.md)) — RPO-vs-write-latency is a real trade-off to validate, not assume.

### P2 — PITR via WAL archiving + base backups, off-site and immutable

Continuous WAL archiving plus scheduled base backups (pgBackRest or Barman) to off-site, retention-locked storage give point-in-time recovery if both primary and standby are lost. Backups are immutable within their retention window (tamper-evidence for DORA/PSD2). Backup *restore* is drilled, not assumed correct.

### P3 — Projections and snapshots are not separately protected — recovery is rebuild

On recovery, restore the `events` volume to the recovery point, then **rebuild projections from the log** ([ADR-PC-002](./ADR-PC-002-application-level-bitemporality.md)); snapshots ([ADR-PC-003](./ADR-PC-003-postgresql-snapshots.md)) accelerate but cold replay must work without them ([event-store §8.2](../feature-design-event-store-projections.md)). This is why projections/snapshots need no independent DR — they are derived state.

### P4 — OpenBao key-store DR, and the crypto-shred-vs-backup horizon

The OpenBao key store ([ADR-PC-004](./ADR-PC-004-pii-crypto-shredding.md)) is HA and backed up with guarantees at least as strong as the event store — key loss is irreversible loss of all PII. The **tension**: a GDPR erasure destroys a subject's key, but key-store backups may still hold it and event-store backups still hold the ciphertext, so erasure is only *complete* once every backup containing the key has rolled past its retention horizon. v1 either propagates key destruction into key-store backups within a bounded window, or documents the backup-retention horizon as the true erasure-completion time. This is a named output of the DPO meeting ([ADR-PC-004 §Gate](./ADR-PC-004-pii-crypto-shredding.md), [event-store §6.4](../feature-design-event-store-projections.md)) and is owned jointly by this ADR and PC-004.

### P5 — Recovery drills as DORA evidence

The monthly projection-rebuild drill ([event-store §7.2](../feature-design-event-store-projections.md)) is extended periodically into a **full recovery drill**: restore the event store from backup into a clean environment, rebuild projections, verify against expectations, and restore the OpenBao key store. The drill produces the DORA resilience-testing evidence and validates the RTO/RPO numbers above are real, not aspirational. A missed drill is a process incident.

---

## Residual Risks

1. **RTO/RPO numbers are POC defaults pending operating-bank sign-off.** Carried in the Status line; the bank confirms or tightens at cutover.
2. **Synchronous-replication write-path latency.** The RPO-≈0 guarantee costs append latency; validated (not assumed) by the Q-AK load test (§P1).
3. **Crypto-shred-vs-backup-retention horizon.** True erasure lags key destruction by the backup retention window (§P4); resolved by the DPO meeting jointly with [ADR-PC-004](./ADR-PC-004-pii-crypto-shredding.md).
4. **OpenBao DR on the critical path.** Key-store recovery is now as critical as event-store recovery; both are drilled (§P5).
5. **Multi-region deferred.** A future cross-region requirement reopens this ADR (per-region engines with cross-region event replay is the likely shape, not synchronous multi-master — [ADR-PC-001 §Consequences](./ADR-PC-001-event-store-technology.md)).

---

## Verifiable commitments

This decision's load-bearing commitments are fitness functions in the [commitment catalogue](./commitment-catalogue.md) — the single source of truth for each commitment's exact claim, gate (pyramid level), and `Live`/`Planned`/`Gap` status ([ADR-PC-020 §P5–§P7](./ADR-PC-020-llm-toolchain-and-conformance-governance.md)):

No executable engine commitments are wired to this ADR: the DR posture is realised by the operational runbooks and the §P5 full recovery drill (the DORA resilience-testing evidence), not by a unit/integration fitness function. Two seams are testable but owned elsewhere — this ADR composes with them rather than claiming them:

- The recovery cold-replay (§P3, §Decision RTO row) is *rebuild-from-log*, so it rides the existing rebuild gates — `PROJECTION_REBUILD_DETERMINISM` (governed by [ADR-PC-002 §P4](./ADR-PC-002-application-level-bitemporality.md)) and the `REPLAY_BUDGET_5S_30S` cold-replay budget (governed by [event-store §8.2](../feature-design-event-store-projections.md)); this ADR adds no separate projection/snapshot protection of its own.
- **Known gap, no Test ID yet wired:** the synchronous-replication append-latency must be included in the Q-AK load test (§P1) — a falsifiable RPO-vs-write-latency claim with no catalogue row today (a deliberate, visible hole per [ADR-PC-020 §P5](./ADR-PC-020-llm-toolchain-and-conformance-governance.md)), to be added under the catalogue's growth provision when synchronous replication is implemented and benchmarked.

---

## Cross-references

- [ADR-PC-001](./ADR-PC-001-event-store-technology.md) — the PostgreSQL event store this ADR protects; §Consequences flags multi-region as a future PC-005 concern.
- [ADR-PC-003](./ADR-PC-003-postgresql-snapshots.md) — snapshots accelerate recovery cold-replay.
- [ADR-PC-004](./ADR-PC-004-pii-crypto-shredding.md) — OpenBao key-store DR; the crypto-shred-vs-backup tension (§P4) owned jointly.
- [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) — hand-rolled engine; recovery rebuilds projections via its replay path.
- [event-store §7.2](../feature-design-event-store-projections.md) — rebuild drills (extended to recovery drills); [§8.2](../feature-design-event-store-projections.md) cold-replay budgets.
- [two-modes §6.1, §8](../feature-design-two-modes-asymmetry.md) — full-book rebuild window; Q-AK load test (must include sync-replication latency).
- [Q-AY, Q-Z](../04-open-questions.md) — DR/RTO/RPO question (resolved here); normal-ops cold-replay benchmark (distinct).

---

*Decided 2026-05-23 by jhosm. Accepted; production-blocking for v1 cutover; RTO/RPO numbers are POC defaults, operating-bank sign-off not required for the POC.*
