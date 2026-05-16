# Banking Ecosystem — Integration Architecture
## Document 01: The Six Primitives

These six primitives are the conceptual foundation of the architecture. Everything else (Outbox, Inbox, sagas, ACL, read models, schema registry) is a pattern *built upon* these primitives, not a primitive itself.

---

## Primitive 1: Command vs Event — Two Semantics, Not One

The distinction starts with the semantics of the verb. English makes it clear too:

- **Command** = imperative, present/future. *Constitute*, *Mobilize*, *Renew*. Expresses intent directed at a specific recipient. Can be **rejected**. There is **one** owner that knows how to process it.
- **Event** = past participle. *Constituted*, *Mobilized*, *Renewed*. Expresses an accomplished fact. **Cannot be rejected** (it already happened). Has no recipient; has **subscribers** that react.

### Five Practical Implications That Change the Design

| Aspect | Command | Event |
|---|---|---|
| Routing | Point-to-point | Publish-subscribe |
| Coupling | Knows the destination | Ignores who listens |
| Validation | Pre-execution | Not applicable |
| Failure | Rejection is a valid response | Cannot fail |
| Versioning | Like an API: contract with client | Like historical data: must remain readable forever |

### Where This Most Often Fails in Practice

Teams create "events" with command-like names (`RequestConstitution`) or commands disguised as events (`DepositToConstitute`). The result is a message bus working as distributed RPC — all the complexity of eventing, none of the benefits.

**Naming discipline isn't aesthetic; it's the first line of defence.**

---

## Primitive 2: Domain Event vs Integration Event

Inside the Term Deposits bounded context — the example system — many events will happen. Not all of them leave for the ecosystem.

### Domain Event (internal)

Lives inside the bounded context. Granular, technical, in service of internal logic:

- `InterestDailyReserveCalculated`
- `StateTransitioned(Active → AwaitingMaturity)`
- `EarlyMobilizationPenaltyCalculated`
- `FGDLimitVerified`

They are volatile: refactorings, optimizations, or new rules change them freely. They live in memory, in a local bus, or in an internal table. **They do not cross Kafka.**

### Integration Event (external)

Public contract, stable, versioned, in the schema registry. Meaningful from a business standpoint, not technical:

- `DepositConstituted`
- `DepositEarlyMobilized`
- `DepositMatured`
- `InterestPaid`
- `DepositCancelled`

External consumers (Core, CRM, Compliance, Documentation, Reporting, Notifications) only know this vocabulary. They don't know that internally there were 47 state transitions and 12 intermediate calculations.

### Why This Distinction Is Non-Negotiable

If you publish domain events directly on Kafka, three things happen within 6 months:

1. **You lose the flexibility you asked for.** Each internal refactoring breaks external consumers. Changing internal logic now costs negotiation with 6 teams.
2. **You leak technical language into other people's domains.** The CRM ends up with handlers for `InterestDailyReserveCalculated` — something it shouldn't even know exists.
3. **Implicit coupling.** Subscribers start depending on details (order, granularity, frequency) you never committed to.

Practical rule: inside the context, fine-grained and abundant events; at the boundary, coarse-grained, rare events with unambiguous business meaning. There is a **boundary publisher** that listens to the domain and decides what deserves to be promoted.

### Concrete Example: Deposit Constitution

Internal sequence (domain events):

```
ConstitutionRequestReceived
PreliminaryValidationsExecuted
CapitalReservationRequested
CapitalReservationConfirmed
ContractGenerated
StateTransitioned(Draft → AwaitingApproval)
ApprovalReceived
StateTransitioned(AwaitingApproval → Active)
```

Integration event (single, on the backbone):

```
DepositConstituted {
  depositId, clientId, amount, rate,
  startDate, maturityDate, interestModality,
  creditAccount, ...
}
```

Eight internal events compressed into a single external business fact. Consumers don't want to see *sausage being made* — they want to know what relevant thing happened to them.

---

## Primitive 3: Bounded Context + Aggregate

These two work at different layers, and confusing them is one of the most common sources of problems in distributed systems.

### Bounded Context — the Unit of Ownership

A bounded context is a **linguistic and organizational** boundary. Within it, words have a precise and unique meaning. *Client* in the Deposits context means something different from *Client* in the Compliance context or in the CRM — and that's healthy, not a problem to solve.

In the example ecosystem, the natural contexts are:

- **Term Deposits** (the example application)
- **Core Banking** (accounts, debits, credits, balances)
- **CRM** (client as commercial relationship)
- **Compliance** (client as KYC/AML subject)
- **Workflow** (approvals)
- **Documentation** (artifact generation)
- **Notifications** (channels and deliveries)
- **Reporting** (analytical and regulatory aggregates)

Each has its own model, its own language, its own team, its own release cycle, its own database. **They do not share tables. They do not share domain objects. They communicate only through explicit contracts** (synchronous APIs, integration events, commands).

The question that validates whether a boundary is well-drawn: *"If this team wants to change its internal model tomorrow, does it need to ask anyone for permission?"* If the answer is yes, the boundary isn't in the right place.

### Aggregate — the Unit of Atomic Consistency

Inside a bounded context live aggregates. An aggregate is a cluster of domain objects treated as a unit for consistency purposes. It has a **root** (the entity through which you access it) and invariant rules that **must** always be true within it.

In the example system, the central aggregate is the **Deposit**. Its root is the `Deposit` entity, and inside it live:

- Contractual conditions (amount, rate, term, interest modality)
- Current state (Draft, Active, Matured, Mobilized, Cancelled)
- History of interest payments
- Penalties applied
- Top-ups (if allowed)

Invariants that the aggregate **always guarantees**:

- *An active deposit always has a maturity date in the future or today*
- *The sum of interest paid never exceeds what the contractual formula computes*
- *Cannot be mobilized if already matured*
- *Cannot be cancelled after activation (only mobilized)*

### The Golden Rule of Distributed Consistency

Inside an aggregate: **ACID transaction in the local database**. Total atomicity. If invariants fail, rollback.

Outside the aggregate: **messages, eventual consistency, compensation**. Never a distributed transaction.

This translates in practice as follows:

| Operation | Where consistency lives |
|---|---|
| Calculate interest for deposit X and update accrued balance | Deposit aggregate, local transaction |
| Constitute deposit + debit account in Core | Saga with compensation |
| Early mobilization: apply penalty + release capital in Core | Saga with compensation |
| Approve deposit in Workflow + activate in Deposits | Saga with compensation |

Notice the pattern? **Anything involving more than one bounded context is a saga.** Anything inside one is a classical ACID transaction.

This is the direct technical translation of "compensation, not transactionality" requested at the outset: there is no attempt to extend transactions outside the aggregate. The boundary is hard and intentional.

### Where This Commonly Slips

Three temptations that destroy the primitive:

1. **Aggregates too large.** "The Client is the aggregate, with all its deposits inside." Result: every change to one deposit locks all others of the same client. **Client is not an aggregate of the Deposits context — it's a reference by ID.**

2. **Aggregates referencing each other by object, not by ID.** If `Deposit` has a field `Client client` loaded, you just broke the boundary. Have `String clientId`. The client lives in another context.

3. **Aggregates calling external services in the middle of an operation.** If `Deposit.constitute()` calls Core to debit, you contaminated the aggregate with distributed I/O. The local operation emits intent; the saga outside coordinates the rest.

### Relationship to the ACL

The [Anti-Corruption Layer](./02-anti-corruption-layer.md) is what **translates** between your aggregate's model and the Core Banking model. The `Deposit` talks about "constituting a deposit"; the Core talks about "debit movement on account X with reference Y". The ACL converts both directions, which ensures that changes in the Core don't force refactorings in the domain. **Without an ACL, the Core's vocabulary ends up infiltrating the aggregate, and you lose evolutionary independence.**

### Summary

Bounded context defines **who owns what**; aggregate defines **what must be consistent together**. The first boundary is organizational, the second is transactional. The two together make compensation-instead-of-2PC possible without descending into chaos.

---

## Primitives 4 + 5: Identity and Idempotency

Treated together because they are literally inseparable in practice: idempotency without stable identity does not work, and identity without idempotency is wasted.

### The Identity Trio

Each message that flows through the ecosystem carries three distinct IDs. They are different things and do different things — confusing them breaks observability and debugging.

#### Entity ID — identifies *what*

The stable identifier of the business entity: `deposit_id`, `client_id`, `account_id`. Assigned at creation, never changes, survives everything. Used to correlate all operations on the same entity throughout its lifetime.

#### Correlation ID — identifies *the business flow*

Assigned **once** at the entry point (typically when the user taps the button), and propagated through **all** messages, events, commands, and synchronous calls that result from that interaction. Crosses bounded contexts, crosses external systems, crosses hours (if there are batch steps). It is what allows you, given a problem, to reconstruct the entire film of "what happened in that constitution".

#### Causation ID — identifies *what caused this message*

The ID of the immediate parent message that originated the current one. Forms a tree (not a list) of causality.

### The Practical Difference Between Correlation and Causation

```
User clicks "Constitute Deposit"
  → Command ConstituteDeposit {msg_id: A, correlation: X, causation: -}
    → Event DepositRequested {msg_id: B, correlation: X, causation: A}
      → Command ReserveCapital {msg_id: C, correlation: X, causation: B}
      → Command ValidateKyc {msg_id: D, correlation: X, causation: B}
        → Event CapitalReserved {msg_id: E, correlation: X, causation: C}
        → Event KycValidated {msg_id: F, correlation: X, causation: D}
          → Command ActivateDeposit {msg_id: G, correlation: X, causation: E,F}
            → Event DepositConstituted {msg_id: H, correlation: X, causation: G}
```

`correlation: X` on **all of them**. `causation` points to the immediate parent. You reconstruct the whole tree, see parallelisms, identify where something was lost.

**Why having only correlation isn't enough:** without causation, you know that A, B, C, D, E, F, G, H belong to the same flow — but you can't reconstruct the causal order or identify parallel branches. In sagas with fan-out (validate KYC and reserve capital in parallel), this is critical for debugging.

### Where IDs Are Generated

- **Entity ID**: by the aggregate, at creation time. ULID or UUIDv7 (temporally orderable, important for indexing and partitioning).
- **Correlation ID**: at the edge, in the API gateway or the first service that receives the user's request. If the `X-Correlation-Id` header already exists, respect it; otherwise generate one.
- **Causation ID**: by every message producer, automatically, in the messaging framework. Should not depend on human discipline.

**Rule: these three fields are never optional in any message.** The event schema rejects publication without them. There is no "add it later" — adding correlation IDs retroactively is one of the most thankless tasks in distributed systems.

### Idempotency Key — the Fifth Primitive

Idempotency means: executing the same operation N times produces the same result as executing it once. In distributed systems with unreliable networks, automatic retries, and at-least-once delivery, **it is not optional**.

The *idempotency key* is what allows the receiver to recognize "I've seen this request already". It is distinct from `entity_id`, `correlation_id`, and `message_id`:

- `message_id` changes with each **physical retry** of the same message
- `idempotency_key` stays **the same** between logical retries of the same intent

#### Concrete Example

The user clicks "Constitute" and the mobile app doesn't receive a response (timeout). The user clicks again. From the backend's standpoint they must be treated as **the same operation** — not two deposits.

How this is guaranteed:

1. **The client generates the idempotency key** (the mobile app, the web, the branch). Typically a UUID associated with the button/user intent. The same button pressed twice in 3 seconds uses the same key.
2. **Arrives on the HTTP header** `Idempotency-Key: <uuid>` for synchronous APIs, or in a field of the command for messaging.
3. **The receiver maintains an idempotency store** with `(idempotency_key → result, expires_at)`.
4. On receipt, **before executing**: lookup. If exists → return the stored result. If not → execute, store result, return.
5. Retention window: 24h-7d for synchronous APIs; longer for batch operations.

### Details That Distinguish Naive From Robust Implementation

**Race condition on first-write.** Two requests with the same key arrive in parallel. Solution: the idempotency store is a database with a UNIQUE constraint; the first to insert "wins", the second receives a conflict and returns the first's result. Don't try to solve with application locks.

**Idempotency must cover the complete effect, not just the local DB.** If the call implies publishing an event + writing to the DB, both must be protected by the same key. This is where the [Outbox Pattern](./04-plumbing-patterns.md) becomes mandatory: event and state written in the same transaction, key associated with the transaction.

**Idempotency in event handlers (inbox).** Consumers also receive duplicate messages (at-least-once delivery). Each handler maintains a `processed_messages (message_id)` table and ignores repetitions. Known as the **Inbox Pattern**, symmetric to the Outbox.

**Don't confuse idempotency with retry-safe.** `GET` is naturally idempotent. `POST /deposits` **is not** — it needs an explicit idempotency key. `PUT /deposits/{id}` with a deterministic `id` can be idempotent. But any operation with a side effect (debiting an account, sending an email, generating a document) needs the key.

### The Special Case: Idempotency on the Core via the ACL

The Core Banking probably **does not** offer native idempotency keys — legacy systems rarely do. Result: if you send the same debit instruction twice, it debits twice. The [ACL](./02-anti-corruption-layer.md) absorbs this semantic difference:

- The ACL maintains its own idempotency store (`idempotency_key → core_transaction_id`).
- Before sending to Core, it checks: have I already sent this? If yes, returns the cached `core_transaction_id`.
- If not, sends, records the `core_transaction_id` returned by Core, associates it with the key, returns.

This is **exactly** the kind of semantic friction the ACL exists to resolve. Your domain behaves as if Core were idempotent; the ACL makes it look like it is.

### Practical Summary of These Two Primitives

In each message of the system:
- `entity_id` — which entity
- `correlation_id` — which flow
- `causation_id` — which immediate parent
- `message_id` — which physical instance of this message
- `idempotency_key` — which logical intent (same across retries)

Five fields. Without any of them, you lose a fundamental capability: tracing, ordering, debugging, deduplicating, or safely compensating.

---

## Primitive 6: Compensating Action as a Domain Operation

This is the primitive most frequently misunderstood — and the one that most distinguishes a robust system from a system that looks robust until it fails for the first time in production.

### What Compensation Is Not

Compensation **is not rollback**. Rollback undoes as if it never happened — the database goes back, nobody saw it. Compensation **cannot** do that, because the effect has already gone out into the world: the debit was made in the Core, the event was published, Compliance already recorded it, the client received an SMS.

Compensation **is not exception-catch**. It is not the `finally` at the end of a try. It is not technical cleanup. It is a **new business operation** that **advances the state**, creating a new fact that neutralizes the previous one. The state doesn't go back — it progresses to a place where the previous effect is semantically annulled.

Analogy: if a bank makes a wrong debit, it doesn't "undo" the debit. It makes a **compensating credit**. Both movements remain on the statement. History isn't rewritten; it's continued.

### Why This Matters for Design

If you model compensation as try/catch:
- Lives in the application layer, far from business rules
- Has no explicit preconditions
- Doesn't emit events
- Is not auditable
- Cannot be partial nor conditional
- Cannot evolve with the business

If you model compensation as an **aggregate method**:
- Lives in the domain, with all associated rules
- Has explicit preconditions (`reverseConstitution()` can fail if 5 days have passed)
- Emits its own event (`ConstitutionReversed`)
- Is naturally audited (part of the aggregate's history)
- Can have variants (`reversePartially()`, `reverseWithPenalty()`)
- Evolves like any other business rule

### Concrete Example: Compensation in Constitution

Happy-path saga of constitution:

```
1. Deposit.request()           → DepositRequested
2. ACL → Core.debit()          → CapitalDebited
3. Compliance.register()       → ConstitutionRegistered
4. Workflow.approve()          → ConstitutionApproved
5. Deposit.activate()          → DepositConstituted
```

Imagine step 4 fails (Workflow rejects due to supervening compliance reason). The capital **has already been debited**. Steps 1–3 **have already produced effects in the world**.

The compensation **is not**: "rollback of the debit".

The compensation **is**: a sequence of domain operations, each with its own semantics:

```
4'. Workflow.reject()                 → ConstitutionRejected
3'. Compliance.cancelRegistration()   → RegistrationCancelled
2'. ACL → Core.creditReversal()       → ReversalExecuted
1'. Deposit.cancel(reason)            → DepositCancelled
```

Each `'` is a **new** domain operation, with its own name, its own effect, its own event. The Core doesn't see "rollback of debit" — it sees a **reversal credit operation** with a reference to the original debit. Compliance doesn't see "forget"; it sees "cancel this record with this reason". The client, if they want to see, sees two entries on the statement and a clear explanation.

### Characteristics That All Compensations Must Model

**Preconditions.** Up to when can a constitution be reversed? Probably until the contractual cancellation window expires (in Portugal, generally 14 days of free withdrawal, but it varies for deposits). `reverse()` validates this and may refuse. This rule is **business**, not technical.

**Compensation can fail.** The Core might be offline at the moment of issuing the reversal. What happens then? The saga can't be "stuck in rollback". You need:
- **Retry with exponential backoff** automatically
- **Attempt limit** after which it escalates to human intervention
- **Explicit intermediate state** ("AwaitingCompensation") visible in the system
- **Dead-letter** for unrecoverable cases

Compensation **is never assumed to succeed**. It is another saga, with its own guarantees.

**Compensation can be partial.** If three steps succeeded and the fourth failed, **you only compensate the three that succeeded**. You don't compensate operations that never happened. The orchestration must know exactly which steps were successfully completed — hence the importance of the saga state being **persisted and versioned**, not in-memory.

**Compensation has cost.** Reversing a debit may imply a penalty to the bank (processing costs, accounting position). Reversing a notification send is not possible — you can only send a corrective notification. These costs are **business decisions** and live in the domain.

**Compensation is not always symmetric.** "Send welcome SMS" has no inverse operation. The correct compensation is "send an SMS cancelling the previous one" — semantically different from the original step. Some operations are **fundamentally irreversible** and the saga design must ensure those land **at the end**, after all reversible steps.

### The Saga Ordering Principle: Reversible First

Follows directly from the previous point. When designing a saga, you order steps by **decreasing reversibility**:

1. First, easily reversible steps (internal reservations, holds, validations)
2. Then, steps with costly but possible compensation (debit in the Core)
3. Last, irreversible or semi-irreversible steps (notifying the client, generating a legal document)

Reason: if something is going to fail, fail early, before irreversible steps. Notifying the client *before* the debit is confirmed invites explaining "your deposit was constituted... well, actually not" emails.

### Saga State as Domain Entity

Inevitable consequence of all this: the **saga state is an auditable entity**, not a transient variable. It has its own aggregate (`ConstitutionProcess`), persisted, with explicit states (see the [Constitution Saga walkthrough](./05-constitution-saga-walkthrough.md) for a concrete materialization):

```
Started → Validated → CapitalReserved → ComplianceRegistered 
  → Approved → Active

or

Started → ... → FailedApproval → AwaitingCompensation 
  → ComplianceCancelled → CapitalReversed → Cancelled
```

Each transition emits an event. Each state is queryable. Exception operations (human intervention) attack this aggregate directly. The saga orchestrator **is** this aggregate in action — not a technical object on the side.

### The Relationship to Everything Before

This primitive closes the cycle of the previous ones:

- The **command/event distinction** allows compensations to emit their own events without semantic confusion
- The **domain/integration distinction** ensures external compensation events are explicit contracts (`DepositCancelled` is as public as `DepositConstituted`)
- The **aggregate boundary** ensures local compensation is ACID and distributed compensation is a saga
- The **identity trio** makes the whole saga (happy-path + compensation) traceable as a unit
- **Idempotency** allows each compensation step to be retried safely

Without any of the previous ones, this last one collapses. With all of them, you gain something very specific: **a system in which distributed failures are expected, modelled events, not accidents to hide**.

---

## Final Notes on the Six Primitives

These six primitives, taken together, are the foundation. From here on, everything we build ([Outbox, Inbox, Schema Registry](./04-plumbing-patterns.md), [detailed ACL](./02-anti-corruption-layer.md), [CQRS model](./03-cqrs-and-read-models.md), [concrete saga orchestrator for the Constitution](./05-constitution-saga-walkthrough.md)) **rests** upon them. Nothing that follows is independent of these six.
