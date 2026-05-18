# Open Questions

> A living register of deferred decisions. Each entry has enough context that a future session knows what would unblock it. The point of this document is honesty: the brief in 00–03 is consciously committal where it can be, and consciously open where it cannot. Open decisions deferred deliberately are not the same as decisions overlooked.
>
> Future sessions add to this register, refine entries, and — when one is resolved — fold the resolution into the relevant numbered document and remove (or annotate) the entry here.

---

## 1. Competitive Positioning

**Context.** The product engine described in this series enters a crowded market. The credible incumbents and challengers are: Temenos T24 (the historical enterprise core, now Transact), Mambu (cloud-native modular core, strong in mid-market and challenger banks), Thought Machine Vault (smart-contracts-based core, used by Lloyds and several tier-1s), 10x Banking (cloud-native, JP Morgan UK), Tuum (European cloud-native modular), and Finxact (now Fiserv, US-focused but EU-ambitious). Each has a different sales motion, a different deployment model, and a different relationship to product configurability.

The PT-incumbent wedge needs to be different from each. A plausible answer is "modernise one product family at a time, on a vendor-trustworthy code base, with a swappable EU regulatory pack from day one" — but that has to be tested against each competitor's actual offering, not against the brand impression. Temenos and Thought Machine, in particular, claim configurability; the question is whether their configurability survives contact with PT regulation and PT incumbents' specific legacy estates.

**Unblocked by.** Competitive landscape research: read the public documentation and analyst coverage for each named vendor; ideally talk to a PT incumbent that has evaluated two or more of them; produce a one-page positioning matrix. Output: a short addendum to [00-product-vision.md](./00-product-vision.md) (or a separate competitive memo) and a sharpened wedge statement.

---

## 2. Pricing Model

**Context.** Four credible models, each shaping the sales motion and the unit economics differently:

- **Per-account.** Bank pays per active account on the engine. Aligns vendor revenue with customer scale. Predictable for the bank, but the bank has the perverse incentive to slow down product growth on the engine.
- **Per-product-line.** Bank pays a flat fee per product family deployed. Aligns with the strangler-fig motion (each new product family is a procurement event). Risks under-pricing very high-volume product lines.
- **Platform fee.** Bank pays a fixed annual platform fee for unlimited usage. Predictable, but disconnects vendor revenue from value delivered and makes early sales harder.
- **Hybrid.** Platform fee + per-account or per-product-line uplift. The most common in enterprise software; the most complex to negotiate.

The choice has second-order effects on packaging (what counts as a "product line" for billing purposes?) and on the SaaS-vs-self-hosted split (per-account metering is harder when the bank operates the engine on its own infrastructure).

**Unblocked by.** Customer-development interviews with three to five PT-incumbent procurement or product-strategy leads. Output: a decision recorded in this document, with the chosen model and the reasoning; subsequently propagated to the GTM materials (which are not part of this repository).

---

## 3. Licensing Posture

**Context.** Three credible postures:

- **Closed-source vendor.** Conventional enterprise-software model. Strongest commercial leverage; weakest evaluation experience (incumbent banks cannot poke at the code before signing). Hardest to win against open-source competitors in EU banks that are increasingly required to demonstrate vendor lock-in mitigation.
- **Commercial open-source.** Permissive licence for the engine; commercial licence (or hosted SaaS) for the regulatory packs, support, and operational tooling. Strong evaluation experience; ecosystem leverage; pricing model has to be designed so the commercial layer is genuinely worth paying for.
- **Open-source-with-managed-service.** All software open-source; revenue from hosted SaaS and managed-service contracts. Strongest go-to-market for technically sophisticated buyers; weakest unit economics; harder fit with the SaaS + self-hosted single-codebase model in [00-product-vision §5](./00-product-vision.md).

The PT-incumbent buyer profile (regulated, risk-averse, requires vendor accountability for production incidents) favours commercial open-source or closed-source vendor; pure open-source-with-managed-service is harder to sell into a bank that wants a single throat to choke.

**Unblocked by.** Founder / team strategic decision. This is not a research question — the answer depends on the team's commercial appetite and capital position more than on customer preference. Output: a one-line statement in [00-product-vision.md](./00-product-vision.md) (or a separate licensing memo) and a corresponding adjustment to the deployment-modes story in [01-product-architecture §5](./01-product-architecture.md).

---

## 4. Legacy Coexistence Targets

**Context.** The strangler-fig motion in [01-product-architecture §5](./01-product-architecture.md) and [02-v1-scope §3](./02-v1-scope-term-deposits.md) requires first-class coexistence with the bank's legacy core. The legacy cores in the PT market are not uniform:

- **BANKA** (the local incumbent core, used by several PT banks for decades).
- **Mainframe / AS400-era systems** (some incumbents still operate them; integration via fixed-format files or middleware).
- **Internal stacks** (some larger PT banks have substantially home-built cores; integration shape is per-bank).
- **Other vendor cores** (Temenos T24, Oracle Flexcube — present in some PT banks).

The engine's coexistence story is described abstractly in terms of the ACL in [integration_concepts/02](../integration_concepts/02-anti-corruption-layer.md). The open question is which of the above gets a first-class, productised adapter (the engine ships with a connector that works out of the box) vs which is handled bespoke per customer (the engine integrates through a customer-built adapter on top of the ACL contract).

The first-class list shapes the sales motion: a bank running BANKA can be onboarded faster if a BANKA connector ships in the box. A bank running an internal stack can never be onboarded faster than its own IT team allows.

**Unblocked by.** Market research on the PT core banking landscape: which cores hold what share of the PT incumbent market; for each, what the integration shape looks like; which one or two cores cover the largest addressable share of the target customer set. Output: a list (two or three names, ranked) added to this document and reflected in the engineering roadmap.

---

## 5. SaaS Multi-Tenancy Isolation Level

**Context.** [00-product-vision §5](./00-product-vision.md) and [01-product-architecture §5](./01-product-architecture.md) both commit to a single codebase that ships in two deployment modes (SaaS multi-tenant and self-hosted). In the SaaS multi-tenant mode, three credible isolation levels are available:

- **Shared database with row-level tenancy.** All tenants in one logical database; tenant ID on every row; access enforced by application code (and optionally by database-level RLS). Highest density; cheapest to operate; weakest isolation; hardest sell to regulators and to security-conscious banks.
- **Database per tenant.** Each tenant has its own database (potentially on shared infrastructure). Strong logical isolation; moderate operational complexity; easier compliance story; higher cost per tenant.
- **Cluster per tenant.** Each tenant has its own full deployment (cluster of services + database + broker namespace). Strongest isolation; effectively a managed dedicated instance; highest cost; easiest regulatory story.

The PT banking regulator and DORA both impose operational-resilience requirements that influence this choice. A shared-database posture is hard to defend in a DORA testing scenario where "tenant A's incident must not affect tenant B." A cluster-per-tenant posture defends easily but undermines the multi-tenant SaaS economics.

A pragmatic intermediate is **database-per-tenant on shared compute**, with cluster-per-tenant available as a premium tier for tier-1 customers.

**Unblocked by.** A security/compliance review of typical PT bank requirements: read DORA's operational resilience requirements and Banco de Portugal's outsourcing guidelines, interview two or three CISO-level contacts in target PT banks, identify the minimum acceptable isolation level. Output: a decision recorded in [01-product-architecture §5](./01-product-architecture.md) under "Deployment modes" and reflected in the architectural diagrams (which are not yet part of this series).

---

## 6. IFRS 9 Signal Boundary

**Context.** IFRS 9 implementation is explicitly out of scope ([00-product-vision §4](./00-product-vision.md)). However, the engine *does* feed an external IFRS 9 system, and the signal contract between them is in scope to define. Three signal families are involved:

- **Staging triggers.** Events that move an exposure between IFRS 9 stages (Stage 1 → Stage 2 on significant increase in credit risk; Stage 2 → Stage 3 on default). The engine has the operational data (days past due, restructuring events, watchlist flags); IFRS 9 staging logic consumes them.
- **Days-past-due tracking.** A continuous signal per exposure. The engine maintains it; the IFRS 9 system reads it to drive staging.
- **Restructuring events.** When a contract is modified (rate change, term extension, payment holiday) under financial-difficulty conditions, IFRS 9 has specific treatment. The engine emits a `LoanRestructured` event with the contextual data; the IFRS 9 system interprets the regulatory meaning.

The open question is the **specific schema** of the signal contract. Is it one big event per change (`Stage1To2`, `Stage2To3`)? Or two signals (a continuous days-past-due tracker plus discrete restructuring/forbearance events) from which the IFRS 9 system derives the staging? The latter is more compositional and reusable across IFRS 9 vendors; the former is simpler if the bank uses a single IFRS 9 system that already has a known contract.

The decision interacts with the event catalogue in [integration_concepts/08](../integration_concepts/08-event-catalog-governance.md) — once the signals are named, they are public API and hard to change.

**Unblocked by.** An IFRS 9 SME conversation: ideally a risk-quant or model-validation lead at a PT bank, or a consultant who has integrated several IFRS 9 vendors. Output: a signal-contract section in [02-v1-scope](./02-v1-scope-term-deposits.md) (or in the v2 / v3 scope documents where credit lands) and corresponding events registered in the catalogue.

---

## 7. Time-Travel / Point-in-Time Correctness

**Context.** Regulated banking products require the ability to reconstruct the state of an account at any past point in time — for audit, for dispute resolution, for regulator inquiries, for IFRS 9 backtesting. Two credible implementation approaches:

- **Event sourcing.** The subledger is rebuildable from the event stream alone; point-in-time queries are answered by replaying events up to the chosen timestamp. Strong audit story by construction; performance characteristics need careful design (snapshots, projections); operational complexity higher.
- **Snapshot and journal.** The subledger stores current state plus a journal of all state changes, each timestamped and immutable. Point-in-time queries are answered by walking the journal backwards from current state. Simpler operational model; weaker reconstructibility guarantee (the journal has to be complete).

Both can satisfy regulatory point-in-time requirements; they differ in operational shape, in cost, and in the failure modes they expose.

The choice has architectural consequences. Event sourcing aligns naturally with the event-emission patterns from [integration_concepts/04](../integration_concepts/04-plumbing-patterns.md) (the outbox) — the events that go onto the bus are the same events that rebuild the state. Snapshot-and-journal is simpler but creates a duality between the subledger's journal and the integration event stream that has to be maintained.

**Unblocked by.** A target-customer audit-requirements discussion: ideally a head of internal audit at a PT bank, or a Banco de Portugal supervisory contact. Output: a decision recorded in [01-product-architecture §1](./01-product-architecture.md) (or a separate subledger-design memo) and corresponding subledger-shape choices in the engineering roadmap.

---

## 8. Configurability Depth

**Context.** The agility wedge ([00-product-vision §2](./00-product-vision.md), [01-product-architecture §2](./01-product-architecture.md)) depends on new products being configuration changes. The open question is the **depth** of the configuration surface — three credible models:

- **Template catalog only.** The engine ships with a bounded catalogue of product templates (term deposit with X variants, Price credit, SAC credit, mortgage, current account, card). New products are template instantiations with parameter overrides. Simplest; safest; tightest scope. Risk: the catalogue is always either too narrow (a product the customer wants is not in it) or too wide (the catalogue is the same complexity the engine was meant to replace).
- **DSL only.** The engine ships with a configuration DSL (cash-flow shape, day-count, compounding, charges, lifecycle hooks) and no templates; every product is composed from primitives. Most flexible; highest learning curve; biggest support surface; risk: customers will use the DSL to build products that violate regulatory or commercial constraints the engine is meant to enforce.
- **Both.** Templates for 80% of common products; DSL for the long tail. Probably correct; specific shape needs work. Risks: dual maintenance burden; the boundary between "template" and "DSL extension" is a per-product judgement that may drift.

This is the heart of the wedge, and getting it wrong in either direction kills it. Template-only is too rigid; DSL-only is too unbounded. The "both" answer is correct in shape but undefined in detail.

**Unblocked by.** Prototyping the configuration surface against the v1–v3 product set. The prototype answers: what does the term-deposit configuration look like as a template; what does a "non-standard" deposit (one whose configuration the template cannot express) look like in the DSL; where is the template/DSL boundary. Output: an addendum to [01-product-architecture §2](./01-product-architecture.md) with worked examples of both shapes and a stated boundary policy.

---

## 9. Primary Economic Buyer

**Context.** [00-product-vision](./00-product-vision.md) leaves the primary economic buyer deliberately unspecified. Three plausible buyers, with materially different sales motions:

- **CIO / Head of IT** — modernisation pitch. Wedge framed as legacy reduction, vendor consolidation, cloud-readiness, and DORA operational resilience. 9–12 month enterprise sale; long procurement; high RFI/RFP overhead; vendor due diligence is rigorous. Largest deal size; longest cycle.
- **Head of Retail / Head of Products** — agility pitch. Wedge framed as time-to-market for new products. Often pulls IT into the deal rather than waiting for IT to initiate. Shorter cycle than CIO; smaller initial scope (one product line); higher chance of expansion.
- **CEO / Board** — strategic-transformation pitch. Wedge framed as competitive response to neobanks and digital challengers. Top-down mandate; the longest cycle but the largest commitment when it closes. Rare in PT incumbents; more common as a follow-on after an initial CIO or Head-of-Retail engagement proves the concept.

The architecture is buyer-agnostic; the messaging, the sales materials, the early reference customers, and the founder's calendar all differ by buyer. Picking one buyer for the first 2-3 customers is a sequencing decision, not a permanent commitment — but it has to be made before the first sales conversation, not improvised.

**Unblocked by.** Customer-development conversations with 5–10 candidates across the three buyer profiles. Output: a named primary buyer for the first wave, recorded in this document and reflected in the [vision](./00-product-vision.md) and the GTM materials (which are not part of this repository).

---

## 10. Founding Team Credibility Story

**Context.** Banking customers buy people more than software, especially on a first deal where there is no production reference. The team's credibility story has three plausible shapes:

- **Banking insider.** Founder(s) with senior roles at a PT incumbent — credibility through direct domain knowledge, regulatory familiarity, and an existing network of decision-makers. Strongest entry into PT incumbents; risk of underestimating the technology shift required by the architecture.
- **Fintech veteran.** Founder(s) with senior roles at a previous fintech or core-banking vendor (Temenos, Mambu, Thought Machine, or a neobank's product team). Credibility through "we have shipped a thing like this before, at scale." Easier on technology; harder on PT-specific incumbent access.
- **External technologist.** Founder(s) from a non-banking software background bringing the architectural thesis (cash-flow primitive, event-driven, regulatory-pack abstraction). Strongest technology story; weakest entry into incumbents without a banking-insider partner or advisor.

Most real teams are a *combination*. The honest question is which combination, and what the team is doing to close the gap on whichever credibility axis is weakest. For example, an external-technologist founding team that lacks the banking-insider axis typically closes the gap with a senior PT banking advisor and a founding board member from the industry.

**Unblocked by.** Founder/team decision and disclosure. This is not a research question; it is a self-assessment. Output: a one-paragraph team statement in [00-product-vision](./00-product-vision.md) or a separate `team.md`, and (where the credibility axis needs closing) named advisors or board members with the relevant background.

---

## 11. Split-Brain Reconciliation with Legacy DDA

**Context.** [02-v1-scope §3](./02-v1-scope-term-deposits.md) describes the happy-path coexistence with the legacy core's current-account module: the engine settles principal and interest into the current account through the [ACL](../integration_concepts/02-anti-corruption-layer.md), the legacy core books the credit, and end-of-day reconciliation compares the engine's outbox against the legacy core's incoming journal. The unhappy path is the open question: **what happens when the engine and the legacy core disagree about an account's state?**

Concrete scenarios:

- The engine settles a deposit-maturity credit to the customer's current account; the legacy core books it; meanwhile the legacy core has booked a separate same-day transaction (a card payment, a salary credit) that the engine doesn't know about. The customer-facing balance is the legacy core's responsibility; the engine's view of "the credit landed" is correct from its perspective. The two views are consistent but only the legacy core has the complete picture.
- The engine sends the settlement instruction through the ACL; the ACL gets an ambiguous response from the legacy core (the [indeterminate state](../integration_concepts/02-anti-corruption-layer.md) problem). The engine has to decide whether to retry, escalate, or compensate. A wrong choice leads to either a double-credit or a missing credit.
- The legacy core's reconciliation file at end of day shows a credit the engine did not emit. The engine has to flag it as an alert; if the count of alerts crosses a threshold, something is fundamentally wrong with the ACL or the deployment.

The architectural answer is "the legacy core is the system of record for current accounts; the engine's view is reconciled against it; intra-day inconsistency is bounded and visible." The operational answer — who reconciles when, what tools the bank's ops team uses, what alerts fire at what thresholds — is genuinely open.

This question must be resolved before v1 ships to a real bank. A demo can hand-wave; a production deployment cannot.

**Unblocked by.** An operations / reconciliation review with the first design-partner bank: walk through the daily reconciliation process the bank uses today, identify where the engine's settlements fit, define the alerting and escalation paths. Output: an operational runbook (not necessarily in this repo) plus an addendum to [02-v1-scope §3](./02-v1-scope-term-deposits.md) describing the contract the engine commits to.

---

## Adding to This Register

Future sessions are expected to add to this list. The shape of a useful entry is:

- A **named** question (one line summary).
- **Context** — enough that a reader cold-reading the document understands the trade-off space.
- **Unblocked by** — the specific input that would let someone make the decision.

Entries should be removed (or marked **Resolved**, with the resolution noted) when the question is answered and the answer has been folded into the relevant numbered document.
