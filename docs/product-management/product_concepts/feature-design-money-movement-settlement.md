# Feature Design — Money Movement: the `Movement` Primitive and the Settlement Leg

> Companion to [ADR-PC-032](./adrs/ADR-PC-032-money-movement-primitive.md) (the decision: every money movement is a first-class `Movement`). This document is the **implementation design** that realises it — how the atom is shaped, where the settlement leg lives, what gets built, and what gets deleted.
>
> Interlocks with [ADR-PC-016](./adrs/ADR-PC-016-legacy-current-account-adapter.md) (the legacy current-account settlement contract the cash leg obeys), [ADR-PC-029](./adrs/ADR-PC-029-engine-command-ingress.md) (the de-settled command ingress this generalises), [ADR-IC-003](../integration_concepts/adrs/ADR-IC-003-saga-orchestrator.md) (the saga substrate the settlement leg is built into), and [feature-design event-store + projections](./feature-design-event-store-projections.md) (the events the `Movement` rides inside).
>
> Reading order: §1 frame · §2 the `Movement` value object · §2A the account-identity model (how a customer's persistent `conta à ordem` maps to the engine-CA settlement leg) · §3 two mechanisms today → one · §4 the substrate-owned settlement saga · §5 retiring the eager port · §6 finality and ordering · §7 decider discipline · §8 the settlement command surface · §9 migration sequence, renames, deletions · §10 fitness functions and risks.

---

## 1. Frame: one append-first atom for every cash leg

Babelstone moves money in many places, and today every place hand-writes the same risky shape: a family decider calls `settlement.SettleAsync(…)` **synchronously**, then `runtime.AppendAsync(…)`. The money move (legacy Core, across the ACL) and the fact (event store) are two systems with no shared transaction, and the cash moves **before** the fact is durable — so a crash between them leaves money gone with no record (bd `babelstone-5r9n.1`, the orphan window). Worse, each family re-decides *eager-vs-gated* from scratch (bd `babelstone-5r9n.2`, bd `babelstone-t7o3.13` — the per-operation fork).

[ADR-PC-032](./adrs/ADR-PC-032-money-movement-primitive.md) fixes the shape with one concept: a **`Movement`** — a single-sided, append-first record of value moving against one engine-owned account. The decider produces it as **data**; the spine records it **inside the event's transaction**; the cash leg is then a **downstream, confirmation-gated consequence**, not a precondition. This document specifies the build. Two structural decisions drive everything:

- **The settlement leg is lifted into the orchestrator substrate** (§4), parameterised by the `Movement` — not re-hand-coded per family.
- **The eager `ISettlementPort` mechanism is deleted** (§5), not preserved — `Movement` is its successor. Babelstone is not live; we carry no compromise forward.

---

## 2. The `Movement` value object

`Movement` is a spine value object (`Babelstone.Engine`), beside the event-store primitives. It **names no family**, so the [`ENGINE_FAMILY_AGNOSTIC`](./adrs/commitment-catalogue.md) gate holds ([ADR-PC-021 §P2](./adrs/ADR-PC-021-application-layer-family-owned-deciders.md)):

| Field | Meaning |
|---|---|
| `account_ref` | the legacy-Core / engine-owned account the value moves against — an **opaque** reference, never PII ([ADR-PC-004](./adrs/ADR-PC-004-pii-crypto-shredding.md)). |
| `direction` | `Debit \| Credit`, **always relative to `account_ref`**: `Debit` = value leaves that account, `Credit` = value enters it. |
| `amount` | `Money` (integer cents; crosses from `decimal` exactly once, [ADR-PC-010 §P2](./adrs/ADR-PC-010-dotnet-hand-rolled-engine.md)). |
| `value_date` | the economic date (`valid_time`), never wall-clock. |
| `operation` | a **closed** operation code (`disburse`, `collect_installment`, `pay_maturity`, `pay_coupon`, `pay_early_termination`, `repay_early`, `rollover_debit`, …) — the engine-side name for the movement, which the settlement leg maps to the ACL `operation_type` (§8). Replaces the old free-string `Reason`. |
| `origin` | `Originated \| Observed` (§4) — did the engine decide it (cash leg to drive) or observe it already cleared. |
| `command_id` | the [ADR-PC-029](./adrs/ADR-PC-029-engine-command-ingress.md) append-idempotency `CommandId`, carried for correlation (§6). |

**Direction is relative to the named account — this kills a real wrinkle.** Today the loan disbursement is coded `SettlementDirection.Debit` against `DisbursementAccountRef`, even though a loan *pays out* (the borrower's account is **credited**). That ambiguity exists only because the old DTO never pinned *whose* account. Under `Movement` each leg's `(direction, account_ref)` pair is re-derived from the real cash flow and must be internally consistent: a disbursement is a **`Credit` to the borrower's account** (or a `Debit` to the bank's funding account — whichever account the leg names). Every migrated leg (§9) re-states its pair explicitly; none is carried over unexamined.

**The `Movement` rides inside the event.** It is **data on the money-moving event's existing opaque payload** ([ADR-PC-010 §P3](./adrs/ADR-PC-010-dotnet-hand-rolled-engine.md) / [ADR-PC-001 §P1](./adrs/ADR-PC-001-event-store-technology.md) column contract untouched — no new `events` column, no envelope change), so it is written **append-first** in the event's outbox transaction. An event may carry **more than one** `Movement` (renewal = a rollover `Debit` **and** an interest `Credit`); the carrier is `IReadOnlyList<Movement>`. Its concrete Avro shape is the carrying event's own schema, governed like every emitted contract and pinned by the **contract-reviewer** when the first family migrates.

---

## 2A. The account-identity model — the customer's persistent `conta à ordem` on the settlement leg

> **Status: ratified — the ADR-PC-043 payload-shape amendment this section needed has landed.** This section is the **keystone** that unblocks the engine-CA settlement build (bd `babelstone-u79p.2/.3/.4/.5`, epic `babelstone-98mj`). It reconciles two things the code already has in tension: the **intent-derived exactly-once reference** ([ADR-PC-043](./adrs/ADR-PC-043-intra-engine-settlement-counterparty.md), Accepted) — which names *which economic payout* a leg effects, but **not which account** — with the **persistent customer account** the money must actually land on. It **honours** [ADR-PC-032](./adrs/ADR-PC-032-money-movement-primitive.md) (slot 1 leaves the `account_ref` payload placement to the family; the staged substrate placeholder awaiting each family's promotion is a code seam — [SettlementCommandPayloadFactory.cs](../../../orchestrator/src/Babelstone.Orchestrator.Substrate/Saga/Settlement/SettlementCommandPayloadFactory.cs) `<remarks>` / [SettlementReferences.cs](../../../orchestrator/src/Babelstone.Orchestrator.Substrate/Saga/Settlement/SettlementReferences.cs) — not yet an ADR-PC-032 amendment) and [ADR-PC-037](./adrs/ADR-PC-037-current-account-family.md). Threading a **persistent customer `AccountRef` onto the settlement command body** is a payload addition ADR-PC-043 slot 1 did not originally sanction (§Payload shape pinned the body as "only opaque process-id-derived refs, **plus two additions**" — the `ce_settlementtarget` header and the `amount` field — and forbade the substrate reading `Movement.AccountRef` from the body). The dated **[ADR-PC-043 §Payload-shape amendment 2026-07-11](./adrs/ADR-PC-043-intra-engine-settlement-counterparty.md)** (bd `babelstone-u79p.13`) has since sanctioned exactly that promotion, so this section is **no longer only proposed** — its account-identity axis is recorded on the Accepted ADR itself (see §2A.7). The promotion below is what that amendment allows, not a design still awaiting one.

### 2A.1 The problem: two different keys, both on the leg, easily conflated

**In plain English.** When a term deposit matures or a personal loan disburses, the money has to reach *a specific customer's everyday account* — their `conta à ordem`. Two separate questions have to be answered for that one cash leg, and today only one of them is fully wired:

1. **"Which economic event is this?"** — answered by the **intent id** `IntentId = f(source_id, occurrence)` (`SettlementReferences.DeriveIntentId`, e.g. `f(deposit_id,"maturity")`). This is the [ADR-PC-043 §Idempotency](./adrs/ADR-PC-043-intra-engine-settlement-counterparty.md) exactly-once key: retries and re-routes of the *same* payout collapse to one landing. It is **per-payout**, deterministic, PII-free, and **says nothing about which account receives the money**.
2. **"Which account does the value move against?"** — answered by the **persistent account identity** `Movement.AccountRef` ([Movement.cs](../../../engine/src/Babelstone.Engine/Movement.cs), the opaque handle at §2). This is a **stable customer-account** reference — the borrower's disbursement account, the depositor's payout account — that lives across many economic events (many maturities, many installments), independent of any single saga occurrence.

These are **orthogonal axes**, and the danger the current code stages around is collapsing them. The substrate's `SettlementCommandPayloadFactory` today writes a **placeholder** account token — `AccountRef = SettlementReferences.Derive(AccountPrefix, processId)` → `ACCT-{processId}` ([SettlementCommandPayloadFactory.cs](../../../orchestrator/src/Babelstone.Orchestrator.Substrate/Saga/Settlement/SettlementCommandPayloadFactory.cs) L81/L97) — a **per-occurrence** value that is emphatically **not** a persistent customer account. That placeholder is correct *only* because no engine-CA leg has a production emitter yet (`SettlementCommandRouter` selects `SettlementTarget.EngineCa` but **zero** production paths emit it — the gap bd `babelstone-u79p.2/.4` fill). The moment a family settles against the engine-CA, the placeholder must be **promoted** to the real persistent account, or the money lands on a per-saga phantom account instead of the customer's `conta à ordem`.

### 2A.2 The stable customer-account identity: `Movement.AccountRef`, an opaque persistent handle

The **stable customer-account identity is `Movement.AccountRef`** — a single opaque string the engine resolves internally, **never PII** ([ADR-PC-004](./adrs/ADR-PC-004-pii-crypto-shredding.md): never an IBAN / NIF / name). It is the atom's [§2](#2-the-movement-value-object) `account_ref` field. Its defining property for identity purposes is **persistence**: unlike the saga `process_id` (one per occurrence) and unlike the `IntentId` (one per payout), a `conta à ordem` account ref is **stable across every economic event that touches that account** — it is the account, not the event.

Where each family gets its persistent handle (**already carried today**, as opaque tokens, not yet threaded onto the settlement leg):

| Family / leg | Persistent-account handle (source of truth) | Where it lives today |
|---|---|---|
| `personal_loan` — disburse (Credit-out) | `DisbursementAccountRef` | [`LoansContracts.cs`](../../../families/personal-loan/src/Babelstone.Families.PersonalLoan.Application/LoansContracts.cs) / `Commands.cs`; recovered from the read-model detail body |
| `personal_loan` — installment collect (Debit-in) | `CollectionAccountRef` (= the loan's own `DisbursementAccountRef`, recovered in [`InstallmentRule.cs`](../../../families/personal-loan/src/Babelstone.Families.PersonalLoan.Lifecycle/InstallmentRule.cs) L130) | loan detail body, rehydrated lifecycle-side |
| `personal_loan` — early repay (Debit-in) | `RepaymentAccountRef` | `LoansContracts.cs` / `Commands.cs` |
| `term_deposit` — maturity / coupon / early-termination payout (Credit-out) | the depositor's **payout/beneficiary account ref** (`BeneficiaryAccountRef`, [`Events.cs`](../../../families/term-deposit/src/Babelstone.Families.TermDeposit/Events.cs) L419) — for a self-settling deposit the degenerate `DepositPosition.AccountRef` (its own stream id) | deposit read model / constitution business-reference store (`source_account_ref` / `interest_account_ref`) |
| `term_deposit` — renewal rollover (Debit) + interest (Credit) | the renewed deposit's account ref (Debit leg) and the payout account ref (Credit leg) | as above, one per `Movement` |

Two clarifications this table fixes:

- **The persistent handle is family-owned data, but the `Movement.AccountRef` field is family-agnostic.** The engine spine never names `DisbursementAccountRef` or `BeneficiaryAccountRef`; the **family** decides which of its own opaque account tokens to place on `Movement.AccountRef` when it emits the money-moving event. `ENGINE_FAMILY_AGNOSTIC` ([ADR-PC-021 §P2](./adrs/ADR-PC-021-application-layer-family-owned-deciders.md)) holds because the field is a generic opaque string, exactly as [§2](#2-the-movement-value-object) requires.
- **Direction is re-derived against this account, per [§2](#2-the-movement-value-object).** A loan disbursement is a **`Credit` to the borrower's `DisbursementAccountRef`** (money enters the customer's account); a loan installment is a **`Debit` against the borrower's `CollectionAccountRef`** (money leaves it); a deposit maturity is a **`Credit` to the depositor's payout account**. The `(direction, account_ref)` pair is the customer-facing truth, not a bank-internal funding-side artefact.

### 2A.3 How a family promotes the identity onto the settlement leg — the two-step promotion

The promotion is **two independent steps**, both at the family's `Movement`-emission seam, both header/data-only (the substrate never reads the body — [ADR-IC-018 §D5](../integration_concepts/adrs/ADR-IC-018-family-owned-saga-modules.md)):

**Step A — put the persistent handle on the atom.** The family sets `Movement.AccountRef = <its persistent account token>` (the table above) when it constructs the `Movement` on the money-moving event. This is a **data** change on the family's own event payload — no engine-spine change.

**Step B — promote the counterparty selector to a header.** The family calls the counterparty-aware relay overload `MovementHeaders.ForOriginatedMovements(movements, SettlementTarget.EngineCa)` ([`MovementHeaders.cs`](../../../engine/src/Babelstone.Engine/MovementHeaders.cs)) to promote **`ce_settlementtarget = engine-ca`** as a CloudEvents extension header. `SettlementTarget.LegacyDda` (or the no-target overload) leaves the leg on the legacy core, unchanged — the **default is `legacy-dda`** ([ADR-PC-043 slots 1–2](./adrs/ADR-PC-043-intra-engine-settlement-counterparty.md); [`SettlementCommandRouter.Resolve`](../../../orchestrator/src/Babelstone.Orchestrator.Substrate/Saga/Settlement/SettlementCommandRouter.cs) L44–47: an absent target routes to `SettlementBaseUrl`). The substrate `SettlementCommandRouter` reads **only** this header to flip the base URL (`engine-ca` → `EngineCaSettlementBaseUrl`, fail-closed if unconfigured); the **path + method stay counterparty-invariant**. The header carries **no account ref and no PII** — only the closed-enum counterparty token ([ADR-PC-004 §P2](./adrs/ADR-PC-004-pii-crypto-shredding.md)).

**Step C — thread the account onto the command body (the promotion the substrate factory awaits).** When the leg reaches the substrate, `SettlementCommandPayloadFactory.Build(...)` must place the **promoted persistent `Movement.AccountRef`** onto the command body's `AccountRef` field — **replacing** the `ACCT-{processId}` placeholder ([SettlementCommandPayloadFactory.cs](../../../orchestrator/src/Babelstone.Orchestrator.Substrate/Saga/Settlement/SettlementCommandPayloadFactory.cs) L81/L97), exactly the seam that file's `<remarks>` flags: *"the wiring that threads the promoted opaque `account_ref` onto the body lands with each consuming family's Movement migration."* Because the substrate is **payload-blind for routing** but the CA **writer** needs the real destination account, the persistent `account_ref` is threaded **the same way the amount and intent are** — via the family-promoted, substrate-carried `SettlementIntent`-style seam (a body/header value the substrate forwards **untouched**, never re-derives). The design decision here: **extend the substrate-carried settlement seam that already threads `(IntentId, AmountCents)` to also carry the promoted `AccountRef`**, so the CA `/capture` and `/credit` writers land the source `Movement.Amount` **onto the source `Movement.AccountRef`** — the destination is the customer's persistent account, never `ACCT-{processId}`. This is a **body addition ADR-PC-043 slot 1 does not yet sanction** (§2A.7): it is proposed here and **requires the dated ADR-PC-043 §D5 amendment named there** before Step C is built.

> **Invariant (design):** on **any** `engine-ca`-targeted leg, the command-body `AccountRef` MUST be the promoted persistent `Movement.AccountRef`, never the `ACCT-{processId}` placeholder. The placeholder survives **only** on the legacy-DDA path (where the legacy core resolves the account from the process-scoped business reference, unchanged) and on the pre-migration platform default. A fitness function (§2A.6) pins this.

### 2A.4 Coexistence with the intent-derived exactly-once key — orthogonal, never conflated

The persistent `account_ref` and the intent-derived reference are **different axes on the same leg** and are **combined, never collapsed**:

| | Intent-derived reference (ADR-PC-043 §Idempotency) | Persistent account identity (this section) |
|---|---|---|
| **Answers** | *which economic payout is this?* | *which account does value move against?* |
| **Derivation** | `IntentId = f(source_id, occurrence)`; the CA-apply `command_id` derives from it (`DeriveFromIntent`) | family places its persistent opaque token on `Movement.AccountRef` |
| **Cardinality** | one per **payout occurrence** | one per **customer account** (stable across many payouts) |
| **On the command body** | `CoreHoldRef` / `CreditRef` (the exactly-once dedup key) | `AccountRef` (the destination the writer lands on) |
| **What it guards** | double-move on retry/reissue/re-route (dedup at `command_dedup`) | money lands on the **right** account (the destination correctness the dedup key does **not** check) |

They are **complementary guards**: the intent key stops the *same* payout landing *twice*; the persistent `account_ref` stops the *right* payout landing on the *wrong* account. Neither substitutes for the other — [ADR-PC-043](./adrs/ADR-PC-043-intra-engine-settlement-counterparty.md) already notes `WRONG-AMOUNT` is a residual the identity key misses; **`WRONG-ACCOUNT` is its sibling**, and pinning the destination to the promoted `Movement.AccountRef` (not a process-scoped placeholder) is the in-band guard against it. The two references sit **side by side** on the body: `CoreHoldRef`/`CreditRef` = intent-derived; `AccountRef` = persistent identity. `SettlementReferences.Derive`/`DeriveFromIntent`/`DeriveIntentId` are unchanged; only the `AccountRef` field's *source* changes (placeholder → promoted handle).

**The undeliverable-credit model rides the persistent identity too.** When a `Credit` cannot land (destination `Closed`/`Erased`), [ADR-PC-043 §Undeliverable credit](./adrs/ADR-PC-043-intra-engine-settlement-counterparty.md) holds the payout **at source** (`term_deposit` `payout-pending` / `personal_loan` `disbursement-pending`) and, past the hold horizon, escheats via `operations.CreditUnapplied(IntentId, BeneficiaryAccountRef, …)` — whose `BeneficiaryAccountRef` **is** the promoted persistent `Movement.AccountRef`. So the same identity that routes the happy-path credit also names the beneficiary of an attributed IOU: the account identity is load-bearing on both the success and the failure path.

### 2A.5 Both directions, both families — the concrete matrix

The design must land **money-in (Debit)** and **money-out (Credit)** for **both** families. Each row states the persistent identity, the direction relative to it, the intent occurrence key, and the gating:

| Family | Leg | Direction (relative to `account_ref`) | Persistent `account_ref` | Intent occurrence | Gating (§4) |
|---|---|---|---|---|---|
| `term_deposit` | fresh-open principal-in | **Debit** (money leaves the funding account) | source/funding account ref (constitution `source_account_ref`) | `"constitution"` | funds-gated `Reserve → Confirm` (embedded constitution saga) |
| `term_deposit` | maturity payout | **Credit** (money enters the payout account) | depositor payout/beneficiary account ref | `"maturity"` | confirmation-gated `ConfirmCredit` |
| `term_deposit` | coupon payout | **Credit** | payout account ref | `"coupon-N"` | confirmation-gated |
| `term_deposit` | early-termination payout | **Credit** | payout account ref | `"early-termination"` | confirmation-gated |
| `term_deposit` | renewal | **Debit** (rollover) + **Credit** (interest) | renewed-deposit ref (Debit) + payout ref (Credit) | `"renewal"` (one intent per `Movement` leg) | per-`Movement`, per-direction; per-account FIFO (§6) |
| `personal_loan` | disburse | **Credit** (borrower's account is credited) | `DisbursementAccountRef` | `"disbursement"` | confirmation-gated `ConfirmCredit` |
| `personal_loan` | installment collect | **Debit** (money leaves the collection account) | `CollectionAccountRef` | `"installment-N"` | funds-gated `Reserve → Confirm` |
| `personal_loan` | early repay | **Debit** | `RepaymentAccountRef` | `"early-repay"` | funds-gated |

The **`Credit` legs** (deposit payouts, loan disbursement) are the ones that reach a customer's `conta à ordem` as money-in *to the customer*; the **`Debit` legs** (deposit funding, loan collection/repayment) take money-out *from the customer's account*, funds-gated. Note the direction is **always relative to the customer account named**: a loan disbursement is a `Credit` because the borrower's account gains value — the [§2](#2-the-movement-value-object) wrinkle-killer, applied per row.

### 2A.6 Fitness functions this identity model needs (design — not yet catalogued)

These pin the identity invariants; they register in the [commitment catalogue](./adrs/commitment-catalogue.md) when their gate lands with the build (the same `Planned`-until-the-gate-lands posture ADR-PC-043's inline commitments take — **the orchestrator flips the catalogue rows centrally**, this draft only names them):

- **`SETTLEMENT_LEG_ACCOUNT_REF_PROMOTED`** — architecture/integration: on an `engine-ca`-targeted leg, the command-body `AccountRef` equals the promoted `Movement.AccountRef`, **never** the `ACCT-{processId}` placeholder. The in-band `WRONG-ACCOUNT` guard.
- **`SETTLEMENT_LEG_ACCOUNT_REF_STABLE`** — integration: two economic events against the **same** persistent account (two maturities on one payout account; installment N and N+1 on one collection account) carry the **same** `Movement.AccountRef`, while their **intent references differ** — proving the two axes are orthogonal (the account persists; the intent is per-payout).
- **`SETTLEMENT_TARGET_HEADER_ONLY`** (honours the existing ADR-PC-043 header-only contract) — architecture: the substrate router selects the counterparty from `ce_settlementtarget` **alone** and never reads `Movement.AccountRef` from the body ([ADR-IC-018 §D5](../integration_concepts/adrs/ADR-IC-018-family-owned-saga-modules.md)).

### 2A.7 What this design does *not* change — and the ADR-PC-043 amendment it *requires* before the build lands

This section **honours** ADR-PC-032 and ADR-PC-037: it fills in the account-identity half that ADR-PC-043 §Idempotency deliberately left to the `Movement.AccountRef` promotion seam, and ADR-PC-032 already frames the `account_ref` placement as staged and family-owned — slot 1 (§Payload shape) leaves *where the `account_ref` lives on a carrying event* to each family's payload; the substrate's placeholder awaiting each family's promotion is a code seam, flagged in the `SettlementCommandPayloadFactory.cs` `<remarks>` and the `SettlementReferences.cs` staged-`account_ref` seam note (the concrete staging point). The exactly-once key, the header-only routing, the loop-breaker (`Origin=Observed`), and the credit-admission gate are all **unchanged**.

**Where this design does *not* yet conform — the required prerequisite (not applied in this lane).** This section does **not** claim ADR-PC-043 conformance for the one thing it adds: **a persistent customer `AccountRef` on the settlement command body**. ADR-PC-043's slot-1 payload list (§Payload shape) pins the body as "only opaque process-id-derived refs, **plus two additions**" — the promoted `ce_settlementtarget` header and the `amount` field — and explicitly says the substrate MUST NOT read `Movement.AccountRef` from the body. The promoted persistent `AccountRef` is therefore a **third** body addition the Accepted ADR does not yet sanction, distinct from the intent-derived `CoreHoldRef`/`CreditRef`. So a dated **[ADR-PC-043](./adrs/ADR-PC-043-intra-engine-settlement-counterparty.md) §D5 amendment** is a **prerequisite** for the downstream build (bd `babelstone-u79p.2/.3/.4/.5`): it must pin, on the ADR itself, that the CA-apply body carries the promoted persistent `AccountRef` as the destination field (the `WRONG-ACCOUNT` in-band guard, sibling to the `amount`/`WRONG-AMOUNT` guard slot 1 already names), distinct from the intent-derived refs — so the account-identity axis is recorded on the Accepted ADR, not only proposed in this companion. **That amendment has since landed** (the dated [ADR-PC-043 §Payload-shape amendment 2026-07-11](./adrs/ADR-PC-043-intra-engine-settlement-counterparty.md), bd `babelstone-u79p.13`): the ADR now sanctions the promoted persistent `AccountRef` as the credit/debit destination selector on an `engine-ca` leg, so §2A is no longer only proposed — its account-identity axis is recorded on the Accepted ADR.

### 2A.8 The engine-CA demo path — funding and paying a customer's `conta à ordem` end to end

> **What this shows, in plain English.** All of §2A above is the *design*; this subsection is the **runnable proof of it** — the loop epic `babelstone-u79p` wires and Mission Control renders (see [docs/demo/mission-control/README.md](../../demo/mission-control/README.md)). A customer holds one **engine-owned current account** — their `conta à ordem`, a real [ADR-PC-037 `current_account`](./adrs/ADR-PC-037-current-account-family.md) instance the engine owns end to end. A term deposit and a personal loan then **fund from** and **pay into** that same account, so you can watch money leave and arrive on one persistent balance as each product moves. It is the concrete instance of the §2A.5 matrix, run against a live engine.

**The loop, leg by leg** — each row is a §2A.5 leg, now pointed at *one shared* customer `conta à ordem` (its persistent `Movement.AccountRef`, §2A.2), settled `engine-ca` (`ce_settlementtarget = engine-ca`, §2A.3 Step B), landing on the engine's own CA writer rather than the legacy core:

| Product move | Effect on the customer's `conta à ordem` | Mechanism (§2A.5 / ADR-PC-033) |
|---|---|---|
| **Constitute a term deposit** (fund it *from* the CA) | **Debit** — a **reversible hold** then an **irreversible capture** | funds-gated `Reserve → Confirm`; `HoldPlaced → HoldCaptured` on the CA |
| **Deposit matures** | **Credit** — money lands back on the CA | confirmation-gated `ConfirmCredit`; `AccountCredited` |
| **Originate a personal loan** (disburse) | **Credit** — the borrower's CA is credited with the principal | confirmation-gated `ConfirmCredit`; `AccountCredited` |
| **Pay a loan installment** (collect) | **Debit** — a hold then a capture as the installment is collected | funds-gated `Reserve → Confirm`; `HoldPlaced → HoldCaptured` |

So constituting a deposit visibly **debits** the customer's engine current account (a reversible hold that the fresh-constitution funds-gate places, then the irreversible capture at approval — §2A.5 `fresh-open principal-in`), and maturing it **credits** the same account (§2A.5 `maturity payout`). Loan disbursement **credits** the account (§2A.5 `disburse`); paying an installment **debits** it (§2A.5 `installment collect`). Every leg is the same `(direction, account_ref)`-relative-to-the-customer-account truth §2 fixes: a disbursement is a `Credit` because the borrower's account *gains* value.

**What the epic wired to make this real** (the three seams §2A named as staged, now built by `babelstone-u79p`):

1. **Producer `engine-ca` target emission** (§2A.3 Step B; bd `babelstone-u79p.2` term-deposit, `babelstone-u79p.4` personal-loan). The families now emit `SettlementTarget.EngineCa` on their CA-bound cash legs — `MovementHeaders.ForOriginatedMovements(movements, SettlementTarget.EngineCa)` — where before every leg defaulted to legacy-DDA via the no-target overload. The `SettlementCommandRouter` reads the `ce_settlementtarget` header alone (§2A.6 `SETTLEMENT_TARGET_HEADER_ONLY`) and flips the base URL to `EngineCaSettlementBaseUrl`.
2. **The real `account_ref` on the leg** (§2A.3 Steps A + C; bd `babelstone-u79p.5`). The family places the customer's **persistent** account token on `Movement.AccountRef` (§2A.2), and the substrate threads it onto the command body's `AccountRef` field — **replacing** the `ACCT-{processId}` placeholder (§2A.3 Step C), the promoted destination the [ADR-PC-043 §Payload-shape amendment 2026-07-11](./adrs/ADR-PC-043-intra-engine-settlement-counterparty.md) now sanctions. The money lands on the customer's `conta à ordem`, never a per-saga phantom (§2A.6 `SETTLEMENT_LEG_ACCOUNT_REF_PROMOTED`).
3. **Engine settlement ingress** (bd `babelstone-u79p.5`). The engine now serves the counterparty-invariant settlement routes (`/v1/reservations`, `/v1/debits`, `/v1/credits`) itself, mapping each to the CA family's authorize/capture/credit ([ADR-PC-037](./adrs/ADR-PC-037-current-account-family.md) / [ADR-PC-033](./adrs/ADR-PC-033-account-abstraction-and-hold-lifecycle.md) hold lifecycle) — so an `engine-ca`-routed leg reaches the engine's own CA, not the legacy Core-ACL stub.

**How the two balances move** (ADR-PC-033: `available balance = accounting balance − Σ active holds`). A **Debit** leg first places a **hold** — the *available* balance drops immediately while the *accounting* (booked) balance is untouched — then **captures** it, at which point the accounting balance drops and the hold clears. A **Credit** leg lands directly on the accounting balance. Reading `GET /v1/accounts/{id}` at any point returns both balances plus the active holds, which is exactly what the Mission Control `conta-a-ordem` panel renders in lockstep with the settlement saga (see the [demo README](../../demo/mission-control/README.md)). Legacy-DDA settlement is untouched: a leg with no `engine-ca` target still routes to the legacy core, byte-for-byte as before — the demo path is purely additive.

---

## 3. Two mechanisms today → one

The current estate has **two** settlement paths, and the design collapses them into one:

1. **Eager (the anti-pattern).** `ISettlementPort.SettleAsync(SettlementInstruction)` — 8 call-sites (3 loan: disburse/installment/repay; 5 deposit: maturity/coupon/early-termination/renewal-debit/renewal-credit), each wired in-engine to the `LoggingSettlementPort` stub. This is what the orphan window and the fork live in.
2. **Gated (the target shape, proven once).** The constitution debit (bd `babelstone-t7o3.4`) does **not** use `SettlementInstruction`; it appends `DepositConstituted` only and the principal debit is the saga's gated `ReserveAccountBalance → ConfirmDebit` step, dispatched to the ACL (`/v1/reservations`, `/v1/debits`). But those settlement commands + routing currently live **inside the term-deposit** `ConstitutionProcessCommands.cs` / `SagaCommandRouter.cs` — family-owned, though conceptually generic.

The design **deletes mechanism 1** and **generalises mechanism 2** out of the family and into the substrate. Every cash leg becomes: *decider emits `Movement` on the event → substrate settlement saga effects it, gated.*

---

## 4. The substrate-owned settlement saga (the leg's home)

The settlement leg lives in the **orchestrator substrate** ([ADR-IC-003](../integration_concepts/adrs/ADR-IC-003-saga-orchestrator.md)), which [bd `babelstone-t7o3.12`](./adrs/ADR-PC-021-application-layer-family-owned-deciders.md) already made family-agnostic. This stays the right side of [ADR-PC-021 §D1](./adrs/ADR-PC-021-application-layer-family-owned-deciders.md) ("no shared cross-family *application* project"): the substrate is orchestrator infrastructure, not a family app layer.

### 4.1 Standalone vs embedded legs

The migration surface splits cleanly:

- **Standalone legs (7 of 8).** The engine appends the fact (`DepositMatured`, `LoanDisbursed`, …) carrying its `Movement`(s); the only remaining work is "effect the cash, gated, park on failure." A degenerate 2–3-state flow.
- **Embedded leg (1).** The constitution debit is one step inside a *multi-step* saga (parallel validation → approval fork → confirm → activate, with compensation). The money move is interleaved with the approval lifecycle.

### 4.2 The decision: one substrate-owned, `Movement`-triggered settlement saga

A new generic saga module — `saga_type` **`settlement`**, the substrate's `SettlementProcess` — handles every **standalone** leg. It is **event-auto-started**: `ISagaModule.StartMode` already offers an event-triggered mode (a start-event type + CloudEvents-header predicate), so a `Movement`-bearing event auto-starts a settlement instance with **no family saga code**. The family emits the `Movement`; the substrate does the rest.

The saga is **parameterised by `direction`** (the gating asymmetry of [ADR-PC-016 slot 5](./adrs/ADR-PC-016-legacy-current-account-adapter.md)):

- **`Debit`** → **funds-gated**: `Reserve → Confirm` two-phase; a refused reserve (`InsufficientBalance`) compensates/parks; an indeterminate confirm enters clearance ([ADR-IC-012 §P5](../integration_concepts/adrs/ADR-IC-012-anti-corruption-layer-implementation.md)).
- **`Credit`** → **confirmation-gated only**: a single `Confirm` (legacy always accepts a credit, but it must confirm for reconciliation flow 1); a non-confirm enters clearance, never silent.

An event carrying multiple `Movement`s (renewal) effects each, gated by its own `direction`, under per-account ordering (§6).

The **embedded** leg (constitution) keeps its rich saga; in a later pass it **composes** the same leg machinery as a sub-step (rule-of-three cleanup). It was **not** refactored in the first pass — it already worked gated, and its reserve/confirm is interleaved with the approval fork. *Leave working code working; prove the new saga on the 7 standalone legs first.* That later pass has now landed (bd `babelstone-t7o3.18`): the constitution debit leg composes the shared `SettlementReferences` derivation rather than a hand-coded copy, so the constitution and the substrate settlement leg derive the identical Core-facing references — the approval-fork lifecycle stays exactly where it is (§10).

---

## 5. Retiring the eager port

`ISettlementPort`, `SettlementInstruction`, and the `LoggingSettlementPort` stub are **deleted**, not preserved. They are the eager mechanism; the gated path never used them, so there is nothing to "keep as the wire shape." `Movement` strictly supersets the old DTO (it adds `value_date`, `origin`, `operation`, `command_id`), so it is the successor as the **engine-side** record; the **ACL-side** wire shape is the substrate's settlement commands (§8).

**Deletion is the last migration step**, gated by the `MOVEMENT_APPEND_FIRST` fitness function: while any leg is still eager, the port survives; when the call-site assertion is green (zero eager `SettleAsync`), the port + DTO + stub are removed in the same change. The fitness function *is* the safe-to-delete signal.

The free-string `Reason` (`"maturity"`, `"renewal_rollover"`, …) hardens into the closed `Movement.operation` code (§2), which maps to the ACL `operation_type` half of the [ADR-IC-012 §P4](../integration_concepts/adrs/ADR-IC-012-anti-corruption-layer-implementation.md) idempotency key.

---

## 6. Finality and ordering

**The fact is final append-first; the cash leg is a downstream consequence.** This reconciles a wording tension: bd `babelstone-t7o3.13` currently says "the engine fact is *not treated as final* until the ACL credit is confirmed." Under `Movement` the cleaner model holds: `DepositMatured` (with its `Movement`) **is** final the moment it appends — the deposit *did* mature on the engine's books. The credit is a separate, gated consequence; if it cannot be effected, it parks in `HUMAN_INTERVENTION_REQUIRED` with **no compensation** ([ADR-IC-003 §P6](../integration_concepts/adrs/ADR-IC-003-saga-orchestrator.md) — the money either moved or did not; the saga never invents an undo). *The fact is durable first; the cash is retryable after.* The `t7o3.13` wording is updated to this model (§9).

**Two idempotency keys, two seams** ([ADR-PC-032 slot 4](./adrs/ADR-PC-032-money-movement-primitive.md)): the `CommandId` dedupes the **engine append** (a replayed command returns the original `commit_sequence`, no second `Movement`, [ADR-PC-029 slot 4](./adrs/ADR-PC-029-engine-command-ingress.md)); the **cash leg**, as a saga step, inherits the ACL's `(operation_type, saga_step_id, external_reference)` key + the refuse-to-send guard ([ADR-IC-012 §P4/§P5](../integration_concepts/adrs/ADR-IC-012-anti-corruption-layer-implementation.md)) the eager call bypassed. The design does **not** collapse or derive one from the other.

**Ordering.** `Movement`s against one `account_ref` are effected in append order, inheriting the outbox's per-aggregate FIFO ([ADR-IC-004](../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md)); the settlement saga preserves it (the dispatcher's per-`process_id` FIFO, bd `babelstone-t7o3.7`). A multi-`Movement` event (renewal) effects its legs in declared order.

---

## 7. Decider discipline

Each migrated decider method changes the same way: **stop calling `SettleAsync`; attach the `Movement`(s) to the event; append.** The flow inverts from *settle-then-append* to *append-the-fact-carrying-the-movement* — the cash leg is the substrate's job, downstream. The decider stays pure-orchestration ([ADR-PC-021](./adrs/ADR-PC-021-application-layer-family-owned-deciders.md)); the **fold** still only records the already-decided facts (the `Movement` is data on the event, not recomputed in a handler). No clock, no I/O, no settlement call on the append path.

This also retires the `ISettlementPort` constructor dependency from `PersonalLoanConstitutionService` and `TermDepositConstitutionService` — the family application services no longer know about settlement at all; they emit facts.

---

## 8. The settlement command surface in the substrate

The settlement commands move from the term-deposit `ConstitutionProcessCommands.cs` / `SagaCommandRouter.cs` into the **substrate**, generalised:

- **Debit leg**: `ReserveAccountBalance`, `ConfirmDebit` (+ compensation `ReleaseBalanceReservation`, `ReverseCoreDebit`; clearance `QueryCoreDebitStatus`) — already exist, relocate verbatim (names are account-generic, not deposit-specific).
- **Credit leg**: a **new** generic `ConfirmCredit` command + its ACL endpoint (e.g. `/v1/credits`) — the gated path only has debit commands today because only the constitution **debit** was de-settled; the credit legs (maturity/coupon/early-termination/renewal-credit) need their gated command.
- **Routing**: a substrate `SettlementCommandRouter` maps each to the ACL `SettlementBaseUrl`; the `operation` code selects the [ADR-PC-016](./adrs/ADR-PC-016-legacy-current-account-adapter.md) settlement command (`debitForConstitution`-class) and the `operation_type` half of the ACL idempotency key.

The constitution saga, while still family-owned in pass 1, **switches to consume these substrate commands** (one settlement-command home from day one, per the agreed mandate) — its `SagaCommandRouter` references the substrate commands instead of family-local copies; its *saga lifecycle* (the approval fork) stays where it is.

---

## 9. Migration sequence, renames, deletions

A vertical slice closes the P1 first, then generalises:

1. **Platform** — the `Movement` value object; the substrate `SettlementProcess` (`settlement` saga) + `ConfirmCredit`/ACL credit endpoint; the settlement commands relocated to the substrate; the WireMock Core ACL extended to credits.
2. **Disbursement (bd `babelstone-5r9n.1`, the P1)** — de-settle `DisburseAsync` onto a `Movement`; first standalone leg through `SettlementProcess`. Closes the orphan window.
3. **Host-wire the loan (bd `babelstone-9g77`)** — the loan lands **Movement-shaped**, never eager (its prior "settlement shape is separate" premise is superseded).
4. **Loan installment + early-repay legs** — the loan's other two standalone legs onto `Movement`.
5. **Deposit legs (bd `babelstone-t7o3.13`)** — maturity / coupon / early-termination / renewal onto `Movement` via `SettlementProcess`; converges with the loan by construction.
6. **Delete the eager port** — `ISettlementPort` / `SettlementInstruction` / `LoggingSettlementPort` removed once `MOVEMENT_APPEND_FIRST` is green (§5).
7. **(Later, optional)** constitution saga composes the shared leg (rule-of-three cleanup).

**Explicit renames / deletions (no compromise carried forward):**

- **Delete**: `ISettlementPort`, `SettlementInstruction`, `LoggingSettlementPort`.
- **Rename**: the `Movement` field `Reason` (free string) → `operation` (closed code); `SettlementDirection` keeps `Debit`/`Credit` but its meaning is **fixed as relative to `account_ref`** (§2), and each leg's pair is re-derived (the disbursement-`Debit`-on-the-borrower's-account wrinkle is corrected).
- **Relocate**: the settlement commands + router from `families/term-deposit/**/Orchestration` into the substrate.
- **Re-word**: the bd `babelstone-t7o3.13` "fact not final until confirmed" framing → the append-first finality model (§6).

---

## 10. Fitness functions and risks

**Fitness functions** ([commitment catalogue](./adrs/commitment-catalogue.md), registered as the ADR-PC-032 commitments flip `Planned → Live`):

- `MOVEMENT_APPEND_FIRST` — a call-site/architecture assertion: no decider calls a settlement port before its append (the orphan window cannot reopen). Also the safe-to-delete signal for the eager port (§5).
- `MOVEMENT_CASH_LEG_IDEMPOTENT` — an integration test (gated-settlement, WireMock Core): a retried `Originated` cash leg cannot double-move (it inherits the ACL guard; the eager bypass is gone).
- `SETTLEMENT_LEG_SCA_GATE_CANNOT_BYPASS` (bd `babelstone-t7o3.19`, `Live`) — an integration test: an `Originated` money-mover cash leg with absent/stale step-up SCA is refused at the RECEIVER (the Core ACL settlement leg) `422 SCA_REQUIRED` before `ConfirmDebit`/`ConfirmCredit`, re-checked at the settlement-dispatch instant; the substrate attests, never denies ([ADR-PC-032 §A7/§A8](./adrs/ADR-PC-032-money-movement-primitive.md)). The settlement-leg analogue of `MCP_SCA_GATE_CANNOT_BYPASS`.

**Risks:**

- **The constitution refactor is DONE (bd `babelstone-t7o3.18`), so the leg machinery is shared, not copied.** Pass 1 left two leg-invocation styles (standalone `SettlementProcess` vs the constitution's embedded reserve/confirm) with the *commands* unified day one (§8). The rule-of-three cleanup then collapsed the duplicated derived-reference machinery: the constitution debit leg and the substrate settlement leg now compose the SAME shared `SettlementReferences` derivation, so they derive the IDENTICAL `external_reference` for a given process id (the cross-saga no-double-debit invariant is structural). The constitution's approval-fork *lifecycle* is unchanged — only the leg's reference assembly is shared.
- **Multi-`Movement` ordering** (renewal's debit + credit) must hold per-account FIFO through the settlement saga — covered by the dispatcher's per-`process_id` order (bd `babelstone-t7o3.7`), exercised by a renewal integration test.
- **Credit clearance** is new surface: credits are now confirmation-gated (not eager), so the indeterminate-clearance path must cover credits, not just debits — the WireMock ACL and `SettlementProcess` model the credit non-confirm case.
- **The `operation` closed code is a contract** the ACL keys on; widening it later is forward-only schema evolution ([ADR-IC-002](../integration_concepts/adrs/ADR-IC-002-schema-format-and-registry.md)), pinned by a consumer-driven contract test ([ADR-IC-009](../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md)).
