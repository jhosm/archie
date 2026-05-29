# ADR-PC-001: Event Store Technology

| Field | Value |
|---|---|
| Status | Accepted |
| Date | 2026-05-22 |
| Deciders | jhosm |
| Shape | Tool-selection |
| Common criteria | [ADR-IC-000](../../integration_concepts/adrs/ADR-IC-000-common-evaluation-criteria.md) (reused per [ADR-PC-000](./ADR-PC-000-namespace-and-contract-shape-framework.md) D2) |
| Depends on | [ADR-IC-001](../../integration_concepts/adrs/ADR-IC-001-event-backbone-message-broker.md) (Redpanda CE), [ADR-IC-004](../../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md) (custom polling publisher on PostgreSQL), [ADR-IC-005](../../integration_concepts/adrs/ADR-IC-005-cqrs-read-model-storage.md) (PostgreSQL read-model store) |
| Resolves | [Q-AC](../04-open-questions.md) (event-store technology selection) |

---

## Context

The engine described in [01 product-architecture §2](../01-product-architecture.md) treats the event store as its source of truth. State derives from events; projections are bitemporal tables built from the event log; the four time-dimensional capabilities (as-of, audit trail, counterfactual replay, forward projection — per [event-store §2](../feature-design-event-store-projections.md)) are properties of this shape rather than features bolted on. The brief defers the choice of *which* event store technology realises this shape to a follow-up issue: [event-store §10.4](../feature-design-event-store-projections.md) names three candidates and rejects an in-house build; [two-modes §6](../feature-design-two-modes-asymmetry.md) refines the deferral with the criteria the choice must satisfy.

This ADR makes the choice.

**Candidates evaluated** (the three named in event-store §10.4, no in-house build per the same section):

| # | Candidate | Notes |
|---|---|---|
| A | **PostgreSQL-based event store** | Append-only `events` table; co-located with the [ADR-IC-004](../../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md) outbox in the same PostgreSQL database; library or hand-rolled implementation chosen at v1 build time |
| B | **Kurrent / EventStoreDB** | Event-sourcing-native database; dedicated streams; built-in subscriptions, snapshotting, and projection runtime |
| C | **Redpanda-as-event-store** | Reuses the [ADR-IC-001](../../integration_concepts/adrs/ADR-IC-001-event-backbone-message-broker.md) broker as the durable event log; topics-as-streams; tiered storage for replay |

### The constraints this decision must honour

The engine commits to four invariants regardless of which store is picked (per [two-modes §6.3](../feature-design-two-modes-asymmetry.md)). The candidates are not interchangeable on any of them:

1. **Outbox co-location.** [ADR-IC-004 P6](../../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md) is explicit: the outbox table must be in the same PostgreSQL database as the domain state it records; cross-database transactions are forbidden because they break the local-atomicity guarantee that makes the outbox pattern correct. The event-store append and the outbox-write must commit in **one local transaction** ([event-store §1](../feature-design-event-store-projections.md): *"the same write that appends to the event log emits to the bus"*).
2. **Envelope shape including the reserved `partition_key`** (per [event-store §4.3](../feature-design-event-store-projections.md) and [two-modes §5.3](../feature-design-two-modes-asymmetry.md)). The store must accept the envelope without transformation and preserve `partition_key` for future v4 sharding.
3. **Forward-only schema discipline** (per [event-store §5.4](../feature-design-event-store-projections.md)). Once written, an event of `event_schema_version: N` remains readable forever; new event types are added, not retro-fitted.
4. **Snapshot mechanism** (per [event-store §8](../feature-design-event-store-projections.md) and [two-modes §5.5](../feature-design-two-modes-asymmetry.md)). Per-stream snapshots with a hash-and-verify path; discard-and-rebuild routinely exercised.

Two further engine commitments interact with the technology choice and are recorded here because they materially differ between candidates:

- **Bitemporal projections with field-level PII crypto-shredding** ([event-store §6.2](../feature-design-event-store-projections.md)). Every PII-bearing event field must be encryptable under a per-subject key so that key destruction is the GDPR Article 17 erasure mechanism. The structural fields (principal, rate, dates) remain queryable after erasure; only PII fields return null.
- **Replay budgets** ([event-store §8.2](../feature-design-event-store-projections.md)): cold replay of a v1 instance (~24–260 events) under 5 seconds; cold replay of a v4 instance (~250–1000 events) under 30 seconds. The [Q-AK synthetic v4-scale load test](../feature-design-two-modes-asymmetry.md) (250 TPS sustained for 24h, 1000 TPS burst for 15min, 200ms p99 sync-projection latency) is part of v1 acceptance — the chosen store must pass.

---

## Evaluation

### Hard filter results

#### F1 · Cost / licensing

| Candidate | Licence | Assessment | Proceeds? |
|---|---|---|---|
| PostgreSQL-based | PostgreSQL Licence (permissive, BSD-style) | Already in the stack ([ADR-IC-004](../../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md) outbox, [ADR-IC-005](../../integration_concepts/adrs/ADR-IC-005-cqrs-read-model-storage.md) read model). Zero incremental licence cost. Helper libraries (e.g. Marten on .NET, eventsourcing-pg on Python) are MIT/Apache 2.0 — the choice of library is deferred to v1 build. | **Pass** |
| Kurrent / EventStoreDB | Kurrent Community Edition: source-available under the **Kurrent Community Licence** (revised from the prior EventStoreDB ESL). Commercial use requires the **Kurrent Enterprise Licence** (paid) once a deployment crosses defined thresholds; the precise CE-vs-Enterprise boundary has shifted across the 2024–2026 licence revisions. | The licence is not OSI-approved and explicitly restricts use beyond the CE thresholds. Per [ADR-IC-000 F1](../../integration_concepts/adrs/ADR-IC-000-common-evaluation-criteria.md), "tools with a licence that restricts use in a financial services context — flag even if currently free, because the licence constrains future use." For the POC itself, CE is usable; for the operating bank's eventual production deployment, the licence is a forward-looking risk that the F1 wording flags. | **Pass (conditional)** — POC-scale CE use only; production hardening requires licence re-assessment against the then-current Kurrent terms and a budget line for the Enterprise tier if thresholds are crossed |
| Redpanda-as-event-store | Already-selected Redpanda Community Edition is Apache 2.0 (per [ADR-IC-001](../../integration_concepts/adrs/ADR-IC-001-event-backbone-message-broker.md) F1). Adding the event-store role to the existing broker introduces no new licence surface. | Free; OSI-permissive. | **Pass** |

*Date of licence assessment: 2026-05-22. Kurrent's licence has been re-shaped twice in the prior 24 months; re-check before any production commitment.*

#### F2 · Regulatory fit

The event store carries PII inside event payloads — customer name, NIF, address, contact on `DepositConstituted` and related lifecycle events. All three candidates must satisfy GDPR, DORA, and PSD2 (per [ADR-IC-000 F2](../../integration_concepts/adrs/ADR-IC-000-common-evaluation-criteria.md)).

| Candidate | GDPR (Article 17 erasure) | DORA (RTO/RPO, resilience drills) | PSD2 (audit trail) | Proceeds? |
|---|---|---|---|---|
| PostgreSQL-based | Per-subject field-level encryption (crypto-shredding per [event-store §6.2](../feature-design-event-store-projections.md)) is straightforward at row-column granularity: PII fields are stored as ciphertext keyed by `subject_id`; key destruction renders plaintext unrecoverable; structural fields remain queryable. PostgreSQL native column-level expression indexes do not need plaintext, so structural queries are unaffected by erased rows. | Backup via WAL archiving + base backups; PITR is native. Synchronous streaming replication for HA. DORA chaos drills (forced failover, WAL replay from PITR) are standard PG operational practice. RTO/RPO are configurable via `synchronous_commit` and replica topology. | The `events` table is append-only by application discipline (no `UPDATE` or `DELETE` paths); ordered by `(stream_id, sequence_number)`; cryptographically chainable via `previous_event_hash` if required for an out-of-band tamper-detection control. The audit trail is the durable PG table. | **Pass** |
| Kurrent / EventStoreDB | Native event sourcing has no first-class per-field encryption; PII must be encrypted application-side before the event is written. Erasure is via key destruction (same crypto-shredding pattern); Kurrent itself stores ciphertext. Tombstone-style overwrites within a stream are supported but do not destroy the underlying cipher-text — the same dual nature as Kafka's tombstones. | Backup and replication are Kurrent-native; the team would adopt a new operational toolchain (separate from the PG operational toolchain already in use). RTO/RPO documentable from Kurrent's own guarantees. | Streams are append-only by design; ordering is native; audit trail is durable. | **Pass (conditional)** — application-layer crypto envelope required for PII; the engine must implement the per-subject encryption discipline outside Kurrent rather than relying on field-level mechanisms |
| Redpanda-as-event-store | Same crypto-shredding pattern as ADR-IC-001 F2 for the broker role — application-layer per-subject encryption before publish. Redpanda's compaction tombstones erase the whole record under a key (not per-field); the engine's field-granular GDPR commitment is met only by encrypting fields before publish. Tiered storage (S3-class object stores) extends the cipher-text persistence horizon, which is acceptable for crypto-shredding but worth flagging. | Redpanda's resilience characteristics already accepted under ADR-IC-001 F2. Adding the event-store role multiplies retention obligations (events must persist indefinitely, not just until consumed) and depends on tiered storage. RTO/RPO inherited from the broker tier. | Ordered, durable, immutable by topic; PSD2 audit properties already accepted under ADR-IC-001 F2. | **Pass (conditional)** — indefinite retention via tiered storage required (event topics cannot be retention-bounded); per-field PII crypto required at the application layer |

All three candidates pass both hard filters at POC scale. The conditional passes do not disqualify; they name mitigations carried into Consequences.

---

### Soft criteria

#### PostgreSQL-based event store

**S1 · Operational complexity for 1–2 people.** PostgreSQL is already operated for the outbox ([ADR-IC-004](../../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md)) and the read model ([ADR-IC-005](../../integration_concepts/adrs/ADR-IC-005-cqrs-read-model-storage.md)). Adding the event store as another set of tables in the same database adds *no* new operational surface — the same backup, monitoring, alerting, PITR, replication, and on-call procedures cover it. The team's existing PG familiarity transfers directly. The schema discipline (append-only by application convention, `INSERT`-only, no `UPDATE`/`DELETE` paths in application code) is a code-review concern, not an operational one.

**S2 · Ecosystem coherence.** Maximum. The event store, the outbox, the read model, and the ACL state all live in the same PG ecosystem with the same drivers, the same OpenTelemetry instrumentation ([ADR-IC-007](../../integration_concepts/adrs/ADR-IC-007-observability-stack.md)), and the same connection-pooling configuration. Testcontainers ([ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md)) spins up a real PG instance per test; the same testing infrastructure that validates the outbox validates the event store. The event-append and outbox-write commit in one local transaction without any cross-system orchestration.

**S3 · Exit cost.** Low. The `events` table schema is a standard relational shape (`stream_id`, `sequence_number`, `event_id`, `event_type`, `event_schema_version`, `partition_key`, `valid_time`, `transaction_time`, `causation_id`, `correlation_id`, `actor`, `payload`, plus crypto envelope columns). Any future migration to a different store reads from this table and writes to the new store; no application-layer framework lock-in if the implementation is hand-rolled or uses a thin library. If a heavier framework (Marten, axon-style) is chosen, exit cost rises but the underlying table shape remains portable.

**S4 · Community and longevity.** PostgreSQL has multi-decade stability, foundation governance, the largest contributor base of any relational database, and no licence-change history. The event-sourcing-on-PG pattern is mainstream (Marten, eventstore-pg, eventsourcing-pg, and countless hand-rolled implementations across the financial industry). Longevity risk is the lowest of the three candidates.

**Where this approach requires explicit engineering effort:**

- **Throughput at v4 scale.** Single-node PG sustains write rates well beyond the v4 acceptance test (the Q-AK 250-sustained / 1000-burst targets are within single-node territory on production-shaped hardware with modest tuning — `synchronous_commit`, WAL configuration, autovacuum tuning on the `events` table). Beyond v4 scale, sharding becomes necessary; the reserved `partition_key` field in the envelope ([two-modes §5.3](../feature-design-two-modes-asymmetry.md)) is the v4 escape hatch — declarative-partitioning of the `events` table by `hash(partition_key)`, with citizen-Citus or PG17+ declarative-partition strategies as the migration shape. The v4-readiness commitment from [two-modes §5.1](../feature-design-two-modes-asymmetry.md) requires the sharding path to be named, not implemented in v1; the partition-key reservation satisfies the requirement.
- **Append-only discipline.** PG does not enforce "no UPDATE, no DELETE" at the table level natively (it can be done with triggers and row-level security, but adds complexity). The engine team enforces append-only by application convention — the engine's data-access layer exposes only `append` and `read` operations; direct SQL on `events` is restricted by code review and database role privileges. This is the same discipline already in place for the outbox table.
- **Cold replay performance.** PG sequential reads against the `events` table indexed by `(stream_id, sequence_number)` are well within the §8.2 budgets at v1 and v4 scale (a 1000-event read of one stream completes in well under a second on commodity SSDs; the §8.2 30-second budget is dominated by handler evaluation and projection writes, not by the read). Tested against the Q-AK rig as part of v1 acceptance.

---

#### Kurrent / EventStoreDB

**S1 · Operational complexity.** Adopting Kurrent introduces a second data-tier dependency alongside PostgreSQL. The team would operate two databases — PG for state, outbox, read model, ACL; Kurrent for events — with two backup regimes, two PITR mechanisms, two replication topologies, two upgrade paths. For a 1–2 person team this is a meaningful step up in operational surface. Kurrent's own operational profile is mature, but it is *new* operational surface for this team, and Q-AC explicitly flags "the team has no operational experience with Kurrent" as a concern.

**S2 · Ecosystem coherence.** This is where Kurrent fails most decisively. The engine's commitments require event-append and outbox-write to commit atomically (see Context). With Kurrent holding events and PG holding the outbox, the two writes are in two databases — and [ADR-IC-004 P6](../../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md) explicitly forbids cross-database transactions for exactly this reason. The options to recover atomicity are all costly:

1. **Move the outbox into Kurrent.** Requires re-deciding ADR-IC-004 (the polling publisher would need a Kurrent-subscription-driven equivalent, the outbox semantics would be re-derived from Kurrent stream behaviour, and the engine would couple to two write paths to two systems anyway because domain state still lives in PG).
2. **Move domain state into Kurrent and read projections from PG.** Kurrent does not handle relational state cleanly; the projection layer would need to bridge two stores; this contradicts the event-sourcing-as-source-of-truth shape from [event-store §1](../feature-design-event-store-projections.md) only superficially while creating a real impedance mismatch.
3. **Accept dual-write and use the outbox as the reconciliation log.** This is precisely the failure mode the outbox pattern exists to prevent. Reintroducing it is a regression.

None of the three is acceptable without expanding the scope of this ADR into a re-decision of ADR-IC-004. The cleanest reading is that Kurrent's ecosystem coherence with the existing stack is structurally low because of the outbox commitment, not because of any Kurrent property.

**S3 · Exit cost.** Medium-high. Kurrent's stream API and subscription semantics permeate the engine code if used natively. Migrating away requires translating Kurrent streams to a relational shape and rewriting the handler-dispatch and projection-rebuild paths. The cost is not catastrophic but it is meaningfully higher than the PG case.

**S4 · Community and longevity.** Kurrent is a single-vendor product (the rebrand of EventStoreDB by EventStore Ltd / Kurrent Technologies). The community is smaller than PostgreSQL's by orders of magnitude. The licence has been revised twice in the prior 24 months — a signal of monetisation pressure that [ADR-IC-000 S4](../../integration_concepts/adrs/ADR-IC-000-common-evaluation-criteria.md) flags as a red flag. For a 1–2 person team committing the engine to a multi-decade source of truth, the longevity risk is non-trivial.

---

#### Redpanda-as-event-store

**S1 · Operational complexity.** Redpanda CE is already operated as the event backbone ([ADR-IC-001](../../integration_concepts/adrs/ADR-IC-001-event-backbone-message-broker.md)). Adding the event-store role re-uses the same operational toolchain, which is a positive S1 signal. However, the event-store role requires *indefinite retention* of every event ever published — fundamentally different from the broker's role, where retention is bounded by what consumers need to re-process. Tiered storage (object-store offload) is mandatory; the operational complexity of operating a long-horizon tiered Redpanda topology is materially higher than operating a bounded-retention broker.

**S2 · Ecosystem coherence.** Redpanda-as-event-store *dissolves* the outbox problem — if the event log is the broker topic, there is no dual-write between a domain database and a published stream; the publish *is* the source-of-truth write. This is structurally elegant. But it breaks three other engine commitments:

1. **Bitemporal projections.** [event-store §6](../feature-design-event-store-projections.md) requires every projection to carry `valid_time` and `transaction_time`. The natural place to derive projections is a relational store (PG, per ADR-IC-005). With Redpanda-as-event-store, the projection-rebuild path reads from Redpanda and writes to PG; this is fine. But the *event store itself* cannot answer bitemporal queries — only the projections can. The four time-dimensional capabilities ([event-store §2](../feature-design-event-store-projections.md)) become projection-only, not event-store-supported. This forecloses replay-against-the-store as a primary mechanism.
2. **Per-field PII crypto-shredding.** Kafka/Redpanda compaction tombstones operate per *key*, not per *field*. Field-level erasure ([event-store §6.2](../feature-design-event-store-projections.md)) requires application-layer encryption at every published event — feasible, but the engine carries the full crypto envelope discipline at the boundary rather than in the store. The structural fields the engine reasons about would need to be cleartext-published while PII is ciphertext-published in the same record. Workable, but a strictly higher surface than the PG case.
3. **Snapshot semantics.** Snapshots ([event-store §8](../feature-design-event-store-projections.md)) are per-stream materialised state with a hash-and-verify path. On Redpanda, "per-stream snapshot" is a separate keyed topic by convention; snapshot writes are not transactional with event writes; the verify path is application code. Possible, but more brittle than the PG case where a snapshot is just another table row in the same transaction as the event append.

**S3 · Exit cost.** High. The events would live in Redpanda's wire format and tiered-storage segments; migrating away requires re-consuming the full history through a translator. At v4 scale, "replay the whole bank's history through a translator" is a multi-week operation. The exit cost is the cost of the indefinite-retention commitment itself.

**S4 · Community and longevity.** Redpanda is open-source CE under Apache 2.0; the ecosystem is the Kafka ecosystem. Longevity inherited from ADR-IC-001 S4 — positive. The *pattern* of Kafka-as-event-store is used by some modern fintechs but is not the mainstream choice for event-sourced systems with the engine's specific commitments (bitemporal, field-level PII, snapshots).

---

## Decision

**Chosen: PostgreSQL-based event store.**

The decisive force is not throughput, not licence, not community — all three candidates pass those bars at POC scale. The decisive force is the **outbox co-location commitment from [ADR-IC-004 P6](../../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md)** combined with the **bitemporal-projections + field-level-PII-crypto + snapshot commitments from [event-store §6 and §8](../feature-design-event-store-projections.md)**. PostgreSQL is the only candidate that satisfies all four without forcing a re-decision of one of those commitments.

Specifically:

- Kurrent satisfies the event-sourcing semantics natively but cannot co-locate with the outbox in a single transaction without expanding the scope of this ADR into a re-decision of ADR-IC-004. The team's lack of Kurrent operational experience and the licence-revision history compound the cost.
- Redpanda-as-event-store dissolves the outbox problem but trades it for three new problems: bitemporal queries move out of the store, field-level PII crypto-shredding moves entirely into the application layer, and snapshot transactionality is lost. Each is workable in isolation; together they amount to a different engineering shape than the brief committed to.
- PostgreSQL meets the four invariants natively: event-append and outbox-write commit in one local transaction; the envelope shape including `partition_key` is a standard column set; forward-only schema discipline is application-level (the same as it would be for any of the candidates); snapshots are rows in another table in the same database.

The throughput question — the only quantitative dimension on which PG is potentially weaker than the alternatives — is answered by the synthetic v4-scale load test ([Q-AK](../feature-design-two-modes-asymmetry.md)) at v1 acceptance. Modern PG on production-shaped hardware sustains the test's 250-TPS-sustained and 1000-TPS-burst targets comfortably for the event-store write path. The v4 sharding path (`partition_key`-keyed declarative partitioning, with a future migration to PG17+ native logical sharding or Citus if scale demands) is named and not foreclosed — satisfying the [two-modes §5.1](../feature-design-two-modes-asymmetry.md) "clear scale path" requirement.

The choice of *library or framework* on top of PostgreSQL — hand-rolled module, Marten (if the engine runtime is .NET), eventsourcing-pg (Python), or an equivalent — is deferred to v1 build (coordinated with [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md): engine implementation language). The decision here is the technology, not the library. The envelope, the table contract, and the four invariants are framework-agnostic.

---

**Rejected: Kurrent / EventStoreDB**

The decisive reason is **outbox co-location**: Kurrent cannot host the [ADR-IC-004](../../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md) outbox table in a way that lets event-append and outbox-write commit atomically. Recovering atomicity would require either expanding the scope to re-decide ADR-IC-004 (move the outbox into Kurrent) or accepting cross-database dual-write (the failure mode the outbox pattern exists to prevent). Neither is acceptable.

Two secondary reasons that would matter even if outbox co-location were not the binding constraint: the team has no Kurrent operational experience (compounding S1 cost in a 1–2 person team), and the Kurrent licence has been revised twice in the prior 24 months in a direction that constrains future use (the F1 conditional pass flags this; S4 longevity inherits the same signal).

If the integration architecture were ever to relax the ADR-IC-004 co-location constraint — e.g. by adopting an event-sourcing-native outbox shape inside an event-store-native database — Kurrent would re-enter consideration. That is a future ADR-IC amendment, not a current ADR-PC decision.

**Rejected: Redpanda-as-event-store**

The decisive reason is the **combined cost of moving bitemporal queries out of the store, field-level PII crypto entirely into the application layer, and snapshot transactionality off the event-append path.** Each can be engineered individually; the combination amounts to a different engineering shape than the [event-store](../feature-design-event-store-projections.md) commitments name. The structural elegance of "the publish is the source-of-truth write" is real, and at a different combination of constraints (no bitemporal requirement, simpler PII story, no per-stream snapshots) it would be the right answer. Under the actual engine commitments, the cost exceeds the elegance.

Two secondary concerns: indefinite retention via tiered storage adds operational complexity to Redpanda's role (Redpanda was selected under ADR-IC-001 for bounded-retention broker semantics, not indefinite-retention event-store semantics), and the exit cost of moving the bank's full event history out of Redpanda's tiered-storage format is the highest of the three candidates.

---

## Consequences

**What this choice makes easier:**

- The event store, the outbox, the read model, and the ACL state all live in the same PostgreSQL ecosystem. One database technology, one operational toolchain, one set of backups, one PITR mechanism, one observability surface. The 1–2 person team operates one data tier, not two or three.
- Event-append and outbox-write commit in one local PostgreSQL transaction. The dual-write problem the outbox pattern exists to solve does not arise here — it is solved by being in the same transaction, not by a delivery mechanism on top of two stores.
- Bitemporal projection storage ([ADR-PC-002](../04-open-questions.md)) and event-store storage share a database, so projection rebuilds read events and write projections without crossing a system boundary. Per-field PII crypto-shredding ([ADR-PC-004](../04-open-questions.md)) is naturally column-level and compatible with PG's native expression-index machinery.
- Snapshots ([ADR-PC-003](../04-open-questions.md)) are rows in a `snapshots` table in the same database. A snapshot write can be transactional with the event append if the engine chooses, or eventually-consistent if it prefers (the design space remains open without the technology constraining it).
- Cold replay reads against the `events` table use the standard PG query path; the §8.2 budgets at v1 (5s for ~24–260 events) and v4 (30s for ~250–1000 events) are met with significant margin on commodity hardware.
- Testcontainers ([ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md)) already spins up real PG instances per test class; the event-store testing fidelity is the same as the outbox testing fidelity, and the projection-rebuild drills ([event-store §7.2](../feature-design-event-store-projections.md)) run against the same fixture.

**What this choice makes harder or impossible:**

- **Native event-sourcing primitives** (built-in subscriptions, server-side projections, stream-replay APIs) are not available — the engine implements them in application code. This is the same discipline already in place for the outbox; the cost is small at POC scale and bounded by the engine team's familiarity with the patterns.
- **Sharding at v4 scale is real engineering work**, not an off-the-shelf operation. The path is named (`partition_key`-keyed declarative partitioning; Citus or PG17+ logical sharding as the migration shape) but the implementation is a v4 deliverable, not a v1 deliverable. The reserved envelope field makes the path possible; the actual sharding is paid for when v4 demands it.
- **Multi-region active-active replication** is harder on PG than on systems designed for it. The engine's v1 deployment is single-region (per [01 §6 Deployment](../01-product-architecture.md)); a future cross-region commitment would require [ADR-PC-005](../04-open-questions.md) (DR/RTO/RPO) to address the gap, potentially via per-region engines with cross-region event replay rather than synchronous multi-master.
- **Subscriptions to event streams from external consumers** go through Redpanda (the outbox publishes to topics consumers subscribe to), not directly against the event store. This is already the engine's architecture; the consequence here is that the PG `events` table is *not* a public interface — consumers see the published topic, not the underlying table. The decoupling is intentional.

**Residual risks:**

- **Throughput at the v4 acceptance test must be validated empirically.** The reasoning above asserts that PG handles 250-TPS sustained and 1000-TPS burst on production-shaped hardware; the [Q-AK load test](../feature-design-two-modes-asymmetry.md) is what proves it. If the test fails on the chosen PG topology, the path forward is tuning (synchronous_commit, WAL configuration, hardware sizing) rather than re-deciding the technology. The acceptance gate is part of v1, not deferred.
- **Append-only discipline relies on application convention plus database-role privileges, not on table-level enforcement.** A buggy engine PR that issues `UPDATE events` or `DELETE FROM events` can violate the source-of-truth commitment silently. Mitigation: the engine's database role has no `UPDATE` or `DELETE` privilege on the `events` table; only the `INSERT` privilege is granted. Schema migrations that need to alter the table run under a separate, more privileged role used only by migration tooling. CI lints reject application code that constructs `UPDATE events` or `DELETE FROM events` SQL strings.
- **PostgreSQL major-version upgrades** require careful coordination with the indefinite-retention commitment. The events written under PG 16 must be readable under PG 17, PG 18, and so on. PG's binary compatibility for table data across major versions is strong, but the upgrade procedure (`pg_upgrade` or logical-replication-based cutover) must be tested as part of [ADR-PC-005](../04-open-questions.md) DR planning. The 10-year-plus retention horizon of the event log is longer than any single PG major-version's support window.
- **Library/framework choice on top of PG** (Marten vs eventsourcing-pg vs hand-rolled vs other) is deferred and could re-introduce coupling. The decision is bounded by the table-shape contract in Implementation Principles below — any library that exposes a compatible `events` table can be replaced by another that does. The decision sits with [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md).

---

## Implementation Principles

The PostgreSQL choice is decided here; the table-level contract that makes the choice portable is recorded here so future implementations on PG (and any future migration off PG) read the same shape. These principles are PG-specific; the framework-agnostic disciplines (envelope shape, forward-only schemas, handler purity) live in [event-store §4–§5](../feature-design-event-store-projections.md) and are not re-stated here.

### P1 — The `events` table shape is the integration boundary

The PG-resident `events` table carries the columns below across every engine deployment. Library implementations (Marten, eventsourcing-pg, hand-rolled) may add internal columns, but must expose the contract columns by these names so projection rebuilds, cold-replay tooling, and any future migration tooling can read the table directly.

| Column | Type | Purpose |
|---|---|---|
| `event_id` | UUID, PK | Stable event identifier (per [event-store §4.3](../feature-design-event-store-projections.md)) |
| `stream_id` | UUID, NOT NULL | The instance the event belongs to (e.g. `deposit_id`) |
| `sequence_number` | BIGINT, NOT NULL | Per-stream monotonic; unique `(stream_id, sequence_number)` |
| `event_type` | VARCHAR, NOT NULL | Fully qualified, e.g. `term_deposit.DepositConstituted` |
| `event_schema_version` | INTEGER, NOT NULL | Monotonic per `event_type` |
| `family` | VARCHAR, NOT NULL | Routing key (per [event-store §4.3](../feature-design-event-store-projections.md)) |
| `partition_key` | UUID or VARCHAR, NOT NULL | Reserved per [two-modes §5.3](../feature-design-two-modes-asymmetry.md); v1 sets equal to `stream_id`; v4 may set otherwise |
| `pack_version` | VARCHAR, NOT NULL | Pack pinned at the instance (per [01 §5](../01-product-architecture.md)) |
| `schema_version` | VARCHAR, NOT NULL | Family-schema pinned at the instance (per [authoring §6](../feature-design-configuration-authoring.md)) |
| `valid_time` | TIMESTAMPTZ, NOT NULL | When the fact was true |
| `transaction_time` | TIMESTAMPTZ, NOT NULL DEFAULT clock_timestamp() | When we recorded it |
| `causation_id` | UUID, nullable | `event_id` of the causing event |
| `correlation_id` | UUID, nullable | Saga correlation per [integration_concepts §08](../../integration_concepts/08-event-catalog-governance.md) |
| `actor` | VARCHAR, NOT NULL | Who or what initiated the event |
| `payload` | BYTEA | Avro-serialized payload (schema per [ADR-IC-002](../../integration_concepts/adrs/ADR-IC-002-schema-format-and-registry.md)) |
| `payload_schema_id` | INTEGER, NOT NULL | Schema-registry ID, embedded at write time per [ADR-IC-004 P3](../../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md) |

PII fields live inside the Avro `payload` as ciphertext under per-subject keys; the field-level crypto envelope is specified in [ADR-PC-004](../04-open-questions.md).

### P2 — Event append and outbox write commit in one transaction

Every engine code path that appends an event also inserts the corresponding outbox row in the **same** PostgreSQL transaction. This is the local-atomicity guarantee that [ADR-IC-004 P6](../../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md) requires. No engine code path may append an event without also writing the outbox row, and no code path may write the outbox row without an event. The engine's data-access layer exposes a single `append(stream_id, events, outbox_rows)` operation; lower-level constructors of the event-append SQL are private to that layer.

### P3 — Append-only enforced by role privileges, not by trigger

The engine's application database role has `INSERT` and `SELECT` on `events` and `outbox`; it does **not** have `UPDATE` or `DELETE`. Schema migrations run under a separate, more privileged role used only by the migration tool. This makes "no UPDATE / no DELETE" enforceable at the database boundary regardless of application-code correctness. The role is provisioned at database setup time, not at runtime.

### P4 — Indexing supports cold replay and v4 sharding without committing to either

Two indices on the `events` table from day one:

- **`events_stream_seq_idx`** — UNIQUE `(stream_id, sequence_number)`. The PK; supports per-stream cold replay in order.
- **`events_partition_key_seq_idx`** — `(partition_key, sequence_number)`. Non-unique; supports future v4 sharding by `partition_key` and partition-level operations. At v1 traffic this index carries low cost and is the seam that makes sharding non-breaking when v4 demands it.

A third index — `(family, valid_time)` — is created if projection-rebuild query patterns require it; the decision is deferred to ADR-PC-002 to keep this ADR scoped to technology.

### P5 — Major-version upgrades are tested as a planned operation, not as an emergency

The event log's retention horizon is longer than any single PostgreSQL major-version support window. A `pg_upgrade` (or logical-replication-based) drill runs on a production-shaped clone of the database as part of the projection-rebuild drill cadence ([event-store §7.2](../feature-design-event-store-projections.md)) every 6 months. The drill ends with a full projection rebuild from the upgraded events table; divergence from the pre-upgrade projections is a release blocker. [ADR-PC-005](../04-open-questions.md) (DR / RTO / RPO) carries the upgrade procedure as part of the operational runbook.

---

## Verifiable commitments

This decision's load-bearing commitments are fitness functions in the [commitment catalogue](./commitment-catalogue.md) — the single source of truth for each commitment's exact claim, gate (pyramid level), and `Live`/`Planned`/`Gap` status ([ADR-PC-020 §P5–§P7](./ADR-PC-020-llm-toolchain-and-conformance-governance.md)):

- `ES_ATOMIC_APPEND_OUTBOX` — atomic event-append + outbox write (§P2).

---

## Amendment — 2026-05-23: Library choice filled — hand-rolled module

Per the Decision section, the choice of library or framework on top of
PostgreSQL was deferred to v1 build. [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md)
selects the engine implementation language as C# (.NET 9) and resolves the
deferred question as a **hand-rolled thin event-sourcing module** on
PostgreSQL — *not* a consolidator framework. This is Candidate A's
"library **or hand-rolled** implementation" option, taken on the hand-rolled
branch. The companion ADRs are [ADR-PC-006](./ADR-PC-006-cue-schema-language.md)
(CUE family-schema language) and [ADR-PC-007](./ADR-PC-007-signed-yaml-oci-pack.md)
(pack manifest format).

This amendment fills the deferred library choice within ADR-PC-001's existing
Decision (PostgreSQL-based event store). It does **not** supersede this ADR.
The four invariants (P1 envelope and `events`-table contract, P2 atomic
append + outbox, P3 append-only by role privilege, P4 indices, P5
major-version-upgrade drill) remain binding — and are now **the specification
the hand-rolled module implements directly**, rather than a contract a library
must be mapped onto. There are no library-internal columns (`mt_*` or
otherwise) to tolerate; the `events` table carries exactly the P1 columns.

Why hand-rolled rather than a framework (full reasoning in
[ADR-PC-010 §Decision](./ADR-PC-010-dotnet-hand-rolled-engine.md)): the engine's
source of truth should be fully controlled for a regulated banking core; the
hand-rolled path carries the **lowest exit cost** (this ADR's S3); and it is the
*least*-friction path through the existing decisions — [ADR-IC-004](../../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md)
already chose a custom polling publisher (the outbox is hand-rolled regardless)
and [ADR-IC-003](../../integration_concepts/adrs/ADR-IC-003-saga-orchestrator.md)
already chose an in-house orchestrator (the saga is hand-rolled regardless).
The [event-store §10.4](../feature-design-event-store-projections.md)
"no in-house build" constraint is honoured under the infrastructure-vs-pattern
reading this ADR already adopts: PostgreSQL is the bought infrastructure;
the thin append/load/project/outbox module is pattern discipline the team owns.

The separate-outbox-table contract from P2 is **preserved and implemented
directly**: the engine's `append(stream_id, events, outbox_rows)` writes event
rows and outbox rows in one local transaction; the [ADR-IC-004](../../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md)
polling publisher reads the outbox table. No framework outbox replaces it, so
no Case-B supersession of this ADR arises. Marten and Wolverine are retained by
ADR-PC-010 as **working reference implementations** of these patterns, not as
runtime dependencies.

---

## Amendment — 2026-05-29: §P3 outbox-privilege scope clarified

Implementing A.1 (the `events` + `outbox` DDL and the append-only role grants)
surfaced an internal inconsistency in §P3. Its Implementation-Principle text
(*"the engine's application database role has `INSERT` and `SELECT` on `events`
and `outbox`; it does not have `UPDATE` or `DELETE`"*) reads, on its own terms,
as a blanket UPDATE/DELETE ban across **both** tables — but the events-scoped
Residual-risk bullet in §Consequences scopes the ban to *"the `events` table … only
the `INSERT` privilege is granted,"* and [ADR-IC-004 §P1/§P2](../../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md)
requires the publisher to flip outbox rows `PENDING → PUBLISHED` (setting
`published_at`), with [§P5](../../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md)
cleanup deleting published rows. A literal blanket ban would make the outbox
undrainable.

This amendment aligns §P3's Implementation-Principle text with the events-scoped
Residual-risk bullet in §Consequences and with ADR-IC-004:

- **The §P3 append-only guarantee is about the `events` log.** On `events` the
  runtime role holds `INSERT` and `SELECT` only — no `UPDATE`, no `DELETE`, no
  `TRUNCATE` — and that is the source-of-truth invariant.
- **The `outbox` is a work queue, not append-only.** The runtime role holds
  `INSERT`, `SELECT`, and column-scoped `UPDATE (status, published_at)` on
  `outbox` so the in-process publisher ([ADR-PC-010 §P4](./ADR-PC-010-dotnet-hand-rolled-engine.md);
  event-store-skeleton §5.1) can mark rows `PUBLISHED`. The runtime role is **not**
  granted row `DELETE` on `outbox`; the [ADR-IC-004 §P5](../../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md)
  cleanup of published rows runs under the migration/maintenance role, keeping the
  runtime role's mutation surface minimal.

This is an additive clarification of the privilege envelope, not a reversal of the
Decision (PostgreSQL event store) or of the append-only-by-role-privilege principle
itself; it does not supersede this ADR. The events-table guarantee in §P3 and the
events-scoped Residual-risk bullet in §Consequences remain binding and are now
mutually consistent.
