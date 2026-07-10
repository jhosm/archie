# ADR-PC-043: Intra-engine settlement counterparty — settling a family's Originated Movement against an engine-owned current account

| Field | Value |
|---|---|
| Status | Accepted |
| Date | 2026-07-09 |
| Accepted | 2026-07-10 |
| Deciders | jhosm |
| Shape | Contract-shape |
| Counterparty | The engine-owned current-account family ([ADR-PC-037](./ADR-PC-037-current-account-family.md)) — **single-owner**: the engine owns both sides of this contract |
| Depends on | [ADR-PC-032](./ADR-PC-032-money-movement-primitive.md), [ADR-PC-033](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md), [ADR-PC-037](./ADR-PC-037-current-account-family.md), [ADR-IC-003](../../integration_concepts/adrs/ADR-IC-003-saga-orchestrator.md), [ADR-IC-018](../../integration_concepts/adrs/ADR-IC-018-family-owned-saga-modules.md), [ADR-PC-029](./ADR-PC-029-engine-command-ingress.md), [ADR-PC-036](./ADR-PC-036-lifecycle-command-driver.md) |
| Amends | [ADR-PC-029](./ADR-PC-029-engine-command-ingress.md) (scoped carve-out — see §*ADRs amended*), [ADR-PC-032](./ADR-PC-032-money-movement-primitive.md) (slot 4, scoped) |
| Resolves | bd `babelstone-98mj` (Engine-CA settlement build epic, filed alongside this ADR) |

## In plain English

When a term deposit matures or a personal loan disburses, real money has to move to or
from the customer's account. Today that cash leg is driven against the **legacy** core;
this decision lets it settle against a current account the **engine itself owns**
([ADR-PC-037](./ADR-PC-037-current-account-family.md)), reusing the settlement saga we
already have. The hard part is making the money land **exactly once** and **never
silently vanish**, even though the two accounts live in different services with **no
shared database transaction**. We do that three ways: (1) key every payment on a stable
"which economic event is this" id so retries and re-routes collapse to one landing; (2)
hold an *undeliverable* payout in its own **source** account (the matured deposit stays
"payout-pending") rather than pushing it into a void; and (3) never let a credit fold
into a closed account, by deciding admissibility in the account's **own family stream**
before anything is recorded.

This ADR is scoped to counterparties the engine **already owns**. Legacy demand accounts
keep settling over the ACL ([ADR-PC-016](./ADR-PC-016-legacy-current-account-adapter.md));
nothing here waits for, assumes, or forces the estate-wide "v4" migration.

## Context

`term_deposit` and `personal_loan` already record every cash leg as a single-sided
`Originated` `Movement` ([ADR-PC-032](./ADR-PC-032-money-movement-primitive.md)) and let
the substrate-owned `SettlementProcess` saga ([ADR-IC-003](../../integration_concepts/adrs/ADR-IC-003-saga-orchestrator.md),
[ADR-IC-018](../../integration_concepts/adrs/ADR-IC-018-family-owned-saga-modules.md))
effect it. Every such leg targets the **legacy** core over the ACL. The engine-owned
current account ([ADR-PC-037](./ADR-PC-037-current-account-family.md)) is **not** wired
as anyone's settlement counterparty, and — verified — the current-account family has **no
credit-writer** at all: its only money-mover is the `authorize` debit; the generic
`movement_ledger` fold is **lifecycle-blind** (a credit to a `Closed`/`Erased` account
would silently fold today).

This ADR closes the mechanism gap behind
[ADR-PC-030](./ADR-PC-030-product-scope-and-boundary.md)'s stated topology ("the hub the
other three settle against") and the deferred **Q-AN** invariant
([04-open-questions.md](../04-open-questions.md)) — *"principal lands exactly once across
families"* — for the **engine-owned-CA universe**, which is answerable now and
independently of the estate-wide migration.

**The boundary this crosses:** the substrate `SettlementProcess` saga → the engine-owned
CA family (the settlement command path), and the source family (`term_deposit` /
`personal_loan`) → the CA (the economic transfer). Both sides are engine-owned, but they
are **distributed** — no shared ACID transaction is assumed across families, because
production co-location is not guaranteed. That single constraint shapes every slot below.

## Decision

The contract is filled across all six [ADR-PC-000 §D3](./ADR-PC-000-namespace-and-contract-shape-framework.md)
slots, then three cross-cutting sub-decisions (the credit-admission gate; the
undeliverable-credit model; the two-ingestion-path asymmetry).

*Revised 2026-07-10 (bd `babelstone-98mj.6`/`.7`/`.8`): the undeliverable-credit model is now
built. Two rules the Decision named are pinned here as they land, without altering the decision
above. (a) **Resolution-key derivation** — an undeliverable credit's `ResolutionIntentId = g(IntentId)`
is derived from the SAME original economic-intent id via `SettlementReferences.DeriveResolutionIntentId`,
never freshly minted (the structural double-pay guard, §Idempotency); the engine's cross-cutting
`operations.CreditUnapplied` / `operations.CreditReapplied` carry the attributed IOU, and the CA
settlement Movements re-point their stopgap verbs to the dedicated `ReceiveCredit` / `SettleDebit`
`MovementOperation` symbols. (b) **Payout-pending retry gate** — a re-attempt of a held payout fires
only when BOTH the source reads payout-pending (`DepositLifecycle.PayoutPending` /
`LoanLifecycle.DisbursementPending`) AND the destination is no longer rejecting (a projection-driven,
clock-free read); it re-fires the SAME one-shot occurrence, so the driver dedupe + engine
`command_dedup` + the slot-4 intent key collapse a late original apply and the re-attempt to exactly
one landing. The now-live §Verifiable-commitments rows migrate to the
[commitment catalogue](./commitment-catalogue.md) as CA-7…CA-12 (the resolution-key row reuses
the existing CA-6; the new capture-hold-match lands as CA-9; the still-Planned credit-admission
own-stream check is filed as CA-13).*

### 1. Payload shape

- Settlement command bodies are **unchanged** (`ReserveAccountBalance` / `ConfirmDebit` /
  `ReleaseBalanceReservation` / `ConfirmCredit`, carrying only opaque process-id-derived
  refs), **plus two additions**:
  - a promoted CloudEvents extension header **`ce_settlementtarget`** (`engine-ca` |
    `legacy-dda`) on the Movement-bearing event, added at the `MovementHeaders`
    promotion seam, that the router keys on — the substrate stays **payload-blind**
    ([ADR-IC-018](../../integration_concepts/adrs/ADR-IC-018-family-owned-saga-modules.md)
    §D5); it MUST NOT read `Movement.AccountRef` from the body;
  - an **`amount`** field (`Money`, integer cents) on the settlement command body so the
    CA writer lands exactly the source `Movement.Amount` — the only in-band guard against
    `WRONG-AMOUNT`, which every identity-keyed dedup misses.
- The CA emits **new `current_account` `IMovementBearing` events** (the family's first):
  `AccountCredited` (Credit `Movement`) and the capture path's `AccountDebited`
  (`HoldCaptured` + Debit `Movement`). No CA event implements `IMovementBearing` today.
- The undeliverable-credit terminal is a cross-cutting engine-spine fact
  **`operations.CreditUnapplied(IntentId, BeneficiaryAccountRef, Amount, Reason,
  UnappliedAt)`** and its lift **`operations.CreditReapplied(IntentId,
  ResolutionIntentId, TargetAccountRef, ResolvedBy)`** — opaque refs only, no PII
  ([ADR-PC-004]), no family named.

### 2. Semantics

1:1 command → CA mapping, family-agnostic (keyed on account, never product):

| Saga command | CA operation | Spine effect |
|---|---|---|
| `ReserveAccountBalance` | CA `authorize` | `HoldPlaced` (available balance drops) |
| `ConfirmDebit` | CA `capture` | `HoldCaptured` + Debit `Movement` (one append) |
| `ReleaseBalanceReservation` | CA `expire` | `HoldExpired` (posting-free) |
| `ConfirmCredit` | CA `credit` | Credit `Movement` |

Four legs discharged: TD fresh-open principal-in (Debit, via the constitution saga's
embedded `Reserve → Confirm`); TD maturity / coupon / early-redemption payout (Credit);
PL disbursement (Credit); PL installment / early-repay collection (Debit).

**Loop-breaker:** a CA-landed `Movement` carries `Origin=Observed` so `MovementHeaders`
emits no `Originated` header and the settlement predicate starts **no** second saga on
the CA's own event. This is the engine's first `Origin=Observed` producer and a *third*
sense of `Observed` — "engine-internal already-effected" — distinct from
[ADR-PC-042](./ADR-PC-042-settlement-posting-feed.md)'s "cleared upstream"; the CA-landed
line MUST NOT be double-folded.

### 3. Ordering and delivery

At-least-once bus, effectively-once advance — unchanged from the substrate. One
`SettlementProcess` instance per `(source event, movement leg)` via the deterministic
per-occurrence `process_id`; a redelivery re-derives the same `process_id` and the
auto-start `INSERT … ON CONFLICT (process_id) DO NOTHING` collides. LCD-2
([ADR-PC-036](./ADR-PC-036-lifecycle-command-driver.md)) holds installment N+1 while any
occurrence for the instance is parked.

**The CA apply is a single atomic read-modify-write on the account's OWN stream**
(`LoadAsync → admit → AppendAsync at the loaded expectedVersion`), so it is serialized
against a concurrent `Close`/`Erase` by the **same per-stream OCC seam**
(`events_stream_seq_uq` stale-head check) that serializes `authorize` against concurrent
debits. See §*The credit-admission gate*.

### 4. Idempotency

**The core decision.** The exactly-once key is a **deterministic economic-intent id**
`IntentId = f(source_id, occurrence)` (e.g. `f(deposit_id,'maturity')`,
`f(loan_id,'installment-N')`) — a **per-payout** key, *not* the per-occurrence
`process_id`. The CA settlement-facing `/capture` and `/credit` endpoints derive the
engine-append `command_id` from the **body's** `IntentId`-derived settlement reference,
**not** the HTTP `Idempotency-Key` header — so a saga *reissue* (byte-identical body,
fresh dispatch `message_id`) collapses at `command_dedup` to **one** append. This is a
**deliberate, scoped inversion** of [ADR-PC-029](./ADR-PC-029-engine-command-ingress.md)
("Idempotency-Key *is* the command_id") and
[ADR-PC-032](./ADR-PC-032-money-movement-primitive.md) slot 4 ("do not derive one key
from the other"), legitimate here because the engine owns **both** sides — see §*ADRs
amended*.

- The debit path is **double-guarded**: `command_dedup` + capture `WHERE state='ACTIVE'`.
- The credit path rests **solely** on `command_dedup` keyed on the `IntentId` reference —
  so its fitness function is load-bearing.
- **Resolution reuses the intent key:** `ResolutionIntentId` MUST be *derived from*
  `IntentId`, never fresh — so an operator re-target / retry and a late original apply
  collapse to exactly one landing by construction. A second `CreditReapplied` for a
  resolved `IntentId` is a reconciliation signal, not a double-pay.
- `command_dedup` **retention** becomes a settlement-correctness operational invariant:
  it MUST exceed the maximum park-and-re-drive horizon, or the replay-into-double floor
  reopens.

**Residual (stated, not hidden):** two *source* appends of the same economic payout (a
bugged/un-threaded source `CommandId`, an operator re-drive, a manual replay) yield two
`IntentId`s only if the source key is non-deterministic. This is closed to the extent the
source-family payout `CommandId` is deterministic (`LifecycleCommandKey` on a stable
occurrence key — [ADR-PC-036](./ADR-PC-036-lifecycle-command-driver.md)) and otherwise
**detected** by the reconciler, not prevented in-band.

### 5. Error model

**Undeliverable credit is never dropped and never anonymised** (full treatment in
§*Undeliverable credit* below). A DECLINED reserve on the **debit** path is shaped as a
**4xx** by a *settlement-facing* CA surface (distinct from the customer `authorize`
endpoint ([ADR-PC-034](./ADR-PC-034-realtime-authorization-technique.md) / [ADR-PC-037](./ADR-PC-037-current-account-family.md)), which returns HTTP 200 with a `Declined` body — a shape the dispatcher would
mis-classify as *Applied* and march the saga to `COMPLETED` with **zero** landing) →
`ReserveRefused` → park in `HUMAN_INTERVENTION_REQUIRED` (no compensation,
[ADR-IC-003](../../integration_concepts/adrs/ADR-IC-003-saga-orchestrator.md) §P6:
nothing was held). A `422 SCA_REQUIRED` at dispatch is classified **retriable-PENDING**
under the **same** `process_id` after SCA refresh — never terminal-FAILED (a drop) and
never re-driven as a fresh occurrence (a double). No-drop liveness: append-first
durability + `saga_outbox` PENDING re-drive + a **loud** HIR park.

### 6. Ownership and versioning

The engine team owns **both** the substrate saga/router **and** the engine-owned CA
family — a **single-owner** contract (unlike the two-party legacy-ACL leg governed by
[ADR-IC-012]). That single ownership is precisely what licenses the slot-4 key inversion.
Evolution is additive and BACKWARD-compatible: `ce_settlementtarget` + the `amount` field
are additive header/body fields; the new `current_account` `IMovementBearing` events and
`operations.CreditUnapplied`/`CreditReapplied` are additive. The `CaSettlementNamespace`
GUID is a fixed committed constant, never regenerated.

---

### The credit-admission gate (concurrency-safe by construction)

Because the generic fold is lifecycle-blind, admission is decided **strictly upstream at
ingestion, in the family**, so the generic `movement_ledger` fold only ever sees
*already-admitted* credits — the gate is **never in the fold** (symmetric with `authorize`
gating debits upstream). A spine-owned, family-implemented seam **`ICreditAdmissible`**
carries the lifecycle knowledge (the engine names no family — the sanctioned
`IAccount`/`IHoldable`/`IMovementBearing` pattern):

- `Active` / `Dormant` → **Admit** (Dormant optionally fires `AccountReactivated` per pack
  policy);
- `Closed` → `Rejected(ACCOUNT_CLOSED)`; `Erased` → `Rejected(ACCOUNT_ERASED)`.

Adversarially verified (both a stale-lifecycle race and a reactivation-vs-close race):
the saga path is **closed by construction** because the credit-receive command reads
lifecycle from the **synchronous own-stream fold** (`LoadAsync`) and appends
`AccountCredited` on the **same stream at the same `expectedVersion`** — a concurrent
`Close`/`Erase` is either seen on reload (→ reject) or loses the per-stream OCC race
(→ `ConcurrencyException` → reload-and-redecide → reject). Resurrection is impossible: the
lifecycle legality table has **no** `Closed→Active` / `Erased→Active` edge. Load-bearing
invariant: **`AccountReactivated` + `AccountCredited` MUST be one atomic append batch**
(or the credit leg re-runs admission on reload), else a `Close` can wedge between them.

> **Correction of record:** it is *not* "drain-before-decide" that closes these races —
> `DrainOnceAsync` refreshes only the lifecycle-*blind* balance/hold projection. The
> guard is the atomic own-stream read-modify-write under OCC.

### Undeliverable credit — source-hold primary, escheat terminal

A **freeze** blocks *debits only*; a credit **lands and folds** into a frozen or dormant
account ([ADR-PC-041](./ADR-PC-041-operation-constraining-legal-holds-and-freezes.md):
"a freeze stops money leaving, not money arriving"). So the residual is **only** the
genuinely-unreceivable `Closed`/`Erased` case, handled by **source controllability**:

- **Engine-originated payout (this ADR's legs) → HOLD AT SOURCE.** An un-admitted payout
  is **not** disgorged: the source aggregate stays in `matured → payout-pending`
  (deposit) / `approved → disbursement-pending` (loan), holding the funds in its own
  already-attributed engine account; the lifecycle driver re-attempts when a live
  destination exists. No new liability record for the common case — the source *is* the
  attributed holder.
- **External already-cleared credit ([ADR-PC-042](./ADR-PC-042-settlement-posting-feed.md)
  Observed feed) OR a held payout past the obligation-to-hold horizon → ATTRIBUTED
  ESCHEAT.** Append `operations.CreditUnapplied` — a **named liability to a specific
  beneficiary, never an anonymous or omnibus pot.** Silently folding into a closed
  account, dropping the credit, or routing it to an unattributed clearing account is
  **prohibited**.

### Two ingestion paths (by-construction vs detection-only)

| Path | Money source | Lifecycle race | On reject |
|---|---|---|---|
| **A — saga-driven** (this ADR) | engine controls it | **closed by construction** (own-stream OCC) | hold at source |
| **B — Observed feed** ([ADR-PC-042](./ADR-PC-042-settlement-posting-feed.md)) | external, already cleared | **detection-only** (generic `account_ref` fold shares no `expectedVersion` with the lifecycle stream) | attributed `CreditUnapplied` |

Path B is **specified-but-dormant** (no v1 producer), so its window is a property of the
future design, not a live bug. Detection: per-`account_ref` `feed_sequence` gap +
reconciliation-flow-2 ([ADR-PC-042](./ADR-PC-042-settlement-posting-feed.md) slot 7) — the
engine **surfaces, never invents** a balancing `Movement`. By-construction gating of
Observed credits, if ever wanted, means promoting them to the same own-stream
credit-receive command — an [ADR-PC-042](./ADR-PC-042-settlement-posting-feed.md)
amendment under the [ADR-PC-020](./ADR-PC-020-llm-toolchain-and-conformance-governance.md)
§D3 drift gate, recorded here as a **Planned** future decision.

## Consequences

**Easier:** Q-AN is discharged early for the engine-owned-CA universe, independent of the
estate-wide migration; a matured deposit can credit a customer's engine CA today. The
saga, router, and 1:1 mapping stay family-agnostic — a fourth family reuses them with no
substrate change. Insufficient-funds becomes a real engine decision
(`FundsAndRulesDecider`), not a stub 422. A frozen/closed credit is a **loud** park or an
attributed liability, never a silent drop.

**Harder / impossible:** exactly-once for the CA cash leg is a set of **new,
deliberately-inverted** invariants (body-derived append key, atomic own-stream
admission+append, 4xx-shaped decline, SCA-retriable, `Origin=Observed` loop-breaker) —
not a free consequence of the existing primitives. A cross-family transfer remains **two
single-sided Movements** across two saga instances, atomic only eventually. The credit
leg is single-guarded, so its fitness function is load-bearing.

**Residual risks:** see below.

## Residual risks

- **Cross-family DOUBLE from two source appends of one payout** is **detected**, not
  prevented — closure depends on deterministic source `CommandId`s
  ([ADR-PC-036](./ADR-PC-036-lifecycle-command-driver.md)).
- **`WRONG-AMOUNT`** upstream of the command (source computes the wrong number) lands
  "exactly once" at the wrong value; caught only in reconciliation.
- **DROP into an unresolved HIR park** (closed/frozen destination, no operator action):
  the credit lands zero times until resolved; liveness depends on an operator SLA (Q-AG
  thresholds uncalibrated).
- **Observed-path close/credit race** is an inherent eventual-consistency window on Path
  B; reconciled after the fact, never prevented, until/unless promoted to the own-stream
  command.
- **The obligation-to-hold horizon** (when hold-at-source becomes escheat) is a
  [financial-concepts](../../financial_concepts/banking_products_financial_mathematics.md)
  input, undocumented today — establish it before pinning any TTL.
- **Greenfield materiality:** every seam here (`ICreditAdmissible`, `AccountCredited`,
  the credit-receive command, `CreditUnapplied`) is net-new; the by-construction closure
  is the **acceptance criterion** the new code must be built and fitness-tested against,
  not a claim about code that exists today.

## ADRs amended / honoured

Per the [ADR-PC-020](./ADR-PC-020-llm-toolchain-and-conformance-governance.md) §D3
explicit-drift gate, the two contradictions are acknowledged here; the dated amendment
lines land on the target ADRs **when this decision is accepted / the code lands** (via
the `amend-adr` skill), not while this ADR is `Proposed`:

- **AMENDS [ADR-PC-029](./ADR-PC-029-engine-command-ingress.md)** — a narrow carve-out
  for the *settlement-facing* CA `/capture` and `/credit` surface: those endpoints ignore
  the HTTP `Idempotency-Key` for dedup and derive `command_id` from the body's
  `IntentId` reference. Every other CA endpoint keeps the header contract.
- **AMENDS [ADR-PC-032](./ADR-PC-032-money-movement-primitive.md) slot 4** — for the
  single-owner engine-CA surface, the CA-apply `command_id` is deliberately derived from
  the process-id-derived settlement reference. Single-sidedness and append-first are
  **preserved** (a cross-family transfer is still two independent single-sided Movements).
- **HONOURS** [ADR-PC-033](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md) (folds
  + three hold transitions), [ADR-PC-037](./ADR-PC-037-current-account-family.md)
  (discharges the deferred capture/credit writers + §D4 reconciliation policy),
  [ADR-PC-041](./ADR-PC-041-operation-constraining-legal-holds-and-freezes.md)
  (freeze is debit-only; a credit lands),
  [ADR-PC-042](./ADR-PC-042-settlement-posting-feed.md) (Observed reused as the
  loop-breaker origin; a **note** that the CA-landed Observed line is engine-internal and
  not double-folded), [ADR-IC-003](../../integration_concepts/adrs/ADR-IC-003-saga-orchestrator.md)
  / [ADR-IC-018](../../integration_concepts/adrs/ADR-IC-018-family-owned-saga-modules.md)
  (saga substrate, header-driven), [ADR-PC-036](./ADR-PC-036-lifecycle-command-driver.md)
  (LCD-2 + hold-expiry self-heal).
- **PRESERVES [ADR-PC-012](./ADR-PC-012-gl-posting-signal-contract.md)** — no suspense /
  clearing account and no balanced double-entry are introduced; the GL keeps double-entry.

## Verifiable commitments

New and load-bearing but not yet catalogued (greenfield); listed inline per
[ADR-PC-000 §A2](./ADR-PC-000-namespace-and-contract-shape-framework.md) form 2 —
each migrates to the [commitment catalogue](./commitment-catalogue.md) when its gate
lands with the build.

| # | Commitment (§-anchor) | Gate (pyramid level) | Test ID | Status |
|---|---|---|---|---|
| 1 | CA-apply `command_id` derived from the body `IntentId` reference, never the HTTP `Idempotency-Key` (§Idempotency) | integration | `SETTLEMENT_CA_APPLY_KEY_INTENT_DERIVED` | Planned |
| 2 | A redelivered OR reissued `ConfirmDebit`/`ConfirmCredit` against the CA lands exactly one Movement (§Idempotency) | integration | `SETTLEMENT_CA_CASH_LEG_IDEMPOTENT` | Planned |
| 3 | Settlement-facing decline returns 4xx → `ReserveRefused` → HIR, never 200-with-Declined (§Error model) | integration | `SETTLEMENT_CA_DECLINE_IS_4XX` | Planned |
| 4 | `422 SCA_REQUIRED` at dispatch is retriable under the same `process_id`, never terminal-FAILED (§Error model) | integration | `SETTLEMENT_CA_SCA_STALE_IS_RETRIABLE` | Planned |
| 5 | Credit-admission + `AccountCredited` append are one own-stream read-modify-write; credit-receive vs `CloseAccount` at the same version yields exactly one commit + an `ACCOUNT_CLOSED` reject on retry (§The credit-admission gate) | integration | `CREDIT_ADMISSION_OWN_STREAM_OCC` | Catalogued (CA-13, Planned) |
| 6 | `AccountReactivated` + `AccountCredited` are one atomic append batch (§The credit-admission gate) | unit | `CREDIT_REACTIVATE_CREDIT_ATOMIC_BATCH` | Catalogued (CA-7, Live) |
| 7 | Admission is decided upstream; the generic fold only ever folds admitted credits (§The credit-admission gate) | architecture | `CREDIT_ADMISSION_UPSTREAM_OF_FOLD` | Catalogued (CA-8, Live) |
| 8 | An engine-originated payout to a non-admitting CA leaves the source in payout-pending; no funds disgorged; retried lands exactly once (§Undeliverable credit) | integration | `CREDIT_UNDELIVERABLE_HELD_AT_SOURCE` | Catalogued (CA-10, Live) |
| 9 | Every `operations.CreditUnapplied` carries a resolvable beneficiary ref + amount + machine reason; no anonymous/omnibus sink (§Undeliverable credit) | unit | `CREDIT_UNAPPLIED_IS_ATTRIBUTED` | Catalogued (CA-11, Live) |
| 10 | `ResolutionIntentId = g(IntentId)`; a fresh-id resolution fails the check (the double-pay guard) (§Idempotency) | integration | `CREDIT_RESOLUTION_KEY_INTENT_DERIVED` | Catalogued (CA-6, Planned) |
| 11 | A reconciler pairs each source payout occurrence against the CA landing, classifying matched / DROP / DOUBLE / WRONG-AMOUNT (§Residual risks) | integration | `XFAMILY_PAYOUT_LANDING_RECONCILED` | Catalogued (CA-12, Live) |
| 12 | Observed-path close/credit race is surfaced (`feed_sequence` gap + flow-2), never invents a balancing Movement (§Two ingestion paths) | integration | `OBSERVED_CREDIT_CLOSE_RACE_DETECTED` | Gap |
| 13 | A `ConfirmDebit` capture appends `operations.HoldCaptured` + `AccountDebited` (the settle-debit Movement) in ONE batch under the SAME `HoldId`, so the earmark release and the Debit post atomically (§Payload) | unit | `SETTLEMENT_CA_CAPTURE_HOLD_MATCH` | Catalogued (CA-9, Live) |
