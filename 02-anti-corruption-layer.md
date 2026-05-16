# Term Deposit System — Integration Architecture
## Document 02: Anti-Corruption Layer

The Anti-Corruption Layer (ACL) is the boundary that protects your domain.

The ACL is a concept from DDD originally by Eric Evans, but it's usually presented in too abstract a way ("a translation layer"). In practice, it is a piece with very concrete responsibilities and a non-trivial internal structure.

---

## The Essence: Your Domain Never Speaks the Core's Language

The Deposits domain says: *"debit capital for the constitution of deposit X"*.

The Core says: *"accounting debit movement on account Y of €10,000, contra-entry on liability account TD-{type}, value-date D+0, external reference TD-{id}, operation code 4471"*.

Two different languages, two different models, two different universes. The ACL is the **only place** where translation happens between the two. In any other place in your code, this translation is a leak.

**Reading test to verify this:** *if the Core vendor were replaced tomorrow, how many files would change?* Healthy answer: only the ACL ones. Everything else compiles and runs unchanged.

---

## The Seven Concrete Responsibilities of the ACL

These are the real frictions between the two worlds. The ACL exists to absorb all of them — not some.

### 1. Semantic Translation

Conversion of concepts: `Deposit` → set of accounting movements; `EarlyMobilization` → partial reversal + interest adjustment + release; `InterestPayment` → debit movement on the liability account + credit on the current account. This translation can be far-from-1-to-1: a domain operation may result in N operations in the Core, and vice-versa.

### 2. Protocol Translation

Your domain speaks REST/JSON internally. The Core may speak SOAP, MQ, batch files, or an unhappy combination of the three. The ACL is where this is solved. The rest of the system never sees a WSDL.

### 3. Adapted Idempotency

[Primitive 5](./01-the-six-primitives.md) covered the essence. The ACL maintains its store `(idempotency_key → core_reference)` and makes the Core *appear* idempotent to the domain.

### 4. ID Mapping

The domain has `deposit_id = "DEP-2026-00012345"`. The Core returns `txn_id = "CT-9876543"` for the constitution debit. This correspondence **must be persisted**, because future operations will need it: *"reverse the transaction that originated this deposit"* requires knowing which `txn_id` to reference. Without this mapping, you lose cross-system traceability.

### 5. Semantic Translation of Errors

The Core returns `ERR-2317`. The domain needs to know if this is `InsufficientBalance` (recoverable business error, show to client), `AccountBlocked` (non-recoverable business error, escalate), or `TransientUnavailability` (technical, automatic retry). This translation table is one of the most important pieces of the ACL and evolves as you discover new scenarios in production.

### 6. Latency Adaptation (sync ↔ async)

Common scenario in legacy Cores: you submit a movement, it stays pending, processes in the nightly cycle, confirms the next morning. Your domain can't wait 14 hours synchronously. The ACL transforms this operation into a clean asynchronous interface: `debitAsync()` returns "pending"; when the Core confirms (via callback, polling, or CDC), the ACL emits the `DebitConfirmedInCore` event for the domain's internal bus. The saga continues from there.

### 7. Periodic Reconciliation

The ACL does not trust that what it sent actually happened. Periodic batch job (typically daily, aligned with the Core's cycle) that crosses *"what the domain thinks is in the Core"* with *"what the Core actually has"*. Divergences → exceptions, alerts, investigation queue. In banking this **is not optional**: without reconciliation, divergences accumulate silently and you discover them months later with material loss.

---

## Internal Structure

A well-built ACL has at least five distinct pieces:

| Piece | Responsibility |
|---|---|
| **Port (domain interface)** | The interface the domain calls. Methods with domain vocabulary: `debitForConstitution(deposit)`, `reverseConstitution(deposit_id)`. |
| **Translator** | Converts domain commands into Core payloads, and Core responses into domain results. Zero business logic. |
| **Protocol client** | The concrete Core client: SOAP, REST, MQ. Replaceable independently. |
| **State store** | Local persistence of the ACL: idempotency keys, ID mappings, in-flight operations, dead-letter for ambiguous operations. |
| **Reconciler** | The batch job that checks consistency with the Core. |

Notice that the ACL **has its own state**. It's not a stateless proxy. Without this state, none of the seven responsibilities works with guarantees.

---

## Who Owns the ACL

Organizational question, but critical: **the Deposits team owns the ACL for the Core**, not the Core team. Reasons:

1. The Deposits team is the one that knows the semantic needs the ACL serves
2. The Core team cannot (and should not) maintain N different ACLs for N consumers
3. The pace of change of the ACL follows the consumer's pace, not the Core's

The Core team maintains the **technical contract** of the Core (its API). The Deposits team maintains **the translation** between that contract and its domain.

---

## The Hard Case: Indeterminate State

This is the scenario that separates a robust ACL from a naive one.

You submit a debit. The network drops before you receive a response. Timeout.

**What happened in the Core?** You don't know. It may have:
- Not received the request (nothing happened)
- Received, processed, and the response was lost (everything happened)
- Received, is processing (will happen)

Three possible realities, one single uncertainty. The worst thing you can do is to assume "if it errored, nothing happened" — in banking, this results in double debits when you retry.

The ACL must handle this explicitly:

1. **Before** sending to Core, record `(idempotency_key, status=IN_FLIGHT)` in the state store
2. Send to Core
3. On timeout/indeterminate error: update to `status=INDETERMINATE`, **no immediate retry**
4. Flag the operation for clearance: query the Core by `external reference` (`TD-{deposit_id}`) to find out whether the operation was actually executed
5. Only after clearance, decide: confirm (operation executed) or retry (operation did not arrive)
6. Explicit surface to the saga: while in `INDETERMINATE`, the saga **waits** or moves into its own state (`AwaitCoreClearance`)

The saga must know the state can be indeterminate. It's not an anomaly hidden by the ACL — it's a modelled reality.

---

## ACL Antipatterns

Five recurring failure modes:

### Wrapper That Doesn't Translate

The ACL has a `postMovement(...)` method that maps 1-to-1 with the Core, just under a different name. Result: you renamed the leak, you didn't avoid it. **Test**: the number of methods of the ACL should reflect the domain's vocabulary, not the Core's.

### Business Logic in the ACL

"If balance is X, then Y". No. The ACL translates and adapts — business decisions live in the aggregate. If the ACL needs to make a domain decision, it returns the information to the domain and lets it decide.

### ACL Shared Across Multiple Consumers

"CRM and Deposits use the same ACL for the Core, to avoid duplication." No. Each bounded context has its own semantic needs and the ACL should reflect that. Yes, there is duplication. It's the right kind of duplication. Trying to unify produces an ACL that serves everyone poorly.

### ACL Without Persistence

Stateless proxy. Without a state store, you lose idempotency, ID mapping, and indeterminate-state handling. It's a wrapper, not an ACL.

### ACL That Hides Errors

Swallows exceptions, silent retries. The saga needs to know what failed and why — without visibility, it cannot compensate correctly. The ACL is an error translator, not an error silencer.

---

## How This Connects to Everything Else

The ACL is the place where **all six primitives** concretely manifest at the most hostile boundary of the ecosystem:

- **[Command vs event](./01-the-six-primitives.md)**: the domain sends commands to the ACL (`debit`); the ACL emits events when Core confirms (`DebitConfirmedInCore`).
- **[Domain vs integration](./01-the-six-primitives.md)**: what leaves the ACL for the internal bus are domain events of the Deposits context, not raw Core events.
- **[Aggregate](./01-the-six-primitives.md)**: the ACL state (idempotency, mapping, in-flight) is itself a small aggregate, with its own local consistency.
- **[Identity](./01-the-six-primitives.md)**: the ACL propagates `correlation_id` and `causation_id` on every call to the Core (even if the Core doesn't understand them, they go in the reference or metadata field). Creates its own chain of `message_id`s.
- **[Idempotency](./01-the-six-primitives.md)**: the ACL's main responsibility, as seen.
- **[Compensation](./01-the-six-primitives.md)**: every operation on the Core has its inverse modelled in the ACL (`debit` → `creditReversal`), with domain vocabulary, not Core vocabulary.

Without the ACL, all these primitives would have to exist scattered through the domain, contaminated by the Core's language. With the ACL, they sit in a place where the friction belongs.
