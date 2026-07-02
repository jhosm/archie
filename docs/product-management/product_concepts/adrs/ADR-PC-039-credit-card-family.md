# ADR-PC-039: credit_card Family — the Account/Revolving Slice of an Open-End Revolving Card on the One-Engine-Many-Families Spine

| Field | Value |
|---|---|
| Status | Proposed |
| Date | 2026-07-02 |
| Deciders | jhosm |
| Shape | Tool-selection ([ADR-PC-000 §D3](./ADR-PC-000-namespace-and-contract-shape-framework.md) residual category — a family-scoping / structural decision, declared tool-selection per the [§D4](./ADR-PC-000-namespace-and-contract-shape-framework.md) default; F1/F2 do not discriminate, the same class as [ADR-PC-021](./ADR-PC-021-application-layer-family-owned-deciders.md), [ADR-PC-030](./ADR-PC-030-product-scope-and-boundary.md), and [ADR-PC-031](./ADR-PC-031-personal-loan-family.md)) |
| Common criteria | [ADR-IC-000](../../integration_concepts/adrs/ADR-IC-000-common-evaluation-criteria.md) (reused per [ADR-PC-000](./ADR-PC-000-namespace-and-contract-shape-framework.md) D2) |
| Depends on | [ADR-PC-030](./ADR-PC-030-product-scope-and-boundary.md) (the product scope — names `credit_card` as roadmap item 3, the open-end revolving asset, and fixes the boundary: the engine owns the account/revolving slice, the four-party scheme stays outside), [ADR-PC-021](./ADR-PC-021-application-layer-family-owned-deciders.md) (the family-as-plugin spine this rides — pure folds + a family-owned decider over a family-agnostic engine), [ADR-PC-033](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md) (the Account abstraction the card account is a **transactional instance** of — accounting/available-balance split, the hold lifecycle, `available = accounting − Σ active holds` as a fold), [ADR-PC-032](./ADR-PC-032-money-movement-primitive.md) (the `Movement` primitive every card cash leg is; already-cleared postings arrive as `Observed` `Movement`s), [ADR-PC-034](./ADR-PC-034-realtime-authorization-technique.md) (the synchronous idempotent authorization technique the card's limit check rides), [ADR-PC-024](./ADR-PC-024-constitution-precondition-contract.md) (the precondition contract — credit-line origination stays upstream; the engine records verdicts, never grants the line), [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) (the deterministic kernel + the `Money`/decimal rounding discipline the revolving-interest math obeys), [ADR-PC-008](./ADR-PC-008-rate-sheet-storage-and-deploy-api.md) (rate/limit resolution at open + repricing — the decider's stamp), [ADR-PC-023](./ADR-PC-023-temporal-signals-projection-derived.md) (the statement cycle is a projection-derived calendar read, never a clock-manufactured engine event), [ADR-PC-036](./ADR-PC-036-lifecycle-command-driver.md) (the lifecycle-command driver that POSTs the statement-cycle / minimum-payment-due command on its due date) |
| Resolves | bd `babelstone-d0ob` (Scope credit_card account/revolving-slice family ADR); realises [ADR-PC-030 §Open Action 3](./ADR-PC-030-product-scope-and-boundary.md) |
| Related | The forthcoming **`conta à ordem` (current-account) family ADR — ADR-PC-037** (bd `babelstone-30hf`), the *first* transactional instance of [ADR-PC-033](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md), whose shape this family reuses and after which it is built; and the **settlement/posting-feed contract-shape ADR** it owns (bd `babelstone-30hf.5`) — the `Observed`-`Movement` capture feed this family *consumes*, not owns. Both are referenced by bd id, not linked, because they are not yet filed. |

---

## Context

[ADR-PC-030](./ADR-PC-030-product-scope-and-boundary.md) fixed what babelstone is *for* — a core product & account ledger — and drew a **family roadmap** spanning the retail product topology: a liability (`term_deposit`, *built*), a **closed-end asset** (`personal_loan`, *built* — [ADR-PC-031](./ADR-PC-031-personal-loan-family.md)), an **open-end revolving asset** (`credit_card`), and the **transactional/demand account** (`conta à ordem`, the hub the others settle against). [ADR-PC-033](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md) then fixed the Account abstraction every engine-owned aggregate is — a balance is a fold over `Movement`s, and a **transactional** account additionally folds a hold ledger so `available balance = accounting balance − Σ active holds`. This ADR adds the **third family**: the account/revolving slice of a credit card.

**In plain English.** A credit card is really two very different things bolted together. One is the **four-party scheme** — the card network, the merchant's acquirer, the authorization switch, clearing, settlement, chargebacks, and interchange fees — the machinery that moves money between a shop and your bank when you tap the card. The other is the **account** behind the card: a revolving credit line with a limit, interest that compounds on whatever you don't pay off, a monthly statement, a minimum payment, and a grace period that waives interest if you clear the balance in full by the due date. This ADR models **only the account side**. The scheme machinery stays firmly **outside** the engine ([ADR-PC-030](./ADR-PC-030-product-scope-and-boundary.md)): the engine never authorizes on the network, never clears, never settles, never adjudicates a chargeback, never books interchange. Instead it **consumes already-cleared postings** — every card purchase, refund, and fee arrives as an `Observed` `Movement` on a capture feed the `conta à ordem` family owns — and it does the three things a ledger does natively: it answers a real-time **limit check** (is there available credit for this authorization?), it **accrues revolving interest** on the outstanding balance, and it **issues a statement** each cycle with the minimum payment and the grace-period status. The card account is deliberately the **second instance** of the transactional-account shape ([ADR-PC-033](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md)) that the `conta à ordem` family proves first — it reuses that family's holds, available-balance fold, and authorize path rather than inventing them. And one event is special: the **statement is sealed**. Once issued it is legally immutable — a mistake is corrected by a *new* event next cycle, never by rewriting the old statement.

This entry is the [ADR-PC-000 §D3](./ADR-PC-000-namespace-and-contract-shape-framework.md) **residual category** ("operational/engineering discipline … fits neither template cleanly … default to tool-selection"), the same class as [ADR-PC-021](./ADR-PC-021-application-layer-family-owned-deciders.md), [ADR-PC-030](./ADR-PC-030-product-scope-and-boundary.md), and [ADR-PC-031](./ADR-PC-031-personal-loan-family.md): it scopes a **family**, not a tool. The honest consequence, surfaced up front: **F1 and F2 do not discriminate** — authoring a family buys no licence, and a card *account* slice carries no PII on the durable bus (the no-PII posture every family holds, [ADR-PC-004](./ADR-PC-004-pii-crypto-shredding.md)) and holds **no card/scheme credentials** (PAN, CVV, track data never enter the engine — those live entirely with the excluded scheme, [ADR-PC-030 §F2](./ADR-PC-030-product-scope-and-boundary.md)). The load-bearing question is **how to model the card's genuinely-new dimensions — revolving interest, the statement cycle, the minimum payment, and the grace period — on the existing spine without pulling any scheme machinery into the boundary, and without letting the sealed statement break replay-determinism**.

### Build order and reuse — the card follows the current account

Per [ADR-PC-030 §P2 as revised 2026-07-02](./ADR-PC-030-product-scope-and-boundary.md) (the amendment this change lands — [ADR-PC-020 §D3](./ADR-PC-020-llm-toolchain-and-conformance-governance.md) explicit-drift), the roadmap order for the two transactional families is **reversed** from ADR-PC-030's original `credit_card`-before-`conta à ordem` sequence: `conta à ordem` (ADR-PC-037, bd `babelstone-30hf`) is now built **first**, `credit_card` **after** it. The reason is reuse, not preference. The `conta à ordem` is the *first* transactional instance of the [ADR-PC-033](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md) Account abstraction — it is where the holds, the `available balance` fold, and the real-time authorize path are first *implemented* against a live family. The card account is the **second** instance of that exact shape: a credit line is an account whose `available balance` is `credit_limit − outstanding − Σ active holds`, authorized the same way, held the same way. Building the current account first means the card lands against a *proven* transactional-account implementation instead of co-inventing it. This is the same de-risking move [ADR-PC-031](./ADR-PC-031-personal-loan-family.md) made (the closed-end loan reused the term-deposit shape) and [ADR-PC-033](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md) made (fixing the Account model before its first transactional family).

### What is genuinely new vs. the current account and the closed-end loan

The card account reuses the transactional-account shape ([ADR-PC-033](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md)) wholesale and the `Movement`/settlement substrate ([ADR-PC-032](./ADR-PC-032-money-movement-primitive.md)) wholesale. Four dimensions are genuinely new, and they are the whole content of the modelling:

| # | New dimension | What it is | Where it lives |
|---|---|---|---|
| 1 | **Revolving interest** (*juro rotativo*) | Interest accrues on the outstanding balance and **capitalizes** at cycle close: `S(m) = S(m-1)·(1+r) − P(m)` — mathematically a Price credit on a balance that changes with every posting (fin-math [§8.4–§8.6](../../financial_concepts/banking_products_financial_mathematics.md)). Unpaid interest is added to capital — compound interest disguised as monthly simple interest. | A pure `Revolving` kernel in `Babelstone.FinancialMath` (generic, names no family); the decider runs it command-side at cycle close. |
| 2 | **The statement cycle** | Each cycle the account's postings are summed into a **closing balance**, a **minimum payment**, a **payment due date**, and the [TAEG](../../financial_concepts/banking_products_financial_mathematics.md) disclosure (fin-math [§8.7](../../financial_concepts/banking_products_financial_mathematics.md)), issued as a **sealed** `CardStatementIssued` fact. The cycle date is a **projection-derived calendar read** ([ADR-PC-023](./ADR-PC-023-temporal-signals-projection-derived.md)), fired by the [ADR-PC-036](./ADR-PC-036-lifecycle-command-driver.md) lifecycle-command driver — never a clock event in the engine. | The decider's `IssueStatementAsync`, driven by the lifecycle-command driver; the statement is a **sealed event** (§D4). |
| 3 | **The minimum payment** | The regulator/pack-bounded minimum the holder must pay by the due date — a computed fact stamped onto the sealed statement (`max(pack_floor, pct × closing_balance)`, capped at the closing balance). | Computed command-side by the decider, carried on `CardStatementIssued`; the pack owns the percentage/floor. |
| 4 | **The grace period** (*período de gração*) | If the prior statement was paid **in full by its due date**, new purchases in the current cycle accrue **no** interest; otherwise revolving interest accrues from the posting date. Whether grace applies **depends on the sealed prior statement** and must be carried in the fold **across the statement boundary**. | A fold input pinned from the prior sealed statement (§D4); the decider reads it to decide whether to accrue. |

**Candidates evaluated** (how to model the card account family):

| # | Candidate | Notes |
|---|---|---|
| A | **Model the card account as a second transactional instance of the [ADR-PC-033](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md) Account** — own event records, pure folds, a lifecycle legality table, projections, an `IFamilyModule`, and a family-owned decider; reuse the holds / available-balance / authorize path; consume already-cleared postings as `Observed` `Movement`s from the `conta à ordem`-owned feed; place revolving-interest math as a generic kernel in `Babelstone.FinancialMath`; issue the statement as a **sealed event**. | The [ADR-PC-021](./ADR-PC-021-application-layer-family-owned-deciders.md) plugin shape applied unchanged, riding the [ADR-PC-033](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md) transactional-account shape. Zero generic-engine diff; the math kernel is generic (names no family); the four-party scheme stays outside. |
| B | **Pull the four-party scheme (authorization network, clearing, settlement, chargeback, interchange) into the engine.** | Contradicts [ADR-PC-030](./ADR-PC-030-product-scope-and-boundary.md) (posture B stops at the wire; posture D — a card switch/PSP — was rejected there). Real-time, I/O-bound, scheme-certified, PSD2-regulated payment *execution* — not a pure fold. |
| C | **Model the statement as a replayable projection** rather than a sealed event. | An issued statement is a legal document; re-deriving it on replay lets a later code/rate change silently alter a *past* statement's numbers — the correctness failure the sealed-event requirement exists to forbid ([ADR-PC-030 §176](./ADR-PC-030-product-scope-and-boundary.md)). |
| D | **Put the revolving-interest kernel in the family project**, not `FinancialMath`. | Duplicates the rounding discipline ([ADR-PC-010 §P1–§P2](./ADR-PC-010-dotnet-hand-rolled-engine.md)) the kernel centralises — and the `Amortization` kernel ([ADR-PC-031 §P2](./ADR-PC-031-personal-loan-family.md)) already proved revolving is a *Price credit on a moving balance*, so the two share arithmetic. |

---

## Evaluation

### Hard filter results

#### F1 · Cost / licensing

| Candidate | Licence / cost | Verdict |
|---|---|---|
| A · card account on the Account abstraction | No tool, no licence; new family projects + a generic kernel addition. | **Pass** |
| B · scheme in-engine | Same licence (zero), but a card-switch/PSP build + scheme certification — a different order of effort. | **Pass** |
| C · statement as projection | Same; no new project. | **Pass** |
| D · kernel in the family | Same; no new project. | **Pass** |

Uniform pass — F1 does not discriminate (a family buys nothing).

#### F2 · Regulatory fit (GDPR / DORA / PSD2)

A family is not itself a regulated runtime artefact, so F2 cannot *fail* a candidate. The regulatory-weight properties this family exercises are owned by *other* ADRs and hold identically under candidates A/C/D: **no PII and no card/scheme credentials on the durable bus** ([ADR-PC-004 §P2](./ADR-PC-004-pii-crypto-shredding.md) — every card event carries computed facts + opaque references, never a PAN, CVV, cardholder name, NIF, or IBAN), **origination stays upstream** ([ADR-PC-024](./ADR-PC-024-constitution-precondition-contract.md) / [ADR-PC-030 §P1](./ADR-PC-030-product-scope-and-boundary.md) — the credit line is granted upstream; the engine records the decision, never makes it), and **the engine is not a PSD2 payment-services provider** ([ADR-PC-030 §F2](./ADR-PC-030-product-scope-and-boundary.md) — it never executes on the rails, performs SCA, screens fraud, or books interchange). The PT/EU consumer-credit **TAEG disclosure** and **minimum-payment / revolving-rate caps** are *correctness* properties of the decider's math and the pack constraints, gated by tests, not a filter a candidate passes or fails.

| Candidate | GDPR | DORA / PSD2 | Verdict |
|---|---|---|---|
| A / C / D | No PII, no card/scheme credentials on the bus; erasure event present; origination upstream. | No payment execution, no scheme, no SCA/fraud/interchange in-engine (stops at the wire, [ADR-PC-030](./ADR-PC-030-product-scope-and-boundary.md)). | **Pass** |
| B · scheme in-engine | Largest surface — cardholder/scheme data at volume. | Engine performs scheme authorization, clearing, settlement — PSD2-regulated payment execution. | **Pass** (largest surface; contradicts [ADR-PC-030](./ADR-PC-030-product-scope-and-boundary.md)) |

A, C, and D clear the hard filters; B clears F1/F2 but **contradicts the [ADR-PC-030](./ADR-PC-030-product-scope-and-boundary.md) boundary** by construction. The decision is in S1–S4 plus the sealed-statement and boundary analysis — the expected shape for the [§D3](./ADR-PC-000-namespace-and-contract-shape-framework.md) residual category.

---

### Soft criteria

#### A · Card account on the [ADR-PC-033](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md) transactional-account shape — **CHOSEN**

**S1 · Operational complexity for 1–2 people.** Lowest of the account-modelling options. The family is a self-contained subtree (`families/credit-card/`) added the [ADR-PC-021](./ADR-PC-021-application-layer-family-owned-deciders.md) way — drop in the fold project + the decider project, wire nothing in the generic engine. The holds, the `available balance` fold, and the authorize path are **already implemented** by the `conta à ordem` family (ADR-PC-037, built first); the card supplies only its own state through the [ADR-PC-033](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md) `IAccount` seam. The only generic-side change is the additive `Revolving` kernel in `Babelstone.FinancialMath`, which names no family.

**S2 · Ecosystem coherence — decisive.** The engine already commits to a **family-as-plugin** model ([ADR-PC-021 §D2](./ADR-PC-021-application-layer-family-owned-deciders.md)) and a **transactional-account abstraction** ([ADR-PC-033](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md)); A extends both to a second transactional family without a new spine concept. The dependency arrow stays **family → engine** (gated by `ENGINE_FAMILY_AGNOSTIC`), the folds stay pure (the revolving math runs command-side, gated by the determinism analysers), and the `Money`/decimal rounding discipline ([ADR-PC-010 §P1–§P2](./ADR-PC-010-dotnet-hand-rolled-engine.md)) governs the interest math. B bends the boundary back (a scheme runtime inside the engine); C breaks replay-correctness of an issued statement; D duplicates the rounding discipline.

**S3 · Exit cost.** Low. The card's authorize/hold path *is* the [ADR-PC-033](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md) path and the `conta à ordem` implementation of it; the posting feed is a *consumed* contract owned elsewhere (bd `babelstone-30hf.5`), so the card carries no clearing/settlement code to unwind. The revolving kernel sits beside `Amortization` in `FinancialMath`.

**S4 · Longevity.** Neutral — the layering outlives any one family; the revolving kernel and the sealed-statement pattern are reusable assets a future overdraft-interest or store-card family would inherit.

**Decisive project-specific reason — completes the retail topology and proves the transactional-account abstraction generalises to a *credit line*.** [ADR-PC-030](./ADR-PC-030-product-scope-and-boundary.md)'s thesis is one deterministic-ledger kernel expressing every retail product shape. The card account is the **open-end revolving asset** — the one shape neither the closed-end loan nor the demand account covers — and modelling it as a *second* transactional-account instance (a credit line is just an account whose available balance is `limit − outstanding − Σ holds`) is the strongest evidence the [ADR-PC-033](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md) abstraction is not secretly current-account-shaped. A delivers that with zero generic-engine diff and the scheme firmly outside.

#### B · Pull the four-party scheme into the engine — **rejected**

Authorization-network, clearing, settlement, chargeback, and interchange are real-time, I/O-bound, scheme-certified, PSD2-regulated payment *execution* — [ADR-PC-030](./ADR-PC-030-product-scope-and-boundary.md) rejected exactly this as posture D (a card switch/PSP build that breaks fold purity and turns a 1–2-person reference project into a processor). Rejected on S1 + S2 + the [ADR-PC-030](./ADR-PC-030-product-scope-and-boundary.md) boundary; the *product* aspects it would unlock are captured by A's account slice, which stops at the wire and consumes already-cleared postings.

#### C · Statement as a replayable projection — **rejected**

A projection is re-derived from the log on every rebuild. An issued statement is a **legal document** with a fixed closing balance, minimum payment, and TAEG disclosure; re-deriving it means a later rate-table fix or code change silently alters a *past* statement's numbers — the correctness failure the [ADR-PC-030 §176](./ADR-PC-030-product-scope-and-boundary.md) sealed-event requirement forbids. Rejected on S2; the statement must be a **sealed fact**, appended once and never recomputed (§D4).

#### D · Revolving kernel in the family project — **rejected**

The `Money`/decimal one-boundary rounding discipline ([ADR-PC-010 §P1–§P2](./ADR-PC-010-dotnet-hand-rolled-engine.md)) exists to be centralised; a revolving kernel in the family project would duplicate it and re-implement arithmetic the `Amortization` kernel already carries (fin-math [§8.5](../../financial_concepts/banking_products_financial_mathematics.md): *paying a fixed payment on a revolving balance is mathematically a Price credit*). The kernel belongs beside `Accrual` / `Withholding` / `Amortization` in `Babelstone.FinancialMath` — generic, naming no family. Rejected on S2/S4.

**Decisive reason for A:** the engine is the reusable asset, the transactional-account shape already exists (built first as the `conta à ordem`), and the sealed statement is the one genuinely-new correctness concern — modelling the card as a second transactional-account instance with a generic revolving kernel and a sealed statement is the only candidate that adds the open-end revolving shape with zero generic-engine diff while keeping the four-party scheme outside. B/C/D each break one of those.

---

## Decision

### `credit_card` is the account/revolving slice of an open-end revolving card, modelled as a **second transactional instance** of the [ADR-PC-033](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md) Account on the [ADR-PC-021](./ADR-PC-021-application-layer-family-owned-deciders.md) spine; the four-party scheme stays outside the boundary; the statement is a **sealed event**.

- **D1 — A family on the proven spine, a transactional account by reuse.** `credit_card` is authored exactly as the reference families: pure fold handlers + event records (`Babelstone.Families.CreditCard`), a lifecycle legality table, projections, an `IFamilyModule`, and a family-owned decider (`Babelstone.Families.CreditCard.Application`). The card **account** declares itself a transactional [ADR-PC-033](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md) account through the spine-owned `IAccount` seam — the *second* instance of that shape after the `conta à ordem` (ADR-PC-037), reusing the holds, the `available balance` fold, and the authorize path rather than re-deriving them. For a card, `available credit = credit_limit − outstanding − Σ active holds`. The dependency arrow is **family → engine**; adding it is **zero generic-engine diff** (the `Revolving` kernel is the only generic addition, and it names no family — gated by `ENGINE_FAMILY_AGNOSTIC`).
- **D2 — The open-end revolving lifecycle.** The card lifecycle is `open → active (revolving, cycling) → (settled/closed | written off)`, plus the open-refusal and GDPR-erasure terminals. The family-owned events are `CardAccountOpened`, `CardAccountOpenFailed`, `RevolvingInterestAccrued`, `CardStatementIssued` (**sealed**, §D4), `CardRepaymentReceived`, `CardAccountClosed`, `CardWrittenOff`; the states are `Pending → Active → (Failed | Closed | WrittenOff) ; Erased`. The **holds** (`HoldPlaced → HoldCaptured | HoldExpired`) and the **captured postings** are **not** family-owned events — they are the [ADR-PC-033](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md) cross-cutting `operations.Hold*` records and the [ADR-PC-032](./ADR-PC-032-money-movement-primitive.md) `Movement`s the spine already owns. GDPR erasure is likewise the engine-declared cross-cutting `operations.PersonalDataErasureRequested` folded via `IErasable` ([ADR-PC-004 §A4](./ADR-PC-004-pii-crypto-shredding.md)). The single `LifecycleTransitions` table is the auditable legality source the decider consults before appending; revolving/statement transitions run **only from `Active`**.
- **D3 — The four-party scheme stays outside; the engine consumes already-cleared postings.** The engine performs **none** of: card-network authorization messaging, clearing, settlement, chargeback adjudication, or interchange calculation — those are the excluded scheme ([ADR-PC-030](./ADR-PC-030-product-scope-and-boundary.md) posture-D rejection). The engine's *only* real-time role is the **funds-and-rules limit check** ([ADR-PC-030 §P3](./ADR-PC-030-product-scope-and-boundary.md) stages 3–5): read `available credit`, apply pack rules (credit limit, velocity), and append `HoldPlaced` or a decline — the [ADR-PC-034](./ADR-PC-034-realtime-authorization-technique.md) synchronous idempotent path, reusing the [ADR-PC-033](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md) hold. Every already-cleared purchase, refund, fee, and interchange net-off arrives as an **`Observed` `Movement`** ([ADR-PC-032 §A4](./ADR-PC-032-money-movement-primitive.md)) on the **capture feed the `conta à ordem` family owns** (the settlement/posting-feed contract-shape ADR, bd `babelstone-30hf.5`) — this family *consumes* that contract, it does not own or restate it. A capture releases the matching hold (`HoldCaptured`) and moves the accounting balance; the card never books the wire.
- **D4 — Statement issuance is a SEALED event; grace-period determinism is carried in the fold across statement boundaries.** `CardStatementIssued` is **legally immutable once appended** — it freezes the cycle's closing balance, minimum payment, payment due date, TAEG disclosure, and grace-period status. It is **not a replayable projection**: the fold **records the sealed fact and never recomputes it** on rebuild (a later rate/code change must not alter a past statement). **Corrections are new events in the next cycle** (an adjusting `Movement` and the next `CardStatementIssued`), never a re-fold of the sealed one. **Grace-period determinism carries across the statement boundary**: whether new purchases accrue interest depends on *whether the prior sealed statement was paid in full by its due date*, so the fold pins the prior sealed statement's paid-in-full flag forward as an input to the current cycle's accrual decision. The statement's **cycle date is a projection-derived calendar read** ([ADR-PC-023](./ADR-PC-023-temporal-signals-projection-derived.md)) fired by the [ADR-PC-036](./ADR-PC-036-lifecycle-command-driver.md) lifecycle-command driver — the engine manufactures no clock event. **Deferred concern (per the conformance advisory on bd `babelstone-d0ob`):** the sealed-event-vs-replay tension — a sealed statement is a fact the fold must *reproduce identically without recomputing* under a discard-and-rebuild — is flagged as the **replay-determinism-auditor**'s lane when that auditor is authored; this ADR fixes the *contract* (sealed, corrected-next-cycle, grace carried in the fold), not the replay-mechanism proof.
- **D5 — Credit-line origination is upstream; the engine records, never grants.** Per [ADR-PC-030 §P1](./ADR-PC-030-product-scope-and-boundary.md) and [ADR-PC-024](./ADR-PC-024-constitution-precondition-contract.md), the card arrives **already approved and priced** — the credit limit, the revolving rate, and the minimum-payment terms are set upstream (underwriting, scoring, affordability). The engine resolves the rate/limit sheet for lineage, records the granted limit, runs the limit check, accrues interest, and issues statements. Solvency/CRC/KYC checks are recorded only as **opaque precondition verdicts** ([ADR-PC-024](./ADR-PC-024-constitution-precondition-contract.md)) the decider refuses on if absent/unsatisfied. Collections *enforcement* (PARI/PERSI) is upstream; the engine only *records* a `CardWrittenOff`.

**Rejected: pulling the four-party scheme into the engine** — real-time, scheme-certified, PSD2-regulated payment execution, not a pure fold ([ADR-PC-030](./ADR-PC-030-product-scope-and-boundary.md) posture D). **Rejected: the statement as a replayable projection** — a later change would silently alter a past legal statement. **Rejected: the revolving kernel in the family project** — duplicates the centralised money rounding discipline and re-implements Price-credit arithmetic.

---

## Implementation Principles

### P1 — Project topology (the [ADR-PC-021 §P1](./ADR-PC-021-application-layer-family-owned-deciders.md) shape)

```
families/credit-card/src/
  Babelstone.Families.CreditCard/              pure folds + events + projections + lifecycle table
      refs: Babelstone.Engine, Babelstone.FinancialTypes        (cannot reach a DB or the kernel)
  Babelstone.Families.CreditCard.Application/  the decider (commands → events)
      refs: Babelstone.Engine, Babelstone.FinancialMath, Babelstone.FinancialTypes,
            Babelstone.RateSheets, Babelstone.Packs, Babelstone.Families.CreditCard
engine/src/Babelstone.FinancialMath/
  + Revolving                                    the one new generic, family-agnostic kernel
contracts/
  avro/cards/credit_card/*.avsc                the bus-published event payload schemas
  cue/families/credit-card.cue                 the product-config family schema
```

### P2 — The revolving-interest kernel is generic and obeys the money discipline

`Revolving` (`PeriodInterestOnDailyBalance`, `CapitalizeAtCycleClose`, `OutstandingAfter`, `MinimumPayment`, `PayoffMonths`) lives beside `Accrual` / `Withholding` / `Amortization` in `Babelstone.FinancialMath`. It names no family (so `ENGINE_FAMILY_AGNOSTIC` holds), computes wholly in `decimal`, crosses to `Money` exactly once per amount ([ADR-PC-010 §P2](./ADR-PC-010-dotnet-hand-rolled-engine.md)), and implements the fin-math [§8.2–§8.6](../../financial_concepts/banking_products_financial_mathematics.md) balance model: interest on the daily balance capitalized at cycle close, `S(m) = S(m-1)·(1+r) − P(m)` — the same difference equation as Price ([§8.5](../../financial_concepts/banking_products_financial_mathematics.md)), so it shares arithmetic with `Amortization` rather than duplicating it. The `(1+r)^n` payoff-duration term uses the kernel's existing `DecimalMath.Pow`, never `Math.Pow`.

### P3 — The card account is a transactional [ADR-PC-033](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md) account; folds stay pure; the decider is the impure layer

The card projection state declares itself an account through the [ADR-PC-033](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md) `IAccount` seam and folds `available credit = credit_limit − outstanding − Σ active holds` with the **spine-owned generic fold** — the family supplies only its state, no balance is a stored mutable number. The folds LABEL state and accumulate carried facts (the outstanding balance, the accrued interest, the active holds, the pinned prior-statement grace flag) — they never *compute* revolving interest or a minimum payment. The decider runs the `Revolving` kernel command-side and emits `RevolvingInterestAccrued` / `CardStatementIssued`; the folds record the already-computed facts. This is the same fold-purity discipline the deposit/loan families hold (gated by `DETERMINISM_GATE`).

### P4 — The posting feed is consumed, not owned

Already-cleared card movements (purchases, refunds, fees, interchange net-offs) arrive as **`Observed` `Movement`s** ([ADR-PC-032 §A4](./ADR-PC-032-money-movement-primitive.md)) via the **settlement/posting-feed contract-shape ADR the `conta à ordem` family owns** (bd `babelstone-30hf.5`). This family declares the `reason` strings its captures carry and folds them into the outstanding balance and hold releases; it **never** owns, restates, or hand-codes clearing/settlement/chargeback/interchange logic. A chargeback or reversal arrives as an `Observed` reversing `Movement` on the same feed — recorded, not adjudicated.

### P5 — The statement cycle is a projection-derived, driver-fired sealed fact

The statement/minimum-payment-due date is a **forward calendar projection** ([ADR-PC-023](./ADR-PC-023-temporal-signals-projection-derived.md)) folded from `CardAccountOpened` + `CardStatementIssued`; the [ADR-PC-036](./ADR-PC-036-lifecycle-command-driver.md) lifecycle-command driver reads it as-of today and POSTs one `IssueStatement` command per due occurrence through the [ADR-PC-029](./ADR-PC-029-engine-command-ingress.md) surface (decider purity + legality gate run; the cycle date rides as `valid_time`). The recurring occurrence key is the **stable cycle number, never the due date** (the same number-pinned idempotency [ADR-PC-036](./ADR-PC-036-lifecycle-command-driver.md) uses for loan installments). The decider seals the statement (§D4) and pins the paid-in-full flag forward for the next cycle's grace decision.

### P6 — Discovery is host assembly-scan; no per-family hand-edit

The family is discovered by the existing `FamilyModuleLoader` (folds) and `HostModuleLoader` (host wiring) assembly scans ([ADR-PC-021 §A13–§A14](./ADR-PC-021-application-layer-family-owned-deciders.md)). Authoring the family is its projects + the host `ProjectReference` (the scan anchor), never a surgical edit to generic compose code.

---

## Consequences

**What this choice makes easier:**

1. **The retail topology is complete.** With the card account, one deterministic-ledger kernel now expresses all four retail shapes — liability, closed-end asset, **open-end revolving asset**, transactional account — the strongest demonstration that the family abstraction generalises ([ADR-PC-030 §S2](./ADR-PC-030-product-scope-and-boundary.md)).
2. **The transactional-account abstraction is proven at n = 2.** A credit line reuses the [ADR-PC-033](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md) holds / available-balance / authorize path with only the available-balance *formula* changed (`limit − outstanding − Σ holds`), evidence the abstraction is not secretly current-account-shaped.
3. **A reusable revolving kernel and sealed-statement pattern.** A future overdraft-interest or store-card family inherits `Revolving`, the centralised money rounding, and the sealed-statement discipline, not a re-implementation.

**What this choice makes harder or impossible:**

1. **The sealed statement is a fact the fold must carry, not recompute.** The statement's numbers are frozen at issuance, so the fold must reproduce a past `CardStatementIssued` identically under replay *without* re-deriving it — a determinism concern **deferred to the replay-determinism-auditor** (§D4). Accepted as the price of statement legality (rejected candidate C would have made past statements silently mutable).
2. **The card depends on the `conta à ordem`-owned posting feed and on that family being built first.** The already-cleared-posting capture feed (bd `babelstone-30hf.5`) and the transactional-account implementation (ADR-PC-037) are prerequisites; until they land, the card family's captures and holds have no substrate. The family declares its captures *as data* (`reason` + direction); it never hand-codes clearing.
3. **The four-party scheme is out of scope by construction.** Anyone wanting network authorization, clearing, settlement, chargeback, or interchange must integrate an external scheme/processor or reopen [ADR-PC-030](./ADR-PC-030-product-scope-and-boundary.md) (posture D, a supersession).

**Residual risks:**

- **Grace-period determinism across cycles.** The grace decision reads the prior sealed statement's paid-in-full flag; the fold must pin that flag across the statement boundary so a rebuild reproduces the same accrual choice. Owned by this family's fold + the deferred replay-determinism-auditor concern.
- **Partial captures, reversals, and re-presentments** arrive as `Observed` `Movement`s on the consumed feed and are reconciliation-sensitive — the hold/capture reconciliation policy is this family's to specify within the [ADR-PC-033](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md) abstraction (its §Residual-risks flag).
- **Pack-grammar for revolving-rate / minimum-payment / limit constructs** widens the [ADR-PC-006](./ADR-PC-006-cue-schema-language.md)/[ADR-PC-007](./ADR-PC-007-signed-yaml-oci-pack.md) surface; must stay declarative (the same expansion [ADR-PC-030 §Residual-risks](./ADR-PC-030-product-scope-and-boundary.md) named for limits/*descoberto autorizado*).

---

## Verifiable commitments

This decision's load-bearing commitments are fitness functions ([ADR-PC-020 §P5–§P7](./ADR-PC-020-llm-toolchain-and-conformance-governance.md)). The card family's family-agnosticism rides the existing [`ENGINE_FAMILY_AGNOSTIC`](./commitment-catalogue.md) gate unchanged (the `Revolving` kernel and the card's `IAccount`-seam use name no family), and its fold purity rides the existing `DETERMINISM_GATE`. Its account/hold behaviour rides [ADR-PC-033](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md)'s `ACCOUNT_BALANCE_IS_A_FOLD` / `HOLD_LIFECYCLE_PURE` and its cash legs ride [ADR-PC-032](./ADR-PC-032-money-movement-primitive.md)'s `MOVEMENT_APPEND_FIRST` / `MOVEMENT_CASH_LEG_IDEMPOTENT`. The two **new** commitments below are family-specific; following the [ADR-PC-033](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md) / [ADR-PC-032](./ADR-PC-032-money-movement-primitive.md) precedent they are **not yet catalogued centrally** — they register into the [commitment catalogue](./commitment-catalogue.md) when the implementing issue lands their gates (this family is built *after* the `conta à ordem`, ADR-PC-037), the same `Planned`-then-promote posture:

| # | Commitment (with §-anchor) | Gate (pyramid level) | Test ID | Status |
|---|---|---|---|---|
| 1 | The revolving-interest math **capitalizes at cycle close and conserves to the cent** — `S(m) = S(m-1)·(1+r) − P(m)`, interest on the daily balance, the minimum payment `max(pack_floor, pct × closing_balance)` capped at the closing balance (§D4, §P2). Pinned by `RevolvingMathTests` (the worked fin-math [§8.4–§8.6](../../financial_concepts/banking_products_financial_mathematics.md) example matched to the cent, the payoff-duration and conservation invariants). | unit (Docker-free) | `CREDITO_CARTAO_REVOLVING_MATH` | Planned |
| 2 | The statement is a **sealed fact reproduced identically without recomputation**, corrections are new next-cycle events, and **grace-period determinism is carried in the fold across the statement boundary** — a discard-and-rebuild reproduces every past `CardStatementIssued` and grace decision identically (§D4). **This is the concern deferred to the replay-determinism-auditor** (bd `babelstone-d0ob` conformance advisory). | replay-determinism (CI) | `CARD_STATEMENT_SEALED_REPLAYABLE` | Planned |

A `Planned` status is a deliberate, listed hole: the tests land with the family implementation (built after ADR-PC-037), and both rows register as the [ADR-PC-020 §P6](./ADR-PC-020-llm-toolchain-and-conformance-governance.md) coverage checker's target when they go `Live`. The four-party scheme staying outside the boundary is *not* a new executable commitment here — it is enforced by the same boundary posture [ADR-PC-030](./ADR-PC-030-product-scope-and-boundary.md) draws (no scheme/rails/settlement code enters the engine; the card consumes `Observed` `Movement`s only).

---

## Cross-references

- [ADR-PC-030](./ADR-PC-030-product-scope-and-boundary.md) — the product scope that names `credit_card` as the open-end revolving asset (roadmap item 3), fixes the boundary (the four-party scheme outside; the engine consumes already-cleared postings), and — **as amended by this change (§P2 revised 2026-07-02)** — reverses the build order so the card follows the `conta à ordem`, landing as the second transactional instance of the [ADR-PC-033](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md) Account.
- [ADR-PC-033](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md) — the Account abstraction the card account is a *second transactional instance* of (holds, `available balance` fold, authorize path).
- [ADR-PC-032](./ADR-PC-032-money-movement-primitive.md) — the `Movement` atom every card cash leg is; already-cleared postings arrive as `Observed` `Movement`s.
- [ADR-PC-034](./ADR-PC-034-realtime-authorization-technique.md) — the synchronous idempotent authorization technique the card's limit check rides.
- [ADR-PC-021](./ADR-PC-021-application-layer-family-owned-deciders.md) — the one-engine-many-families spine this family rides (pure folds + a family-owned decider over a family-agnostic engine).
- [ADR-PC-024](./ADR-PC-024-constitution-precondition-contract.md) — the precondition contract keeping credit-line origination upstream; the card family reuses it for the solvency/CRC verdicts.
- [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) — the deterministic kernel + the `Money`/decimal one-boundary rounding the `Revolving` kernel obeys.
- [ADR-PC-023](./ADR-PC-023-temporal-signals-projection-derived.md) / [ADR-PC-036](./ADR-PC-036-lifecycle-command-driver.md) — the statement cycle is a projection-derived calendar read fired by the lifecycle-command driver, never a clock-manufactured engine event.
- [ADR-PC-031](./ADR-PC-031-personal-loan-family.md) — the sibling closed-end-asset family; the `Amortization` kernel the `Revolving` kernel shares Price-credit arithmetic with.
- [financial_concepts §8.1–§8.7](../../financial_concepts/banking_products_financial_mathematics.md) — the revolving-balance (irregular-product) and TAEG math the `Revolving` kernel implements.
- The forthcoming **`conta à ordem` family ADR (ADR-PC-037, bd `babelstone-30hf`)** — the first transactional instance of [ADR-PC-033](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md) this family follows and reuses; and its **settlement/posting-feed contract-shape ADR (bd `babelstone-30hf.5`)** — the capture feed this family consumes. Referenced by bd id; not yet filed.
- [01 §1](../01-product-architecture.md) — the one-engine-many-families thesis this family extends to the open-end revolving shape.

---

*Proposed 2026-07-02 by jhosm.*
