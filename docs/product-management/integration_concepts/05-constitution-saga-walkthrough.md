# Banking Ecosystem — Integration Architecture
## Document 05: Constitution Saga Walkthrough

The primitives and patterns from Documents 01–04, materialized in a real flow.

This document draws the complete flow, with concrete IDs, concrete events, concrete states. For tangibility, it uses an illustrative scenario: client João Silva (`client_id = CLI-2026-007842`) constituting a €10,000 deposit for 12 months in product "Traditional TD 12M" with a 2.5% gross nominal annual rate.

---

## Scenario Setup

Click on "Constitute" in the mobile app at 14:32:17 on 15 May 2026. The app generates:

- `correlation_id = corr-aB7xK2pQ9` (this ID will follow everything)
- `idempotency_key = idem-c4d8e2f1` (even if the user clicks twice, it's the same intent)

**The saga will be orchestrated** (not choreographed). Remember the decision from [Primitive 6](./01-the-six-primitives.md): orchestration for multi-step flows with complex compensation. Constitution is exactly that.

The walkthrough presents four overlapping perspectives: **orchestrator states**, **messages that flow**, **state of each participant**, **compensations if something fails**.

---

## Step 0: Entering at the Edge — Synchronous Boundary

```http
POST /api/v1/deposits/constitute
Headers:
  Idempotency-Key: idem-c4d8e2f1
  X-Correlation-Id: corr-aB7xK2pQ9
  Authorization: Bearer ...

Body:
{
  "client_id": "CLI-2026-007842",
  "product_code": "TD-TRAD-12M",
  "amount": 1000000,            // cents
  "source_account": "PT50...123",
  "interest_account": "PT50...123",
  "interest_modality": "AT_MATURITY",
  "automatic_renewal": false
}
```

The Deposits API gateway does **only** what fits within the 500ms:

1. Authentication/authorization (token validated by upstream IAM; claims propagated as signed assertions)
2. **PSD2 SCA pre-condition**: deposit constitution is a significant financial operation. The IAM must confirm that SCA has been completed for this session before the saga proceeds to any irreversible step. If SCA is absent or timed out, the edge returns `403` with reason `SCA_REQUIRED` — the orchestrator never starts. If SCA fails mid-saga (e.g., a step-up challenge times out during a long-running workflow approval), the orchestrator treats it as a `ConstitutionRejected` outcome and triggers the standard compensation path.
3. **AML / KYC clearance pre-condition**: financial-crime adjudication (sanctions/PEP screening, KYC, source-of-funds) is **upstream of this engine** and is enforced here as a precondition, exactly like SCA. The edge requires an upstream-issued clearance assertion (an `aml_clearance_ref` signed claim) on the request; if it is absent or invalid, the edge returns `403` with reason `AML_CLEARANCE_REQUIRED` — the orchestrator never starts. The engine **records the opaque `aml_clearance_ref` for audit lineage and never re-adjudicates it**; the verdict, the screening model, and any SAR/STR workflow belong to the upstream financial-crime systems. This clearance precondition is an **edge / gateway** concern ([ADR-IC-006](./adrs/ADR-IC-006-edge-api-gateway.md)), upstream of and out of scope for the product engine ([00 §4](../product_concepts/00-product-vision.md)); it is why there is **no `ValidateClientEligibility` step inside the saga below**.
4. **Synchronous idempotency check**: does `(client_id, idem-c4d8e2f1)` exist? The key is scoped to the client — see [Primitive 5](./01-the-six-primitives.md). If yes, return cached response. If no, proceed.
5. **Light validations**: payload schema, product exists in catalogue (local read model), amount within product's limits
6. **Creates the `ConstitutionProcess` aggregate** in state `STARTED`
7. Also creates the `Deposit` aggregate (in state `DRAFT`)
8. Persists everything + event `ConstitutionRequested` in the outbox **in the same local transaction**
9. Returns `202 Accepted` with `deposit_id = "DEP-2026-00012345"` and `process_id = "PROC-2026-00098765"`

```http
HTTP 202
{
  "deposit_id": "DEP-2026-00012345",
  "process_id": "PROC-2026-00098765",
  "status": "PROCESSING",
  "stream_url": "/api/v1/processes/PROC-2026-00098765/stream"
}
```

Time: ~150ms. The client sees "Processing..." and subscribes to the SSE/WebSocket to receive updates.

**SSE endpoint authorization note.** The `stream_url` uses the `process_id` as the path parameter. This must not be treated as a security token — sequential or UUID-format IDs are not secrets. The SSE endpoint must independently validate that the requester's bearer token is authorized to receive updates for that specific process (i.e., the token's `client_id` matches the process's owning client). A client that guesses or obtains another client's `process_id` must not receive their saga updates.

### What Is Guaranteed Up To Here

- The deposit exists in the DB (state: `DRAFT`)
- The constitution process exists (state: `STARTED`)
- The domain event `ConstitutionRequested` is in the outbox, ready to be published
- Nothing has left for the ecosystem yet. If it crashes here, we can resume from the outbox without any external effect.

---

## Step 1: Outbox Publishes → Orchestrator Takes Over

The outbox publisher reads `ConstitutionRequested` and publishes to the internal topic `deposits.process.events` (this is a **domain** event, not integration — it stays in the Deposits context).

```yaml
message_id: msg-001-a7b3c
correlation_id: corr-aB7xK2pQ9
causation_id: -
event_type: ConstitutionRequested
aggregate_id: PROC-2026-00098765
timestamp: 2026-05-15T14:32:17.342Z
payload:
  process_id: PROC-2026-00098765
  deposit_id: DEP-2026-00012345
  client_id: CLI-2026-007842
  amount: 1000000
  product_code: TD-TRAD-12M
  source_account: PT50...123
  ...
```

The **Constitution Saga Orchestrator** subscribes to this topic. Inbox check, deduplicates, begins.

The orchestrator transitions the `ConstitutionProcess` state to `PARALLEL_VALIDATION` and dispatches **two commands in parallel** (fan-out):

```
→ Command ReserveAccountBalance (to Core ACL)
→ Command ValidateProductLimits (internal, to the Deposit aggregate itself)
```

> **No eligibility step here.** AML/KYC/sanctions clearance was already enforced at the edge as a precondition (step 0) and adjudicated upstream of this engine — the saga performs no *financial-crime* eligibility adjudication — clearance is an edge precondition ([ADR-IC-006](./adrs/ADR-IC-006-edge-api-gateway.md)), upstream and out of scope for the engine ([00 §4](../product_concepts/00-product-vision.md)). (Commercial-eligibility *verdicts* — new-money, salary-domiciliation — when a product requires them are gathered upstream and refused by the decider per [ADR-PC-024](../product_concepts/adrs/ADR-PC-024-constitution-precondition-contract.md); v1's products are not eligibility-gated, so none appears in this walkthrough.) What remains is `ValidateProductLimits` — a *product-engine* rule bound to the pack ("does the client already hold N of this product; is the amount in range"), **not** a financial-crime check.

The two carry the same `correlation_id`, `causation_id = msg-001-a7b3c`, and derived `idempotency_key`s (`idem-c4d8e2f1::reservation`, `idem-c4d8e2f1::limits`).

### Ordering by the Reversibility Principle ([Primitive 6](./01-the-six-primitives.md))

Notice: neither of the two parallel steps has an irreversible external effect yet.

- `ReserveAccountBalance` is a **hold** in the Core (not yet a real debit)
- `ValidateProductLimits` is pure local computation

Everything easily reversible. By design.

---

## Step 2: The Two Validations Execute (Parallel)

### 2a. Core ACL Receives `ReserveAccountBalance`

Here the ACL's full responsibilities (Document 02) come into play:

1. Local idempotency check: does `idem-c4d8e2f1::reservation` exist? No.
2. Records intent: `(idempotency_key, status=IN_FLIGHT, started_at=...)`.
3. Translates: domain command → SOAP call to Core: `POST /core/services/HoldsService` with `{account: PT50...123, amount: 1000000, reference: "TD-DEP-2026-00012345", duration_seconds: 300}`.
4. Core responds in ~120ms: `{hold_id: "CORE-HOLD-554433", status: "ACCEPTED"}`.
5. ACL saves mapping: `(idempotency_key, process_id, core_hold_id, status=CONFIRMED)`.
6. ACL emits domain event:

```yaml
event_type: BalanceReserved
payload:
  process_id: PROC-2026-00098765
  core_hold_id: CORE-HOLD-554433
  expires_at: 2026-05-15T14:37:17Z
```

### 2b. Internal Validation of Product Limits

Synchronous calculation in the `Deposit` aggregate itself: does the client already have N deposits of the same product? Does it exceed the maximum limit? Is the amount within range? All OK. Emits `LimitsValidated`.

### 2c. Orchestrator Waits for the Two

Inbox check for each event. When the two arrive, the orchestrator transitions the `ConstitutionProcess` to `VALIDATIONS_COMPLETE`.

**Time elapsed up to here:** ~250ms from the click. Still well within the budget.

### What Is Guaranteed

- Client clearance (AML/KYC) was already a precondition at the edge — not re-checked here
- Balance is reserved (Core has hold)
- Internal limits OK
- **Nothing irreversible has happened yet.** The Core hold expires on its own if nothing is confirmed.

---

## Step 3: Approval Decision — Synchronous or via Workflow?

Here there's a fork that depends on the product and the amount. For our case (€10,000, standard product, existing client), rules say: **auto-approval**. For >€25,000 or new client, it would go to the external Workflow system (saga extends to hours/days).

We go with auto-approval for the main flow.

Orchestrator emits internal command `ApproveConstitution`. The `ConstitutionProcess` aggregate transitions to `APPROVED`. `ConstitutionApproved` event emitted.

---

## Step 4: Execution — Where Effects Become Real

Now the orchestrator enters the irreversible phase, in carefully chosen order:

### 4a. Confirm Debit in Core (Convert Hold Into Real Debit)

Command to the ACL: `ConfirmDebit(core_hold_id=CORE-HOLD-554433, process_id=...)`.

ACL:

1. Local idempotency check
2. Call to Core: `POST /core/services/HoldsService/{CORE-HOLD-554433}/confirm`
3. Core responds: `{txn_id: "CT-2026-9988776655", status: "COMMITTED", value_date: "2026-05-15"}`
4. ACL saves definitive mapping: `(deposit_id, core_txn_id=CT-2026-9988776655)` — **this mapping is crucial for future operations** (early mobilization, interest payment, maturity)
5. ACL emits `DebitConfirmed` with the `core_txn_id`

**Here a significant step happens: the effect is now real in the banking world.** The money has left the client's current account.

### 4b. Activate the Deposit Aggregate

Internal command: `Deposit.activate(core_txn_id, start_date, maturity_date)`.

`Deposit` aggregate:
- Validates invariants (was in `DRAFT`, valid transition to `ACTIVE`)
- Computes `maturity_date = 2027-05-15`
- Persists state + emits both a domain event **and an integration event** via outbox:

```sql
-- Same DB transaction --
UPDATE deposits SET status='ACTIVE', start_date='2026-05-15',
                    maturity_date='2027-05-15',
                    core_txn_id='CT-2026-9988776655'
WHERE id='DEP-2026-00012345';

INSERT INTO outbox (event_type='DepositConstituted', payload={...}, target='INTEGRATION');
INSERT INTO outbox (event_type='ProcessConstituted', payload={...}, target='DOMAIN');
```

**Notice the distinction (Primitive 2):** two events emitted.

- `ProcessConstituted` is internal, goes to the domain topic, the orchestrator consumes it to close the saga
- `DepositConstituted` is the **integration event**, goes to the backbone, **it's the public contract**

---

## Step 5: Fan-Out to the Ecosystem (Natural Choreography)

`DepositConstituted` is published to Kafka. Consumers (Primitive 2) react each in their own way, **without coordination**:

```yaml
event_type: DepositConstituted
version: 1
message_id: msg-009-i7j8k
correlation_id: corr-aB7xK2pQ9
causation_id: msg-008-h6i7j
aggregate_id: DEP-2026-00012345
timestamp: 2026-05-15T14:32:17.687Z
payload:
  deposit_id: DEP-2026-00012345
  client_id: CLI-2026-007842
  product_code: TD-TRAD-12M
  amount: 1000000
  rate_anb: 0.0250
  rate_anl: 0.0180
  start_date: 2026-05-15
  maturity_date: 2027-05-15
  interest_modality: AT_MATURITY
  interest_account: PT50...123
  capital_account: PT50...123
  automatic_renewal: false
  metadata:
    core_txn_id: CT-2026-9988776655
```

### Consumers in Parallel

| Consumer | Reaction |
|---|---|
| **Projector `client_deposits`** | Inbox check, INSERT in the read model |
| **Projector `upcoming_maturities`** | Inbox check, INSERT (matures 2027-05-15) |
| **Notifications Adapter** | Inbox check, sends event to notifications system ("send confirmation to client") |
| **Documentation Adapter** | Inbox check, requests generation of FIN + deposit certificate |
| **Reporting Adapter** | Inbox check, aggregates for Banco de Portugal statistics |
| **CRM (external consumer)** | Inbox check, updates the client relationship |

Each one has its own inbox, its own idempotency, its own pace. Failing one doesn't affect the others. **This is the choreography that follows the orchestration: the "side-effects fan-out".**

---

## Step 6: Closing the Saga

The orchestrator consumes `ProcessConstituted`, transitions the `ConstitutionProcess` to state `COMPLETED`. Emits internal event `ProcessCompleted` (for audit and observability). Saga ended.

In parallel, the frontend receives via SSE:

```
event: status_update
data: {status: "CONSTITUTED", deposit_id: "DEP-2026-00012345"}
```

And displays "Deposit successfully constituted" to the client. Total perceived time: ~700ms–1s (the entire saga), but the `202 Accepted` arrived at ~150ms. To the user, the sense is near-instantaneous.

---

## The Complete Visual Flow (Happy Path)

```
T+0ms     Edge: API receives, SCA + AML/KYC preconditions, light validations, local outbox
          ConstitutionProcess: STARTED
          Deposit: DRAFT
          → HTTP 202 to client (~150ms)

T+200ms   Outbox publishes ConstitutionRequested (internal topic)
          Orchestrator consumes, parallel fan-out

          ┌─→ Core ACL: reserve balance
          └─→ Limits: local computation

T+400ms   2× validation events reach the orchestrator
          ConstitutionProcess: VALIDATIONS_COMPLETE → APPROVED

T+450ms   Sequential (real effects):
          → ACL: confirm debit in Core
          → Deposit: activate (emits DepositConstituted in outbox)

T+700ms   DepositConstituted published on Kafka (backbone)
          Choreographed fan-out:
          - Read models update
          - Notifications fires
          - Documentation generates FIN
          - Reporting aggregates
          - CRM updates

T+800ms   SSE notifies frontend
          Client sees confirmation
```

---

## Now the Part That Matters Most: Compensations

Everything above has been the happy path. The robustness of the system is in knowing what happens when it fails. Three representative scenarios follow.

### Scenario A: Product Limit Exceeded (Fails Early, in Validation)

`ValidateProductLimits` fails: the client already holds the maximum number of this product (or the amount is out of the product's range). The `Deposit` aggregate emits `LimitsRejected`. (Financial-crime eligibility is *not* a failure mode here — AML/KYC clearance was a precondition at the edge, so an uncleared client never reached this saga at all; commercial-eligibility refusals, when a product is gated, are a separate `DepositConstitutionFailed` path per [ADR-PC-024](../product_concepts/adrs/ADR-PC-024-constitution-precondition-contract.md).)

The orchestrator receives. Transitions `ConstitutionProcess` to `COMPENSATE_VALIDATIONS`.

**What needs to be compensated?** Only what was done:

| Step | Compensation needed? |
|---|---|
| **Core hold** | **Yes — release it** (was done in parallel and succeeded) |
| Internal validation | No — stateless, and it is the step that rejected |

The orchestrator sends `ReleaseBalanceReservation` to the ACL. The ACL calls Core: `DELETE /core/services/HoldsService/{CORE-HOLD-554433}`. Confirms. Emits `ReservationReleased`.

`Deposit` transitions to `CANCELLED` (it was still in `DRAFT`, trivial cancellation). `ConstitutionProcess` to `CANCELLED`.

Important: **`DepositCancelled` is emitted as an integration event**, even in a cancellation that never actually constituted. Reason: the ecosystem needs to know. Read models clean up, eventual consumers that reacted to `ConstitutionRequested` (if any exist) know they will not receive `DepositConstituted`.

The frontend receives SSE: `{status: "REJECTED", reason: "Product limit exceeded for this client"}`.

**Total time:** <500ms. Clean business error, no real-world effects.

### Scenario B: Failure After Confirmed Debit (Late Failure, Partially Irreversible)

A harder scenario. Everything went well until the debit was confirmed in Core (step 4a — money has moved). Then the **internal `Deposit.activate` step fails**: the activation transaction does not commit (a persistence failure, or an aggregate invariant that rejects the transition).

Current state:

| Participant | State |
|---|---|
| Core | Debit confirmed, money has moved |
| Deposit | Not yet activated (activation failed) |

The orchestrator enters `COMPENSATE_POST_DEBIT`. **Critical business decision**: is there still a window to retry activation, or do we compensate directly?

Sensible policy: **retry with backoff** first (3 attempts, 1s/3s/10s). If persistent failure, escalate.

If it persists, compensation:

1. `ReverseCoreDebit` to the ACL → Core executes a **reversal credit operation**, with reference to the original `core_txn_id`. Returns a new `core_txn_id` for the reversal. **Notice: two movements on the Core statement, not an undo.**
2. `Deposit.cancel(reason="activation_failure")` — emits `DepositCancelled` on the backbone
3. `ConstitutionProcess` → `CANCELLED_AFTER_DEBIT`

The client is notified: "We couldn't constitute your deposit. The amount has been returned to your account. Please contact..."

**Even worse case: reversal also fails.** The ACL marks as `INDETERMINATE`. The orchestrator transitions the process to `HUMAN_INTERVENTION_REQUIRED`. Alert fires to operations. State persisted with all the information so humans can reconcile manually. **The system doesn't "give up"; it makes explicit that it needs help.**

### Scenario C: Indeterminate State in Core (Network Failure After Debit Confirmation)

You sent `ConfirmDebit` to the Core, the network dropped before the response arrived.

The ACL enters `INDETERMINATE` ([Document 02](./02-anti-corruption-layer.md) covers this state in detail). Instead of blind retry:

1. The ACL marks the operation as `AWAIT_CLEARANCE`
2. A clearance job (can be near-immediate) queries the Core by `reference: TD-DEP-2026-00012345`: was the debit actually executed?
3. If yes: ACL updates `(idempotency_key, status=CONFIRMED, core_txn_id=...)`, emits `DebitConfirmed` (arrived late, but arrived). The saga continues.
4. If no: ACL marks as `NOT_EXECUTED`, emits `DebitNotExecuted`. The orchestrator handles it as a normal error → retry or compensate.

The saga orchestrator, while this isn't resolved, keeps the process in `AWAIT_CORE_CLEARANCE`. **No blocking thread, no aggressive retries, no inventing state**. It waits, and the system converges.

---

## Important Variation: When the External Workflow Comes In

For amounts >€25,000, after validations the orchestrator does not auto-approve. Instead:

1. State → `AWAIT_WORKFLOW_APPROVAL`
2. Workflow adapter sends a command to the external system: `StartApproval(process_id, context)`
3. **The saga "sleeps"**. It can take hours or days.
4. Eventually, Workflow publishes `ApprovalCompleted(process_id, decision)` (via another Kafka topic, or callback).
5. Workflow adapter consumes, emits `ApprovalReceived` to the internal topic.
6. The orchestrator wakes up, resumes from step 4 of the main flow.

**Crucial detail**: during the wait, the **Core hold has already expired** (5 minutes). Solutions:

- Reserve balance only when workflow approves (sequential)
- Long hold during workflow (negotiate with Core, often costly)
- Re-reserve before confirming (more flexible, but needs re-validation)

This choice is a **saga design** decision — there is no universal answer, it depends on the product and the bank's policy.

---

## What This Concrete Saga Shows

Three points worth extracting from the flow, beyond what the steps already make obvious:

1. **The saga aggregate (`ConstitutionProcess`) is itself a domain entity.** It is persisted, has explicit valid transitions, and is queryable. **As a payload-shape decision ([Primitive 2 in Doc 01](./01-the-six-primitives.md)), `ConstitutionProcess` is a natural Option A aggregate** — its events are signals about transitions, have different audiences per event type (the orchestrator consumes `ProcessConstituted` to close the saga; operations consoles consume `HumanInterventionRequired`), and feed no projector-style consumers. The narrow event-per-type position serves better here than a shared envelope. The orchestrator is not a technical coordination object floating outside the domain — it *is* this aggregate in action. This is what allows `HUMAN_INTERVENTION_REQUIRED` to be a first-class state with its own operations console, rather than an unhandled exception. The operations console — which lets operators retry, cancel, and force-compensate sagas in this state — is the highest-privilege interface in the system. Its authorization model must match that privilege: strong authentication, role-based access, 4-eyes approval for operations above a defined amount threshold, and an immutable audit log of every action taken. See [Document 10](./10-security-and-threat-model.md) for the full model.

2. **Compensation is never assumed to succeed.** Each scenario (product-limit rejection, post-debit activation failure, indeterminate state) treats the compensation path as another saga with its own retries, its own intermediate states, and its own escalation path. The system doesn't *give up* — it makes explicit when it needs help.

3. **The reversibility-ordering principle is doing real work.** The two parallel validations are both reversible by construction (a Core hold, not a commit; and a stateless check). The irreversible operation (debit confirmation in Core) lands after every reversible step has succeeded. If failure ordering had been chosen differently, Scenario B would not have been recoverable. This is the [saga ordering principle from Primitive 6](./01-the-six-primitives.md) in direct operation — designing for failure before writing a single line of happy-path code.

---

## Summary

With this, the cycle closes. You have:

- **Conceptual foundation**: [6 primitives](./01-the-six-primitives.md)
- **Boundaries**: [ACL for the Core](./02-anti-corruption-layer.md)
- **Read model**: [CQRS for the 500ms](./03-cqrs-and-read-models.md)
- **Plumbing**: [Outbox, Inbox, Schema Registry, guarantees](./04-plumbing-patterns.md)
- **Materialization**: concrete saga of the Constitution with happy path + 3 failure scenarios

The architecture is coherent. Each piece serves an identified purpose, and the trade-off decisions are explicit (orchestration vs choreography at each moment, at-least-once + idempotency instead of exactly-once, eventual consistency assumed, compensation instead of transactionality).
