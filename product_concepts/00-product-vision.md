# Product Vision

> A configurable core banking product engine. One engine across every product family; one swappable regulatory pack across every geography.

---

## 1. The Problem

The operating organization is an **incumbent Portuguese retail bank** modernising its core. The problem is structural. The legacy estate is **product-per-module**: term deposits live in one subsystem, personal credit in a second, mortgage in a third, current accounts in a fourth. Each module has its own data model, its own batch cycles, its own configuration grammar, and its own integration shape. Launching a new product means changing one or more modules, with each change passing through the regulatory-conformance and operational-acceptance gates of every team involved. The path to a new product is long because the architecture made it so.

The product engine described in this series replaces that pattern with a single configurable engine that runs every retail product family. New products are configuration changes, not new modules.

---

## 1.5. Why Build Rather Than Buy

A steering committee can ask this question at any point in the engine's lifetime, and the brief must contain an answer that holds up at year 1 and at year 10 alike. Vendor products — Thought Machine, Mambu, Temenos, Tuum, Vault, Finxact — all promise variations of "configurable core banking." The operating bank evaluated them and chose to build. This section names the reasoning so future readers find it where they look for it, not buried in implementation choices.

**The load-bearing claim: legacy-estate integration ownership.** The bank's most valuable asset in this estate is the integration shape between core, channels, payments, CRM, GL, IFRS 9, and regulatory reporting — developed over decades and shaped by this bank's specific operating model. A vendor engine arriving cold has to rebuild that integration to fit its own contracts; the existing integration is discarded or rewritten. An engine built fitted to the existing integration preserves the asset. The engine is not the headline; integration is. The engine is the new product layer that lets the bank use the existing integration asset with a better product model.

**Honest concession: no single component is unique IP.** Cash-flow-primitive engines exist (Thought Machine Vault). Configurable product surfaces exist (Mambu, Tuum). Pack abstractions exist (most vendors localise per geography). The IP being built is not novelty at the component level — it is the *integrated fit*: a configuration model matching this bank's PM-author-engineer-review workflow ([feature-design-configuration-authoring](./feature-design-configuration-authoring.md)); a regulatory pack maintained by this bank's compliance team rather than received from a vendor's PT localisation team ([feature-design-configuration-surface](./feature-design-configuration-surface.md)); an integration shape designed against this bank's specific legacy estate. No part on its own justifies the build; the whole does.

**Honest concession: build risk includes "we never finish."** Build-it-yourself core banking has a history of failing — projects that run 5+ years without a production-ready engine, projects that ship something inferior to what the bank would have bought. The risk is real. The mitigations are structural:

- *Strangler-fig adoption.* The legacy still runs while the engine is being built. v1 ships into a slice (PT term deposits); failure does not destroy operations.
- *Scope discipline.* The out-of-scope list ([§4](#4-whats-out-of-scope)) is enforced. The engine is a product engine, not a re-implementation of the bank's estate.
- *Falsifiable agility wedge.* Zero engine code per new variant (architectural invariant); PM commit to production ≤ 5 working days (workflow target). Both are testable; failure is diagnosable. See [feature-design-configuration-authoring §7](./feature-design-configuration-authoring.md).
- *Mathematically validated thesis.* [financial_concepts §9.2](../financial_concepts/banking_products_financial_mathematics.md) proves the cash-flow-primitive unification independent of any implementation. The build is not betting on an unproven idea.

**Year-5 TCO inflection.** Build TCO is higher than vendor licensing for the first 3-5 years and lower beyond year 5, given a stable engineering team and a stable pack-maintenance function — both of which the bank is explicitly committing to as part of the build decision, not assuming. The claim is aggressive: it implies an aggregate delivery pace across v1–v3 plus foundational v4 engine readiness that must collectively stabilise within the inflection horizon for the financial thesis to hold. The [roadmap](./03-roadmap.md) does not state per-phase calendar windows — the brief strips project-effort framings — but the cumulative pace implied by the TCO claim is part of what the engineering team commits to.

**Failure contingency.** Deliberately not part of this thesis. If the build runs into trouble the mitigations above cannot absorb, the contingency lives in a separate risk document owned by senior engineering and the strategy function jointly. Naming it in the vision weakens the thesis without changing the underlying probability; deferring it keeps the brief committal where commitment is what serves the work.

---

## 2. The Wedge

The wedge is a single architectural choice: **cash flows are the primitive**. Products are configurations of (cash-flow shape, day-count, compounding, charges, regulatory pack). Everything else is a consequence.

[financial_concepts §9.2](../financial_concepts/banking_products_financial_mathematics.md) proves the unification mathematically. Every retail banking product — term deposit, Price loan, SAC loan, mortgage with variable rate and grace period, current account, credit card — obeys the same equation:

```
S(t + Δt) = S(t) × (1 + r × Δt) − payments(Δt) + drawdowns(Δt)
```

What varies between products is bounded and small: cash-flow shape (fixed/variable/irregular), day-count convention (Act/360, Act/365, 30/360), and compounding frequency. Three dimensions. Everything else — Price vs SAC vs American, deposit vs credit, *carência* vs balloon, *juros antecipados* vs juros at maturity — is a choice about what is fixed inside those three.

Two consequences flow from this choice:

- **Agility.** A new product is a new row in a configuration table, not a new module.
- **Unification.** One engine, one operational model, one set of audit and reporting hooks, one subledger across every retail product family.

Both consequences fall out of the same architectural insight. Take the insight away and neither holds.

---

## 3. What's In Scope

Three things, and exactly three:

- **The product engine** — the runtime that takes a product configuration plus a sequence of events and produces the cash flows, accrual schedule, balance evolution, and lifecycle transitions for an instance of that product.
- **The product subledger** — a per-account record of positions, accruals, charges, and lifecycle events. The subledger is the engine's source of truth for "what is the state of this account at this point in time?" It is *not* a general ledger; it is a product-side journal that feeds GLs and IFRS 9 systems elsewhere.
- **The regulatory pack** — a swappable, geography-specific bundle of rules: rate conventions, tax treatments, mandatory disclosures, reporting hooks, day-count defaults. v1 ships with the PT pack. ES and EU packs are roadmap items.

The product engine, the subledger, and the regulatory pack together form one cohesive deliverable. Operating any two of the three without the third is operating a half-product.

---

## 4. What's Out of Scope

The discipline of the brief lives in this list. Each item is genuinely deferred — owned by other systems in the bank's estate, not by this engine. **"Out of scope" means "the engine does not build it, but the engine owns the integration to it."** Channels, GL, IFRS 9, payments rails, fraud / AML, KYC are out-of-scope *products*; the integration shapes to them are in-scope and are the load-bearing asset of the build (see [§1.5](#15-why-build-rather-than-buy)). Stating them explicitly prevents scope creep from eroding the wedge.

- **General ledger / double-entry accounting.** The engine emits signals; a GL system consumes them. We do not write a GL.
- **IFRS 9 staging and ECL.** We emit the events an IFRS 9 system needs (days past due, restructuring, write-off triggers). The IFRS 9 logic itself runs elsewhere. The signal-boundary contract is in scope; the staging engine is not.
- **Channels.** Mobile apps, web banking, branch teller, call centre. The engine exposes APIs and events; channels are someone else's product.
- **Payments rails.** SEPA, TARGET2, instant payments, card schemes. The engine settles to a current account; how that current account moves money is a payments problem.
- **Fraud and AML.** These are first-class systems in their own right, with their own vendors and their own regulators. The engine integrates with them; it does not absorb them.
- **KYC and onboarding.** A customer exists before they hold a product. KYC is upstream.

If the answer to "should we build X?" is "X is in the explicit out-of-scope list," the answer is no. Re-opening one of these items costs the wedge.

---

## 5. Strategic Frame

**Geography: PT → Iberia → EU.** Portugal first — the operating bank's home regulator and the regulatory expertise already in-house. Spain second — a familiar legal family (civil law, EU directive transposition). Then EU expansion country by country. The non-negotiable: the regulatory pack is swappable from day one. If regulation is buried in the engine, the geographic roadmap costs a rewrite. It will not be.

**Adoption: strangler fig.** No core is replaced at once. The adoption motion is product-line at a time: one product family (term deposits, or a new credit line) runs on the new engine, the rest stay on the legacy core, and further families migrate when the first one is operationally proven. The engine and the legacy core coexist; the integration architecture documented in integration_concepts/ is what makes coexistence possible without double-counting or split-brain ledgers.

**Build: self-hosted, single codebase.** The engine is deployed into the operating bank's own infrastructure (typically a private cloud or on-prem Kubernetes). One codebase, one set of images, one configuration grammar.

**v1 slice: Portuguese *depósito a prazo*.** Smallest surface that exercises both the engine and the PT regulatory pack end-to-end. Simple math ([financial_concepts §5](../financial_concepts/banking_products_financial_mathematics.md)), narrow regulatory surface (Act/360, TANB/TANL split, 28% IRS withholding, BdP reporting hooks), aligned with the running example in integration_concepts/ so the integration backbone is already proven on the same product. Validating v1 validates the architecture; subsequent products are configuration on top of a known-working engine.
