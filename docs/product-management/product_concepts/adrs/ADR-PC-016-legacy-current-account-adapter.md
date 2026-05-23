# ADR-PC-016: Legacy Current-Account Adapter Implementation

| Field | Value |
|---|---|
| Status | Accepted |
| Date | 2026-05-23 |
| Shape | Contract-shape (hybrid — see Decision 0) |
| Counterparty | The operating bank's legacy core current-account (DDA) module, reached through the Deposits ACL ([ADR-IC-012](../../integration_concepts/adrs/ADR-IC-012-anti-corruption-layer-implementation.md)) |

---

## Context

Every term-deposit lifecycle event in v1 moves money on a legacy current account. `DepositConstituted` debits the depositor's *conta à ordem*; `InterestPaid`, `DepositMatured`, `DepositTerminatedEarly`, and `DepositPartiallyWithdrawn` credit it ([coexistence §4](../feature-design-strangler-fig-coexistence.md), [02 §3](../02-v1-scope-term-deposits.md)). The current account stays in the legacy core through the whole v1–v3 period; only at v4 does it move onto the engine. The legacy current-account module is therefore the dominant integration the engine cannot operate without — [coexistence §12.2](../feature-design-strangler-fig-coexistence.md) and [04 §1](../04-open-questions.md) both name it the load-bearing first-class candidate.

[ADR-IC-012](../../integration_concepts/adrs/ADR-IC-012-anti-corruption-layer-implementation.md) already decided *how an ACL is built* against a legacy Core: a dedicated service per bounded context (D1), hand-rolled per-operation clients (D2), pluggable inbound adapters (D3), per-adapter circuit breakers and bulkheads (D4), and the ACL owning its own database with its own outbox (D5). What ADR-IC-012 does **not** decide is whether the engine team treats the current-account integration as *first-class engine scope* or pushes it to the bank as a generic ACL-only integration — and what the settlement and reconciliation contract across that seam actually commits to. That is this ADR.

This is the [ADR-PC-000 D4](./ADR-PC-000-namespace-and-contract-shape-framework.md) hybrid case named explicitly in the framework: both an implementation approach and a contract. The approach question (first-class vs generic) cannot be scored on the F1/F2 hard filters — both options are zero-budget in-house builds on the same ACL runtime, equally compliant — so its cells would be empty. Per D4 ("empty cells in the hard-filter table → switch to contract-shape"), this ADR is contract-shape. The approach decision is captured as a leading subsection (Decision 0); the six required slots describe the settlement contract the adapter carries.

---

## Decision

### Decision 0 — implementation approach: first-class adapter

The legacy current-account module gets a **first-class adapter**, not generic ACL-only handling.

[Coexistence §12.2](../feature-design-strangler-fig-coexistence.md) defines the three classifications:

- **First-class adapter** — the engine team builds and maintains a system-aware adapter that absorbs the legacy specifics, measurably shortening v1 onboarding. Used for the system the engine cannot operate without.
- **Generic ACL-only** — the engine commits to the ACL *pattern*; the bank builds its own integration on top. Used for per-operator-bespoke or rarely-touched systems.
- **Out-of-scope at v1** — deferred to a later phase.

The current-account module is first-class because it is the v1 settlement counterparty for every deposit flow. Pushing it to generic ACL-only would leave the engine unable to constitute a single deposit until the bank independently built the integration — which contradicts the [04 §1](../04-open-questions.md) commitment that a first-class connector is what makes the v1 onboarding promise real.

**"First-class adapter" does not mean a new runtime.** The adapter *is* the Deposits ACL — the dedicated service from [ADR-IC-012](../../integration_concepts/adrs/ADR-IC-012-anti-corruption-layer-implementation.md) D1 — specialised with hand-rolled per-operation clients (ADR-IC-012 D2) for the five settlement operations against this specific Core's transaction model. "First-class" is a statement about *ownership and v1 scope* (the engine team owns and ships it as v1 engineering), not about deploying a second kind of component. The engine never holds a shadow balance of the current account ([02 §3](../02-v1-scope-term-deposits.md) commitment 1); it reads through and commands, the legacy core owns the balance.

The settlement contract below is what that first-class adapter materialises.

### 1 · Payload shape

Five settlement commands flow engine → legacy through the adapter, one per [coexistence §4](../feature-design-strangler-fig-coexistence.md) settlement-table row:

| Engine event | Settlement command | Effect on legacy current account |
|---|---|---|
| `DepositConstituted` | `debitForConstitution` | Debit principal |
| `InterestPaid` | `creditInterest` | Credit net interest |
| `DepositMatured` | `creditMaturity` | Credit principal + final net interest |
| `DepositTerminatedEarly` | `creditEarlyTermination` | Credit (principal − penalty) + net accrued interest |
| `DepositPartiallyWithdrawn` | `creditPartialWithdrawal` | Credit withdrawn principal + net accrued interest on the withdrawn portion |

Each command carries domain-shaped fields: `deposit_id` (engine instance), `current_account_id` (legacy target), `amount_cents`, `currency` (always `EUR`, always explicit), `value_date`, `correlation_id` and `causation_id` (the originating engine event), and the ACL idempotency key (slot 4). The translator's public API speaks this domain vocabulary (ADR-IC-012 P2: `debitForConstitution(deposit)`, `payInterest(deposit_id, amount, value_date)`); the Core's wire shape — SOAP envelope, MQ message, or batch line, including the fan-out of one debit into N accounting movements — lives only inside the protocol-client module and never crosses into the engine. Inbound confirmations arrive as the ACL's `CoreInboundEvent` abstraction (ADR-IC-012 D3) and reach the engine as domain events (`DebitConfirmedInCore`, `CreditConfirmedInCore`) published from the ACL's own outbox onto Redpanda.

### 2 · Semantics

A settlement command is the money-movement leg of an engine lifecycle event: the engine has finalised the flow in its own books and instructs legacy to move the cash. It is **not** a request for permission on credits — legacy always accepts a credit — but constitution debits *are* conditional on funds (slot 5). The current account remains under legacy's system-of-record ([coexistence §3](../feature-design-strangler-fig-coexistence.md)); the engine reads through, never owns, and keeps no shadow balance. The direction is asymmetric by design ([coexistence §4](../feature-design-strangler-fig-coexistence.md)): the engine commands legacy through this adapter; legacy reports *facts* back through the daily batch file ([ADR-PC-017](./ADR-PC-017-legacy-batch-ingest-contract.md)), never through a command channel. There is no path by which legacy mutates engine state synchronously.

### 3 · Ordering and delivery guarantees

At-least-once delivery toward the Core, made effectively-once by ACL idempotency (slot 4). Per-`deposit_id` causal order is the contract: the constitution debit precedes any credit on the same deposit. There is **no** cross-deposit ordering guarantee, and none is needed — settlements on different deposits are independent. The engine emits each command through the outbox ([ADR-IC-004](../../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md)): the event-store append and the outbox row commit in one local transaction, so a settlement command can never be lost relative to the engine event that triggered it. Confirmations return through the ACL's own outbox (ADR-IC-012 D5/P6). Redpanda is the single seam in both directions; neither side reads the other's database.

### 4 · Idempotency

Inherited verbatim from [ADR-IC-012](../../integration_concepts/adrs/ADR-IC-012-anti-corruption-layer-implementation.md) P4/P5; this ADR adds nothing and re-decides nothing. The idempotency key is the Core's contract, derived deterministically from `(operation_type, saga_step_id, external_reference)` — stable across saga retries, unique per operation. Dedupe runs ACL-side in the `idempotency_keys` table; a second send with the same key returns the recorded Core reference without contacting the Core. The indeterminate-state protocol (ADR-IC-012 D5, P5) governs the heart of the split-brain risk — the debit that succeeded at the Core but whose confirmation was lost: the state machine is `IN_FLIGHT → CONFIRMED | INDETERMINATE | REJECTED`, an `INDETERMINATE` row is never silently retried, a clearance task queries the Core for ground truth, and the saga sees the modelled state `CoreOperationAwaitingClearance` rather than a stuck step. The "double debit" bug is prevented by construction: the outbound client refuses to send if an in-flight row with the same key exists in any state other than `RETRY_PERMITTED`.

### 5 · Error model

**Gated, not post-flagged**, on the constitution path. The constitution saga ([ADR-IC-003](../../integration_concepts/adrs/ADR-IC-003-saga-orchestrator.md)) blocks on settlement confirmation before completing. The ACL error catalogue (ADR-IC-012 D2) maps every Core response into three domain categories:

- **Recoverable business** (`InsufficientBalance`, `LimitExceeded`) → the saga compensates and emits `DepositConstitutionFailed` with the matching `failure_reason` (`INSUFFICIENT_FUNDS`, `LIMIT_EXCEEDED`) per [02 §2.4.1](../02-v1-scope-term-deposits.md).
- **Non-recoverable business** (`AccountBlocked`, `ProductRetired`) → compensate and escalate to human review.
- **Transient technical** (`CoreUnavailable`, `Timeout`) → backoff, or the indeterminate-state clearance path of slot 4.

Unknown Core codes default to "non-recoverable, escalate" (ADR-IC-012 D2) — erring toward refusing to retry an ambiguous state. Credits (interest, maturity, termination, partial withdrawal) are not gated on funds but **are** gated on confirmation, because reconciliation flow 1 (below) depends on every credit having a confirmed legacy-journal counterpart. No settlement failure is ever silently dropped.

### 6 · Ownership and versioning

The engine team owns the adapter — that is what "first-class" means. The settlement-command contract is owned jointly with the legacy current-account team. A breaking change to the Core's settlement API is absorbed inside the ACL's protocol-client module through an explicit ACL release (ADR-IC-012 D2 WSDL-versioning discipline: the pinned WSDL hash is part of the release notes), invisible to the engine domain. The engine ↔ ACL boundary is the domain-event contract, gated by consumer-driven contract tests per [ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md). The engine's own event names (`DepositConstituted`, …) evolve under [integration_concepts §09](../../integration_concepts/09-long-term-schema-evolution.md) independently of the Core's wire format.

### Reconciliation contract

The first-class adapter ships with **reconciliation flow 1** ([coexistence §7.2](../feature-design-strangler-fig-coexistence.md)) — the split-brain check. At end of day a reconciliation job compares the engine's settlement outbox (every command sent, with amount, target `current_account_id`, and `correlation_id`) against the legacy core's incoming credit/debit journal:

- **Match** — every outbox command has a journal entry with matching amount, account, and `correlation_id`.
- **Engine-side orphan** — outbox has a command, journal does not (silent ACL failure or legacy miss). Alerts ops.
- **Legacy-side orphan** — journal entry the engine did not emit (lost outbox record — very serious — or an unrelated legacy entry to be filtered). Alerts ops.
- **Amount mismatch** — both sides present, amounts disagree. Pages on-call.

The reconciler is mandatory and self-evidencing (ADR-IC-012 P7): it emits a report every day, including zero-divergence days. The engine team owns the reconciliation *runtime*; the operating bank's operations function owns the *interpretation* and the decision tree ([coexistence §7.4](../feature-design-strangler-fig-coexistence.md)). The alert thresholds that separate operational noise from "freeze new constitutions" require a calibration period under real-data load — [Q-AG](../04-open-questions.md), a production-readiness input, not a POC blocker.

---

## Consequences

**What this makes easier:**

- The v1 onboarding promise of [04 §1](../04-open-questions.md) is real: the engine ships its own current-account adapter on day one rather than waiting for a bank-built integration.
- The engine's data model stays free of legacy concerns. Read-through with no shadow balance ([02 §3](../02-v1-scope-term-deposits.md)) means the engine never forks into a "core + legacy mirror" hybrid; the [00 §2](../00-product-vision.md) wedge survives.
- All five slots' hard parts (idempotency, indeterminate-state, failure isolation, the outbox seam) are inherited from [ADR-IC-012](../../integration_concepts/adrs/ADR-IC-012-anti-corruption-layer-implementation.md) unchanged. This ADR specialises that ACL for the current-account module; it does not reinvent the seam.

**What this makes harder or locks in:**

- A first-class adapter is engine-team capacity and a second runtime to operate (ADR-IC-012's "second runtime" consequence). The choice spends that capacity deliberately on the one integration v1 cannot do without.
- The engine is coupled to the legacy current-account module's availability for the whole v1–v3 period. Every constitution and every settlement depends on the Core being reachable through the ACL; the ACL's circuit breakers and bulkheads (ADR-IC-012 D4) bound the blast radius but do not remove the dependency.
- The ACL remains the most concentrated cross-system risk in the architecture and survives the end of term-deposit coexistence — it is needed until v4 brings current accounts onto the engine ([coexistence §11.3](../feature-design-strangler-fig-coexistence.md)).

---

## Residual risks

- **The legacy-inventory meeting is the production gate.** This ADR commits the *approach* (first-class) and the *contract shape* on the strength of the current-account module being the named load-bearing candidate. It is **Accepted** but not yet production-confirmed: the [coexistence §12.1](../feature-design-strangler-fig-coexistence.md) ten-dimension questionnaire must be filled in for this specific module before cutover, and three dimensions can foreclose feasibility ([§12.3](../feature-design-strangler-fig-coexistence.md)): the **transaction model** (does the Core offer a compensation primitive, or must the saga invent the undo?), **idempotency guarantees** (will the engine see double-delivery the ACL must dedupe?), and the **outage profile** (does the Core have headroom to absorb engine-driven settlement traffic?). If the meeting surfaces a model that forecloses a first-class adapter, this ADR is revisited — the same posture as POC-exempt-but-production-blocking ADRs [PC-002](./ADR-PC-002-application-level-bitemporality.md) and [PC-005](./ADR-PC-005-dr-rto-rpo.md).
- **Indeterminate-state backlog.** A slow clearance task lets the `INDETERMINATE` queue grow — operations whose Core-side reality is unknown. ADR-IC-012 names this one of the most dangerous states the system can enter; queue depth must be a monitored SLI.
- **Q-AG reconciliation thresholds are uncalibrated** until a real-data calibration period sets them. Until then, flow-1 mismatches are reviewed manually with elevated attention (the [coexistence §10.3](../feature-design-strangler-fig-coexistence.md) post-cutover regime).
- **Customer-master coupling.** The adapter resolves `current_account_id` against the legacy customer-master, which has no end date and may outlive coexistence ([04 §6](../04-open-questions.md), [Q-BA](../04-open-questions.md)). This ADR does not address customer-master cutover.
- **What this contract does not commit to:** the legacy core's specific API surface (SOAP/MQ/batch — absorbed by the ACL per ADR-IC-012 scope), the reconciliation job's tooling and schedule (ADR-IC-012 defers the cron/triage tooling), and the day-1 renewal load spike ([Q-AD](../04-open-questions.md), [coexistence §9.3](../feature-design-strangler-fig-coexistence.md)) which stresses this adapter but is a load-test concern owned by [ADR-PC-011](./ADR-PC-011-in-house-load-test-harness.md).
