# Banking Ecosystem — Integration Architecture
## Document 04: Plumbing Patterns

The mechanisms that make events reliable.

These patterns solve **real and specific problems** that appear when implementing the primitives. Each pattern exists because, without it, one of the primitives silently collapses. Four are covered here: Outbox, Inbox, Schema Registry, and the package of delivery guarantees.

---

## Outbox Pattern — Solving the Dual-Write

### The Problem It Solves

Imagine the constitution handler:

```
1. INSERT INTO deposits (...)            ← DB
2. publish("DepositConstituted", ...)    ← Kafka
3. return success
```

Four things can happen:
- (a) Both succeed → all good
- (b) DB fails, Kafka never got called → all good (rollback)
- (c) DB succeeds, Kafka fails → **deposit exists without event. The other systems never knew.**
- (d) DB succeeds, Kafka succeeds, but the ack to Kafka is lost → you try again → **duplicate event** (resolvable with idempotency, but problematic if it's "publish + immediately do X")

Scenario (c) is catastrophic. The Core never debits, Compliance never registers, the client is never notified, but the deposit exists in your DB. In banking, this is the worst kind of silent data corruption.

### The Solution

You don't publish events directly. You write the event to a **table in the same database as the state**, **in the same transaction**:

```sql
BEGIN TRANSACTION;
  INSERT INTO deposits (...);
  INSERT INTO outbox (event_id, event_type, payload, status='PENDING', created_at);
COMMIT;
```

Either both succeed, or both fail. Classical local atomicity — no dual-write, no ambiguity.

Then, a **separate process** (relay/publisher) reads continuously from the `outbox` table, publishes to Kafka, and marks as `PUBLISHED`:

```
loop:
  SELECT * FROM outbox WHERE status='PENDING' ORDER BY created_at LIMIT N
  for each row:
    kafka.publish(row.event_type, row.payload)
    UPDATE outbox SET status='PUBLISHED', published_at=NOW() WHERE event_id=...
```

If the publisher crashes mid-way: on the next execution, it resumes. Events can be published more than once (at-least-once) — but never **lost**. The duplication is resolved at the consumer (Inbox, next).

### Implementation: Polling

The relay runs a periodic `SELECT` (every 200ms–1s) on the outbox table, publishes pending events to Kafka, and marks them published. Additional latency: 1× the polling interval. Puts some read pressure on the DB. Works on any DBMS. This is the right implementation — not a compromise while waiting for something better.

**Why not CDC?** Change Data Capture — tools like Debezium that read the database WAL/redo log — is sometimes proposed as an alternative relay mechanism. It is a poor fit for an event-stream-native architecture for a structural reason: **CDC derives events from database side effects, inverting the causal relationship between events and data.**

In an event-sourced system, the event is the cause and the database state is the effect. Your application code explicitly decides that `TermDepositOpened` happened and writes it to the outbox as part of the domain transaction. That outbox row is a pending publication for an event the domain already raised — its structure, name, and semantics are defined by the domain model.

With CDC, this causality is reversed: the relay reads WAL mutations and infers events from storage changes. The consequences are architectural, not just operational:

- **Event semantics are dictated by the storage schema.** A column rename, a table split, or a normalisation refactor changes your events without any domain-level decision being made.
- **Events become database side effects.** There is no longer an explicit moment where the domain says "this business fact occurred." The event is an observation of data, not a declaration of intent.
- **The coupling is invisible.** Application code looks clean; the implicit contract lives in a Debezium connector configuration file no one reviews during schema migrations.

On top of the architectural problem, CDC adds real infrastructure: Debezium requires WAL-level database access (replication slots in PostgreSQL), a Kafka Connect worker cluster, connector configurations, and lag monitoring. This is significant operational weight for a problem polling already solves cleanly.

At typical banking volumes — thousands of operations per day per product, not millions per second — the polling interval (200ms–1s) is never the bottleneck. If it ever were, the right response would be to re-examine the volume assumptions, not to add CDC infrastructure.

### Details That Distinguish a Serious Implementation From a Naive One

**Publication order.** Read with `ORDER BY created_at, event_id` and publish sequentially **per aggregate**. If you publish `DepositConstituted` before `DepositRequested`, you break consumers that assume order. Solution: partition the Kafka topic by `aggregate_id` (in your case, `deposit_id`), and the publisher respects the order within the partition.

**Outbox cleanup.** Published events accumulate. Nightly batch job that moves `PUBLISHED` events to an archive table or deletes after a defined retention (typically 7-30 days). Without cleanup, the table degrades queries.

**Publisher failure.** If Kafka is down, events accumulate in the outbox. Healthy, not a problem. Alert when the `lag` (how old the oldest `PENDING` is) exceeds a threshold. **Never abandon events by timeout** — the outbox is the source of truth.

**Outbox poll worker in HA.** Multiple worker instances simultaneously cause duplicate publications. Resolve with `SELECT ... FOR UPDATE SKIP LOCKED` (Postgres) or leadership via distributed lock. **Architectural decision to take early.**

---

## Inbox Pattern — Solving Duplication at the Consumer

### The Problem It Solves

Kafka guarantees **at-least-once** by default (delivery at least once, possibly more). This is by design — distributed exactly-once is expensive and frequently brittle.

Result: your projector that updates `client_deposits` may receive `DepositConstituted` twice. If the handler is naive:

```sql
UPDATE client_deposits SET accrued_interest = accrued_interest + ?
```

…you've duplicated the interest. In production. In real money.

### The Solution

Each consumer maintains an `inbox` table (or `processed_messages`) in its DB:

```sql
inbox
─────────────────────
message_id (PK)
processed_at
result_summary (optional)
```

The handler processes like this:

```
BEGIN TRANSACTION;
  IF EXISTS (SELECT 1 FROM inbox WHERE message_id=?) THEN
    ROLLBACK; -- already processed, ignore
    return;
  END IF;
  
  INSERT INTO inbox (message_id, processed_at) VALUES (?, NOW());
  
  -- execute business logic: update read model, side effects, etc.
  UPDATE client_deposits SET ...
COMMIT;
```

The `INSERT` with PK on `message_id` is what guarantees atomicity: if two threads receive the same event simultaneously, one wins (insert succeeds), the other fails by constraint violation (and ignores).

### Important Details

**Inbox and idempotency_key are cousins, not twins.** `message_id` deduplicates **physical deliveries** of the same event. `idempotency_key` deduplicates **logical intents** (two clicks of the user). Both are needed. The `message_id` is always in the envelope; the `idempotency_key` is in the payload of commands.

**Side effects outside the DB are a separate problem.** If the handler sends HTTP to an external system, the DB transaction doesn't cover it. Solution: either you make the call **before** the inbox insert (if it's retryable and idempotent on the other side) or you **publish another event** via the consumer's local outbox (outbox → inbox → outbox → ... chain). In pure event-driven systems, this pattern is ubiquitous.

**Inbox retention.** The table grows indefinitely. Policy: keep `message_id` for the window of possible re-delivery (Kafka retention × N). Typically 7-30 days. Cleanup via job.

---

## Schema Registry and Versioning — Keeping Healthy Contracts

### The Problem It Solves

Today you publish `DepositConstituted` with fields `{id, amount, rate}`. In 6 months you need to add `interest_modality`. In 1 year, you change `amount` from integer (cents) to decimal. In 2 years, you decide to split `rate` into `gross_rate` and `net_rate`.

Meanwhile:
- 6 systems consume this event
- Some still have old consumers running in parallel with new ones
- Old events persist in long retention and can be re-projected

Without discipline, you evolve the schema and silently break consumers. In production.

### The Solution

Explicit, versioned schema, validated centrally and mechanically. Typically with Avro or Protobuf in the Confluent Schema Registry (Kafka) or equivalent.

Each event has a registered schema. Before publishing, the producer validates against the schema. Before consuming, the consumer obtains the schema of the right version and deserializes.

### Compatibility Rules — Where the Real Value Lies

The schema registry lets you define **compatibility modes** for each topic:

- **Backward compatible**: new consumers read old events (new field has default, removed field was optional). **What you typically want for producers.**
- **Forward compatible**: old consumers read new events (you only add optional fields). **What you want for consumers in gradual upgrade.**
- **Full compatible**: both. **What you want for safe evolution.**
- **None**: anything goes. Fast track to chaos.

Practical rule: **Full compatible by default**, incompatible changes require **new major version** of the event (`DepositConstitutedV2`) coexisting with the previous one until consumers migrate.

### Concrete Principles for Your System

1. **Adding optional fields with default**: free, no coordination needed.
2. **Removing fields**: never directly — mark as `deprecated`, give consumers a sprint, remove only after.
3. **Renaming fields**: never. Add new, deprecate the old, remove the old later.
4. **Changing field type**: not compatible. Create new field, deprecate the old.
5. **New major version of the event**: coexists with the previous one; consumers migrate at their own pace; old topic eventually closes.

All this is mechanically enforced by the registry at schema publication time. It's not friendly convention; it's a gate.

### The Event Catalog

Beyond the technical schema, I recommend keeping a documental **event catalog**: list of all public integration events, with:
- Name, current version, schema
- Business meaning (when emitted, what it represents)
- Producer (which context)
- Known consumers (informational)
- Versioning policy
- Payload examples

This catalog is as important as the technical registry. In event-driven systems, **events are the public API** of the ecosystem. Document them with the same care you document REST APIs. [Document 08](./08-event-catalog-governance.md) covers event catalogue governance in full — ownership model, naming conventions, review process, and the living catalogue.

---

## Delivery Guarantees — What You Promise and What You Don't

It's worth making explicit the complete package of guarantees of the system, because each one has practical implications.

| Guarantee | How obtained | When sacrificed |
|---|---|---|
| **Not losing events** | Outbox + Kafka with `acks=all` + adequate retention | Never, in banking |
| **Not duplicating effects** | Inbox + idempotency keys | Never, in banking |
| **Order within an aggregate** | Partitioning by `aggregate_id` | Never, in most cases |
| **Order across aggregates** | Not guaranteed | Always. Accept it. Design consumers that don't depend on it. |
| **Sub-second end-to-end latency** | Fast polling + well-dimensioned Kafka | May be violated under load; sagas must tolerate it |
| **Exactly-once delivery** | No. You guarantee **at-least-once + idempotency = effective once** | The right trade-off |

The last line is the most important point. "Exactly-once" is often sold as possible; in practice, it's an expensive and brittle combination, and the alternative (at-least-once + idempotency at the consumer) gives you the **same observable result** with much less complexity.

---

## Security Properties of the Plumbing

The three patterns above have security implications that are easy to overlook because they feel like infrastructure, not application logic.

**Outbox table: sensitive data at rest.** The outbox holds full event payloads — integration events that include `client_id`, account numbers, amounts, and in some cases IBANS. This data sits in the application database until the publisher picks it up, typically for seconds to minutes under normal conditions and up to days during Kafka outages. The outbox table must be treated with the same access controls as the application's primary tables: encryption at rest, restrictive DB roles, and included in the backup/restore security perimeter.

**Kafka topic ACLs: consumers are not implicitly trusted.** Kafka is a shared medium. By default, any service that can connect to the Kafka cluster can subscribe to any topic. In a bounded-context architecture where multiple teams share one cluster, this is not acceptable. Topic-level ACLs must be part of the infrastructure configuration from day one — not added later when there are already ten services sharing the cluster. The rule: a service subscribes only to the topics it needs; it produces only to the topics it owns. ACL configuration lives in the same repository as the service and is reviewed in the same PR. See [Document 10](./10-security-and-threat-model.md) for the full Kafka trust boundary.

**Schema registry access controls.** The schema registry controls the compatibility mode of every topic. A compatibility change from `BACKWARD` to `NONE` on a production topic removes the mechanical guard against breaking changes. This must be an authorized action — not something an individual developer can do in a browser. Schema registration (new schemas and version updates) is performed by the producer's CI/CD pipeline, using a deployment-scoped service account. Humans can read the registry; only CI can write to it. Changing compatibility modes requires the same review process as a breaking schema change.

---

## The Mental Test to Apply to Each Component

Before assuming something works, run it through these four questions:

1. **If the DB is written and the network drops before publishing the event, what happens?** Healthy answer: outbox recovers.
2. **If a consumer receives the same event twice, what happens?** Healthy answer: inbox deduplicates.
3. **If I add a field to the schema, what happens?** Healthy answer: registry allows it (backward compat), old consumers ignore it.
4. **If a consumer is offline for 6 hours and comes back, what happens?** Healthy answer: it consumes all the lost events from the topic, the projection catches up, lag closes.

If any of the four doesn't have a clear and mechanically guaranteed answer, plumbing is missing.

---

## How It All Composes

Visually, a complete end-to-end flow:

```
Command arrives
  │
  ▼
[Idempotency check (write side)]
  │
  ▼
Aggregate loads, validates invariants, transitions
  │
  ▼
┌─ Same DB transaction ────────────┐
│  - UPDATE deposits                │
│  - INSERT outbox (integ. event)   │
└──────────────────────────────────┘
  │
  ▼ (commit OK → response to client)
  
... asynchronously ...

Outbox publisher polling
  │
  ▼
Kafka topic partitioned by deposit_id
  │
  ├──→ Projector "client_deposits"
  │      ├─ Inbox check
  │      └─ UPSERT read model
  │
  ├──→ Projector "upcoming_maturities"
  │      ├─ Inbox check
  │      └─ UPSERT read model
  │
  ├──→ Outbound ACL → Core (via internal command)
  │      ├─ Inbox check
  │      ├─ Idempotency key vs Core
  │      └─ Send to Core, record mapping
  │
  ├──→ Compliance Adapter
  │      ├─ Inbox check
  │      └─ Notify Compliance
  │
  └──→ Notifications Adapter
         ├─ Inbox check
         └─ Notify the notifications system
```

Each arrow has explicit guarantees. Each box has a clear responsibility. Each failure has predictable behaviour.
