# Roadmap

> Sequenced expansion across product families and geographies. Each phase has a rationale, not just a label. The rationale matters because the order is the strategy: the engine and the pack abstraction are being **proven** at each step, not retrofitted later.

---

## The Sequence at a Glance

| Phase | Product family | Pack | Rationale |
|---|---|---|---|
| **v1** | Term deposits | PT | Simplest math; validates engine + pack end-to-end |
| **v2** | Personal credit (Price / SAC) | PT | First product family where the unification wedge runs in production; introduces TAEG with charges and DL 133/2009 compliance |
| **v3** | *Crédito à habitação* (mortgage) | PT | Largest portfolio in PT retail; adds variable rate, Euribor revision, mandatory insurance, DL 74-A/2017 |
| **v4** | Current accounts + cards (irregular family) | PT | Completes the engine's range; firm long-term goal, optional in practice (see v4 section below) |
| **v5+** | Term deposits + personal credit | ES | First proof that regulatory-as-a-pack works; lowest-risk products in a new geography |
| **v6+** | EU expansion | EU baseline + per-country deltas | CCD 2008/48/EC and MCD 2014/17/EU baseline; country-specific deltas as additional packs |

The numbering is illustrative, not contractual — phases may overlap and may be adopted in a different order. What is fixed is the **sequencing logic** below, not the version labels.

---

## v1 — Term Deposits (PT)

The v1 slice is fully specified in [02-v1-scope-term-deposits.md](./02-v1-scope-term-deposits.md). The rationale belongs here:

The simplest cash-flow math in retail banking (financial_concepts §5). At most three cash flows in the basic case. The narrowest slice of the PT regulatory pack (Act/360, TANB/TANL, 28% withholding, BdP reporting hooks). End-to-end exercise of the engine, the subledger, the regulatory pack, and the integration seam — but on a product where every formula has a closed form and a worked example. Aligned with the running example in [integration_concepts/](../integration_concepts/00-introduction-and-decisions.md), so the integration architecture is not theoretical for this product, it is documented and worked out.

v1 is the architectural proof. Every subsequent phase is configuration on top of a known-working engine.

---

## v2 — Personal Credit (PT)

The first phase where the **unification wedge runs in production on more than one product family**. v1 ran a single product on the engine; v2 runs a second, structurally different product on the *same* engine. The product configuration changes; the runtime is unchanged. That is the agility wedge in operation, observed for the first time.

In scope: Portuguese unsecured personal credit (*crédito pessoal*) under the Price (French) and SAC (constant-amortisation) systems from [financial_concepts §4](../financial_concepts/banking_products_financial_mathematics.md). Fixed rate, fixed term, monthly installments. The new pieces relative to v1:

- **TAEG with charges.** [financial_concepts §6.2](../financial_concepts/banking_products_financial_mathematics.md) defines TAEG as the IRR of the full cash flow including all mandatory charges. The engine has to treat charges (opening fee, monthly maintenance fee, mandatory PPI premium if any) as first-class cash flows and run a numerical IRR solver to publish the TAEG on every offer. v1 had charges as a configuration capability but no v1 product exercised it; v2 is where it lands in production.
- **DL 133/2009 compliance.** *Decreto-Lei* 133/2009 transposes CCD 2008/48/EC (the EU Consumer Credit Directive). The PT pack now has to ship the SECCI pre-contractual information sheet, the legal right of withdrawal, the explicit cost-of-credit breakdown, and the dispute-resolution disclosures.
- **Amortisation schedule semantics.** A credit produces an amortisation schedule on day one; events on the account either match the schedule (`InstallmentPaid`) or trigger deviations (`InstallmentMissed`, `AmortizationAdvanced`, `PrestaçãoExtraordináriaApplied`). The engine's with-a-plan mode from [01-product-architecture §3](./01-product-architecture.md) is exercised in earnest.

v2 is the phase where the wedge is exercised on a structurally different product. After v2, new credit product configurations are configuration work, not module work.

---

## v3 — *Crédito à Habitação* (PT)

The **largest portfolio in PT retail** by balance and by political weight. Mortgages are the product family that most directly tests whether the engine can absorb a substantively more complex configuration without breaking the abstraction. The new pieces relative to v2:

- **Variable rate.** Almost all PT mortgages are Euribor-indexed with periodic revision (typically every 3, 6, or 12 months). [financial_concepts §7.2](../financial_concepts/banking_products_financial_mathematics.md) covers the math: the schedule is recomputed at each revision date with the new effective rate, and the engine has to react to a `EuriborRateRevised` event by re-projecting the remaining installments. This is a substantively different cash-flow shape from v2's fixed-rate credit, but it is still a *configuration* of the same engine.
- **Mandatory insurance.** Life insurance (*seguro de vida*) and property insurance (*seguro multirriscos*) are typically mandatory for PT mortgages. Premiums are cash flows in the engine's view; they are part of the TAEG; coverage events are state transitions. The pack handles which insurances are mandatory at the regulatory level; the product configuration handles which specific products the bank ties to a given mortgage offer.
- **DL 74-A/2017 compliance.** *Decreto-Lei* 74-A/2017 transposes MCD 2014/17/EU (the Mortgage Credit Directive) and is the PT mortgage-credit regulation. New pack items: the FINE (*Ficha de Informação Normalizada Europeia*), the 7-day reflection period, the creditworthiness assessment requirements, the early-repayment compensation rules, the foreign-currency-loan provisions.
- **Composite cases.** PT mortgages frequently include grace periods (*carência*), balloon installments (*prestações extraordinárias*), and early-repayment events (*amortização antecipada*). [financial_concepts §7.1, §7.3, §7.5](../financial_concepts/banking_products_financial_mathematics.md) cover the math. The engine's configuration surface needs to support each as a parameter, not a code change.

v3 is the phase where the regulatory pack is *seriously* exercised. DL 74-A/2017 is many times the surface of v1's depósito-a-prazo subset. If the pack abstraction survives v3, it survives anything.

---

## v4 — Current Accounts and Cards (PT)

The irregular family. **Completes the engine's range** — once v4 ships, every retail product family is on the same engine. The new pieces relative to v3:

- **Irregular operating mode.** v1–v3 ran the engine's *with-a-plan* mode (schedules computed ex ante, events reconcile to the schedule). v4 introduces the *irregular* mode from [01-product-architecture §3](./01-product-architecture.md) and [financial_concepts §8](../financial_concepts/banking_products_financial_mathematics.md): no schedule, balance evolves event by event, interest is computed retrospectively over the realised balance path (`J = Σ S(d) × r × Δt`). Same engine, different mode.
- **Continuous-state subledger.** Current accounts and cards have permanently open balances; the subledger has to support point-in-time queries efficiently across a long history. v1–v3 subledgers handled at-most-a-few-years lifecycles; v4 changes the access pattern.
- **Card-specific surface.** Credit limits, billing cycles, minimum-payment rules, revolving evolution ([financial_concepts §8.5](../financial_concepts/banking_products_financial_mathematics.md)). Treated as a configuration of the irregular mode, not a separate product type.

**Why last in PT.** The legacy core's current-account module is the **most deeply entrenched** piece of the bank's estate. Every other system in the bank references current-account IDs; payments rails settle into current accounts; the GL is structured around them. Moving current accounts is not a product migration, it is an estate-wide event. By going last, the strangler-fig motion gives the bank time to (a) prove the engine on three other product families first, (b) build out the coexistence APIs from [02-v1-scope §3](./02-v1-scope-term-deposits.md) to a level where multiple product families on the engine settle cleanly into the legacy DDA, and (c) make the v4 cutover a genuine decision rather than an act of faith.

### v4 stance: firm long-term goal, optional in practice

v4 is committed as a **firm long-term goal**: the architecture is built so the engine can run current accounts and cards, the irregular mode is part of the engine's design point (not a retrofit), and the operational tooling for high-volume ingest is built out by v3 at the latest. The destination is "the engine runs every retail product family the bank holds." The six v1 commitments that keep v4 architecturally viable — and the synthetic v4-scale load test that proves them at v1 acceptance — are specified in [feature-design-two-modes-asymmetry](./feature-design-two-modes-asymmetry.md).

v4 is also **explicitly optional in practice**. The bank can stop at v1–v3 on the new engine, keep current accounts and cards on legacy DDA indefinitely, and still extract the full agility wedge for the product families that have moved. This is a valid endpoint — what is sometimes called a "non-core core": the engine handles configurable products, the legacy core handles current accounts and the GL, and the integration architecture from [integration_concepts/](../integration_concepts/00-introduction-and-decisions.md) keeps them coherent.

The two framings are not in tension. The architecture supports v4; the decision to take v4 into production is separate from the decision to build the capability.

---

## v5+ — Iberia: ES (Term Deposits + Personal Credit)

The **first proof that regulatory-as-a-pack works**. Up to v4 the engine has been run on a single pack (PT). v5 is where a second pack (ES) is filled in, exercised on the lowest-risk product families to validate the abstraction.

Why those two families: term deposits and personal credit are the products with the smallest cross-border regulatory delta inside the EU. Both are covered by harmonising EU directives (term deposits via the deposit-guarantee scheme directive 2014/49/EU; personal credit via CCD 2008/48/EC) with relatively thin per-country deltas. Spain transposes CCD 2008/48/EC as *Ley 16/2011 de Contratos de Crédito al Consumo*; the IRS-equivalent withholding regime (*retención a cuenta del IRPF*) is administratively different from PT but mathematically the same shape (a flat withholding on interest, applied flow-by-flow).

What v5 must prove:

- **Pack swap is a configuration change.** A deployment pointing at the ES pack uses the same images, the same engine binary, the same event schemas, the same subledger structure. Only the pack differs. If anything in the engine has to change to support ES, the pack abstraction failed and the wedge is at risk.
- **Reporting hooks remap cleanly.** Banco de Portugal reporting (v1–v4) becomes Banco de España and AEAT reporting (v5+). The engine emits abstracted signals; the geography-specific reporting application interprets them.
- **Disclosure documents are pack outputs.** The FIN (PT depósito disclosure) and SECCI (PT consumer credit disclosure) have ES counterparts. The pack ships the disclosure templates and the data the templates need; the engine doesn't know about specific documents.

v5 is unglamorous if the architecture is right: a re-deployment with a different pack and a supervised operating period. If it is *not* unglamorous, the pack abstraction needs to go back to the drawing board before v6+ is contemplated.

---

## v6+ — EU Expansion

EU expansion is **not** "one phase." It is a per-country sequence with a common floor. The common floor is the EU baseline:

- **CCD 2008/48/EC** — the Consumer Credit Directive. Common rules for unsecured personal credit across the EU: TAEG calculation method, mandatory pre-contractual information (SECCI), the 14-day right of withdrawal, the cost-of-credit definition.
- **MCD 2014/17/EU** — the Mortgage Credit Directive. Common rules for mortgages: the FINE, the 7-day reflection period, creditworthiness assessment, foreign-currency-loan rules.
- **DGSD 2014/49/EU** — the Deposit Guarantee Schemes Directive. Common rules for the €100,000 deposit-guarantee coverage and its reporting.
- **PSD2** (Directive 2015/2366) and adjacent — cross-cutting for payments and account access; relevant for the integration seam more than the product engine itself.

Each country then ships **deltas** on top of the baseline: the transposition law, the tax treatment, the reporting agency, the disclosure templates in the local language, the day-count or rate conventions that local market practice has standardised. The pack for each country is therefore a small file by design: the EU baseline pack does most of the work, the country pack overrides only what is genuinely different.

The roadmap inside v6+ is **demand-driven**, not architecturally driven. The architecture is ready after v5; which country comes next depends on which subsidiary or operating geography the bank takes on next. Until a specific geography is committed, the order is left open.

---

## Pack Maintenance — A Continuous Track

The phase table above suggests a discrete march of pack introductions: PT in v1, ES in v5, EU baseline in v6. The reality is that **a regulatory pack is not finished when it ships**. PT regulation changes continuously: a Banco de Portugal *Aviso* updates a reporting threshold; a new *Decreto-Lei* transposes a revised EU directive; an IRS Budget Law changes the withholding rate. ES and EU packs evolve at the same cadence.

Pack maintenance is therefore a **continuous track in parallel with the phase roadmap**, not an event inside any single phase. The maintenance shape:

- **Watch.** A small per-jurisdiction surveillance function tracks regulatory publications (BdP *Avisos* and *Instruções*; *Diário da República* for PT primary legislation; *Boletín Oficial del Estado* and Banco de España for ES; *Official Journal of the EU* for directives). One named owner per pack.
- **Diff and decide.** Each change is classified: configuration-only (a parameter changes), pack-data (a new disclosure template, a new reporting field), or engine (rare; a fundamentally new regulatory primitive that doesn't fit the current pack surface). Configuration-only and pack-data changes ship as pack updates; engine changes feed the roadmap.
- **Release cadence.** Packs ship on a known cadence (e.g. monthly minor releases, quarterly major releases) plus emergency releases for regulatory deadlines.
- **Backward compatibility.** A pack update **cannot** retroactively change accruals or balances on existing accounts (that would be unauditable). Pack changes apply prospectively from a `pack_effective_date`; the engine carries the effective pack version per account so historical reconstructions remain consistent.

Pack maintenance is a product, not a side-effect. It is the **largest recurring operational cost** beyond engine development itself, and it has to be staffed accordingly from day one.

---

## The Underlying Logic

Two axes drive the order:

**1. Ramp complexity along the engine's range.** Term deposits (simple cash flows, with-a-plan) → personal credit (Price/SAC, with-a-plan plus TAEG) → mortgage (variable rate, composite cases, with-a-plan at maximum complexity) → current accounts and cards (irregular mode). The engine acquires capability in the order in which that capability is needed for the next product family. No capability is built speculatively.

**2. Validate the pack swap on familiar product families before expanding the family set in new geographies.** PT covers all four families first (v1–v4); the first geographic swap (v5) repeats only the first two families in the new pack. New geographies do not have to expand the family set at the same time they validate the pack. Once a geography has v5+ on term deposits and credit, expanding it to mortgage and current accounts is a separate decision, not a forced step.

The combination is what protects the wedge. If new product families had to be built per geography, the wedge dies under the combinatorial weight. If the pack swap had to be validated on every product family at once, the bar for a new geography is too high. Sequencing both axes separately keeps each step contained.

---

## What Is **Not** on the Roadmap

To match the discipline of the [vision](./00-product-vision.md), some things are deliberately omitted:

- **GL, IFRS 9, channels, payments rails, fraud, KYC, onboarding.** Still out of scope, at every phase. These are not "later v's"; they are someone else's product. The engine's job is to emit clean signals to whichever systems do those.
- **Wholesale, corporate, treasury, investment-banking products.** This is a retail product engine; the wedge depends on it staying that way. Corporate banking has fundamentally different products and a fundamentally different operating model; absorbing it would dilute the architecture.
- **Non-EU geographies.** Switzerland, UK, US — each has a substantially different regulatory shape. The pack abstraction may eventually extend that far, but it is not a roadmap commitment.
- **A multi-currency core.** v1–v4 are EUR-only by configuration. The events carry `currency` because the schema convention requires it, but the engine's TAEG, withholding, and reporting paths are not exercised with mixed currencies. Multi-currency is a pack/configuration extension that lands when the operating bank needs it, not on the v-numbered roadmap.
