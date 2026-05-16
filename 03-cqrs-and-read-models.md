# Term Deposit System — Integration Architecture
## Document 03: CQRS and Read Models

Separating reads from writes to honour the 500ms requirement.

---

## The Practical Motivation

The motivation comes straight from the requirements. When a client opens the "My deposits" screen, what they expect to see:

- List of active deposits
- For each one: amount, rate, start date, maturity date, accrued interest up to today, next payment, status
- All in sub-500ms

Each of these fields logically lives in a different place: contractual conditions in the Deposits aggregate, balances in the Core, personal data in the CRM, KYC state in Compliance. If each query aggregates everything at runtime — synchronous calls to 4 systems + computations — the 500ms collapse by the third deposit of the user.

The solution isn't "optimize"; it's to change the model. This is where CQRS comes in.

---

## What CQRS Is (and What It Isn't)

**Command Query Responsibility Segregation** = separating the model used to **write** (commands) from the model used to **read** (queries). It is not necessarily two databases. It is not event sourcing (these are distinct concepts, frequently confused). It is not two microservices. It is a **separation of models** that can be implemented at various depths.

| Side | Optimization | Structure | Guarantees |
|---|---|---|---|
| **Write model** | Transactional consistency, business rules | Rich aggregate, normalized | Local ACID, invariants |
| **Read model** | Read latency, consumption format | Denormalized, pre-computed, optimized by query | Eventual consistency |

The key is to assume **both models derive from the same events**, but have **different goals** and therefore **different structures**.

---

## How This Manifests in Your System

On the write side, you live in the `Deposit` aggregate with all the richness we've seen: invariants, rules, state transitions, compensations. Optimized to guarantee correctness. Tables are normalized, there are joins, there is logic.

On the read side, you have **projections** — materialized views, denormalized, sized exactly for the screens they serve. Example:

```
read_model.client_deposits
─────────────────────────────────
client_id              (index)
deposit_id
product_name
amount
rate_anb                  (gross nominal annual rate)
rate_anl                  (net nominal annual rate)
start_date
maturity_date
days_to_maturity        (pre-computed)
accrued_interest_today  (pre-computed)
next_payment_date
next_payment_amount
status
last_updated
```

A query `SELECT * FROM client_deposits WHERE client_id = ?` returns everything the screen needs, without joins, without aggregations, without calls to other systems. Latency: <50ms typically, with an index. Plenty of budget left for authentication, network, and rendering — you fit comfortably within the 500ms.

---

## The Relationship with the Event Backbone

Read models are not a magical copy of the write model. They are **fed by the integration events** we are already publishing (Primitive 2). When the aggregate emits `DepositConstituted`, a **projector** subscribes to that event and updates the `client_deposits` table.

```
Deposit Aggregate (write)
   │
   ├─ Local ACID
   │
   ├─ emits integration event via Outbox
   │
   ▼
Kafka (backbone)
   │
   ├─────────────┬─────────────┬─────────────┐
   ▼             ▼             ▼             ▼
Projector A    Projector B   Reporting   Notifications
   │             │              ...           ...
   ▼             ▼
Read model 1  Read model 2
(client        (upcoming
 deposits)      maturities)
```

Notice: **each read model has its own projector**, dedicated, independent. There is no "the read model" — there are **N read models**, each designed for a specific use case.

---

## Read Models Are Designed by Query, Not by Entity

This is the mental inversion that distinguishes well-done CQRS from CQRS-called-CQRS-but-just-cache. You don't design read models starting from "entities" — you design them from the **queries you need to serve**.

For your system, real queries you'll need:

| Query | Dedicated read model |
|---|---|
| "The deposits of client X" | `deposits_by_client` |
| "Deposits maturing in the next 30 days" (for notifications) | `upcoming_maturities` |
| "History of interest payments for deposit Y" | `interest_history_by_deposit` |
| "Quick simulation with current rates for product Z" | `product_catalog_for_simulation` |
| "For the detail screen of deposit X" | `deposit_detail` (fully denormalized) |
| "For BdP reporting: aggregated positions by product/term" | `aggregated_positions` |

Each one is a **different** table (or set of tables), optimized for that specific query, updated by **one or more projectors** subscribing to **relevant events**.

Data duplication? Yes, and intentional. The cost of storage is trivial compared to the cost of runtime-aggregation latency.

---

## Eventual Consistency and How to Manage It Honestly

The read model is not always consistent with the write model. There is a window — typically 100ms-2s, depending on the pipeline — between the event being published and the projection being updated.

### Real Implications

**Read-your-own-writes.** The user constitutes a deposit and immediately goes to the "My deposits" screen. If the read model hasn't updated yet, the deposit doesn't appear. Frustration guaranteed. Solutions:

1. **Read-your-writes via write model.** For the immediate post-command case, the frontend reads from the write model (slower, but consistent). For general listings, it reads from the read model.
2. **Optimistic version on the client.** The command response returns the projected state; the frontend shows it before the real projection is ready. When it arrives, it reconciles.
3. **Wait for projection.** The command API only responds when the main projection is updated. Sacrifices latency for perceived consistency. In some flows it's worthwhile.

**Explicit staleness.** In cases where the client makes financial decisions (early mobilization), showing stale data is risky. For those screens, you either read from the write model, or accept the higher latency to guarantee freshness. **Product decision, not technical.**

**Periodic reconciliation.** Job that verifies drift between write and read. In well-built systems it's just confirmed paranoia; but it's necessary paranoia.

---

## Where This Lives — Technology Options

The read model doesn't need to live in the same DBMS as the write. In fact, often it shouldn't. Some common choices:

- **Write side**: Postgres / SQL Server / Oracle — transactional consistency, joins, referential integrity
- **Read side**:
  - Postgres with separate tables (simpler, sufficient for most cases)
  - Redis (sub-10ms latency, ideal for hot-path queries like "deposits of the client")
  - Elasticsearch (complex queries, full-text, aggregations)
  - Read replicas with different schema (for reporting)

In greenfield, **the recommendation is to start simple**: Postgres for both, separate schemas, projectors populating dedicated tables. Only introduce specialized technologies when a specific query doesn't meet SLA with Postgres. **Don't prematurely optimize the read side** — the operational complexity of maintaining Redis/ES/etc. is real and grows silently.

---

## Projectors — the Piece That Deserves Care

Projectors are stateless code that consume events and update read models. They look simple; they have pitfalls.

### Idempotency

We saw this in Primitive 5: at-least-once delivery means duplicated events. If the handler is naive:

```sql
UPDATE client_deposits SET accrued_interest = accrued_interest + ?
```

…you've duplicated the interest. In production. In real money.

Each projector maintains a `processed_events (message_id)` table or uses `UPSERT` with `WHERE last_event_offset < current`. Without this, counters duplicate, accrued interest skyrockets.

### Order

Inside a Kafka partition, order is guaranteed. Across partitions, it is not. If your read model depends on order between different deposits of the same client, you partition by `client_id`. If it depends on order within the same deposit, you partition by `deposit_id`. **A partitioning decision is a consistency decision.**

### Rebuild

The ability to **recreate** the read model from scratch, replaying events from the beginning, is non-negotiable. Three reasons: bugs in the projector (you fix the code and re-project), evolution of the read model (a new column that needs to be computed from history), corruption recovery. For this, Kafka must retain events long enough (compacted topics, or tiered storage). Alternatively, a **dedicated event store**.

### Lag Monitoring

How far behind is the projector compared to the tail of the topic? This metric is one of the first symptoms of problems in production and must always be exposed.

---

## The "Lite" Version for Greenfield — What I Recommend Starting With

Full CQRS is tempting but heavy. Pragmatic suggestion to start:

1. **Rich write side** (aggregate, rules, invariants), Postgres, normalized schema.
2. **Integration events via Outbox** to Kafka. We already have this from the general architecture.
3. **Read models as separate Postgres tables** in the same cluster (distinct schemas). No Redis, no ES, at first.
4. **Projectors as isolated processes** (consumer workers), each responsible for a read model. Independent, scalable.
5. **For latency-critical queries**, optimize with aggressive indexes in the read model.
6. **For queries that exceed SLA**, **then** introduce Redis or ES, case by case. Decision informed by metrics, not anticipated.

This gives you 80% of CQRS's value at 30% of the complexity. The rest evolves as you learn.

---

## Relationship to Everything Before

CQRS closes the shape of the system:

- **Command vs event** (Primitive 1) is the literal foundation: commands attack the write model, events feed read models
- **Domain vs integration** (Primitive 2) is what makes projectors stable: they subscribe to public, versioned events, not volatile internal events
- **Aggregate** (Primitive 3) is where the write model lives, with all its local consistency
- **Identity** (Primitive 4) propagates through the read models — the `correlation_id` that originated a deposit is recorded in the projection, fundamental for cross-system debugging
- **Idempotency** (Primitive 5) is what makes projectors robust to duplication
- **Compensation** (Primitive 6) generates its own events (`DepositCancelled`, `MobilizationExecuted`), which projectors consume like any other — the read model naturally reflects compensations without special code

The 500ms become possible because the read model **has already done all the hard work in the background**. At the moment of the query, it is literally an indexed `SELECT`.
