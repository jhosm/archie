# ADR-PC-003: Snapshot Mechanism — Same-Database PostgreSQL Snapshot Table

| Field | Value |
|---|---|
| Status | Accepted |
| Date | 2026-05-23 |
| Deciders | jhosm |
| Shape | Tool-selection |
| Common criteria | [ADR-IC-000](../../integration_concepts/adrs/ADR-IC-000-common-evaluation-criteria.md) (reused per [ADR-PC-000](./ADR-PC-000-namespace-and-contract-shape-framework.md) D2) |
| Depends on | [ADR-PC-001](./ADR-PC-001-event-store-technology.md) (PostgreSQL event store), [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) (hand-rolled engine; owns the snapshot machinery), [ADR-PC-002](./ADR-PC-002-application-level-bitemporality.md) (projections that snapshots accelerate) |
| Resolves | bd `archie-10r.4` (ADR-PC-003: Snapshot mechanism for the event store) |

---

## Context

Snapshots are a **performance optimisation, not architecture** ([01 §2](../01-product-architecture.md), [event-store §8](../feature-design-event-store-projections.md)): the engine must always be able to rebuild any projection from the event log alone; snapshots only accelerate the rebuild. Building the snapshot infrastructure in v1 is **non-negotiable** ([two-modes §5.5](../feature-design-two-modes-asymmetry.md)) — not because v1's modest replay needs demand it (lifecycle and calendar boundaries barely exercise it), but because v4's do (a current account with ~250–1000 events needs cold replay under 30 s per [event-store §8.2](../feature-design-event-store-projections.md)), and snapshot infrastructure that is never exercised rots. v1 builds and continuously exercises it so it is operationally correct by the time v4 demands it.

This ADR decides the snapshot **storage shape**, **generation cadence**, **replay-from-snapshot semantics**, **validation**, and **retention** ([bd archie-10r.4](../04-open-questions.md) acceptance: read/write API, validation cadence, retention policy, runbook stub).

**Candidates evaluated** (storage location for snapshots):

| # | Candidate | Notes |
|---|---|---|
| A | **Rows in a same-database PostgreSQL `snapshots` table** | Beside `events`/`outbox`/projections; one operational substrate; optionally transactional with the append. |
| B | **Separate blob / object store** (S3-class) | Snapshots as serialised blobs in object storage, referenced by key. |
| C | **Inside a dedicated event-store product** | Native snapshotting of a framework/event-store DB. |

Candidate C is foreclosed by [ADR-PC-001](./ADR-PC-001-event-store-technology.md) (PostgreSQL, no dedicated event-store product) and [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) (no Marten snapshot lifecycle). The live decision is **A vs B**.

---

## Evaluation

### Hard filter results

#### F1 · Cost / licensing

| Candidate | Licence | Verdict |
|---|---|---|
| A · Same-DB PG table | PostgreSQL licence; already in the stack. Zero incremental cost. | **Pass** |
| B · Blob / object store | MinIO (AGPL) or cloud object store (free tier / paid). | **Pass (conditional)** — self-hosted MinIO is OSS but a new operational surface; cloud object store is a managed dependency the on-prem deployment ([01 §6](../01-product-architecture.md)) may not want. |

#### F2 · Regulatory fit (GDPR / DORA / PSD2)

Snapshots are **recomputable performance state, not source of truth** — they carry no independent regulatory weight (the event log is the audit trail). GDPR note: a snapshot may materialise PII into its serialised state, so a snapshot of an erased subject must be discardable/rebuildable so that post-erasure rebuild yields the null-PII state ([ADR-PC-004](./ADR-PC-004-pii-crypto-shredding.md)). Both candidates can satisfy this; same-DB snapshots inherit the event store's DR and erasure handling directly.

| Candidate | Verdict |
|---|---|
| A · Same-DB PG table | **Pass** — inherits PG PITR/replication ([ADR-PC-005](./ADR-PC-005-dr-rto-rpo.md)); discard-and-rebuild clears PII materialised in stale snapshots. |
| B · Blob store | **Pass (conditional)** — a second store with its own DR/erasure lifecycle that must be kept consistent with crypto-shredding. |

---

### Soft criteria

#### A · Same-database PostgreSQL `snapshots` table — **CHOSEN**

**S1 · Operational complexity.** Lowest — no new store. Same backup, PITR, replication, monitoring, and on-call as the event store. [ADR-PC-001 §Consequences](./ADR-PC-001-event-store-technology.md) already commits this shape: "Snapshots are rows in a `snapshots` table in the same database."

**S2 · Ecosystem coherence.** A snapshot write *can* be transactional with the event append if the engine ever wants it (same DB, same transaction) — though §8.1 prefers eventually-consistent writes (below). The hand-rolled engine ([ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md)) owns the snapshot read/write API as plain SQL.

**S3 · Exit cost.** Low. A snapshot row is `(stream_id, serialized_state, hash, …)`; recomputable from the log regardless, so exit cost is near-zero (you can simply discard snapshots and rebuild on any new substrate).

**S4 · Community and longevity.** PostgreSQL's.

#### B · Separate blob / object store

Object storage suits very large snapshot blobs and decouples snapshot size from DB bloat. But it adds a second store with its own DR, consistency, and erasure lifecycle for a 1–2 person team, and forecloses the (optional) transactional-with-append path. **Decisive reason for not choosing:** snapshots are small per-stream state (a deposit/account aggregate), not large blobs; the same-DB table carries them with zero new operational surface and inherits the event store's DR and crypto-shred handling. Object storage is a v4+ option if snapshot volume ever justifies it — the read/write API (§P1) hides the storage location.

---

## Decision

**Chosen: snapshots are rows in a `snapshots` table in the same PostgreSQL database as the event store.** Decisive reasons: zero new operational surface for the 1–2 person team; it inherits the event store's DR ([ADR-PC-005](./ADR-PC-005-dr-rto-rpo.md)) and crypto-shred ([ADR-PC-004](./ADR-PC-004-pii-crypto-shredding.md)) handling directly; and it is the shape [ADR-PC-001](./ADR-PC-001-event-store-technology.md) already committed. The snapshot read/write API (§P1) abstracts the location, so a future move to object storage at v4 scale is non-breaking.

**Rejected: separate blob/object store** — a second store's DR/consistency/erasure lifecycle is unjustified at v1 snapshot sizes; reconsider only if v4 snapshot volume demands it. **Rejected: dedicated event-store native snapshotting** — foreclosed by PC-001/PC-010.

---

## Implementation Principles

### P1 — Snapshot table shape and read/write API

The `snapshots` table carries: `stream_id`, `projection_type`, `last_sequence_number` (the event sequence the snapshot covers), `last_event_id` (covered, for the hash), `snapshot_state` (serialised aggregate/projection state), `snapshot_hash`, `created_at`, plus `pack_version` and `schema_version` (the snapshot is only valid for the pins it was built under). The engine exposes `writeSnapshot(stream_id, projection_type, …)` and `readLatestSnapshot(stream_id, projection_type, atOrBeforeSequence)`; lower-level SQL is private to the engine's snapshot module ([ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md)).

### P2 — Generation cadence: three composing triggers, eventually-consistent writes

A snapshot is taken when **any** of three conditions fires ([event-store §8.1](../feature-design-event-store-projections.md)): per-N-events (configurable per family, typically 100–1000 un-snapshotted events), at lifecycle boundaries (constitution, renewal, partial withdrawal, maturity, termination), or at calendar boundaries (month-end / year-end, for fast period-boundary as-of queries). Snapshot writes are **eventually-consistent with the event log, not transactional with it** ([event-store §8.1](../feature-design-event-store-projections.md)) — a background snapshotter writes them; if a write fails the engine continues and the next rebuild is merely slower, never wrong. (The same-DB substrate keeps the transactional option open should a family ever need it, but the default is async.)

### P3 — Replay-from-snapshot semantics; cold replay must work with zero snapshots

To reconstruct state at a target sequence, the engine loads the latest *valid* snapshot at or before the target and applies only the tail of events since. **Cold replay — rebuilding from the first event with no snapshot — must always work** ([event-store §8.2](../feature-design-event-store-projections.md)): the correctness fallback. v1 budget: ~24–260-event instance under 5 s; v4: ~250–1000-event instance under 30 s. Budgets are validated by the Q-AK rig ([two-modes §8](../feature-design-two-modes-asymmetry.md)).

### P4 — Validation: hash-and-verify, advisory until proven, discard-and-rebuild monthly

A buggy snapshot is the worst event-sourcing failure mode (reads trust it blindly — [event-store §8.3](../feature-design-event-store-projections.md)). Two defences: (1) the snapshot hash includes `last_event_id`, and any rebuild *from* a snapshot verifies the rebuilt state at that sequence matches the hash; (2) the monthly projection-rebuild drill ([event-store §7.2](../feature-design-event-store-projections.md)) discards all snapshots and rebuilds cold — match ⇒ snapshots correct, mismatch ⇒ snapshot infrastructure is investigated. Snapshots are **advisory only** until they pass these checks for six months ([event-store §8.3](../feature-design-event-store-projections.md)); only then are they trusted in production replays.

### P5 — Retention; never the substitute for the log

Keep the latest snapshot per `(stream_id, projection_type)` plus calendar-boundary snapshots required for period as-of queries; older intermediate snapshots are GC'd. The event log is **never** pruned on account of a snapshot — snapshots accelerate, they do not replace ([01 §2](../01-product-architecture.md)). A PII-bearing snapshot of an erased subject is discarded and rebuilt so post-erasure state shows null PII ([ADR-PC-004](./ADR-PC-004-pii-crypto-shredding.md)).

### P6 — Operational runbook stub

(1) Snapshot lag alarm (un-snapshotted event count exceeds threshold ⇒ snapshotter health check). (2) Hash-mismatch on read ⇒ discard the snapshot, cold-rebuild the stream, page if it recurs. (3) Monthly discard-and-rebuild drill is on the ops calendar ([event-store §10.2](../feature-design-event-store-projections.md)); a missed drill is a process incident. (4) v4 cadence turn-up (more aggressive triggers, finer scopes) changes config, not architecture ([two-modes §5.5](../feature-design-two-modes-asymmetry.md)).

---

## Residual Risks

1. **Buggy snapshot trusted blindly** — the canonical failure mode. Mitigated by hash-and-verify, advisory-until-six-months, and monthly discard-and-rebuild (§P4).
2. **Snapshot table bloat** — mitigated by the retention policy (§P5); object-store offload is the v4 escape hatch behind the read/write API (§P1).
3. **PII materialised in snapshots vs crypto-shredding** — a stale snapshot can hold pre-erasure PII; mitigated by discard-and-rebuild after erasure and by treating snapshots as recomputable (coordinate with [ADR-PC-004](./ADR-PC-004-pii-crypto-shredding.md) and the [ADR-PC-005](./ADR-PC-005-dr-rto-rpo.md) backup-retention horizon).
4. **Cold-replay budget at v4 scale** — validated empirically by the Q-AK rig; snapshots are the mechanism that keeps the budget met as histories deepen.

---

## Cross-references

- [ADR-PC-001](./ADR-PC-001-event-store-technology.md) — PostgreSQL event store; §Consequences already places snapshots as rows in the same database.
- [ADR-PC-002](./ADR-PC-002-application-level-bitemporality.md) — the projections snapshots accelerate; both rebuildable from the log.
- [ADR-PC-004](./ADR-PC-004-pii-crypto-shredding.md) — PII in snapshots must be discardable so post-erasure rebuild shows null.
- [ADR-PC-005](./ADR-PC-005-dr-rto-rpo.md) — snapshots accelerate recovery cold-replay; snapshot DR is inherited (recomputable, so not separately protected).
- [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) — the hand-rolled engine owns the snapshot module.
- [event-store §8](../feature-design-event-store-projections.md) — snapshot strategy; [§7.2](../feature-design-event-store-projections.md) rebuild drills; [two-modes §5.5](../feature-design-two-modes-asymmetry.md) snapshot infra in v1.

---

*Decided 2026-05-23 by jhosm.*
