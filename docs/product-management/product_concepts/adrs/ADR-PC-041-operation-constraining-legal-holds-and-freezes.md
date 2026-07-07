# ADR-PC-041: Operation-Constraining Semantics for Legal Holds (`FundsHeld`) and Compliance Freezes (`AccountFrozen`) — a Balance-Folded Earmark and a Decider-Level Block

| Field | Value |
|---|---|
| Status | Accepted |
| Date | 2026-07-05 |
| Deciders | jhosm |
| Shape | Contract-shape |
| Counterparty | The **product families** whose projection state folds a balance (`term_deposit`, `personal_loan`, and the shape-3/4 `conta à ordem` / `credit_card`), and the **real-time authorization path** ([ADR-PC-030 §P3](./ADR-PC-030-product-scope-and-boundary.md) stages 3–5) that reads `available balance` and now also consults the freeze guard. A spine-internal contract, the same intra-engine posture [ADR-PC-033](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md) took — not a wire contract to an external system. |
| Depends on | [ADR-PC-033](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md) (the Account abstraction, the `available balance = accounting balance − Σ active holds` fold, and the `operations.Hold*` authorization-hold lifecycle this ADR extends with a second hold kind and a freeze guard), [ADR-PC-030 §P3](./ADR-PC-030-product-scope-and-boundary.md) (the stages-3–5 authorization decider the freeze guard sits in), [ADR-PC-023](./ADR-PC-023-temporal-signals-projection-derived.md) (expiry is a projection-derived read, never a clock-manufactured fold), [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) (pure replayable folds; `Money`/integer-cents discipline), [ADR-PC-021](./ADR-PC-021-application-layer-family-owned-deciders.md) (the family-agnostic spine; the cross-cutting `operations.*` events name no family), [ADR-IC-017](../../integration_concepts/adrs/ADR-IC-017-integration-event-promotion-criterion.md) (the store-only posture these facts held in v1, now given operation-constraining folds) |
| Resolves | bd `babelstone-32hf` (the decision **and** its engine enforcement — the acceptance criteria's "if enforcement is built" branch) |
| Related | [ADR-PC-004](./ADR-PC-004-pii-crypto-shredding.md) (`LegalReference` / `FreezeReason` / `ComplianceActor` are structural, never PII), [ADR-PC-012](./ADR-PC-012-gl-posting-signal-contract.md) (a hold/freeze is an engine-internal constraint, never a GL posting), [ADR-PC-029](./ADR-PC-029-engine-command-ingress.md) (the idempotent command surface the release/unfreeze commands extend) |

---

## Context

**In plain English.** Two audit facts already exist in the engine but do nothing yet. `FundsHeld` records a **legal hold** — a court order or garnishment saying "this much money must not be spent." `AccountFrozen` records a **compliance freeze** — an AML/sanctions instruction saying "this account is blocked." Both shipped in v1 as *store-only* facts ([ADR-IC-017](../../integration_concepts/adrs/ADR-IC-017-integration-event-promotion-criterion.md)): the engine writes them down and can replay them, but their fold is a deliberate no-op, so recording one today neither earmarks funds nor blocks any operation. A production engine eventually has to **act** on them. This ADR decides how — and, because bd `babelstone-32hf` asked for enforcement, builds it.

The two facts have **different shapes**, so they get **different homes**. A legal hold is *an amount* — "€500 is unspendable" — which is exactly the earmark shape [ADR-PC-033](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md) already folds into `available balance` for authorization holds. So a legal hold **joins the available-balance fold** as a second kind of active hold. A compliance freeze is *a total block* — "no debits at all" — which cannot be honestly expressed as subtracting an amount (there is no amount; subtracting the whole balance is a hack that misbehaves on incoming credits). So a freeze becomes **a guard in the authorization decider**: while a freeze is active, the decider refuses any debit outright, naming why.

**Which boundary this crosses.** Not a wire boundary — an **intra-engine contract** between the family-agnostic spine (which owns the two facts, their lifting events, the extended available-balance fold, and the freeze guard) and each family's authorization path (which reads `available balance` and now also asks "is this instance frozen?"). It extends the [ADR-PC-033](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md) Account contract; it does not restate it.

**Why a contract-shape ADR.** Two parties build against this independently — the spine (the second hold kind + the freeze predicate) and the decider/families (which consult both). So it takes the [ADR-PC-000 §D3](./ADR-PC-000-namespace-and-contract-shape-framework.md) six-slot template, like its parent [ADR-PC-033](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md). Nothing is bought, so there is no F1/F2 tool pick.

**A terminology disambiguation this ADR forces.** [ADR-PC-033 §Decision slot 1](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md) loosely calls its *authorization*-hold events (`HoldPlaced`/`HoldCaptured`/`HoldExpired`) "the `FundsHeld`-family the spine already names as planned." But in shipped code `FundsHeld` is the **distinct legal-hold** audit fact, separate from the `Hold*` authorization lifecycle. This ADR fixes the vocabulary: **authorization holds** are the `operations.Hold*` lifecycle (transient, tied to an approved-but-unsettled debit, lifted by capture/expiry); **legal holds** are `operations.FundsHeld` / `operations.FundsReleased` (externally instructed, lifted by a release instruction/expiry). Both are "active holds" that lower `available balance`, but they are different lifecycles under the same synthetic `operations` aggregate_type. PC-033 carries a dated note pointing here (its §D5 append, made in this change).

**Scope.** This ADR decides *and builds* the operation-constraining semantics: the extended available-balance fold (legal holds included), the two new lifting events (`FundsReleased`, `AccountUnfrozen`), the freeze guard in the stages-3–5 decider, the reason/provenance surfaced into the read model and the decline path, and the fitness functions that pin them. It does **not** change the store-only *emission* of `FundsHeld`/`AccountFrozen` (they are still appended by the same command surface); it changes what their folds and the decider *do* with them.

---

## Decision

### A **legal hold** (`FundsHeld`) is a second kind of active hold folded into `available balance = accounting balance − Σ active authorization-holds − Σ active legal-holds`, lifted by `FundsReleased`; a **compliance freeze** (`AccountFrozen`) is a total-block **guard in the stages-3–5 authorization decider** ([ADR-PC-030 §P3](./ADR-PC-030-product-scope-and-boundary.md)) that refuses debits/withdrawals while active (no matching `AccountUnfrozen`), naming the `FreezeReason`/`ComplianceActor`. Both are pure, rebuildable folds; neither is a stored mutable flag; expiry of either is a projection-derived appended fact ([ADR-PC-023](./ADR-PC-023-temporal-signals-projection-derived.md)), never a clock-reading fold.

The six contract slots:

1. **Payload shape — two lifecycles, four events, all cross-cutting under `operations`.**
   - **Legal hold** — `operations.FundsHeld(InstanceId, HoldId, HeldAmount: Money, LegalReference, HoldExpiresAt: DateOnly?)` (unchanged, already shipped) placed the earmark; **new** `operations.FundsReleased(InstanceId, HoldId, ReleaseReference)` lifts it. `HoldId` correlates the pair (slot 4). `HeldAmount` is integer-cents `Money` ([ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md)). `LegalReference`/`ReleaseReference` are court/case references — **structural, never PII** ([ADR-PC-004](./ADR-PC-004-pii-crypto-shredding.md)).
   - **Compliance freeze** — `operations.AccountFrozen(InstanceId, FreezeId, FreezeReason, ComplianceActor, FreezeExpiresAt: DateOnly?)` (unchanged, already shipped) placed the block; **new** `operations.AccountUnfrozen(InstanceId, FreezeId, UnfreezeActor, UnfreezeReason)` lifts it. `FreezeId` correlates the pair. `FreezeReason` is a stable machine code (`AML_SCREENING`, `SANCTIONS_MATCH`, …); `ComplianceActor`/`UnfreezeActor` are operator identities — **never PII**.
   - **Distinct simple names, by construction.** `FundsHeld`, `FundsReleased`, `AccountFrozen`, `AccountUnfrozen` are all distinct from each other and from the authorization `HoldPlaced`/`HoldCaptured`/`HoldExpired` — required, because the Avro codec keys on the **unqualified** record name (`AvroEventSerializer.ForRecordName`); a name clash would fail the catalog build. This is the same collision [ADR-PC-033 §Rejected](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md) avoided.

2. **Semantics — a legal hold lowers spendable funds; a freeze blocks debits.**
   - **Legal hold.** From `FundsHeld`'s sequence forward, `HeldAmount` is **active** and reduces `available balance`, exactly as an authorization `HoldPlaced` does — the fold sums *both* hold kinds. It leaves the active set on `FundsReleased` (restoring `available balance`, **no posting** — no money moved; a legal hold releases funds, it does not settle them). Unlike an authorization hold, a legal hold has **no capture**: it is never `HoldCaptured`; a garnishment that is actually paid out is a *separate* debit `Movement` the legal process instructs, not a capture of the hold.
   - **Compliance freeze.** From `AccountFrozen`'s sequence forward the instance is **frozen** until a matching `AccountUnfrozen`. A frozen instance's `available balance` is **unchanged** (a freeze is not an amount); instead the **authorization decider refuses every debit/withdrawal** attempt while frozen (slot 5). Credits, interest accrual, and other non-debit postings **still fold** — a freeze stops money leaving, not money arriving or the ledger keeping score. (A stricter "block everything" variant is a listed residual risk, not this decision.)
   - **Expiry is a projection-derived appended fact, never a fold-time clock read.** `HoldExpiresAt`/`FreezeExpiresAt` are **advisory horizons**: a projection over the active set flags a hold/freeze whose horizon has passed, and an operator/command-shell action appends the `FundsReleased`/`AccountUnfrozen` fact — the same discipline [ADR-PC-033 slot 2](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md) uses for `HoldExpired` ([ADR-PC-023](./ADR-PC-023-temporal-signals-projection-derived.md)). The fold never reads "now", so replay stays deterministic.

3. **Ordering and delivery.** Per-instance append order (the outbox per-aggregate FIFO, [ADR-IC-004](../../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md)): `FundsHeld` before its `FundsReleased`; `AccountFrozen` before its `AccountUnfrozen`. A release of an unknown/already-released `HoldId`, or an unfreeze of an unknown/already-lifted `FreezeId`, is a **fold-surfaced reconciliation signal**, not a silent no-op. Balances and the frozen/active-hold predicates are folds, so a rebuild re-derives them identically ([ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md)). No new external delivery guarantee — the constraint is intra-engine.

4. **Idempotency — `HoldId` for the legal-hold lifecycle, `FreezeId` for the freeze lifecycle; the existing `CommandId` for each append.** A second `FundsReleased` for an already-released `HoldId` folds at most once (a reconciliation signal, not a double-restore); likewise `AccountUnfrozen` per `FreezeId`. The command that appends each fact is idempotent by its `CommandId` ([ADR-PC-029 slot 4](./ADR-PC-029-engine-command-ingress.md)). Two keys for two seams, as in [ADR-PC-033 slot 4](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md). Uniqueness window: the life of the instance's stream.

5. **Error model (the load-bearing slot) — the debit decision is gated; the folds never are.**
   - **A debit on a frozen instance is refused at the decider** ([ADR-PC-030 §P3](./ADR-PC-030-product-scope-and-boundary.md) stages 3–5): before the funds-available check, the decider evaluates the **freeze predicate** (is there an active `AccountFrozen` with no matching `AccountUnfrozen`?); if frozen, it appends a **refusal**, not a `HoldPlaced`, and the caller gets `declined` with a reason that **names the `FreezeReason` and `ComplianceActor`**. Pure read-state-and-append — no clock, no I/O — so it stays a deterministic decision even though its answer gates the debit.
   - **A debit that exceeds `available balance` net of legal holds is refused** the same way it already is for authorization holds — the legal hold simply lowered the number the decider reads. Nothing new in the gate mechanism; only the fold feeding it changed.
   - **The folds are never gated.** Recording `FundsHeld`/`FundsReleased`/`AccountFrozen`/`AccountUnfrozen`, and folding `available balance` or the frozen predicate, cannot fail independently of folding the stream. A freeze **blocks the authorization, never the recording of facts** — an incoming credit or an accrual on a frozen instance still folds (money may arrive; the ledger keeps score).
   - **The GL is untouched** ([ADR-PC-012](./ADR-PC-012-gl-posting-signal-contract.md)): a legal hold and a freeze are engine-internal constraints, never GL postings. Only a real debit/credit `Movement` is a business fact the GL may book.

6. **Ownership and versioning.** The **engine spine** owns the four `operations.*` events, the extended available-balance fold (both hold kinds), and the freeze predicate — **all name no family** (opaque `InstanceId`, generic `Money`/`HoldId`/`FreezeId`), so `ENGINE_FAMILY_AGNOSTIC` holds and the `family → engine` arrow stays one-way ([ADR-PC-021](./ADR-PC-021-application-layer-family-owned-deciders.md)). Each **family** owns which of its states is a hold-bearing/freezable account (the [ADR-PC-033](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md) seam) and which command surface emits the facts. **No pack rule** governs a freeze or a legal hold — unlike limits/arranged overdraft, these are legal/compliance overrides instructed from outside the product grammar, so they sit *before* the pack-rule stage, not inside it. Forward-only evolution ([ADR-IC-002](../../integration_concepts/adrs/ADR-IC-002-schema-format-and-registry.md)) once any of these is bus-promoted (they stay store-only until a named consumer appears — [ADR-IC-017](../../integration_concepts/adrs/ADR-IC-017-integration-event-promotion-criterion.md)).

**Rejected: fold the freeze into available balance too (pure option a).** Modelling a freeze as "subtract the whole balance" gives a false amount that breaks the moment a credit arrives (available would go negative-then-positive nonsensically) and conflates "blocked" with "broke". A total block is a predicate, not a number — it belongs in the decider. **Rejected: model the legal hold as a decider guard too (pure option b).** A legal hold *is* an amount earmark; re-expressing "available − legalHeld ≥ debit" as a bespoke guard duplicates the `Σ active holds` fold [ADR-PC-033](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md) already owns. Reuse the fold. **Rejected: reuse `HoldPlaced` for legal holds.** An authorization hold is transient and *captured* on settlement; a legal hold is externally instructed and *never* captured — overloading one event conflates two lifecycles and re-introduces the simple-name ambiguity this ADR exists to remove. **Rejected: clock-driven expiry.** An engine timer emitting `FundsReleased`/`AccountUnfrozen` when a horizon passes manufactures an event from a clock — [ADR-PC-023](./ADR-PC-023-temporal-signals-projection-derived.md) forbids it; expiry is a projection-derived read that prompts an appended fact.

---

## Consequences

**What this makes easier:**

1. **Legal holds cost almost nothing to enforce** — they reuse the built `Σ active holds` fold; the only new machinery is the `FundsReleased` lifting event and tagging the hold kind so the amount is attributed. `available balance` already flows through every debit decision.
2. **A freeze is a single well-placed predicate** — one guard at the top of the stages-3–5 decider, evaluated as a pure fold, with the reason carried into the decline. No new balance math.
3. **The reason for a constraint is always answerable** — the active-hold projection carries each hold's kind + `LegalReference`, and a freeze decline carries `FreezeReason`/`ComplianceActor`, so "why is €500 held / why was this refused?" is a read, not a forensic log dig.
4. **Replay stays total** — both balances, the active-hold set (both kinds), and the frozen predicate are rebuildable folds ([ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md)).

**What this makes harder or impossible:**

1. **A freeze needs the decider in the debit path** — a family whose debit does not route through the stages-3–5 decider gets no freeze enforcement for free; it must consult the freeze predicate. Accepted: the decider is the one place that already gates debits.
2. **Expiry is operational plumbing, not a timer** — an operator/command drives `FundsReleased`/`AccountUnfrozen` off a projection, more moving parts than a scheduler, but the only shape that keeps the fold pure ([ADR-PC-023](./ADR-PC-023-temporal-signals-projection-derived.md)).
3. **Two hold kinds in one fold demand disciplined attribution** — the projection must not conflate an authorization hold with a legal hold when it reports *why* funds are held (correctness-sensitive; covered by the reason-observable gate below).

---

## Residual risks (what this decision does **not** commit to)

- **"Block everything" (credits too) is not this decision.** A freeze here blocks **debits/withdrawals** only (the bd `babelstone-32hf` acceptance scope); allowing inbound credits and accrual on a frozen instance is the standard sanctions-freeze posture. A jurisdiction that requires a total block (no credits either) is a **stricter variant** — a follow-up amendment, not a silent behaviour.
- **Partial legal release is coarse.** `FundsReleased` lifts a `HoldId` in full; a partial garnishment release (lift €200 of a €500 hold) would need either a released-amount on the event or a re-hold — deferred, flagged, and out of scope until a real partial-release requirement lands.
- **Freeze vs in-flight authorization holds is not reconciled here.** Whether placing a freeze should also expire outstanding authorization holds, or leave them to capture/expire normally, is a family/compliance policy the transactional-family ADR owns — this ADR blocks *new* debits, it does not retroactively unwind approved-but-unsettled ones.
- **No cross-instance / customer-level freeze.** `AccountFrozen` freezes one `InstanceId`. Freezing every account a customer holds is an orchestration concern (fan-out over instances), not a single engine fact.
- **Emission/authority is unchanged.** *Who* may append a `FundsHeld`/`AccountFrozen` (and now `FundsReleased`/`AccountUnfrozen`), and the authorization on those commands, stays the command-surface's concern ([ADR-PC-029](./ADR-PC-029-engine-command-ingress.md)) — this ADR governs what the facts *do*, not who may state them.

---

## Verifiable commitments

This decision's load-bearing commitments are fitness functions ([ADR-PC-020 §P5–§P7](./ADR-PC-020-llm-toolchain-and-conformance-governance.md)). Because bd `babelstone-32hf` builds the enforcement in this change, they land **Live**, not Planned. They register into the [commitment catalogue](./commitment-catalogue.md) with this change.

| # | Commitment (with §-anchor) | Gate (pyramid level) | Test ID | Status |
|---|---|---|---|---|
| 1 | **A legal hold lowers spendable funds and is rebuildable** — `available balance = accounting balance − Σ active authorization-holds − Σ active legal-holds`; a `FundsHeld` reduces it until its `FundsReleased`, reproduced identically by a discard-and-rebuild (§Decision slot 1/2, §Consequences 4). | unit + architecture / replay-determinism (CI) | `LEGAL_HOLD_LOWERS_AVAILABLE` | Live |
| 2 | **A compliance freeze gates the debit decision, never the fold** — while an active `AccountFrozen` (no matching `AccountUnfrozen`) exists, the stages-3–5 decider refuses debits/withdrawals and names the `FreezeReason`/`ComplianceActor`; credits/accrual still fold; recording facts is never gated (§Decision slot 2/5). | unit / decider (CI) | `FREEZE_GATES_AUTHORIZATION` | Live |
| 3 | **The reason a constraint applies is observable** — the active-hold projection carries each hold's kind + `LegalReference`; a freeze decline carries `FreezeReason`/`ComplianceActor` (§Decision slot 1/5, §Consequences 3). | unit / projection + decline (CI) | `HOLD_REASON_OBSERVABLE` | Live |
| 4 | **Expiry is never a clock-reading fold** — `FundsReleased`/`AccountUnfrozen` are appended facts prompted by a projection over the advisory horizon; no fold reads "now" ([ADR-PC-023](./ADR-PC-023-temporal-signals-projection-derived.md); §Decision slot 2). Rides the existing engine determinism gate (`BENG001/002/003`). | architecture / determinism (CI) | `DETERMINISM_GATE` | Live |

---

## Cross-references

- [ADR-PC-033](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md) — the Account abstraction and the `operations.Hold*` authorization-hold lifecycle this ADR extends with a legal-hold kind and a freeze guard; the terminology it loosely called "the FundsHeld-family" is disambiguated here (see its §D5 dated note).
- [ADR-PC-030 §P3](./ADR-PC-030-product-scope-and-boundary.md) — the stages-3–5 authorization decider the freeze guard sits in front of.
- [ADR-PC-023](./ADR-PC-023-temporal-signals-projection-derived.md) — expiry as a projection-derived appended fact, never a clock-manufactured event.
- [ADR-IC-017](../../integration_concepts/adrs/ADR-IC-017-integration-event-promotion-criterion.md) — the store-only posture `FundsHeld`/`AccountFrozen` held in v1; this ADR gives their folds operation-constraining behaviour without promoting them to the bus.
- [ADR-PC-004](./ADR-PC-004-pii-crypto-shredding.md) — `LegalReference`/`FreezeReason`/`ComplianceActor` are structural identifiers, never PII.
- [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) — pure replayable folds; the `Money`/integer-cents discipline the held amount obeys.
- bd `babelstone-32hf` — the decision **and** its enforcement (this change).

---

*Accepted 2026-07-05 by jhosm.*
*Revised 2026-07-07: terminology only — the overdraft reference in §Decision slot 6 now reads as the UK term **arranged overdraft** (it previously carried the Portuguese label). A pure rename with no change to the legal-holds/freezes decision. Aligns with [ADR-PC-037](./ADR-PC-037-current-account-family.md); PR #462.*
