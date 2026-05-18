# Product Vision

> A configurable core banking product engine for incumbent Portuguese banks that need to ship new products in days instead of quarters. One engine across every product family; one swappable regulatory pack across every geography.

---

## 1. Customer & Problem

The customer is an **incumbent Portuguese retail bank** modernising under pressure. The pressure is not theoretical: neobanks ship a new savings product in a sprint; the incumbent's legacy core takes a quarter and a steering committee.

The problem is structural, not effort-based. The legacy estate is **product-per-module**: term deposits live in one subsystem, personal credit in a second, mortgage in a third, current accounts in a fourth. Each module has its own data model, its own batch cycles, its own configuration grammar, and its own integration shape. Launching a new product means changing one or more modules, with each change passing through the regulatory-conformance and operational-acceptance gates of every team involved. The shortest path to a new product is months long because the architecture made it so.

The product engine described in this series replaces that pattern with a single configurable engine that runs every retail product family. New products are configuration changes, not new modules.

---

## 2. The Wedge

The wedge is a single architectural choice: **cash flows are the primitive**. Products are configurations of (cash-flow shape, day-count, compounding, charges, regulatory pack). Everything else is a consequence.

[financial_concepts §9.2](../financial_concepts/banking_products_financial_mathematics.md) proves the unification mathematically. Every retail banking product — term deposit, Price loan, SAC loan, mortgage with variable rate and grace period, current account, credit card — obeys the same equation:

```
S(t + Δt) = S(t) × (1 + r × Δt) − payments(Δt) + drawdowns(Δt)
```

What varies between products is bounded and small: cash-flow shape (fixed/variable/irregular), day-count convention (Act/360, Act/365, 30/360), and compounding frequency. Three dimensions. Everything else — Price vs SAC vs American, deposit vs credit, *carência* vs balloon, *juros antecipados* vs juros at maturity — is a choice about what is fixed inside those three.

Two consequences flow from this choice, and both are buyer-visible:

- **Agility.** A new product is a new row in a configuration table, not a new module. New products in days, not quarters. This is what a product team buys.
- **Unification.** One engine, one operational model, one set of audit and reporting hooks, one subledger across every retail product family. This is what a CIO and a Head of Operations buy.

Both consequences fall out of the same architectural insight. Take the insight away and neither holds.

**The wedge as a falsifiable claim.** "Days, not quarters" needs a number to be testable. The working target:

> *A new variant of an existing product family — for example, a new term deposit with a different compounding rule or a new fixed-rate personal credit with adjusted charges — goes from configuration commit to first booked instance in production in under 5 working days, end-to-end.*

This is the operational claim that has to survive contact with v1 in production. Adding a new *product family* (a new family in the [01-product-architecture §3](./01-product-architecture.md) sense, e.g. moving from credits to current accounts) takes longer because new pack work or new mode work is involved; that's a separate target for the corresponding roadmap phase. The 5-day claim is for variants within a family on an existing pack — the exact case the legacy product-per-module pattern handles worst.

**Who buys this.** The primary economic buyer is **deliberately unspecified at this stage**: CIO modernisation, head-of-retail agility, and CEO strategic-response all map plausibly. The sales motion and the messaging differ by buyer, but the architecture does not. The decision is tracked in [04-open-questions](./04-open-questions.md) and will be sharpened by customer-development conversations.

---

## 3. What's In Scope

Three things, and exactly three:

- **The product engine** — the runtime that takes a product configuration plus a sequence of events and produces the cash flows, accrual schedule, balance evolution, and lifecycle transitions for an instance of that product.
- **The product subledger** — a per-account record of positions, accruals, charges, and lifecycle events. The subledger is the engine's source of truth for "what is the state of this account at this point in time?" It is *not* a general ledger; it is a product-side journal that feeds GLs and IFRS 9 systems elsewhere.
- **The regulatory pack** — a swappable, geography-specific bundle of rules: rate conventions, tax treatments, mandatory disclosures, reporting hooks, day-count defaults. v1 ships with the PT pack. ES and EU packs are roadmap items.

The product engine, the subledger, and the regulatory pack together form one cohesive deliverable. Buying any two of the three is buying a half-product.

---

## 4. What's Out of Scope

The discipline of the brief lives in this list. Each item is genuinely deferred — owned by other systems in the bank's estate, not by this engine. Stating them explicitly prevents scope creep from eroding the wedge.

- **General ledger / double-entry accounting.** The engine emits signals; a GL system consumes them. We do not write a GL.
- **IFRS 9 staging and ECL.** We emit the events an IFRS 9 system needs (days past due, restructuring, write-off triggers). The IFRS 9 logic itself runs elsewhere. The signal-boundary contract is in scope; the staging engine is not.
- **Channels.** Mobile apps, web banking, branch teller, call centre. The engine exposes APIs and events; channels are someone else's product.
- **Payments rails.** SEPA, TARGET2, instant payments, card schemes. The engine settles to a current account; how that current account moves money is a payments problem.
- **Fraud and AML.** These are first-class systems in their own right, with their own vendors and their own regulators. The engine integrates with them; it does not absorb them.
- **KYC and onboarding.** A customer exists before they hold a product. KYC is upstream.

If the answer to "should we build X?" is "X is in the explicit out-of-scope list," the answer is no. Re-opening one of these items costs the wedge.

---

## 5. Strategic Frame

**Geography: PT → Iberia → EU.** Portugal first — the founding team's regulatory expertise, a market small enough to learn in but large enough to monetise. Spain second — a familiar legal family (civil law, EU directive transposition) and large enough to matter. Then EU expansion country by country. The non-negotiable: the regulatory pack is swappable from day one. If regulation is buried in the engine, the geographic roadmap costs a rewrite. It will not be.

**Adoption: strangler fig.** No bank moves a whole core at once. The adoption motion is product-line at a time: the bank picks one product family (term deposits, or a new credit line), runs it on the new engine, leaves the rest on the legacy core, and migrates further families when the first one is operationally proven. The engine and the legacy core coexist; the integration architecture documented in integration_concepts/ is what makes coexistence possible without double-counting or split-brain ledgers.

**Build: vendor-style, single codebase.** The engine is built the way a core banking vendor builds: configurable per customer, deployable as SaaS multi-tenant *or* self-hosted on a customer's infrastructure, from one codebase. Many incumbents will not put deposit balances in someone else's cloud; many will. The product accommodates both without forking.

**v1 slice: Portuguese *depósito a prazo*.** Smallest surface that exercises both the engine and the PT regulatory pack end-to-end. Simple math ([financial_concepts §5](../financial_concepts/banking_products_financial_mathematics.md)), narrow regulatory surface (Act/360, TANB/TANL split, 28% IRS withholding, BdP reporting hooks), aligned with the running example in integration_concepts/ so the integration backbone is already proven on the same product. Validating v1 validates the architecture; subsequent products are configuration on top of a known-working engine.
