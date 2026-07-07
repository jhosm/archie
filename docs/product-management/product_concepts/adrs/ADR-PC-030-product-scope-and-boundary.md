# ADR-PC-030: babelstone Product Scope & Boundary — a Core Product & Account Ledger

| Field | Value |
|---|---|
| Status | Accepted |
| Date | 2026-06-20 |
| Deciders | jhosm |
| Shape | Tool-selection |
| Common criteria | [ADR-IC-000](../../integration_concepts/adrs/ADR-IC-000-common-evaluation-criteria.md) (reused per [ADR-PC-000](./ADR-PC-000-namespace-and-contract-shape-framework.md) D2; this is the [ADR-PC-000 §D3](./ADR-PC-000-namespace-and-contract-shape-framework.md) "operational discipline" residual category — a scope/posture decision, declared tool-selection per the [§D4](./ADR-PC-000-namespace-and-contract-shape-framework.md) default) |
| Depends on | [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) (the hand-rolled deterministic kernel whose nature this bounds), [ADR-PC-021](./ADR-PC-021-application-layer-family-owned-deciders.md) (the one-engine-many-families spine the roadmap rides on), [ADR-PC-024](./ADR-PC-024-constitution-precondition-contract.md) (engine declares preconditions, upstream evaluates — keeps origination out of the kernel), [ADR-PC-029](./ADR-PC-029-engine-command-ingress.md) (the synchronous idempotent command path this extends to the authorization regime), [ADR-PC-016](./ADR-PC-016-legacy-current-account-adapter.md) (the conta à ordem coexistence/settlement contract whose v4 migration this names the destination of) |
| Resolves | bd `babelstone-nyan` (ADR-PC-030: babelstone product scope & boundary) |

---

## Context

The engine has, until now, been **scoped by accretion**: [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) fixed *what kind of thing* it is (a hand-rolled, event-sourced kernel), [ADR-PC-021](./ADR-PC-021-application-layer-family-owned-deciders.md) fixed *how it grows* (one engine, many families), and a single reference family — `term_deposit` — proved both. No ADR has yet stated, in one place, **what babelstone is *for* and where its responsibility *stops*.** That gap is now load-bearing: market research into candidate products ([crédito pessoal](../research/personal-loan/00-research-plan.md), [credit cards](../research/credit-cards/00-research-plan.md)) and the question of the **conta à ordem** (the demand/current account) force it, because answering "should we build them" is impossible without first fixing the boundary itself.

This entry is the [ADR-PC-000 §D3](./ADR-PC-000-namespace-and-contract-shape-framework.md) **residual category** ("operational discipline … fits neither template cleanly … default to tool-selection"), the same class as [ADR-PC-019](./ADR-PC-019-repository-strategy-monorepo.md): it selects a **posture**, not a tool. The honest consequence, surfaced up front: **F1 and F2 do not discriminate** — a scope statement buys nothing and (see F2) does not itself make the engine a regulated payment-services provider. The load-bearing question is which posture keeps the kernel coherent while letting it model the products a reference banking engine should — settled on S1–S4 plus a decisive reference-architecture reason.

### What the engine *is*, restated, so the boundary has a referent

babelstone's kernel is a **deterministic ledger**: append-only events, pure folds, rebuildable projections, regulatory configuration as signed packs ([ADR-PC-007](./ADR-PC-007-signed-yaml-oci-pack.md)), boundary signals as contracts ([ADR-PC-012](./ADR-PC-012-gl-posting-signal-contract.md), [ADR-PC-025](./ADR-PC-025-customer-notification-emit-contract.md)), no PII on the durable bus, and a synchronous idempotent command surface ([ADR-PC-029](./ADR-PC-029-engine-command-ingress.md)). Folding a stream of movements into a balance is the *most native thing it does*. The purpose this ADR also fixes — babelstone is a **reference architecture / portfolio piece** — means scope should optimise for *illustrating distinct product shapes correctly*, and the most fundamental shape of all is the **transactional balance account**.

### The boundary runs *through* authorization, not around the products

The earlier instinct to call a conta à ordem "out of scope because it is a payment account" conflated two different exclusions, and getting them apart is the whole decision:

- **Physically moving money** — the rails/scheme, clearing, settlement, payment initiation, plus strong customer authentication (SCA) and fraud screening — is genuinely external. The engine never touches the wire.
- **Owning the authoritative balance and the account's own rules** — the balance, the posting history, the lifecycle, fees, statements, limits/overdraft, *and the funds-and-rules core of the authorization decision* — is squarely the kernel's competence.

A card/payment **authorization is a pipeline**, and the boundary cuts through it:

| # | Stage | Detail | Owner |
|---|---|---|---|
| 1 | Instrument valid? not blocked/expired? | *valid card* | External |
| 2 | Customer authenticated (PIN / 3DS / SCA) | *strong authentication (PSD2)* | External (regulated) |
| 3 | **Funds available?** | *sufficient available balance?* | **Engine** |
| 4 | **Within product rules / limits / overdraft?** | *within arranged overdraft, pack limits* | **Engine** |
| 5 | **Earmark the funds (place the hold)** | *place the hold* | **Engine** |
| 6 | Fraud screen | *fraud analysis* | External |
| 7 | Effect on the rails | *network settlement* | External |

Stages 3–5 are pure, deterministic *read-state-and-append* deciders — the same pattern a term deposit already uses to refuse an early withdrawal. The excluded stages are exactly the ones that need a clock, an external call, or a model. So the engine does not "do authorization" or "not do it": it owns **the ledger-and-rules core of the decision** and answers `authorized` (+ a hold) or `declined`, **in real time**.

### The hold — why a transactional account fits the kernel natively

A transactional account carries two balances: the **accounting balance** (what has posted) and the **available balance** (what is spendable now). Their gap is the **held amount** — funds earmarked by approved-but-unsettled authorizations (the hotel pre-authorization is the canonical case). `available balance` is therefore **not a stored number but a fold**: `accounting balance − Σ(active holds)`. The hold has an event lifecycle — `HoldPlaced` (authorize) → `HoldCaptured` (on capture/settlement) → `HoldExpired` (on timeout) — each step a pure event. Holds are also what make concurrent authorization safe without locking: the first debit appends a `HoldPlaced` that lowers `available balance` before the second is evaluated. Transactional accounts fit babelstone *natively*; they are not a concession.

**Candidates evaluated (scope postures):**

| # | Candidate | Notes |
|---|---|---|
| A | **Pure product/accrual kernel** — own only product math + accrual lifecycle; push transactional balance accounts and any authorization role entirely external. | The original narrow reading. Exiles the most fundamental product shape and contradicts the [ADR-PC-016](./ADR-PC-016-legacy-current-account-adapter.md) intent to bring the conta à ordem onto the engine at v4. |
| B | **Core product & account ledger** — own product math + account lifecycle + **transactional balance accounts** + the **ledger-and-rules core of authorization** (the available-balance funds check, pack rules incl. arranged overdraft, the hold) as a **real-time dependency**; exclude the rails/scheme/clearing/settlement, instrument validation, SCA, fraud, payment initiation, origination, and collections enforcement. | The engine is the authoritative balance and answers debit attempts in real time; it never moves money on the wire. "Decide and record" is in; "physically move" is out. |
| C | **Ledger + servicing orchestration** — B plus disbursement/direct-debit *orchestration* and running PARI/PERSI as engine state. | Widens into workflow the saga estate ([ADR-IC-003](../../integration_concepts/adrs/ADR-IC-003-saga-orchestrator.md)) already owns. |
| D | **Full transaction processing incl. rails** — own authorization *end to end including the wire* (scheme, clearing, settlement). | A card-switch / payment-services-provider build; contradicts the no-wire boundary and the 1–2-person reference scope. |

---

## Evaluation

### Hard filter results

#### F1 · Cost / licensing

| Candidate | Licence / cost | Verdict |
|---|---|---|
| A · accrual kernel | Buys nothing. | **Pass** |
| B · core ledger | Buys nothing; more engine to build, but no licence. | **Pass** |
| C · +servicing | Same; duplicates saga work. | **Pass** |
| D · +rails | Same licence (zero), but a processor/PSP build is a different order of effort and certification. | **Pass** |

Uniform pass — F1 does not discriminate (scope buys nothing).

#### F2 · Regulatory fit (GDPR / DORA / PSD2)

A scope posture is not itself a regulated runtime artefact, so F2 cannot *fail* a candidate; it carries a directional signal. The key clarification for the chosen posture B: **owning the balance and answering the funds-and-rules stage of authorization does not make the engine a PSD2 payment-services provider** — it never *executes* a payment on the rails, never performs SCA, and holds no card/scheme credentials; those (stages 1–2, 6–7) stay external. The engine is a **ledger and a decision component**, not a PSP. It keeps the no-PII-on-the-bus posture ([ADR-PC-004](./ADR-PC-004-pii-crypto-shredding.md)) and the contract-emit boundaries. D, by contrast, *would* pull PSD2 payment-execution and a far larger cardholder-data surface into the engine.

| Candidate | GDPR | DORA / PSD2 | Verdict |
|---|---|---|---|
| A · accrual kernel | Smallest surface. | No payment activity in-engine. | **Pass** |
| B · core ledger | Balance + transactional data, no card/scheme PII on the bus. | Not a PSP — no rails, no SCA, no payment execution; a ledger/decision component only. | **Pass** |
| C · +servicing | As B. | As B. | **Pass** |
| D · +rails | Largest — cardholder/transaction data at volume. | Engine performs PSD2-regulated payment execution. | **Pass** (largest surface) |

All clear the hard filters; the decision is in S1–S4 and the reference-architecture reason — the expected shape for the [§D3](./ADR-PC-000-namespace-and-contract-shape-framework.md) residual category.

### Soft criteria

#### B · Core product & account ledger — **CHOSEN**

**S1 · Operational complexity for 1–2 people.** Moderate, and the right amount. B adds the transactional-account shape and a real-time authorization path, but stages 3–5 are pure deciders the engine already knows how to express, and the synchronous answer regime is the [ADR-PC-029](./ADR-PC-029-engine-command-ingress.md) command surface under a heavier load profile — an extension, not a new kind of component. C duplicates the saga estate; D adds a rails/scheme tier (latency SLAs, scheme certification, settlement) no 1–2-person team should own. B is the widest posture whose whole surface a small team can still build correctly.

**S2 · Ecosystem coherence — decisive.** The kernel's value is **deterministic-fold purity** ([ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md), [ADR-PC-021](./ADR-PC-021-application-layer-family-owned-deciders.md)). B *preserves* it: a balance is a fold over postings, `available balance` is a fold net of holds, and the authorization decision (stages 3–5) is a pure read-state-and-append — all replayable. The real-time requirement is a **non-functional** property (latency/availability), not an architectural impurity; the impure stages (SCA, fraud, rails) are precisely the ones held *outside*. D *breaks* purity by pulling clock/I/O-bound rails into the kernel; C drags orchestration state in. B keeps the engine one coherent kind of thing while owning the foundational shape.

**S3 · Exit cost.** B is well-placed. It keeps C (servicing) and D (rails) as *future widenings* if a real need appears, while not under-committing the way A does (A would have to be reopened the moment the conta à ordem migration of [ADR-PC-016](./ADR-PC-016-legacy-current-account-adapter.md) lands). Starting at B avoids both an immediate reopen (A's fate) and a baked-in processor assumption (D's cost).

**S4 · Longevity.** Neutral — the posture outlives any single family; B is the one the existing architecture can carry without becoming a different kind of system.

**Decisive project-specific reason — reference-architecture topology.** As a *reference* piece, babelstone's scope should **span the product topology**. B delivers the full map of retail shapes: a **liability** (term deposit, accrues to maturity), a **closed-end asset** (crédito pessoal, deterministic amortization), an **open-end revolving asset** (credit-card account slice, statement cycle), and the **transactional/demand account** (conta à ordem) — the most fundamental shape and *the hub the other three settle against*. A omits the hub; C and D add operational machinery that illustrates *workflow/rails*, not *product shape*. Owning the transactional account as a **general capability** (instances: conta à ordem, the card account) is the strongest thing the reference engine can demonstrate — that one deterministic-ledger kernel expresses every retail product shape.

#### A · Pure product/accrual kernel — **rejected (too narrow)**

A draws the boundary so that the engine's most native competence — folding movements into an authoritative balance — is exiled. It mistakes "a conta à ordem rides payment rails" for "a conta à ordem *is* payment rails," and so pushes the balance, the rules, and the funds-check decision out with the wire. It also contradicts [ADR-PC-016](./ADR-PC-016-legacy-current-account-adapter.md)'s stated v4 destination (the conta à ordem moving onto the engine). Rejected: it under-scopes the kernel and would be reopened on first contact with the current account.

#### C · Ledger + servicing orchestration — **rejected (reserved as a future widening)**

Disbursement and direct-debit *orchestration* are saga concerns the orchestrator estate ([ADR-IC-003](../../integration_concepts/adrs/ADR-IC-003-saga-orchestrator.md)) already owns; running PARI/PERSI confuses *recording* a regulated procedure's transitions (which B already permits as events) with *executing* it. Rejected on S1 + S2; the legitimate slice (recording servicing/default state) lives inside B.

#### D · Full transaction processing incl. rails — **rejected**

D is what a *full* card switch or PSP needs, and it is rejected precisely because the price is the kernel's nature: rails/clearing/settlement are real-time, I/O-bound, scheme-certified, and PSD2-regulated payment *execution* — not a pure replayable fold. It turns a 1–2-person reference project into a payments processor. Rejected on S1 + S2 + S3 + the F2 surface; the *product* aspects it would unlock are captured by B's account slices, and B explicitly stops at the wire.

**Decisive reason for B:** it owns the foundational transactional-account shape and the ledger-and-rules core of authorization while keeping deterministic-fold purity and stopping at the wire — the most a coherent, small-team reference kernel can be without becoming a payments processor.

---

## Decision

### babelstone is a core product & account ledger — it owns balances, rules, and the funds-and-rules core of real-time authorization; it never touches the wire.

**Posture (B).** babelstone owns **product math**, the **account lifecycle**, **transactional balance accounts**, and the **ledger-and-rules core of authorization** — the `available balance` funds check, the pack rules (limits, arranged overdraft), and the hold — answered as a **real-time dependency** of the authorization path. It is the authoritative balance. It **delegates** everything that physically moves money or authenticates/screens a payer: the rails/scheme, clearing, settlement, payment initiation, instrument validation, **SCA**, **fraud**, plus **origination/underwriting** and **collections enforcement**. The dividing line is **"decide and record" (in) vs "physically move / authenticate / screen" (out)**. The engine is not a payments processor (rejected D), an orchestrator (rejected C), or a narrow accrual kernel (rejected A).

> **Real-time, technique deferred.** This ADR fixes the *commitment* — the engine answers authorization in real time and is the authoritative balance — not the *mechanism*. Whether that is a synchronous call ([ADR-PC-029](./ADR-PC-029-engine-command-ingress.md) shape) or an asynchronous/reactive design with a fast round-trip is a runtime question deferred to a future ADR.

**Transactional balance account is a general capability.** The 4th product shape is first-class, not a one-off; the conta à ordem and the credit-card account are *instances* of it.

**Family roadmap (the product topology).**
1. **term_deposit** — a **liability** that accrues to maturity. *Built* (the reference family).
2. **personal_loan** — a **closed-end asset** with a deterministic amortization schedule. *Next* — lowest architectural risk (mirror of the term deposit).
3. **credit_card (account/revolving slice)** — an **open-end revolving asset** with a statement cycle. *After* — the scheme/auth/clearing/dispute machinery stays outside.
4. **conta à ordem (transactional balance account)** — the **demand account**; the hub the others settle against. Introduces the available/accounting balance split, the hold lifecycle, and real-time authorization (stages 3–5). It is the [ADR-PC-016](./ADR-PC-016-legacy-current-account-adapter.md) **v4 destination**: through v1–v3 the legacy core owns the conta à ordem balance and the engine holds **no shadow balance** (PC-016 unchanged); at v4 the account migrates onto the engine *as an instance of this capability*. The "no shadow balance" rule is a **coexistence-topology** rule, not a permanent prohibition: its source ([02 §3 commitment 1](../02-v1-scope-term-deposits.md)) defines it as *not mirroring* a balance the legacy core authoritatively owns — *"the engine does not maintain a shadow balance — that would be the double-counting failure mode."* The engine *being* the authoritative owner at v4 is the opposite of a shadow, so no contradiction arises.

**Origination is upstream.** The engine receives an **already-approved, already-priced** instruction; solvency assessment, CRC, KYC/AML, and scoring live in external/ACL systems ([ADR-PC-024](./ADR-PC-024-constitution-precondition-contract.md) shape). The engine may **record** the decision for audit/replay; it never **makes** it.

**Succession is upstream-decided, recorded-not-executed.** When a deposit holder dies, the engine **records** the transfer to heirs (`DepositTransferredToHeirs`, a closing lifecycle fact) and stops there: it never adjudicates the succession — who inherits, in what shares is a court/notary decision, upstream — and it never moves money to an heir. Through v1–v3 the legacy core authoritatively owns the destination conta à ordem balance and the engine holds **no shadow balance** (§Decision item 4 above; [ADR-PC-016](./ADR-PC-016-legacy-current-account-adapter.md) unchanged), so the engine is not the authoritative payer-out; the heir payout is the upstream succession authority's and the legacy core's responsibility. It is therefore **not a sixth confirmation-gated settlement command** on [ADR-PC-016](./ADR-PC-016-legacy-current-account-adapter.md) — whose five-command settlement table stays five, deliberately. This is a **coexistence-topology** stance, not a permanent prohibition: if and when the conta à ordem v4 migration makes the engine the authoritative owner of the heir's demand-account balance, heir-credit may be **re-opened** as a settlement leg — folded into the lifecycle-credit settlement concept (bd `babelstone-t7o3.13`) via the §P3 settlement/posting-feed contract-shape ADR. The recorded succession event carries only the opaque `HeirCaseRef` — the upstream succession-case decision reference ([ADR-PC-024](./ADR-PC-024-constitution-precondition-contract.md) shape) — and never an heir's name, NIF, or IBAN, in cleartext or ciphertext ([ADR-PC-004 §P2](./ADR-PC-004-pii-crypto-shredding.md)). (Decides bd `babelstone-k6r8.12`.)

**Rejected: A** (under-scopes — exiles the foundational shape; reopened on first contact with the conta à ordem). **C** (servicing orchestration — saga-estate concern; the engine records state, does not drive workflow). **D** (rails/scheme — real-time payment execution cannot be a pure fold; would make a reference project a processor).

---

## Implementation Principles

### P1 — The boundary: what is in the kernel (IS) and what is delegated (IS NOT)

The kernel **IS** responsible for, across every family:

| Concern | term_deposit | personal_loan | credit_card (slice) | conta à ordem |
|---|---|---|---|---|
| Product math | interest accrual, withholding | amortization schedule | revolving interest, grace period | fee/interest accrual (if any) |
| Account lifecycle | constitute → accrue → mature | disburse → amortize → close | open → revolve → statement | open → active → dormant → close |
| Authoritative balance | principal + accrued | outstanding capital | revolving balance | accounting / available balance |
| Real-time authorization (stages 3–5) | n/a | n/a | (limit check) | **funds + pack rules + hold** |
| Regulatory-pack config | rate sheets, withholding | per-*finalidade* caps, selo, early-repay | TAEG cap, selo, min-payment | fees, arranged overdraft, limits |
| Audit / replay | ✓ | ✓ | ✓ | ✓ |

The kernel **IS NOT** responsible for (delegated across the ACL / to external systems) — the more valuable half of the boundary:

1. **No physical money movement.** No rails/scheme, no clearing, no settlement, no payment initiation. The engine emits a verdict (`authorized` + hold, or `declined`) and consumes capture/settlement as postings; it never moves money on the wire.
2. **No authentication or fraud.** No SCA/3DS (stage 2 — regulated, external) and no fraud screening (stage 6 — model/I/O-bound, external).
3. **No origination / underwriting.** No solvency, CRC, KYC/AML, scoring, affordability — the engine receives an already-approved, already-priced instruction ([ADR-PC-024](./ADR-PC-024-constitution-precondition-contract.md)).
4. **No collections *enforcement*.** The engine may *record* PARI/PERSI transitions as events; it does not *run* the legal procedure.
5. **No servicing-decision authority.** Limit increases, repricing, consolidation arrive as instructions; the engine applies, it does not decide.
6. **No succession adjudication or heir payout.** On a holder's death the engine **records** the transfer to heirs (`DepositTransferredToHeirs`); it does not adjudicate the succession (a court/notary decides who inherits, upstream) and does not move money to an heir. Through v1–v3 the legacy core owns the destination balance, so the heir payout is upstream's and the legacy core's job — **no sixth settlement command** is added to [ADR-PC-016](./ADR-PC-016-legacy-current-account-adapter.md) (its five-command table is unchanged). See the §Decision "Succession is upstream-decided" paragraph for the coexistence-topology scope and the conta à ordem v4 re-opening trigger (bd `babelstone-k6r8.12`).

The recorded-not-executed pattern (items 3–6) is how the legitimate slice of rejected posture C lives *inside* B: state is captured as events for audit/replay without the engine owning the workflow that produces it.

### P2 — The family roadmap rides the existing one-engine-many-families spine

Each family is added the [ADR-PC-021](./ADR-PC-021-application-layer-family-owned-deciders.md) way — own event records, pure fold handlers, lifecycle legality table, projections, bound through an `IFamilyModule`, with **no `ProjectReference` from the generic spine into `families/**`** (gated by `ENGINE_FAMILY_AGNOSTIC`). Order is deliberate: `personal_loan` (reuses the term-deposit shape) before `credit_card` (new revolving lifecycle) before `conta à ordem` (introduces holds + real-time authorization, and aligns with the [ADR-PC-016](./ADR-PC-016-legacy-current-account-adapter.md) v4 migration). The closed-end loan de-risks the family abstraction before the heavier shapes land.

### P3 — Authorization and holds: the engine owns stages 3–5 in real time

- **The authorization decider.** On a debit attempt the engine answers an `authorize`-style command: compute `available balance` (= `accounting balance − Σ active holds`, a fold), apply pack rules (limits, arranged overdraft), and append `HoldPlaced` (with the verdict) or a refusal. Pure, deterministic, replayable.
- **The hold lifecycle.** `HoldPlaced` → `HoldCaptured` (on capture/settlement) → `HoldExpired` (on timeout). `available balance` is always a projection, never a stored mutable number.
- **Real-time dependency.** The engine is a live dependency of the authorization path; its latency and availability become payment-path concerns. The *technique* (synchronous vs asynchronous/reactive) is a deferred runtime ADR; the *commitment* (real-time answer, engine authoritative) is fixed here.
- **Overdraft and limits are pack rules.** Arranged overdraft and transaction/velocity limits are expressed in the regulatory/product pack and evaluated at stage 4 — so the same rule surface that prices a deposit also governs "can this debit go through." The pack grammar must therefore carry limit/overdraft constructs, not only rates.
- **The settlement/posting feed.** Capture and other already-cleared movements arrive as events across the ACL; their shape, ordering, and idempotency are a future **contract-shape ADR**. Statement issuance (for the card and the conta à ordem) is a **sealed event**, not a replayable projection — an issued statement is legally immutable; corrections are new events on the next cycle. These are flagged as open questions owned by the relevant family + contract ADRs — **not decided here.**

---

## Consequences

**What this choice makes easier:**

1. **A fixed boundary every future family ADR honours.** "Is this the engine's job?" resolves to "decide and record" (in) vs "physically move / authenticate / screen" (out), so scope drift is visible — what the explicit-drift gate ([ADR-PC-020 §D3](./ADR-PC-020-llm-toolchain-and-conformance-governance.md)) wants.
2. **One authoritative balance, no double-spend.** Because the engine owns the balance and the hold, concurrent authorizations are serialised by the append-only log without distributed locking.
3. **A unified pack rule surface.** The same pack that prices a product also expresses its limits/overdraft, evaluated at authorization.
4. **The full reference topology.** Four product shapes on one kernel — liability, closed-end asset, revolving asset, transactional account — the strongest demonstration that the family abstraction generalises.
5. **Alignment with the conta à ordem migration.** [ADR-PC-016](./ADR-PC-016-legacy-current-account-adapter.md)'s v4 "moves onto the engine" now has a named destination: the transactional-balance-account family.

**What this choice makes harder or impossible:**

1. **The engine becomes a real-time dependency of payments.** Its latency and uptime are now payment-path SLOs — [ADR-PC-001](./ADR-PC-001-event-store-technology.md) (store throughput) and [ADR-PC-011](./ADR-PC-011-in-house-load-test-harness.md) (load harness) become load-bearing, not background. The async-vs-sync technique is an unresolved runtime question.
2. **A *full* payment processor / PSP is out of scope by construction.** Anyone wanting rails/scheme/clearing must integrate an external processor or reopen this ADR to adopt posture D (a supersession).
3. **Servicing *orchestration* is not the engine's to own** (posture C) — the engine records resulting state only.

**Residual risks:**

- **Real-time event-sourcing under load.** Answering authorization on the hot path with an append-only store is the central engineering risk; the technique ADR and the load harness ([ADR-PC-011](./ADR-PC-011-in-house-load-test-harness.md)) must prove it.
- **Hold reconciliation.** Hold expiry vs late capture, partial captures, and reversals are correctness-sensitive; owned by the conta à ordem / card family ADRs.
- **Pack-grammar expansion.** Expressing limits/overdraft as pack rules widens the [ADR-PC-006](./ADR-PC-006-cue-schema-language.md)/[ADR-PC-007](./ADR-PC-007-signed-yaml-oci-pack.md) surface; must stay declarative.
- **"Recorded not executed" can blur** (items 3–6 of §P1). Mitigation: those events carry an upstream decision reference ([ADR-PC-024](./ADR-PC-024-constitution-precondition-contract.md)) — for succession, the opaque `HeirCaseRef` anchors the recorded `DepositTransferredToHeirs` to the upstream succession-case decision, so "recorded" never blurs into "executed". The companion risk for succession is the inverse: because the heir payout stays upstream/legacy, operational owners must confirm the legacy/upstream estate genuinely *executes and reconciles* it — there is no engine-side succession saga, so the move is a real-world responsibility the engine deliberately does not hold.

---

## Open Actions

1. **Promote the supporting research** into [`product_concepts/research/`](../research/) (done in the same change as this ADR).
2. **Author the `personal_loan` family** ([ADR-PC-021](./ADR-PC-021-application-layer-family-owned-deciders.md) shape) — amortization schedule, disbursement, capped early repayment.
3. **Scope the `credit_card` account-slice** — a future family ADR plus the §P3 settlement/posting-feed contract-shape ADR and the statement-issuance event design.
4. **Scope the `conta à ordem` transactional-account family** — the hold model, `available balance` as a fold, overdraft-as-pack-rule, and (separately) the **real-time authorization technique ADR** (sync vs async). Aligns with the [ADR-PC-016](./ADR-PC-016-legacy-current-account-adapter.md) v4 migration.

---

## Verifiable commitments

This decision's load-bearing commitments are fitness functions in the [commitment catalogue](./commitment-catalogue.md) — the single source of truth for each commitment's exact claim, gate (pyramid level), and `Live`/`Planned`/`Gap` status ([ADR-PC-020 §P5–§P7](./ADR-PC-020-llm-toolchain-and-conformance-governance.md)):

No *new* executable commitments are added by this scope/posture decision itself — it is realised by the *downstream* family and contract ADRs it governs. (The succession recorded-not-executed stance added 2026-06-23 likewise adds none: it *forbids* a settlement leg rather than introducing one, and the existing store-only nature of `DepositTransferredToHeirs` is what enforces it.) Two existing gates already enforce the boundary it draws: the `family → engine` one-way dependency is gated by **`ENGINE_FAMILY_AGNOSTIC`** ([ADR-PC-021 §P2](./ADR-PC-021-application-layer-family-owned-deciders.md)); and handler purity (no clock/I/O/randomness — the property that keeps stages 3–5 a pure fold and rejects posture D) is gated by the replay-determinism discipline. New commitments **will** be catalogued when the families land — in particular, when `conta à ordem` is authored, the hold lifecycle determinism, `available balance = accounting balance − Σ holds` as a rebuildable fold, and the authorization decider's purity are expected gates.

---

## Cross-references

- [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) — the deterministic-fold kernel whose *nature* this bounds; stages 3–5 stay pure folds, which is why posture D is excluded.
- [ADR-PC-021](./ADR-PC-021-application-layer-family-owned-deciders.md) — the one-engine-many-families spine; `ENGINE_FAMILY_AGNOSTIC` backstops the boundary.
- [ADR-PC-024](./ADR-PC-024-constitution-precondition-contract.md) — engine declares preconditions, upstream evaluates; keeps origination out of the kernel.
- [ADR-PC-029](./ADR-PC-029-engine-command-ingress.md) — the synchronous idempotent command surface the real-time authorization regime extends.
- [ADR-PC-016](./ADR-PC-016-legacy-current-account-adapter.md) — the conta à ordem coexistence/settlement contract; this ADR names its v4 "moves onto the engine" the transactional-balance-account family (and leaves v1–v3 "no shadow balance" unchanged).
- [ADR-PC-019](./ADR-PC-019-repository-strategy-monorepo.md) — the sibling [§D3](./ADR-PC-000-namespace-and-contract-shape-framework.md) residual-category posture ADR whose shape this follows.
- [ADR-IC-003](../../integration_concepts/adrs/ADR-IC-003-saga-orchestrator.md) — the orchestrator that owns the servicing orchestration rejected posture C would have pulled in.
- [crédito pessoal research](../research/personal-loan/00-research-plan.md) / [credit-card research](../research/credit-cards/00-research-plan.md) — the promoted market research that motivated, and is bounded by, this decision.
- [01 §1](../01-product-architecture.md) — the one-engine-many-families thesis this scopes.

---

*Decided 2026-06-20 by jhosm. Accepted 2026-06-23.*
*Revised 2026-06-20 (pre-acceptance): widened from a narrow product/accrual kernel (former posture A) to a **core product & account ledger** (posture B) — the engine owns transactional balance accounts as a general 4th product shape and the funds-and-rules core of authorization (stages 3–5: available balance, pack rules/arranged overdraft, the hold) as a real-time dependency, while still stopping at the wire (no rails/scheme/SCA/fraud). Adds the conta à ordem to the roadmap as the ADR-PC-016 v4 destination. The candidate set was corrected: the original framing omitted this posture between the narrow kernel and full transaction processing.*
*Revised 2026-06-23 (pre-acceptance): named **succession** explicitly as a recorded-not-executed instance (§Decision "Succession is upstream-decided" + §P1 item 6 + the §Residual-risks blur mitigation) — the engine records `DepositTransferredToHeirs` but does not adjudicate the succession or pay the heir; the heir payout stays upstream/legacy through v1–v3, with a named conta à ordem v4 re-opening trigger. Resolves the heir-credit-vs-settlement-command question (bd `babelstone-k6r8.12`): the [ADR-PC-016](./ADR-PC-016-legacy-current-account-adapter.md) five-command table is unchanged, and the companion [ADR-PC-012](./ADR-PC-012-gl-posting-signal-contract.md) amendment of the same date drops transfer-to-heirs from the engine-emitted GL subset.*
*Revised 2026-07-02: roadmap order (§P2 and the §Decision family-roadmap items 3–4) — `credit_card` is now sequenced **after** `conta à ordem`, not before it, so the roadmap reads `term_deposit → personal_loan → conta à ordem → credit_card`. The reason is reuse, not preference: `conta à ordem` (ADR-PC-037) is the *first* transactional instance of the [ADR-PC-033](./ADR-PC-033-account-abstraction-and-hold-lifecycle.md) Account abstraction — where the holds, the `available balance` fold, and the real-time authorize path are first implemented against a live family — and the card account lands as its **second** instance against that proven implementation rather than co-inventing it (the same de-risking move `personal_loan` made against `term_deposit`). Proposed with, and load-bearing for, [ADR-PC-039](./ADR-PC-039-credit-card-family.md); pending maintainer approval on that change's merge.*
*Revised 2026-07-07: terminology only — the overdraft label in §Context, §Decision (the stage-4 funds-and-rules authorization), §Implementation Principles, and the cross-references now reads as the UK term **arranged overdraft** throughout (it previously carried the Portuguese label). A pure rename with **no change to the decision or the boundary** — the authorization stages, the pack-read overdraft limit, and the stop-at-the-wire posture are unchanged. Aligns the estate with [ADR-PC-037](./ADR-PC-037-current-account-family.md) (the current_account family, which keeps the Portuguese term as a single translation gloss); PR #462.*
