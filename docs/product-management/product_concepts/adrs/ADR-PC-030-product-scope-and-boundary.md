# ADR-PC-030: babelstone Product Scope & Boundary — a Product/Accrual Kernel

| Field | Value |
|---|---|
| Status | Proposed |
| Date | 2026-06-20 |
| Deciders | jhosm |
| Shape | Tool-selection |
| Common criteria | [ADR-IC-000](../../integration_concepts/adrs/ADR-IC-000-common-evaluation-criteria.md) (reused per [ADR-PC-000](./ADR-PC-000-namespace-and-contract-shape-framework.md) D2; this is the [ADR-PC-000 §D3](./ADR-PC-000-namespace-and-contract-shape-framework.md) "operational discipline" residual category — a scope/posture decision, declared tool-selection per the [§D4](./ADR-PC-000-namespace-and-contract-shape-framework.md) default) |
| Depends on | [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) (the hand-rolled product kernel whose nature this bounds), [ADR-PC-021](./ADR-PC-021-application-layer-family-owned-deciders.md) (the one-engine-many-families spine the roadmap rides on), [ADR-PC-024](./ADR-PC-024-constitution-precondition-contract.md) (engine declares preconditions, upstream evaluates them — the mechanism that keeps origination out of the kernel) |
| Resolves | bd `babelstone-nyan` (ADR-PC-030: babelstone product scope & boundary) |

---

## Context

The engine has, until now, been **scoped by accretion**: [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) fixed *what kind of thing* it is (a hand-rolled, event-sourced product kernel), [ADR-PC-021](./ADR-PC-021-application-layer-family-owned-deciders.md) fixed *how it grows* (one engine, many families, each family owning its deciders/handlers/projections), and a single reference family — `term_deposit` — proved both. No ADR has yet stated, in one place, **what babelstone is *for* and where its responsibility *stops*.** That gap is now load-bearing: market research into two candidate products ([crédito pessoal](../research/credito-pessoal/00-research-plan.md) and [credit cards](../research/credit-cards/00-research-plan.md)) forces the question, because the two products sit on opposite sides of the engine's natural boundary and answering "should we build them" is impossible without first fixing the boundary itself.

This entry is the [ADR-PC-000 §D3](./ADR-PC-000-namespace-and-contract-shape-framework.md) **residual category** ("operational discipline … fits neither template cleanly … default to tool-selection"), the same class as [ADR-PC-019](./ADR-PC-019-repository-strategy-monorepo.md) (repository strategy): it selects a **posture**, not a tool. The honest consequence, surfaced up front: **F1 and F2 do not discriminate** — a scope statement buys nothing and ships no regulated runtime artefact. The load-bearing question is which posture keeps the kernel's architecture coherent while letting the engine illustrate the products a reference banking engine should illustrate — settled on S1–S4 plus a decisive reference-architecture reason, not on the hard filters.

### What the engine *is*, restated, so the boundary has a referent

babelstone's kernel is a **deterministic product/accrual ledger**: append-only events, pure folds, rebuildable projections, regulatory configuration delivered as signed packs ([ADR-PC-007](./ADR-PC-007-signed-yaml-oci-pack.md)), boundary signals emitted as contracts ([ADR-PC-012](./ADR-PC-012-gl-posting-signal-contract.md), [ADR-PC-025](./ADR-PC-025-customer-notification-emit-contract.md)), and no PII on the durable bus. That architecture is naturally a **product ledger and lifecycle engine** — it owns the *product math* and the *account lifecycle* and leans on the integration estate / external systems for everything else. The purpose this ADR also fixes — babelstone is a **reference architecture / portfolio piece**, not a product to operate commercially — means scope should optimise for *illustrating distinct product shapes correctly*, not for commercial completeness in any one of them.

### The two research products land on opposite sides of the boundary

- **Crédito pessoal** ([research](../research/credito-pessoal/01-fundamentals.md)) is **closed-end**: money moves out once (disbursement) and back many times on a deterministic amortization schedule. It is the *mirror* of the term deposit — one event stream, a pure schedule fold, no per-transaction machinery. It fits the kernel almost exactly.
- **A credit card** ([research](../research/credit-cards/01-fundamentals.md)) has its *essence* in the part the kernel does **not** model: a revolving line driven by a **four-party scheme** (issuer, acquirer, scheme, merchant) — real-time authorization, clearing, settlement, chargebacks, interchange. That is a payments switch/processor's concern. Only the *account slice* of a card (the credit line, revolving interest, the statement cycle, the minimum payment) has the kernel's shape.

So the real decision is not "cards vs. loans"; it is **how wide is the engine's nature, and how far past the product-math boundary does it reach**.

**Candidates evaluated (scope postures):**

| # | Candidate | Notes |
|---|---|---|
| A | **Pure product/accrual kernel** — own product math + account lifecycle + regulatory-pack config; delegate payment rails, card schemes, origination/underwriting, and collections *enforcement* to the integration estate / external systems. | Keeps the deterministic-fold architecture intact. Crédito pessoal fits whole; a credit card fits only as its account/revolving slice (scheme/auth/clearing/dispute stay outside the ACL). |
| B | **Kernel + servicing** — A, plus absorb servicing concerns adjacent to the product: disbursement *orchestration*, direct-debit collection *scheduling*, and running the PARI/PERSI default procedure as engine state. | Widens the engine into workflow/orchestration the saga estate ([ADR-IC-003](../../integration_concepts/adrs/ADR-IC-003-saga-orchestrator.md)) already owns; blurs "the engine computes the product" with "the engine drives the operation." |
| C | **Expand toward transaction processing** — grow the engine to own per-transaction authorization / clearing / dispute machinery, so a *full* credit card (scheme side included) becomes feasible. | Largest architectural change; turns a product kernel into (part of) a card processor; contradicts the deterministic-fold, no-real-time-rails nature of [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md). |

---

## Evaluation

### Hard filter results

#### F1 · Cost / licensing

| Candidate | Licence / cost | Verdict |
|---|---|---|
| A · kernel | Buys nothing; a scope statement has no licence. | **Pass** |
| B · kernel+servicing | Same — but more engine to build/maintain for a 1–2-person team. | **Pass** |
| C · transaction processing | Same licence cost (zero), but a scheme/processor build is a different order of effort. | **Pass** |

Uniform pass — F1 does not discriminate (scope buys nothing).

#### F2 · Regulatory fit (GDPR / DORA / PSD2)

A scope posture is not itself a regulated runtime artefact, so F2 cannot *fail* a candidate. It does, however, carry a directional signal worth recording: the **narrower** the kernel, the **smaller** its regulatory surface. Keeping origination/underwriting (KYC/AML, solvency, scoring) and the card scheme (PSD2-regulated payment-services activity) **outside** the boundary means the engine never holds the data or performs the activity those regimes bite hardest on — consistent with the no-PII-on-the-bus posture ([ADR-PC-004](./ADR-PC-004-pii-crypto-shredding.md)) and the GL/notification *contract* boundaries already chosen.

| Candidate | GDPR | DORA / PSD2 | Verdict |
|---|---|---|---|
| A · kernel | Smallest PII/data surface; no payment-services activity in-engine. | Engine is not a payment-services provider; rails/scheme are external. | **Pass** |
| B · kernel+servicing | As A. | As A (servicing scheduling is not itself a payment service). | **Pass** |
| C · transaction processing | Larger — handling authorizations pulls in cardholder/transaction data at volume. | Engine would perform PSD2-regulated payment processing. | **Pass** (with the largest surface) |

All clear the hard filters; the decision is entirely in S1–S4 and the reference-architecture reason below — the expected shape for the [§D3](./ADR-PC-000-namespace-and-contract-shape-framework.md) residual category.

### Soft criteria

#### A · Pure product/accrual kernel — **CHOSEN**

**S1 · Operational complexity for 1–2 people.** Lowest. A pulls in only product math and account lifecycle — the work the existing fold/family/projection machinery already does. B adds orchestration that duplicates the saga estate; C adds a real-time processing tier (authorization latency, scheme certification, dispute workflows) no 1–2-person team should own. For a reference engine, A is the only posture whose *whole* surface a small team can build correctly.

**S2 · Ecosystem coherence — decisive.** The kernel's value is its **deterministic-fold purity** ([ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md), [ADR-PC-021](./ADR-PC-021-application-layer-family-owned-deciders.md)): every state is a pure replay of events, gated by `ENGINE_FAMILY_AGNOSTIC` and the replay-determinism discipline. A *preserves* that — crédito pessoal and the card account-slice are both expressible as pure folds over an event stream the engine controls. C *breaks* it: real-time authorization is clock- and I/O-bound, the antithesis of a pure fold, and would force a non-deterministic tier into the kernel. B sits between, dragging workflow state that belongs to the orchestrator into the engine. A is the posture that keeps the engine *one coherent kind of thing*.

**S3 · Exit cost.** A is the most reversible. Choosing the narrow boundary now keeps B and C as *future widenings* (add a servicing concern, or a processing tier, if a real need appears) at near-zero cost today. Choosing B or C now bakes orchestration/processing assumptions into the kernel that are expensive to unwind — the asymmetry favours starting narrow.

**S4 · Longevity.** Neutral — all three postures outlive any single family; the question is which the architecture can carry, and A is the one the existing architecture already supports.

**Decisive project-specific reason — reference-architecture topology.** Because babelstone is a *reference* piece (the purpose fixed above), its scope should **span the product topology**, not maximise depth in one product. A delivers exactly that: term deposit is a **liability** (accrues to maturity), crédito pessoal is a **closed-end asset** (deterministic amortization), and the credit-card account-slice is an **open-end revolving asset** (statement cycle). Liability / closed-end / revolving is the whole map of retail product shapes — three families that *prove the family abstraction generalises*, which is the strongest thing a reference engine can demonstrate. B and C add operational machinery that illustrates *workflow*, not *product shape*, and so add cost without advancing the reference story.

#### B · Kernel + servicing — **rejected (reserved as a future widening)**

B is not wrong, only premature and mis-placed. Disbursement orchestration and direct-debit scheduling are **saga** concerns the orchestrator estate ([ADR-IC-003](../../integration_concepts/adrs/ADR-IC-003-saga-orchestrator.md)) already owns; running PARI/PERSI as engine state confuses *recording* a regulated procedure's transitions (which the engine can do as events) with *executing* it (which it should not). Rejected on S1 + S2 with no offsetting gain; the legitimate slice of B — the engine *recording* servicing/default state as events — is preserved inside A's boundary (see §P1).

#### C · Expand toward transaction processing — **rejected**

C is the posture that would make a *full* credit card feasible, and it is rejected precisely because the price is the kernel's nature. Per-transaction authorization is real-time, I/O- and clock-bound, and scheme-certified — it cannot be a pure, replayable fold, so it contradicts [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) and the replay-determinism discipline at the root of the engine's value. It also turns a 1–2-person reference project into a payments-processor build. Rejected on S1 + S2 + S3; the *product* aspects of a card it would unlock are captured instead by A's account-slice (see §P3).

**Decisive reason for A over B and C:** the engine's worth is its deterministic-fold purity and the reference value of spanning product *shapes*; A preserves both, B dilutes the first, C destroys it.

---

## Decision

### babelstone is a pure product/accrual kernel, scoped to three product shapes, with origination upstream.

**Posture (A).** babelstone owns **product math**, the **account lifecycle**, and **regulatory-pack configuration**. It **delegates** payment rails, card schemes, origination/underwriting, and collections *enforcement* to the integration estate and external systems, reached across the ACL. It stays a deterministic event-sourced ledger; it does not become an orchestrator (rejected B) or a transaction processor (rejected C).

**Family roadmap (the product topology).** Three families, one per retail product shape:
1. **term_deposit** — a **liability** that accrues to maturity. *Built* (the reference family).
2. **credito_pessoal** — a **closed-end asset** with a deterministic amortization schedule. *Next* — lowest architectural risk (mirror of the term deposit); validates that the family abstraction generalises from liability to asset.
3. **credit_card (account/revolving slice)** — an **open-end revolving asset** with a statement cycle. *After* — the genuinely new shape; the scheme/authorization/clearing/dispute machinery stays **outside** the ACL (rejected C).

**Origination is upstream.** The engine receives an **already-approved, already-priced** product instruction; solvency assessment, CRC consultation, KYC/AML, and scoring live in external/ACL systems. This is the [ADR-PC-024](./ADR-PC-024-constitution-precondition-contract.md) shape — *the engine declares the preconditions it requires; upstream evaluates them; the family decider refuses an instruction whose declared preconditions are unmet* — applied to credit origination. The engine may **record** the approval/pricing decision as events for audit and replay, but it never **makes** it.

**Rejected: kernel + servicing (B)** — disbursement orchestration and direct-debit scheduling are saga concerns the orchestrator already owns; the engine records servicing/default *state* as events but does not drive the operation. **Rejected: transaction processing (C)** — real-time authorization cannot be a pure replayable fold; owning it would destroy the determinism that is the engine's value and turn a reference project into a card processor.

---

## Implementation Principles

### P1 — The boundary: what is in the kernel (IS) and what is delegated (IS NOT)

The kernel **IS** responsible for, across every family:

| Concern | term_deposit | credito_pessoal | credit_card (slice) |
|---|---|---|---|
| Product math | interest accrual, withholding | amortization schedule, level installment | revolving interest, grace period |
| Account lifecycle | constitute → accrue → mature | disburse → amortize → early-repay → close | open → revolve → statement → repay |
| Regulatory-pack config | rate sheets, withholding | per-*finalidade* TAEG caps, imposto do selo, early-repay cap | TAEG cap, stamp duty, min-payment rule |
| Audit / replay | ✓ | ✓ | ✓ |

The kernel **IS NOT** responsible for (delegated across the ACL / to external systems) — the more valuable half of the boundary:

1. **No payment rails / card scheme.** No authorization, clearing, settlement, chargeback, or interchange. The scheme produces *cleared postings*; the engine consumes them (§P3).
2. **No origination / underwriting.** No solvency assessment, CRC, KYC/AML, scoring, or affordability decision. The engine receives an already-approved, already-priced instruction ([ADR-PC-024](./ADR-PC-024-constitution-precondition-contract.md)).
3. **No collections *enforcement*.** The engine may *record* PARI/PERSI state transitions as events; it does not *run* the legal procedure.
4. **No servicing-decision authority.** Limit increases, repricing, and consolidation decisions arrive as instructions; the engine applies them, it does not decide them.

The recorded-not-executed pattern (items 2–4) is how the legitimate slice of rejected posture B lives *inside* A: state is captured as events for audit/replay without the engine owning the workflow that produces it.

### P2 — The family roadmap rides the existing one-engine-many-families spine

Each new family is added the [ADR-PC-021](./ADR-PC-021-application-layer-family-owned-deciders.md) way — its own event records, pure fold handlers, lifecycle legality table, and projections, bound through an `IFamilyModule`, with **no `ProjectReference` from the generic spine into `families/**`** (gated by `ENGINE_FAMILY_AGNOSTIC`). The roadmap order — `credito_pessoal` before `credit_card` — is deliberate: the closed-end loan reuses the term-deposit shape and so de-risks the family abstraction *before* the revolving card introduces a genuinely new lifecycle.

### P3 — The credit-card slice consumes a cleared-posting feed; statement issuance is a sealed event

Two design constraints follow from keeping the card *slice* in and the scheme *out*:

- **Posting-feed contract.** The engine consumes a feed of **already-cleared transaction postings** as events across the ACL — never authorization requests. The four-party scheme (authorization → clearing → settlement → chargeback) runs entirely outside the boundary; its *output* (a settled posting) is the engine's *input*. The shape, ordering, and idempotency of that feed is a future **contract-shape ADR**, not settled here.
- **Statement issuance is a sealed event, not a replayable projection.** A statement is legally **immutable once issued**, so it cannot be a projection that re-derives on replay (a late-arriving correction would silently change an issued statement). At cycle close the engine emits a `StatementIssued`-style event that freezes the closing balance, minimum payment, and due date; corrections become *new* events on the next cycle, never edits to the issued one. Grace-period determinism (grace depends on prior-cycle full payment) is carried in the fold across statement boundaries. These are flagged as the open design questions the card family must resolve, owned by its future family + contract ADRs — **not decided here.**

---

## Consequences

**What this choice makes easier:**

1. **A fixed boundary every future family ADR honours.** "Is this the engine's job?" has a written answer (§P1), so scope drift is visible, not silent — exactly what the explicit-drift gate ([ADR-PC-020 §D3](./ADR-PC-020-llm-toolchain-and-conformance-governance.md)) wants for scope decisions.
2. **A de-risked roadmap.** `credito_pessoal` reuses the proven term-deposit shape; the card slice is taken only after the family abstraction is shown to generalise (§P2).
3. **A small, coherent regulatory surface.** Origination, scheme, and collections-enforcement stay outside, so the engine never holds the data or performs the activity those regimes bite hardest on (F2).

**What this choice makes harder or impossible:**

1. **A *full* credit card is out of scope by construction.** Anyone wanting scheme-side behaviour must either integrate an external processor or reopen this ADR to adopt posture C (a supersession, not a quiet extension).
2. **Servicing *orchestration* is not the engine's to own.** Disbursement and direct-debit *workflows* live in the saga estate; the engine only records their resulting state. A future need to co-locate them would be a supersession toward posture B.

**Residual risks:**

- **The card-slice boundary is the easiest to erode.** The pressure to "just handle one authorization in-engine" is real; the posting-feed contract (§P3) is the wall, and `ENGINE_FAMILY_AGNOSTIC` plus the replay-determinism gate are the backstops. If a future change needs real-time processing, it must come back through posture C explicitly.
- **"Recorded not executed" can blur.** Items 2–4 of §P1 let the engine hold origination/servicing/default *state*; the risk is that recording quietly becomes deciding. Mitigation: those events carry an upstream decision reference ([ADR-PC-024](./ADR-PC-024-constitution-precondition-contract.md)), making "who decided" auditable.

---

## Open Actions

1. **Promote the supporting research** into the corpus under [`product_concepts/research/`](../research/) (done in the same change as this ADR).
2. **Author the `credito_pessoal` family** ([ADR-PC-021](./ADR-PC-021-application-layer-family-owned-deciders.md) shape) — its own family + (where new) contract ADRs, modelling the amortization schedule, disbursement, and capped early repayment.
3. **Scope the `credit_card` account-slice** — a future family ADR plus the §P3 **posting-feed contract-shape ADR** and the statement-issuance event design, taken only after `credito_pessoal` lands.

---

## Verifiable commitments

This decision's load-bearing commitments are fitness functions in the [commitment catalogue](./commitment-catalogue.md) — the single source of truth for each commitment's exact claim, gate (pyramid level), and `Live`/`Planned`/`Gap` status ([ADR-PC-020 §P5–§P7](./ADR-PC-020-llm-toolchain-and-conformance-governance.md)):

No *new* executable commitments — this is a scope/posture decision realised by the *downstream* family and contract ADRs it governs, not by buildable engine behaviour an implementation can drift from on its own. Two existing gates already enforce the boundary it draws: the `family → engine` one-way dependency (no `ProjectReference` from the generic spine into `families/**`) is gated by **`ENGINE_FAMILY_AGNOSTIC`**, owned by [ADR-PC-021 §P2](./ADR-PC-021-application-layer-family-owned-deciders.md); and handler purity (no clock/I/O/randomness — the property that makes posture C incompatible with the kernel) is gated by the replay-determinism discipline. New commitments will be catalogued when the `credito_pessoal` and `credit_card` family ADRs are authored.

---

## Cross-references

- [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) — the hand-rolled, deterministic-fold kernel whose *nature* this ADR bounds.
- [ADR-PC-021](./ADR-PC-021-application-layer-family-owned-deciders.md) — the one-engine-many-families spine the roadmap rides on; the `ENGINE_FAMILY_AGNOSTIC` gate that backstops the boundary.
- [ADR-PC-024](./ADR-PC-024-constitution-precondition-contract.md) — engine declares preconditions, upstream evaluates them; the mechanism that keeps origination/underwriting out of the kernel.
- [ADR-PC-019](./ADR-PC-019-repository-strategy-monorepo.md) — the sibling [§D3](./ADR-PC-000-namespace-and-contract-shape-framework.md) residual-category posture ADR whose shape this one follows.
- [ADR-IC-003](../../integration_concepts/adrs/ADR-IC-003-saga-orchestrator.md) — the saga orchestrator that owns the servicing *orchestration* rejected posture B would have pulled into the engine.
- [crédito pessoal research](../research/credito-pessoal/00-research-plan.md) / [credit-card research](../research/credit-cards/00-research-plan.md) — the promoted market research that motivated, and is bounded by, this decision.
- [01 §1](../01-product-architecture.md) — the one-engine-many-families thesis this scopes.

---

*Decided 2026-06-20 by jhosm.*
