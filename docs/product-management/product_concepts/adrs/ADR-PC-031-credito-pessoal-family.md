# ADR-PC-031: credito_pessoal Family — a Closed-End Amortizing Personal Loan on the One-Engine-Many-Families Spine

| Field | Value |
|---|---|
| Status | Proposed |
| Date | 2026-06-21 |
| Deciders | jhosm |
| Shape | Tool-selection ([ADR-PC-000 §D3](./ADR-PC-000-namespace-and-contract-shape-framework.md) residual category — a family-scoping / structural decision, declared tool-selection per the [§D4](./ADR-PC-000-namespace-and-contract-shape-framework.md) default; F1/F2 do not discriminate, the same class as [ADR-PC-021](./ADR-PC-021-application-layer-family-owned-deciders.md) and [ADR-PC-030](./ADR-PC-030-product-scope-and-boundary.md)) |
| Common criteria | [ADR-IC-000](../../integration_concepts/adrs/ADR-IC-000-common-evaluation-criteria.md) (reused per [ADR-PC-000](./ADR-PC-000-namespace-and-contract-shape-framework.md) D2) |
| Depends on | [ADR-PC-030](./ADR-PC-030-product-scope-and-boundary.md) (the product scope — names `credito_pessoal` as roadmap item 2, the closed-end asset; fixes the boundary this family honours), [ADR-PC-021](./ADR-PC-021-application-layer-family-owned-deciders.md) (the family-as-plugin spine this rides — pure folds + a family-owned decider over a family-agnostic engine), [ADR-PC-024](./ADR-PC-024-constitution-precondition-contract.md) (the precondition contract — keeps origination upstream; the engine records verdicts, never makes the decision), [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) (the deterministic kernel + the `Money`/decimal rounding discipline the amortization math obeys), [ADR-PC-008](./ADR-PC-008-rate-sheet-storage-and-deploy-api.md) (rate resolution at disbursement — the decider's stamp) |
| Resolves | bd `babelstone-g6ar` (Author credito_pessoal family); realises [ADR-PC-030 §Open Action 2](./ADR-PC-030-product-scope-and-boundary.md) |

---

## Context

[ADR-PC-030](./ADR-PC-030-product-scope-and-boundary.md) fixed what babelstone is *for* — a core product & account ledger — and drew a **family roadmap** spanning the retail product topology: a liability (term deposit, *built*), a **closed-end asset** (`credito_pessoal`, *next*), an open-end revolving asset (credit card), and the transactional account (conta à ordem). [ADR-PC-021](./ADR-PC-021-application-layer-family-owned-deciders.md) fixed *how a family is added*: own event records, pure fold handlers, a lifecycle legality table, projections, an `IFamilyModule`, and a family-owned decider — all reaching `main` through a one-way **family → engine** dependency arrow, with **zero generic-engine diff**. A single reference family — `term_deposit` — proved both.

**In plain English:** this ADR adds the second family — a closed-end personal loan (*crédito pessoal*). Where a term deposit takes money *in* and accrues it to a single maturity, a personal loan pays a lump sum *out* at the start and the borrower pays it back in equal monthly installments, each split into shrinking interest and growing capital until the balance hits zero. It is deliberately the **lowest-risk** next family: it mirrors the term deposit's closed-end, deterministic-schedule shape, so it proves the family abstraction generalises **from a liability to an asset** before the heavier revolving/transactional shapes land. Origination — the underwriting that decided to grant the loan — stays **upstream** ([ADR-PC-024](./ADR-PC-024-constitution-precondition-contract.md), [ADR-PC-030 §P1](./ADR-PC-030-product-scope-and-boundary.md)): the engine receives an **already-approved, already-priced** loan, computes its amortization schedule, disburses it, and records its lifecycle. It never models solvency, CRC, KYC, or scoring.

This entry is the [ADR-PC-000 §D3](./ADR-PC-000-namespace-and-contract-shape-framework.md) **residual category** ("operational/engineering discipline … fits neither template cleanly … default to tool-selection"), the same class as [ADR-PC-021](./ADR-PC-021-application-layer-family-owned-deciders.md) and [ADR-PC-030](./ADR-PC-030-product-scope-and-boundary.md): it scopes a **family**, not a tool. The honest consequence, surfaced up front: **F1 and F2 do not discriminate** — authoring a family buys no licence, and a closed-end loan family carries no PII on the durable bus (the same no-PII posture every family holds, [ADR-PC-004](./ADR-PC-004-pii-crypto-shredding.md)) and is not itself a regulated runtime artefact. The load-bearing question is **how to model the loan's three new dimensions on the existing spine without leaking any of them into the generic engine** — settled on S1–S4 plus the open/closed property the fold-plugin model already commits to.

### What is genuinely new vs. the term deposit

A term deposit and a personal loan are *both* closed-end instruments with a deterministic schedule — which is exactly why this family is low-risk. Three dimensions are genuinely new, and they are the whole content of the modelling:

| # | New dimension | What it is | Where it lives |
|---|---|---|---|
| 1 | **The amortization schedule** (*quadro de amortização*, French / constant-installment) | A fixed principal repaid in `n` equal monthly installments at the periodic rate `r = TAN / 12`; each installment splits into a shrinking interest leg `J(t) = S(t-1)·r` and a growing capital leg `A(t) = P − J(t)` until the balance reaches zero (fin-math §3–§4.1). | A pure `Amortization` kernel in `Babelstone.FinancialMath` (generic, names no family); the decider runs it command-side. |
| 2 | **Lump-sum disbursement** | The loan pays out the whole principal at `t=0` (a settlement **debit** against the lender's funding), where a deposit takes the principal *in*. | The `DisburseAsync` path: resolve → decide → **settle (debit)** → append, mirroring the deposit constitution path's choreography but with the money leg inverted. |
| 3 | **Capped early repayment** (*reembolso antecipado*) | A partial or full prepayment of the outstanding capital plus a **legally-capped** commission: `min(charged, statutory_cap) × capital_repaid`, never exceeding the interest the borrower would still have paid (fin-math §7.5). PT consumer-credit caps: 0.50% with >1y remaining, 0.25% with ≤1y. | The decider's `DecideEarlyRepayment`, off `Amortization.EarlyRepaymentCommission`; a full repayment pairs with a closing settlement. |

**Candidates evaluated** (how to model the loan family):

| # | Candidate | Notes |
|---|---|---|
| A | **Model on the term-deposit reference family, one-for-one** — own event records, pure folds, a lifecycle legality table, projections, an `IFamilyModule`, and a family-owned decider; add only the three new dimensions, and place the amortization math as a generic kernel in `Babelstone.FinancialMath`. | The [ADR-PC-021](./ADR-PC-021-application-layer-family-owned-deciders.md) plugin shape applied unchanged. Zero generic-engine diff; the math kernel is generic (names no family); the family → engine arrow holds. |
| B | **A bespoke loan engine / a loan-specific runtime** outside the family-plugin model. | Re-invents the spine the deposit family already proved; breaks the one-engine-many-families thesis ([01 §1](../01-product-architecture.md)); a second runtime to operate. |
| C | **Fold amortization math into the family handlers** rather than the command-side decider. | Breaks handler purity (BENG001/002/003) — a fold would compute a schedule rather than record an already-computed fact; the same collapse [ADR-PC-021 §D3](./ADR-PC-021-application-layer-family-owned-deciders.md) forbids. |
| D | **Put the amortization kernel in the family project, not `FinancialMath`.** | Duplicates the rounding discipline ([ADR-PC-010 §P1–§P2](./ADR-PC-010-dotnet-hand-rolled-engine.md)) the kernel exists to centralise; a future family (credit-card revolving interest) would re-implement it. |

---

## Evaluation

### Hard filter results

#### F1 · Cost / licensing

| Candidate | Licence / cost | Verdict |
|---|---|---|
| A · model on term_deposit | No tool, no licence; new family projects + a generic kernel addition. | **Pass** |
| B · bespoke loan engine | Same licence (zero), but a second runtime to build and operate. | **Pass** |
| C · math in folds | Same; no new project. | **Pass** |
| D · kernel in the family | Same; no new project. | **Pass** |

Uniform pass — F1 does not discriminate (a family buys nothing).

#### F2 · Regulatory fit (GDPR / DORA / PSD2)

A family is not itself a regulated runtime artefact, so F2 cannot *fail* a candidate. The regulatory-weight properties this family exercises are owned by *other* ADRs and hold identically under all four candidates: **no PII on the durable bus** ([ADR-PC-004 §P2](./ADR-PC-004-pii-crypto-shredding.md) — every loan event carries computed facts + opaque references, never a borrower's name/NIF/IBAN), **origination stays upstream** ([ADR-PC-024](./ADR-PC-024-constitution-precondition-contract.md) / [ADR-PC-030 §P1](./ADR-PC-030-product-scope-and-boundary.md) — the engine records the upstream decision, never makes it), and **GDPR Article 17 crypto-shredding** ([ADR-PC-004 §P3](./ADR-PC-004-pii-crypto-shredding.md) — the loan carries the same erasure event the deposit does). The PT consumer-credit **early-repayment cap** (DL 133/2009 art. 19) is a *correctness* property of the decider's math, gated by tests, not a filter a candidate passes or fails.

| Candidate | GDPR | DORA / PSD2 | Verdict |
|---|---|---|---|
| A / B / C / D | No PII on the bus; erasure event present; origination upstream. | No payment execution in-engine (stops at the wire, [ADR-PC-030](./ADR-PC-030-product-scope-and-boundary.md)). | **Pass** |

All four clear the hard filters. The decision is entirely in S1–S4 and the open/closed analysis — the expected shape for the [§D3](./ADR-PC-000-namespace-and-contract-shape-framework.md) residual category.

---

### Soft criteria

#### A · Model on the term-deposit reference family, one-for-one — **CHOSEN**

**S1 · Operational complexity for 1–2 people.** Lowest. The family is a self-contained subtree (`families/credito-pessoal/`) added the [ADR-PC-021](./ADR-PC-021-application-layer-family-owned-deciders.md) way — drop in the fold project + the decider project, wire nothing in the generic engine. The host already assembly-scans `IFamilyModule` fold modules (`FamilyModuleLoader`) and `IFamilyHostModule` host modules (`HostModuleLoader`), so discovery needs **no per-family hand-edit**. The only generic-side change is the additive `Amortization` kernel in `Babelstone.FinancialMath`, which names no family.

**S2 · Ecosystem coherence — decisive.** The engine already commits to a **family-as-plugin** model ([ADR-PC-021 §D2](./ADR-PC-021-application-layer-family-owned-deciders.md)); A extends that exact commitment to a second, *asset-side* family. The dependency arrow stays **family → engine** (gated by `ENGINE_FAMILY_AGNOSTIC`), the folds stay pure (the math runs command-side, gated by the determinism analysers), and the `Money`/decimal rounding discipline ([ADR-PC-010 §P1–§P2](./ADR-PC-010-dotnet-hand-rolled-engine.md)) governs the amortization math. B bends the arrow back (a second runtime); C collapses fold purity; D duplicates the rounding discipline.

**S3 · Exit cost.** Low. The decider's disbursement choreography (resolve → stamp → settle → append) is written as the *same separable steps* the term-deposit constitution path uses, so the generic `ConstitutionPipeline` extraction ([ADR-PC-021 §P5](./ADR-PC-021-application-layer-family-owned-deciders.md), bd `babelstone-osv6`) — triggered by *this* being the second decider — is a lift, not a rewrite.

**S4 · Longevity.** Neutral — the layering outlives any one family; the loan amortization kernel is the reusable asset the credit-card revolving family will build on.

**Decisive project-specific reason — proves the family abstraction generalises liability → asset.** [ADR-PC-030](./ADR-PC-030-product-scope-and-boundary.md)'s thesis is one deterministic-ledger kernel expressing every retail product shape. The closest-to-the-deposit asset is the lowest-risk evidence that the abstraction is not secretly liability-shaped: a loan inverts the cash-flow direction (out at `t=0`, in over the term) and replaces accrual-to-maturity with amortization-to-zero, yet reuses the *identical* spine — pure folds, a lifecycle table, a family-owned decider, the precondition contract, the erasure event, the rate-sheet stamp. A de-risks the heavier revolving/transactional families by proving the asset direction first.

#### B · Bespoke loan engine — **rejected**

A second runtime re-invents the spine the deposit family already proved and breaks the one-engine-many-families thesis ([01 §1](../01-product-architecture.md)). Rejected on S1 + S2 with no offsetting gain.

#### C · Amortization math in the folds — **rejected**

A fold that computes a schedule (rather than recording an already-computed installment fact) reads the rate and runs the kernel inside the pure layer — exactly the pure/impure collapse [ADR-PC-021 §D3](./ADR-PC-021-application-layer-family-owned-deciders.md) forbids and the BENG001/002/003 analysers gate. Rejected on S2.

#### D · Amortization kernel in the family project — **rejected**

The `Money`/decimal one-boundary rounding discipline ([ADR-PC-010 §P1–§P2](./ADR-PC-010-dotnet-hand-rolled-engine.md)) exists to be centralised; an amortization kernel in the family project would duplicate it, and the next family that needs amortization (credit-card revolving interest) would re-implement it. The kernel belongs beside `Accrual` / `Withholding` / `RateSchedule` in `Babelstone.FinancialMath` — generic, naming no family. Rejected on S2/S4.

**Decisive reason for A:** the engine is the reusable asset, the team is 1–2 people adding families over time, and the fold layer already commits to a `family → engine` plugin arrow with a centralised money kernel — all three point to modelling the loan on the proven term-deposit shape with its math in `FinancialMath`. B/C/D each break one of those three.

---

## Decision

### `credito_pessoal` is a closed-end amortizing-loan family on the [ADR-PC-021](./ADR-PC-021-application-layer-family-owned-deciders.md) spine; its amortization math is a generic `FinancialMath` kernel; origination stays upstream.

- **D1 — A family on the proven spine.** `credito_pessoal` is authored exactly as the term-deposit reference family: pure fold handlers + event records (`Babelstone.Families.CreditoPessoal`), a lifecycle legality table, projections, an `IFamilyModule`, and a family-owned decider (`Babelstone.Families.CreditoPessoal.Application`). The dependency arrow is **family → engine**; adding it is **zero generic-engine diff** (the `Amortization` kernel is the only generic addition, and it names no family — gated by `ENGINE_FAMILY_AGNOSTIC`).
- **D2 — The closed-end-asset lifecycle.** The loan lifecycle is `disburse → amortize → (settle | write off)`, plus the disbursement-refusal and GDPR-erasure terminals. The events are `LoanDisbursed`, `LoanDisbursementFailed`, `LoanInstallmentPaid`, `LoanRepaidEarly`, `LoanSettled`, `LoanWrittenOff`, `PersonalDataErasureRequested`; the states are `Pending → Active → (Failed | Settled | WrittenOff) ; Erased`. The single `LifecycleTransitions` table is the auditable legality source the decider consults before appending — every business-terminal state is closed to every business transition; GDPR erasure is the one cross-cutting transition legal from any PII-holding state.
- **D3 — Three new dimensions, all command-side.** The **amortization schedule** (French / constant-installment), **lump-sum disbursement** (a settlement *debit* at `t=0`), and **capped early repayment** are the only genuinely-new modelling vs. the deposit. All three run in the **decider** (the impure command layer); the **folds stay pure** and only record the already-computed facts the events carry (the installment split, the capped commission). The amortization math is the generic `Amortization` kernel in `Babelstone.FinancialMath`, obeying the [ADR-PC-010 §P1–§P2](./ADR-PC-010-dotnet-hand-rolled-engine.md) one-boundary `Money` rounding (each installment's interest rounded once, capital the integer residual, the schedule conserving to the cent with a balancing final row).
- **D4 — Origination is upstream; the engine records, never decides.** Per [ADR-PC-030 §P1](./ADR-PC-030-product-scope-and-boundary.md) and [ADR-PC-024](./ADR-PC-024-constitution-precondition-contract.md), the loan arrives **already approved and priced**. The engine resolves the rate sheet for lineage, computes the schedule, disburses, and amortizes. The solvency / CRC origination checks are recorded only as **opaque precondition verdicts** ([ADR-PC-024](./ADR-PC-024-constitution-precondition-contract.md)) the family decider refuses on if a required verdict is absent or unsatisfied — the *same* precondition contract the deposit family uses, never an in-engine evaluation. Collections *enforcement* (PARI/PERSI) is likewise upstream; the engine only *records* a `LoanWrittenOff`.
- **D5 — The capped early repayment is statute-bounded.** The early-repayment commission is `min(charged_bps, statutory_cap_bps) × capital_repaid`, further capped at the lost-interest ceiling (fin-math §7.5). The PT consumer-credit statutory caps (0.50% with >1y remaining, 0.25% with ≤1y) are the ceiling the decider enforces; the CUE schema bounds the *charged* rate to the >1y ceiling, and the runtime decider applies the tighter remaining-term cap and the lost-interest ceiling.

**Rejected: a bespoke loan engine** — re-invents the spine, breaks one-engine-many-families. **Rejected: amortization math in the folds** — collapses fold purity. **Rejected: the kernel in the family project** — duplicates the centralised money rounding discipline.

---

## Implementation Principles

### P1 — Project topology (the [ADR-PC-021 §P1](./ADR-PC-021-application-layer-family-owned-deciders.md) shape)

```
families/credito-pessoal/src/
  Babelstone.Families.CreditoPessoal/              pure folds + events + projections + lifecycle table
      refs: Babelstone.Engine, Babelstone.FinancialTypes        (cannot reach a DB or the kernel)
  Babelstone.Families.CreditoPessoal.Application/  the decider (commands → events)
      refs: Babelstone.Engine, Babelstone.FinancialMath, Babelstone.FinancialTypes,
            Babelstone.RateSheets, Babelstone.Packs, Babelstone.Families.CreditoPessoal
engine/src/Babelstone.FinancialMath/
  + Amortization                                   the one new generic, family-agnostic kernel
contracts/
  avro/loans/credito_pessoal/*.avsc                the bus-published event payload schemas
  cue/families/credito-pessoal.cue                 the product-config family schema
```

### P2 — The amortization kernel is generic and obeys the money discipline

`Amortization` (`LevelInstallment`, `Schedule`, `PeriodInterest`, `OutstandingBalanceAfter`, `EarlyRepaymentCommission`) lives beside `Accrual` / `Withholding` / `RateSchedule` in `Babelstone.FinancialMath`. It names no family (so `ENGINE_FAMILY_AGNOSTIC` holds), computes wholly in `decimal`, crosses to `Money` exactly once per amount ([ADR-PC-010 §P2](./ADR-PC-010-dotnet-hand-rolled-engine.md)), and builds the schedule by exact integer-cent arithmetic off the one rounded installment — so the capital legs sum to the principal and the balance reaches zero exactly (the balancing final row absorbs rounding). The `(1+r)^n` term uses the kernel's existing `DecimalMath.Pow`, never `Math.Pow`.

### P3 — Folds stay pure; the decider is the impure layer

The folds LABEL state and accumulate the carried facts (the installment legs, the outstanding balance, the running totals) — they never compute a schedule or re-derive an amortization split. The decider rebuilds the schedule from the loan's pinned facts and emits the next row; the folds record it. The amortization-schedule projection records each installment *as stamped*, never recomputed — the same flow-by-flow discipline the deposit's accrual/withholding projections hold.

### P4 — The disbursement choreography mirrors the deposit constitution for the pipeline lift

The decider's `DisburseAsync` is written as the same separable steps the term-deposit constitution path uses — **resolve rate sheet → stamp tan + version → decide (compute schedule) → settle → append**, with the money leg a *debit* (loan pays out) where the deposit's is the principal *in*. This deliberate shape-match is what lets the second-decider `ConstitutionPipeline` extraction ([ADR-PC-021 §P5](./ADR-PC-021-application-layer-family-owned-deciders.md), bd `babelstone-osv6`) be a lift rather than a rewrite.

### P5 — Discovery is host assembly-scan; no per-family hand-edit

The family is discovered by the existing `FamilyModuleLoader` (folds) and `HostModuleLoader` (host wiring) assembly scans ([ADR-PC-021 §A13–§A14](./ADR-PC-021-application-layer-family-owned-deciders.md)). Authoring the family is its projects + the host `ProjectReference` (the scan anchor), never a surgical edit to generic compose code.

---

## Consequences

**What this choice makes easier:**

1. **The family abstraction is proven liability → asset.** A closed-end loan reuses the identical spine with an inverted cash-flow direction and amortization-to-zero, evidence the abstraction is not secretly liability-shaped — de-risking the revolving/transactional families.
2. **A reusable amortization kernel.** The credit-card revolving-interest family inherits `Amortization` and the centralised money rounding, not a re-implementation.
3. **The second decider triggers the pipeline lift.** With two deciders sharing the resolve → stamp → settle → append choreography, the `ConstitutionPipeline` extraction (bd `babelstone-osv6`) is now actionable on evidence, not pre-built on one example.

**What this choice makes harder or impossible:**

1. **The family decider repeats the term-deposit choreography until the pipeline is extracted.** Accepted as cheaper than a one-example abstraction; the steps are written for-lift ([ADR-PC-021 §P5](./ADR-PC-021-application-layer-family-owned-deciders.md), §P4 above).
2. **Variable-rate, grace-period (*carência*), and balloon-installment loans are out of scope here.** v1 prices a fixed-rate, full-amortization, monthly-grid loan; the richer shapes are fine-drift extensions (the same coarse-start discipline the term-deposit schema took, authoring §3.1).

**Residual risks:**

- **The lost-interest ceiling is an upper-bound approximation.** The decider bounds the early-repayment commission by an over-stated lost-interest figure (the remaining schedule's interest on the repaid capital), so the cap is conservative (never under-clamps). An exact remaining-schedule interest sum is a tightening available later if a product needs it.
- **The schedule is rebuilt per installment.** The decider rebuilds the whole schedule and indexes the next row, rather than incrementally re-deriving a balance — chosen so the integer-cent conservation and the balancing final row hold. For long terms this is cheap; a memoised schedule projection is the optimisation if profiling ever demands it.

---

## Verifiable commitments

This decision's load-bearing commitments are fitness functions in the [commitment catalogue](./commitment-catalogue.md) — the single source of truth for each commitment's exact claim, gate (pyramid level), and `Live`/`Planned`/`Gap` status ([ADR-PC-020 §P5–§P7](./ADR-PC-020-llm-toolchain-and-conformance-governance.md)):

| # | Commitment (with §-anchor) | Gate (pyramid level) | Test ID | Status |
|---|---|---|---|---|
| 1 | The `credito_pessoal` family adds **zero generic-engine diff** — the spine carries no reference to `families/**`, and the `Amortization` kernel added to `Babelstone.FinancialMath` names no family (§D1, §P2). The existing `ENGINE_FAMILY_AGNOSTIC` gate covers this family unchanged. | architecture / dependency assertion (CI) | `ENGINE_FAMILY_AGNOSTIC` | Live |
| 2 | The family's fold handlers read no clock, do no I/O, and use no randomness — the amortization math runs command-side in the decider, never in a fold (§D3, §P3). The existing `DETERMINISM_GATE` analyser covers this family's folds unchanged. | analyser / CI determinism gate | `DETERMINISM_GATE` | Live |
| 3 | The French-system amortization schedule **conserves capital to the principal to the cent** and zeroes the balance, and the early-repayment commission **never exceeds `min(charged, statutory_cap) × capital_repaid` nor the lost-interest ceiling** (§D3, §D5, §P2). Pinned by `AmortizationMathTests` / `CreditoPessoalDeciderTests` (the worked fin-math §4.1 example matched to the cent, the conservation invariants, the statutory-cap clamp). | unit (Docker-free) | `CREDITO_PESSOAL_AMORTIZATION_MATH` | Planned |

Related: this family's family-agnosticism rides the engine-level [`ENGINE_FAMILY_AGNOSTIC`](./commitment-catalogue.md) (row 12) and the variant-level [`ZERO_ENGINE_DIFF_PER_VARIANT`](./commitment-catalogue.md) (row 9) — adding a *family* is zero *generic*-engine diff, the same property [ADR-PC-021](./ADR-PC-021-application-layer-family-owned-deciders.md) gates. Row 3 above (`CREDITO_PESSOAL_AMORTIZATION_MATH`) is the family-specific financial-math correctness commitment; it is `Planned` (the tests exist and pass Docker-free, but the catalogue row is registered as the §P6 coverage checker's target when this ADR is Accepted).

---

## Cross-references

- [ADR-PC-030](./ADR-PC-030-product-scope-and-boundary.md) — the product scope that names `credito_pessoal` as the closed-end asset (roadmap item 2) and fixes the boundary (origination upstream; the engine records, never decides).
- [ADR-PC-021](./ADR-PC-021-application-layer-family-owned-deciders.md) — the one-engine-many-families spine this family rides (pure folds + a family-owned decider over a family-agnostic engine); the `ConstitutionPipeline` lift (bd `babelstone-osv6`) the disbursement choreography is written for.
- [ADR-PC-024](./ADR-PC-024-constitution-precondition-contract.md) — the precondition contract keeping origination upstream; the loan family reuses it for the solvency/CRC verdicts.
- [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) — the deterministic kernel + the `Money`/decimal one-boundary rounding the `Amortization` kernel obeys.
- [ADR-PC-008](./ADR-PC-008-rate-sheet-storage-and-deploy-api.md) — rate resolution at disbursement (the decider's stamp).
- [ADR-PC-004](./ADR-PC-004-pii-crypto-shredding.md) — the no-PII-on-the-bus posture and the GDPR Article 17 crypto-shredding the loan's erasure event records.
- [financial_concepts §3–§4, §7.4–§7.5](../../financial_concepts/banking_products_financial_mathematics.md) — the amortization (French system) and early-repayment math the kernel implements.
- [01 §1](../01-product-architecture.md) — the one-engine-many-families thesis this family extends from a liability to an asset.

---

*Proposed 2026-06-21 by jhosm.*
