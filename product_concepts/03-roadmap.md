# Roadmap

> Sequenced expansion across product families and geographies. Each phase has a rationale, not just a label. The order is the strategy: the engine and the pack abstraction are proven at each step, not retrofitted later.

---

## The Sequence at a Glance

The table sequences *product-family deployment*. Pack design runs as a parallel track from v2 onwards (see [Parallel Track](#parallel-track-es-pack-design-starts-at-v2)) — ES pack design begins at v2 even though ES first deploys at v5+.

| Phase | Product family | Pack (deployment) | Rationale |
|---|---|---|---|
| **v1** | Term deposits | PT | Simplest math; validates engine + pack end-to-end |
| **v2** | Personal credit (Price / SAC) | PT | First production run of the unification wedge across a second family; introduces TAEG with charges and DL 133/2009 |
| **v3** | *Crédito à habitação* (mortgage) | PT | Largest PT retail portfolio; adds variable rate, Euribor revision, mandatory insurance, DL 74-A/2017 |
| **v4** | Current accounts + cards (irregular family) | PT | Completes the engine's range. Firm long-term goal, optional in practice (see below) |
| **v5+** | Term deposits + personal credit | ES (deployment) | First *deployment* of the second pack; pack design has been active since v2 |
| **v6+** | EU expansion | EU baseline + per-country deltas | CCD 2008/48/EC and MCD 2014/17/EU baseline; country-specific deltas as additional packs |

Numbering is illustrative, not contractual — phases may overlap or be adopted in a different order. The sequencing logic is fixed; the version labels are not.

---

## v1 — Term Deposits (PT)

Full specification: [02](./02-v1-scope-term-deposits.md). The rationale belongs here.

The simplest cash-flow math in retail banking (per [financial_concepts §5](../financial_concepts/banking_products_financial_mathematics.md)) — at most three cash flows in the basic case. The narrowest slice of the PT regulatory pack: Act/360, TANB/TANL, 28% withholding, BdP reporting hooks. End-to-end exercise of the engine, event store + projections, regulatory pack, and integration seam — on a product where every formula has a closed form and a worked example. Aligned with the running example in [integration_concepts/](../integration_concepts/00-introduction-and-decisions.md), so the integration architecture is documented and worked out for this product, not theoretical.

v1 is the architectural proof. Every subsequent phase is configuration on top of a known-working engine.

---

## v2 — Personal Credit (PT)

The first phase where the unification wedge runs in production across more than one family. v1 ran a single product on the engine; v2 runs a second, structurally different product on the *same* engine. Product configuration changes; runtime does not.

In scope: Portuguese unsecured personal credit (*crédito pessoal*) under Price and SAC systems (per [financial_concepts §4](../financial_concepts/banking_products_financial_mathematics.md)). Fixed rate, fixed term, monthly installments. New pieces relative to v1:

- **TAEG with charges.** TAEG is the IRR of the full cash flow including all mandatory charges (per [financial_concepts §6.2](../financial_concepts/banking_products_financial_mathematics.md)). The engine treats charges (opening fee, maintenance fee, mandatory PPI premium if any) as first-class cash flows and runs a numerical IRR solver to publish TAEG on every offer. v1 had charges as a configuration capability; v2 exercises it in production.
- **DL 133/2009 compliance.** Transposes CCD 2008/48/EC. The PT pack ships the SECCI pre-contractual information sheet, the legal right of withdrawal, the cost-of-credit breakdown, and dispute-resolution disclosures.
- **Amortisation schedule semantics.** A credit produces an amortisation schedule on day one. Events either match the schedule (`InstallmentPaid`) or trigger deviations (`InstallmentMissed`, `AmortizationAdvanced`, `PrestaçãoExtraordináriaApplied`). The with-a-plan mode from [01 §4](./01-product-architecture.md) is exercised in earnest.

After v2, new credit configurations are configuration work, not module work.

---

## v3 — *Crédito à Habitação* (PT)

The largest PT retail portfolio by balance and by political weight. Mortgages are the family that most directly tests whether the engine can absorb a substantively more complex configuration without breaking the abstraction. New pieces relative to v2:

- **Variable rate.** Almost all PT mortgages are Euribor-indexed with periodic revision (typically 3, 6, or 12 months). The math (per [financial_concepts §7.2](../financial_concepts/banking_products_financial_mathematics.md)): the schedule is recomputed at each revision date with the new effective rate; the engine reacts to a `EuriborRateRevised` event by re-projecting the remaining installments. A substantively different cash-flow shape from v2's fixed-rate credit, but still a configuration of the same engine.
- **Mandatory insurance.** Life insurance (*seguro de vida*) and property insurance (*seguro multirriscos*) are typically mandatory. Premiums are cash flows; they enter the TAEG; coverage events are state transitions. The pack governs which insurances are mandatory; the product configuration governs which specific products the bank ties to a given offer.
- **DL 74-A/2017 compliance.** Transposes MCD 2014/17/EU. New pack items: FINE (*Ficha de Informação Normalizada Europeia*), the 7-day reflection period, creditworthiness assessment, early-repayment compensation rules, foreign-currency-loan provisions.
- **Composite cases.** PT mortgages frequently include grace periods (*carência*), balloon installments (*prestações extraordinárias*), and early-repayment events (*amortização antecipada*). Math: [financial_concepts §7.1, §7.3, §7.5](../financial_concepts/banking_products_financial_mathematics.md). The configuration surface supports each as a parameter, not a code change.

v3 exercises the regulatory pack seriously. DL 74-A/2017 is many times the surface of v1's depósito-a-prazo subset. If the pack abstraction survives v3, it survives anything.

---

## v4 — Current Accounts and Cards (PT)

The irregular family. Once v4 ships, every retail product family is on the same engine. New pieces relative to v3:

- **Irregular operating mode.** v1–v3 ran the engine's *with-a-plan* mode — schedules computed ex ante, events reconcile to the schedule. v4 introduces the *irregular* mode (per [01 §4](./01-product-architecture.md), [financial_concepts §8](../financial_concepts/banking_products_financial_mathematics.md)): no schedule, balance evolves event by event, interest computed retrospectively over the realised balance path (`J = Σ S(d) × r × Δt`).
- **Continuous-state projections.** Permanently open balances. Projections must support point-in-time queries efficiently across a long history. v1–v3 handled at-most-a-few-years lifecycles; v4 changes the access pattern. Snapshot-infrastructure implications: [two-modes §5.5](./feature-design-two-modes-asymmetry.md).
- **Card-specific surface.** Credit limits, billing cycles, minimum-payment rules, revolving evolution (per [financial_concepts §8.5](../financial_concepts/banking_products_financial_mathematics.md)). A configuration of the irregular mode, not a separate product type.

**Why last in PT.** The legacy core's current-account module is the most deeply entrenched piece of the bank's estate. Every other system references current-account IDs; payments rails settle into current accounts; the GL is structured around them. Moving current accounts is an estate-wide event, not a product migration. Going last lets the strangler-fig motion (a) prove the engine on three other families first, (b) build out the coexistence APIs from [02 §3](./02-v1-scope-term-deposits.md) to a level where multiple families settle cleanly into legacy DDA, and (c) make the v4 cutover a genuine decision rather than an act of faith.

### v4 stance: firm long-term goal, optional in practice

v4 is a firm long-term goal. The architecture supports current accounts and cards, the irregular mode is part of the engine's design point (not a retrofit), and operational tooling for high-volume ingest is built out by v3 at the latest. The six v1 commitments that keep v4 architecturally viable — and the synthetic v4-scale load test that proves them at v1 acceptance — are specified in [two-modes](./feature-design-two-modes-asymmetry.md).

v4 is also explicitly optional in practice. The bank can stop at v1–v3 on the new engine, keep current accounts and cards on legacy DDA indefinitely, and still extract the full agility wedge for the families that have moved. This is a valid endpoint — sometimes called a "non-core core": the engine handles configurable products, the legacy core handles current accounts and the GL, and the integration architecture from [integration_concepts/](../integration_concepts/00-introduction-and-decisions.md) keeps them coherent.

The two framings are not in tension. The architecture supports v4; the decision to take v4 into production is separate from the decision to build the capability.

---

## v5+ — Iberia: ES (Term Deposits + Personal Credit)

The first production proof of regulatory-as-a-pack. Up to v4 the engine runs on a single deployed pack (PT). v5 is where the ES pack — designed as a parallel track during v2–v3 — first deploys in production, exercised on the lowest-risk product families.

Why those two families: term deposits and personal credit have the smallest cross-border regulatory delta inside the EU. Both are covered by harmonising directives (term deposits via DGSD 2014/49/EU, personal credit via CCD 2008/48/EC) with thin per-country deltas. Spain transposes CCD 2008/48/EC as *Ley 16/2011 de Contratos de Crédito al Consumo*; the IRS-equivalent withholding regime (*retención a cuenta del IRPF*) is administratively different from PT but mathematically the same shape — a flat withholding on interest, applied flow-by-flow.

What v5 must prove:

- **Pack swap is a configuration change.** A deployment pointing at the ES pack uses the same images, engine binary, event schemas, event-store and projection structure. Only the pack differs. If anything in the engine has to change for ES, the pack abstraction failed and the wedge is at risk.
- **Reporting hooks remap cleanly.** BdP reporting (v1–v4) becomes Banco de España and AEAT reporting (v5+). The engine emits abstracted signals; the geography-specific reporting application interprets them.
- **Disclosure documents are pack outputs.** FIN and SECCI have ES counterparts. The pack ships the templates and the data they need; the engine doesn't know about specific documents.

If the architecture is right, v5 is unglamorous — a re-deployment with a different pack and a supervised operating period. If it is *not* unglamorous, the pack abstraction goes back to the drawing board before v6+ is contemplated.

---

## v6+ — EU Expansion

Not one phase but a per-country sequence with a common floor. The EU baseline:

- **CCD 2008/48/EC** — Consumer Credit Directive. TAEG calculation method, SECCI pre-contractual information, 14-day right of withdrawal, cost-of-credit definition.
- **MCD 2014/17/EU** — Mortgage Credit Directive. FINE, 7-day reflection period, creditworthiness assessment, foreign-currency-loan rules.
- **DGSD 2014/49/EU** — Deposit Guarantee Schemes Directive. €100,000 coverage and reporting.
- **PSD2** (2015/2366) and adjacent — payments and account access. Relevant for the integration seam more than the product engine.

Each country then ships deltas: the transposition law, tax treatment, reporting agency, local-language disclosure templates, day-count or rate conventions that local market practice has standardised. The country pack is a small file by design — the EU baseline does most of the work, the country pack overrides only what is genuinely different.

The order inside v6+ is demand-driven, not architecturally driven. The architecture is ready after v5; which country comes next depends on which subsidiary or operating geography the bank takes on. Until a specific geography is committed, the order is open.

---

## Pack Maintenance — A Continuous Track

The phase table suggests a discrete march of pack introductions: PT in v1, ES in v5, EU baseline in v6. The reality is that a regulatory pack is not finished when it ships. PT regulation changes continuously: a BdP *Aviso* updates a reporting threshold; a new *Decreto-Lei* transposes a revised EU directive; an IRS Budget Law changes the withholding rate. ES and EU packs evolve at the same cadence.

Pack maintenance is a continuous track in parallel with the phase roadmap, not an event inside any phase:

- **Watch.** A small per-jurisdiction surveillance function tracks regulatory publications — BdP *Avisos* and *Instruções* and *Diário da República* for PT; *Boletín Oficial del Estado* and Banco de España for ES; *Official Journal of the EU* for directives. One named owner per pack.
- **Diff and decide.** Each change is classified: *configuration-only* (a parameter changes), *pack-data* (a new disclosure template, a new reporting field), or *engine* (rare — a fundamentally new regulatory primitive that doesn't fit the current pack surface). The first two ship as pack updates; engine changes feed the roadmap.
- **Release cadence.** Known cadence (e.g. monthly minor releases, quarterly major releases) plus emergency releases for regulatory deadlines.
- **Backward compatibility.** A pack update cannot retroactively change accruals or balances on existing accounts — that would be unauditable. Pack changes apply prospectively from a `pack_effective_date`; the engine carries the effective pack version per account so historical reconstructions remain consistent.

Pack maintenance is a product, not a side-effect. It is the largest recurring operational cost beyond engine development itself, and it has to be staffed accordingly from day one.

---

## Parallel Track: ES Pack Design Starts at v2

The phase table sequences ES under v5+, which is when the ES pack first *runs* in production. It does not sequence when the ES pack is *designed*. Read literally, ES pack design starts only after v4 — too late.

ES pack design is a parallel track that begins at v2 and is complete by v3. The v-numbered sequence remains a product-family deployment ladder; ES pack design overlaps it as named parallel work. v5+ is a *deployment* milestone, not a *design* milestone. Full reasoning: [authoring §8](./feature-design-configuration-authoring.md). Summary:

- **v2 is the first phase where the pack abstraction is genuinely exercised.** TAEG, DL 133/2009 disclosures, charge handling — all are pack-defined and all are designed alongside the second pack. The right phase to start the ES pack is v2, because the abstractions being designed are the ones that have to swap cleanly between PT and ES.
- **v3 is the phase where the pack carries the most surface.** DL 74-A/2017, mandatory insurance, variable rate. The test of pack abstraction is whether the most complex surface swaps cleanly. v3 is where ES pack design catches up to PT's depth.
- **v5+ is the deployment milestone.** The pack is two phases old by the time it ships. v5+ work is operational: a re-deployment with the ES pack and a supervised operating period, plus the deltas (reporting agencies, disclosure rendering, local-language templates) that emerge only in production.

The PT product-family order does not change — v2 and v3 still ship PT-first because the bank's volume and regulatory expertise are PT-side. What changes is that pack work is a parallel track, not a sequential phase. [01 §5](./01-product-architecture.md) commits to "the pack is swappable from day one"; a pack that only ever holds PT until v5 is a *de facto* fork. Only a pack that holds two jurisdictions concurrently in active development proves the abstraction is real.

Deliverables of the ES-pack parallel track during v2–v3:

- An ES pack manifest (the v5+ shape, per [surface §3.4](./feature-design-configuration-surface.md)) with primitives, parameters, and reporting hooks bound for ES.
- An ES test corpus (per [surface §3.9](./feature-design-configuration-surface.md)) — canonical instances with expected event sequences for the v5+ product set, run in CI against every engine release.
- ES-pack versions of the disclosure templates referenced by v2 (SECCI-equivalent) and v1 (FIN-equivalent).
- A documented PT-vs-ES delta per category (rates, day-count, withholding, reporting), so the v5+ deployment team focuses on operational onboarding rather than architectural design.

If the ES pack has to be invented under deployment pressure at v5, the pack abstraction has failed.

---

## The Underlying Logic

Two axes drive the order:

1. **Ramp complexity along the engine's range.** Term deposits (simple cash flows, with-a-plan) → personal credit (Price/SAC, with-a-plan plus TAEG) → mortgage (variable rate, composite cases, with-a-plan at maximum complexity) → current accounts and cards (irregular mode). The engine acquires capability in the order it is needed. No capability is built speculatively.
2. **Validate the pack swap on familiar product families before expanding the family set in new geographies.** PT covers all four families first (v1–v4); the first geographic swap (v5) repeats only the first two families in the new pack. New geographies do not expand the family set at the same time they validate the pack.

If new product families had to be built per geography, the wedge dies under the combinatorial weight. If the pack swap had to be validated on every family at once, the bar for a new geography is too high. Sequencing both axes separately keeps each step contained.

---

## What Is **Not** on the Roadmap

- **GL, IFRS 9, channels, payments rails, fraud, KYC, onboarding.** Out of scope at every phase. Not later v's — someone else's product. The engine emits clean signals.
- **Wholesale, corporate, treasury, investment-banking.** This is a retail engine. Corporate banking has fundamentally different products and a fundamentally different operating model; absorbing it would dilute the architecture.
- **Non-EU geographies.** Switzerland, UK, US — each has a substantially different regulatory shape. The pack abstraction may eventually extend that far, but it is not a roadmap commitment.
- **A multi-currency core.** v1–v4 are EUR-only by configuration. Events carry `currency` because the schema convention requires it, but TAEG, withholding, and reporting paths are not exercised with mixed currencies. Multi-currency lands when the operating bank needs it, not on the v-numbered roadmap.
