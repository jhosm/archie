# Term Deposit System — Integration Architecture

A documentation series covering the integration architecture of a Portuguese banking term deposit management system. The series captures the full design reasoning — from the initial constraints that shaped the architecture, through the conceptual primitives it rests on, down to the concrete patterns, flows, testing strategy, and long-term governance.

The documents are ordered to follow the logic of the design, not alphabetical or historical order. They should be read in sequence.

---

## Context

The system manages the complete operational lifecycle of term deposits in Portugal: constitution, maturity, early mobilization, interest payments, renewal. It integrates with Core Banking, CRM, Compliance, Workflow, Documentation, Notifications, and Reporting.

The system must operate within Portugal's regulatory framework (Banco de Portugal, FGD deposit guarantee schemes, specific tax treatment) and is designed for a greenfield stack — no legacy constraints on the integration infrastructure.

---

## The Three Constraints That Shaped Everything

Before any patterns were chosen, three constraints were fixed. Every architectural decision in the series is traceable to one or more of these.

**Sub-500ms edge response.** When a client taps "Constitute Term Deposit", they see confirmation within 500ms. Coordinating Core + Compliance + CRM + Workflow synchronously to *actually complete* a constitution within 500ms is physically impossible across distributed systems. Therefore the system uses an **optimistic acceptance model**: the edge validates what it can within budget, persists the request, and returns `202 Accepted` with a status stream URL. The saga runs asynchronously; the client receives updates via SSE or WebSocket as each step completes.

**Hybrid saga — orchestration + choreography.** Multi-step flows with complex compensation (constitution, early mobilization, maturity with renewal) are coordinated by a stateful **orchestrator** that knows each step and its explicit compensating action. Fan-out of side-effects without coordination requirements (notifications, reporting, audit, document generation) uses **choreography** — services react to events independently, without a central coordinator.

**Compensation, not transactionality.** Classical 2PC/XA distributed transactions kill flexibility and are often unavailable in Core Banking systems. Compensation is the right trade-off — but *how* it is implemented (idempotency, outbox, compensating actions as domain operations) determines whether the system is actually robust under failure.

---

## The Six Primitives (the Foundation)

Before any infrastructure patterns were introduced, six conceptual primitives were defined. Every subsequent pattern is built on top of these; nothing that follows is independent of them.

### 1. Command vs Event — Two Semantics, Not One

Commands are imperative: *Constitute*, *Mobilize*, *Renew*. They express intent directed at a specific recipient, can be rejected, and have a single owner that processes them.

Events are past participles: *Constituted*, *Mobilized*, *Renewed*. They express an accomplished fact, cannot be rejected (it already happened), have no designated recipient — only subscribers that react.

These two semantics imply different routing (point-to-point vs pub-sub), different coupling, different validation models, and different versioning strategies. Teams that blend them — creating "events" with command names like `RequestConstitution`, or commands disguised as events like `DepositToConstitute` — end up with a message bus operating as distributed RPC: all the complexity of eventing, none of the benefits.

### 2. Domain Event vs Integration Event

Inside the Deposits bounded context, many events occur during a constitution: `ConstitutionRequestReceived`, `PreliminaryValidationsExecuted`, `CapitalReservationRequested`, `ContractGenerated`, multiple `StateTransitioned` events. These are **domain events**: granular, technical, volatile, in service of internal logic. They can change freely with refactorings. They do not cross Kafka.

What external systems (Core, CRM, Compliance, Reporting, Notifications) see is a single **integration event**: `DepositConstituted` with the business-meaningful fields they care about. Eight internal events compressed into one external business fact.

If internal domain events are published directly to the integration backbone, three things happen within six months: (1) every internal refactoring breaks external consumers, (2) technical language leaks into other domains (CRM ends up handling `InterestDailyReserveCalculated`), (3) implicit coupling forms around details never committed to.

The boundary between domain and integration events is maintained by a **boundary publisher**: a component that subscribes to internal domain events and decides what deserves promotion to the integration backbone.

### 3. Bounded Context + Aggregate

The ecosystem has eight bounded contexts: Term Deposits, Core Banking, CRM, Compliance, Workflow, Documentation, Notifications, Reporting. Each owns its model, its language, its team, its release cycle, its database. They communicate only through explicit contracts. They do not share tables or domain objects.

The validation question for a boundary: *"If this team wants to change its internal model tomorrow, does it need to ask anyone for permission?"* If yes, the boundary is wrong.

Inside a bounded context, the **Deposit** aggregate is the unit of local consistency. It enforces invariants: an active deposit always has a maturity date in the future or today; the sum of interest paid never exceeds what the contractual formula computes; a cancelled deposit cannot be mobilized. These invariants are enforced locally, with ACID transactions.

The golden rule: inside an aggregate, local ACID transactions. Outside an aggregate — across bounded contexts — messages, eventual consistency, and compensation. Any operation touching more than one bounded context is a saga. This is the direct technical implementation of "compensation, not transactionality."

### 4. The Identity Trio (Entity ID + Correlation ID + Causation ID)

Each message in the system carries three distinct identifiers.

**Entity ID** (`deposit_id`, `client_id`, `account_id`) identifies *what*: the stable business entity, assigned at creation, never changes.

**Correlation ID** identifies *the business flow*: assigned once at the edge (the API gateway, when the user's request arrives) and propagated unchanged through every message, event, command, and synchronous call that results from that interaction. It crosses bounded contexts, crosses external systems, crosses hours. It is what allows reconstructing the complete film of "what happened in that constitution".

**Causation ID** identifies *what caused this specific message*: the ID of the immediate parent message. This forms a causal tree (not a flat list), enabling reconstruction of parallel branches in the saga and identification of where a message was lost.

Without causation ID, you know that messages A through H belong to the same flow — but you cannot reconstruct the causal order or identify parallel branches. In sagas with fan-out (parallel KYC validation + balance reservation), this is critical for debugging.

These three fields, plus `message_id` and `idempotency_key`, are **never optional** in any message. The schema rejects publication without them.

### 5. Idempotency Key

Idempotency means executing the same operation N times produces the same result as executing it once. In distributed systems with unreliable networks, automatic retries, and at-least-once delivery, this is not optional.

The `idempotency_key` is what allows receivers to recognize "I've seen this intent before". It differs from `message_id` in a critical way: `message_id` changes with each physical retry of the same message; `idempotency_key` stays the same across logical retries of the same intent. When a user double-taps "Constitute" after a timeout, both taps must be treated as one operation — not two deposits.

The Core Banking system probably does not offer native idempotency keys. The Anti-Corruption Layer absorbs this: it maintains its own idempotency store (`idempotency_key → core_transaction_id`) and makes the Core appear idempotent to the domain.

### 6. Compensating Action as a Domain Operation

Compensation is **not rollback**. Rollback undoes as if nothing happened; compensation is impossible because the effect has already gone into the world (the debit was made, the event was published, Compliance recorded it, the client received an SMS). Compensation is also not exception-catch — not a `finally` block, not technical cleanup.

A compensating action is a **new business operation that advances the state**: `cancelConstitution()`, `reverseDebit()`, `releaseKycHold()`. Each has its own name, its own preconditions, its own domain event. The Core doesn't see "rollback of debit" — it sees a reversal credit operation with a reference to the original debit. Both movements remain on the statement.

If compensation is modelled as try/catch: it lives in the application layer, has no preconditions, emits no events, is not auditable, cannot be partial or conditional, cannot evolve. If modelled as an aggregate method: it lives in the domain, has business preconditions, emits its own event, is naturally audited, can have variants (`reversePartially()`, `reverseWithPenalty()`).

**The saga ordering principle**: when designing a saga, steps are ordered by decreasing reversibility. Easily reversible steps (internal reservations, holds, validations) come first. Steps with costly but possible compensation (Core debit) come next. Irreversible or semi-irreversible steps (notifying the client, generating a legal document) come last. If something is going to fail, fail early.

---

## Document Map

| # | Title | What It Covers |
|---|---|---|
| [00](./00-introduction-and-decisions.md) | Introduction and Foundational Decisions | Context, the three driving constraints, high-level architectural shape |
| [01](./01-the-six-primitives.md) | The Six Primitives | Detailed treatment of all six primitives above |
| [02](./02-anti-corruption-layer.md) | Anti-Corruption Layer | Seven ACL responsibilities, internal structure, the indeterminate-state problem, antipatterns |
| [03](./03-cqrs-and-read-models.md) | CQRS and Read Models | Read/write model separation, projectors, eventual consistency management, the greenfield-pragmatic starting point |
| [04](./04-plumbing-patterns.md) | Plumbing Patterns | Outbox, Inbox, Schema Registry, delivery guarantees — the mechanics that make events reliable |
| [05](./05-constitution-saga-walkthrough.md) | Constitution Saga Walkthrough | All primitives and patterns materialized in a real constitution flow, with concrete IDs, timings, and compensation paths |
| [06](./06-observability-and-tracing.md) | Observability and Distributed Tracing | Three pillars (logs/metrics/traces), OpenTelemetry, the concrete trace of a constitution, what to instrument and alert on |
| [07](./07-testing-strategy.md) | Testing Strategy | Adapted test pyramid for event-driven systems: aggregate unit tests, integration with testcontainers, contract tests (Pact), saga tests, selective E2E |
| [08](./08-event-catalog-governance.md) | Event Catalog Governance | Four governance pillars, ownership model, naming conventions, review process, the living catalogue |
| [09](./09-long-term-schema-evolution.md) | Long-term Schema Evolution | Taxonomy of compatible/incompatible changes, concrete techniques for each, antipatterns, real scenarios |

---

## The Anti-Corruption Layer

The ACL is the boundary between the Deposits domain and Core Banking. It exists because the Core's model, vocabulary, and protocol would contaminate the rest of the system without isolation.

The reading test: *"If the Core vendor were replaced tomorrow, how many files would change?"* Healthy answer: only ACL files.

The ACL has **seven concrete responsibilities**:
1. **Semantic translation** — `EarlyMobilization` → partial reversal + interest adjustment + release
2. **Protocol translation** — the domain speaks REST/JSON; the Core may speak SOAP, MQ, or batch files
3. **Adapted idempotency** — the ACL maintains its own `(idempotency_key → core_reference)` store so the Core *appears* idempotent
4. **ID mapping** — `deposit_id` ↔ `core_txn_id` persisted for cross-system traceability
5. **Semantic translation of errors** — `ERR-2317` → `InsufficientBalance` (recoverable) or `AccountBlocked` (non-recoverable) or `TransientUnavailability` (retry)
6. **Latency adaptation** — transforms Core's overnight-batch processing into a clean async interface for the domain
7. **Periodic reconciliation** — daily batch job that crosses the domain's view of the Core with what the Core actually has; in banking, divergences discovered months later are material losses

The hardest case: **indeterminate state**. A debit is submitted; the network drops before a response arrives. The ACL must not assume "error = nothing happened" — that assumption causes double debits on retry. The correct sequence: record `status=IN_FLIGHT` before sending, update to `INDETERMINATE` on timeout, query the Core by external reference to determine what actually happened, only then decide to confirm or retry. The saga explicitly knows the `INDETERMINATE` state exists.

The ACL is **owned by the Deposits team**, not the Core team. The Core team maintains the Core's technical contract; the Deposits team maintains the translation between that contract and its domain.

---

## CQRS and Read Models

The 500ms SLA cannot be met by querying Core + CRM + Compliance at runtime for each screen render. The solution is read models — denormalized, pre-computed projections fed by integration events.

Each read model is designed **by the query it serves**, not by the entities it maps. Examples: `deposits_by_client` (the "My Deposits" screen), `upcoming_maturities` (notification jobs), `interest_history_by_deposit` (deposit detail), `aggregated_positions` (BdP reporting). Each is a separate table, populated by a dedicated projector subscribing to relevant integration events.

Read models are eventually consistent. The propagation window is typically 100ms–2s. For the "read your own writes" problem — user constitutes a deposit and immediately navigates to "My Deposits" — three options exist: read from the write model for the immediate post-command case, use optimistic UI (the command response returns the projected state before the projection updates), or wait for projection confirmation before responding. This is a product decision, not a technical one.

Projectors must be idempotent (at-least-once delivery from Kafka means duplicated events; naive `UPDATE accrued_interest = accrued_interest + ?` in a projector doubles real money). Projectors must be **rebuildable from scratch** — events must be retained long enough to replay; the ability to re-project is non-negotiable for fixing bugs in projectors and for schema evolution.

---

## Plumbing Patterns

Four patterns that make the primitives reliable in production:

**Outbox.** The dual-write problem: if the application writes to the DB and then publishes to Kafka as separate operations, the DB write can succeed while the Kafka publish fails — the deposit exists but no other system knows. The outbox writes the event to a table in the same database as the state, in the same transaction. A separate relay process reads `PENDING` outbox rows and publishes them. Either both DB state and event are committed, or neither is. At-least-once publication; consumers resolve duplicates with the Inbox.

**Inbox.** Each event consumer maintains a `processed_messages (message_id)` table. Before processing, it checks whether the message was already handled. The inbox insert and the business logic execute in the same local transaction — if two threads race on the same event, one wins by PK constraint, the other is ignored.

**Schema Registry.** Every integration event has a registered, versioned schema (Avro or Protobuf). The registry enforces compatibility rules: adding optional fields with defaults is free; removing fields requires deprecation first; renaming fields is not done — a new field is added and the old one deprecated. Incompatible changes (type changes, semantic changes) require a new major version of the event coexisting with the previous one until consumers migrate. This is enforced mechanically at publication time.

**Delivery guarantees.** The system does not achieve exactly-once delivery — distributed exactly-once is expensive and fragile. It achieves **at-least-once delivery + idempotency at the consumer = effectively-once observable behaviour**. Within a single Kafka partition (partitioned by `aggregate_id`), order is guaranteed. Across partitions, it is not, and consumers must be designed accordingly.

---

## The Constitution Saga — Concrete Walkthrough

Document 05 materializes all patterns in a real flow: client João Silva constituting a €10,000 deposit for 12 months at 2.5% gross nominal annual rate. The walkthrough covers:

- **Step 0 (edge, ~150ms)**: Synchronous idempotency check, light validations, creation of `ConstitutionProcess` (state: `STARTED`) and `Deposit` (state: `DRAFT`) in a single local transaction with the `ConstitutionRequested` outbox event. Returns `202 Accepted`.
- **Step 1**: Outbox publishes `ConstitutionRequested`; orchestrator consumes it and fans out three parallel commands: `ValidateClientEligibility` (Compliance hold), `ReserveAccountBalance` (Core balance hold), `ValidateProductLimits` (local computation). All three are reversible by design — no irreversible effect yet.
- **Steps 2–5**: Parallel validations resolve; orchestrator transitions to `APPROVED`; real debit executes through ACL; Compliance registers definitively; Deposit activates; `DepositConstituted` emitted to the backbone.
- **Async fan-out**: Projectors update read models; Notifications sends the client confirmation; Documentation generates the contract; Reporting updates regulatory aggregates. All independent, all idempotent.
- **Compensation paths**: For each step, the document shows the exact compensating operation if that step fails, with the reversed order (compensation flows backward from the last successful step). The saga state (`ConstitutionProcess` aggregate) records which steps completed successfully, so compensation is always exact.

---

## Observability

Distributed tracing is the **dominant pillar** for this system — sagas crossing multiple services are the canonical use case. A single trace of a constitution spans the API Gateway, Outbox Publisher, Orchestrator, Compliance Adapter, Core ACL, and the async fan-out, with timings on each span. Given a `correlation_id`, the entire flow is visible in Jaeger, Tempo, Honeycomb, or Datadog APM.

OpenTelemetry is the standard. The `correlation_id` maps to the OpenTelemetry trace ID; the `causation_id` maps to the parent span ID. Propagation across Kafka messages requires explicit W3C TraceContext headers in message metadata, not just in HTTP headers.

Key metrics to instrument and alert on: outbox lag (oldest PENDING event age), projector consumer group lag, ACL in-flight and indeterminate state counts, saga duration by step and overall, compensation trigger rate.

---

## Testing Strategy

The test pyramid is adapted for event-driven systems. Two inversions from the traditional shape:

**Contract tests gain disproportionate weight.** In monoliths they are unnecessary; in event-driven systems they are existential. Six systems depending on `DepositConstituted` cannot all run in the same integration suite. Pact-style consumer-driven contracts allow each consumer to specify what fields it uses and at what version; the producer's CI validates its schema against all registered consumers before promoting any change. This makes the schema registry's governance mechanical.

**Saga tests are their own level.** They test the orchestrator's state machine in isolation: given event X in state Y, the orchestrator transitions to state Z and emits commands A, B. They validate the full compensation paths — including partial compensation (only steps that succeeded are compensated). They run against an in-memory or testcontainers implementation of the state store, not production infrastructure.

**Aggregate unit tests** form the rich foundation: thousands of tests, executing in seconds, covering every invariant, every valid and invalid state transition, every financial computation, every compensation precondition. Rich aggregates (no I/O inside) are trivially testable. If mocks are needed to test an aggregate, there is I/O contamination that belongs outside.

**E2E tests** are selective and slow — used to validate golden paths and critical business flows against a full environment. Not the safety net; the smoke signal.

---

## Event Catalog Governance

Integration events are **public API**, more durable than REST APIs. Events persist in retention; old events published months ago can be re-projected when new read models are built. The schema in use six months ago must still be readable. This makes schema mistakes harder to reverse than REST API mistakes — and most organizations treat events with less care than REST APIs, which is why event-driven systems degrade.

Governance rests on four pillars:

1. **Ownership**: each event belongs to the bounded context that produces it. The Deposits team owns all `Deposit*` events. Ownership means maintaining schema, guaranteeing backward compatibility, and coordinating deprecations — not controlling who subscribes.

2. **Conventions**: event names follow past-tense domain verbs (`DepositConstituted`, not `DepositCreated` or `DepositActivated`). Payload fields follow consistent naming. Semantics are documented: when the event is emitted, what business state it represents.

3. **Review process**: new events and incompatible changes go through an explicit review gate — a small cross-team stewardship group (not the owning team alone). This is where the "two similar events for the same concept" problem is caught before it reaches production.

4. **Living catalogue**: beyond the technical schema registry, a documentation catalogue lists every public integration event with its business meaning, producer, known consumers, versioning history, and payload examples. Events are the system's public API; they deserve the same documentation care as REST APIs.

---

## Long-term Schema Evolution

Schema changes fall into four categories:

**Additive compatible** (90% of changes): adding an optional field with a default, adding a new event. Free — no coordination needed. The registry validates automatically.

**Subtractive compatible**: removing an optional field, deprecating a field while keeping it in the payload. Technically compatible, but requires verifying no consumer depends on the field before removing.

**Modifying incompatible**: changing a field type, renaming a field, changing semantics of an existing field. These cannot be done in-place. The strategy is: introduce the new field alongside the old one, produce both for a transition period, deprecate the old field once consumers have migrated, remove after a defined window. The registry blocks incompatible in-place changes mechanically.

**Structural**: splitting or merging events, changing granularity. These are essentially domain model redesigns and require a versioned event (`DepositConstitutedV2`) coexisting with the previous version, with an explicit sunset date for the old one.

The mental model that anchors the discipline: schemas are **archaeological data**. Five years from now, someone will need to interpret an event published today, without the context available today. Design schemas so that interpretation remains possible.

---

## Reading Order

For a reader coming to this series cold, the intended order is:

1. Start with **00** to understand the constraints that shaped the architecture
2. Read **01** carefully — the six primitives are referenced in every subsequent document
3. **02** (ACL) and **03** (CQRS) can be read in either order; both build directly on the primitives
4. **04** (Plumbing) explains *how* the primitives are made reliable in production
5. **05** (Constitution walkthrough) is where everything comes together — the most concrete document
6. **06** through **09** are transversal concerns that apply throughout but are discussed after the architecture is established
