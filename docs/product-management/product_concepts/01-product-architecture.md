# Product Architecture

> The architectural thesis. Six sections, each load-bearing. The engine inherits the integration architecture from [integration_concepts/](../integration_concepts/00-introduction-and-decisions.md); it does not redefine it.

---

## 1. The Cash-Flow Primitive

Every retail banking product obeys the same equation. [financial_concepts §9.2](../financial_concepts/banking_products_financial_mathematics.md) states the unification:

```
S(t + Δt) = S(t) × (1 + r × Δt) − payments(Δt) + drawdowns(Δt)
```

`S(t)` is the outstanding balance, `r` is the rate matched to the unit of `Δt`, and `payments` and `drawdowns` are the cash flows crossing the account boundary in the interval.

Term deposits, Price and SAC loans, bullet credit, mortgages with grace periods and quarterly Euribor revision, current accounts, revolving credit cards — pick any product, substitute the right shape for `payments` and `drawdowns`, the right `r` and `Δt`, and the equation produces the right answer. The proof is in [financial_concepts §9.2](../financial_concepts/banking_products_financial_mathematics.md); the consequence is architectural.

A product engine that takes this equation as its primitive is *structurally* one engine. Not "one engine that special-cases each product family," nor "a shared library that each product module reuses." One engine, one runtime, one balance-evolution function — invoked with different parameters for different products. The product-per-module pattern that haunts legacy cores is a modelling accident. Unification dissolves it.

This is the single insight on which everything else in this document depends. The configuration surface, the two-families story, the regulatory pack, and the integration seam are all consequences of taking it seriously.

Concretely: the cash-flow primitive is the mathematical rule that governs event handlers. Every product instance is a stream of events; deterministic, side-effect-free handlers fold events into derived state by applying this equation under the parameters declared in the product configuration. Handlers append to the event log; projections derive state from the log. §2 names the source-of-truth shape that follows.

---

## 2. The Event Store and Projections

The engine's source of truth is the event store, co-located with the outbox (per [ADR-004](../integration_concepts/adrs/ADR-004-outbox-pattern-mechanism.md)). State is derived by deterministic, side-effect-free handlers; projections (positions, accrual schedules, maturity calendars, withholding ledgers) are bitemporal tables built from the event store. The CQRS read model (per [integration_concepts §03](../integration_concepts/03-cqrs-and-read-models.md)), the GL system, the IFRS 9 system, and the regulatory reporting application are consumers of these projections; none is the engine's primary state holder.

Four properties follow:

- **The events are the truth.** State that does not derive from events does not exist. A projection that cannot be rebuilt from the log is broken.
- **Replay is routine.** Counterfactual queries — *what would the accrual be if pack `pt.2027.1` had applied from 2026-01-01?* — are answered by replay with modified inputs, not by separate analysis tooling.
- **Snapshots are performance optimisation, not architecture.** Snapshots accelerate rebuild; they do not replace the log.
- **Schema evolution is forward-only.** Events stay forever readable. Payload migrations are versioned. Breaking changes are new event types, not new versions of old ones.

The four time-dimensional capabilities — as-of queries (state as of date X, as known on date Y), audit trails, counterfactual replay, forward projection — are properties of this event-sourced model, not features bolted on. Replay correctness is testable: stored fixture event sequences applied to handlers must produce the same projections every time. Full treatment: [event-store](./feature-design-event-store-projections.md).

---

## 3. The Configuration Surface

The engine's runtime is fixed; the configuration surface is the variable part. The three irreducible dimensions (per [financial_concepts §2.2](../financial_concepts/banking_products_financial_mathematics.md)):

1. **Cash-flow shape** — fixed (Price installments), variable (Euribor-revised mortgage), irregular (current-account movements).
2. **Day-count convention** — Act/360, Act/365, 30/360, and the geographic conventions layered on top.
3. **Compounding frequency** — daily, monthly, quarterly, annual.

Three dimensions are not enough on their own to specify a sellable product. A product also carries:

- **Charges.** Opening fees, maintenance fees, early-repayment penalties, currency-conversion margins. Some are mandatory for the TAEG calculation (per [financial_concepts §6.2](../financial_concepts/banking_products_financial_mathematics.md)); some are optional. The engine treats charges as first-class cash flows, not metadata.
- **Insurance.** Mandatory life insurance on a mortgage (PT *seguro de vida*), optional payment-protection insurance on a personal loan. Premiums are cash flows; coverage events are state transitions. Both belong inside the product configuration.
- **Regulatory pack.** The geography-specific bundle, covered in §5 below. The pack is swapped at deployment time, not at product-design time. Product configurations layer on top of a chosen pack.

The agility wedge from the [vision](./00-product-vision.md) lives concretely here. A new variant of *depósito a prazo* with a different compounding rule is a parameter change. A new credit line with a balloon is a new cash-flow shape attached to existing day-count and compounding settings. The engine's job is to be the runtime; the product team's job is to fill in the surface. The legacy product-per-module pattern dies because it has nothing left to do.

The surface has three load-bearing properties. It must be *declarative* — no engine code change to ship a new variant within an existing family. Validation must be *synchronous at commit time* — the product team learns at commit time that a configuration is well-formed and pack-compliant. Deployment must be *safe-by-default* — a new configuration cannot break configurations already running in production. The depth question — templates only, DSL only, or both — is resolved in [authoring §9](./feature-design-configuration-authoring.md): the configuration model is typed family schemas with variants, evolving under coarse-start fine-drift. The schema is the boundary; no DSL escape hatch. The three properties above are met by construction.

The surface is not one artefact family but three, with distinct cadences and approvers:

| Artefact | Owner | Cadence |
|---|---|---|
| Product configs (structure) | Product team | Days–weeks |
| Rate sheets (numerical rates) | Treasury / ALM | Daily–weekly |
| Pack (jurisdiction-scoped vocabulary) | Engine team + regulatory counsel | Per regulatory change |

The split lets a weekly rate change move through treasury sign-off without paying the cost of a product-redesign approval. Collapsing the three into one artefact collapses the cheapest change onto the most expensive approval.

The same model splits a second way, by authoring layer: engine primitives, family schemas, and variants. Full treatment of both decompositions: [surface](./feature-design-configuration-surface.md) (artefact split) and [authoring](./feature-design-configuration-authoring.md) (authoring split, plus the wedge as two falsifiable claims — zero engine code per variant; ≤ 5 working days PM commit to production).

The engine does not ship with a configuration for "anything imaginable." It ships with a bounded surface that covers the product families in scope. Expanding the surface is a roadmap decision, not a runtime extension point.

---

## 4. Two Families Inside One Engine

Even with a unified equation, retail banking products split cleanly into two operating modes — *prospective* and *retrospective* in financial-math terms (per [financial_concepts §9.1](../financial_concepts/banking_products_financial_mathematics.md)); this document calls them by what they do to cash flows.

**With-a-plan (forecast cash flows).** Term deposits and credits. The schedule is computed *ex ante* from the product configuration plus the constituting parameters (principal, rate, term). The engine produces an amortisation schedule (credits) or an accrual + maturity schedule (deposits). Actual events either match the schedule or trigger a known set of deviations (`amortização antecipada`, `prestação extraordinária`, early termination). Math: [financial_concepts §4, §5, §7](../financial_concepts/banking_products_financial_mathematics.md).

**Irregular (observed cash flows).** Current accounts and credit cards. There is no schedule. Movements happen; the engine observes them; balance and interest are computed *ex post* by integration over the realised balance path. The PT operational formula `J(period) = (TAN / base) × Σ S(d)` — sum-of-daily-balances — is derived in [financial_concepts §8](../financial_concepts/banking_products_financial_mathematics.md).

The same equation governs both, which is what [financial_concepts §9.2](../financial_concepts/banking_products_financial_mathematics.md) proves. The operational differences (fixed vs variable `Δt`; forecast vs observed cash flows) translate into two *modes* of the same engine, not two engines. A single runtime supports both: it accepts events as they arrive (irregular mode) or generates a schedule and reconciles events against it (with-a-plan mode). Event store and projection semantics are the same. Reporting hooks are the same. The lifecycle state machine differs in detail but not in structure.

The mathematical sameness does not erase an operational asymmetry. With-a-plan has predictable ingest — one or two events per account per period, schedulable in advance. Irregular has unpredictable, high-volume ingest — every card swipe, every direct debit, every salary credit is an event the engine absorbs, accrues, and reconciles within tight timing. The runtime is the same; the operational profile (throughput, latency, batch-window behaviour, peak handling) is materially different. The architecture must be built with the irregular profile as the upper-bound design point, even though the irregular mode lands later in the [roadmap](./03-roadmap.md). Sizing for with-a-plan only and retrofitting irregular is how "one engine, two modes" turns into two engines under the same name.

The architectural commitment is **interfaces for v4, implementations for v1**: v1 builds for the v1 workload but reserves the envelope shapes, handler signatures, and infrastructure choices that absorb v4 without breaking changes. Six non-negotiable v1 commitments make it operable — event store with a credible scale path, no batch-only assumptions in core code paths, `partition_key` reserved on every envelope, per-projection sync/async update mechanism, snapshot infrastructure exercised in v1, and synthetic v4-scale load tests as part of v1 acceptance. Full treatment: [two-modes](./feature-design-two-modes-asymmetry.md).

One engine, two modes, one cash-flow primitive, two operational profiles the runtime has to absorb without forking.

---

## 5. The Regulatory Pack

The regulatory pack is the third dimension of the wedge, alongside agility and unification. Without it, the engine works only in Portugal. With it, the engine works in any EU country once the pack is filled in.

A pack bundles, for one geography:

- **Rate and day-count conventions.** PT retail deposits use Act/360; PT retail credit uses TAN with proportional periodic rate; *taxa equivalente* vs *taxa proporcional* defaults vary by product family.
- **Tax treatment.** PT applies 28% IRS withholding on interest paid to resident individuals. The engine splits TANB and TANL ([financial_concepts §5.4](../financial_concepts/banking_products_financial_mathematics.md)) and applies withholding flow-by-flow for multi-period deposits.
- **Mandatory disclosures.** PT credit products require the FINE (*Ficha de Informação Normalizada Europeia*) for mortgages and SECCI for consumer credit; PT term deposits require a *Ficha de Informação Normalizada* under BdP rules.
- **Reporting hooks.** Banco de Portugal reporting (Central de Responsabilidades de Crédito for credit, deposit-guarantee-fund reporting for deposits). The engine emits signals; reports are downstream consumers.
- **Product-class rules.** What can legally be called a "depósito a prazo"; what compounding rules apply to which product types; what counts as a *prazo* eligible for the deposit guarantee scheme.

**v1 — PT.** *Decreto-Lei* 133/2009 (consumer credit, transposing CCD 2008/48/EC), *Decreto-Lei* 74-A/2017 (mortgage credit, transposing MCD 2014/17/EU), Banco de Portugal *Avisos* and *Instruções* on retail conduct, the 28% IRS withholding rule, and the Act/360 deposit day-count. v1 implements exactly what depósito a prazo needs; later products in the [roadmap](./03-roadmap.md) fill in the rest.

**v5+ — ES.** Spain transposes the same EU directives with its own administrative rules, tax treatment, and reporting agencies (Banco de España, AEAT). The pack abstraction means filling in those rules without rewriting the engine.

**The EU baseline.** CCD 2008/48/EC (consumer credit) and MCD 2014/17/EU (mortgage credit) are the common floor for every EU pack. PSD2 (2015/2366), GDPR, and DORA cut across packs and feed into the integration architecture rather than the product engine (per [integration_concepts §10](../integration_concepts/10-security-and-threat-model.md)).

**The non-negotiable.** The pack is swappable from day one. A hardcoded `if country == "PT"`, a hardcoded `0.28` for withholding, a hardcoded "Act/360" for day-count — any of these turns geographic expansion into a rewrite. The pack has to be a first-class swap point, read at runtime, not baked in.

**Pack pinning + schema pinning.** Two stability invariants run in parallel. Every constituted instance pins to the pack version *and* the family-schema version active at constitution, and carries both for its entire life. A deposit constituted on 2026-03-15 under `pack: pt.2026.1` and `schema: term_deposit@2026.1` keeps computing under both even after `pt.2027.1` or `term_deposit@2027.1` ships. Regulators expect it; auditors expect it; banks rely on it. Retroactive change is rare and explicit: a pack migration emits a `PackVersionMigrated` event per instance; a schema migration emits a `SchemaVersionMigrated`. Both are auditable. Mechanics: [surface §3.5–§3.6](./feature-design-configuration-surface.md) (pack), [authoring §6](./feature-design-configuration-authoring.md) (schema).

---

## 6. The Integration Seam

The bank's most valuable asset in this estate is the integration shape — the network of contracts between core, channels, payments, CRM, GL, IFRS 9, and regulatory reporting, developed over decades and shaped by this bank's specific operating model. The engine extends that asset with a configurable product layer; it does not replace it. This is the build-vs-buy thesis from [00 §1.5](./00-product-vision.md) made architectural: a vendor engine forces the integration to be rebuilt to fit its contracts; an engine fitted to the existing integration preserves the asset.

The integration architecture is documented in full in [integration_concepts/](../integration_concepts/00-introduction-and-decisions.md). This section names the seam — where the engine plugs into events, sagas, the ACL, observability, and the MCP surface. The engine honours the architecture; it does not redefine it.

- **Event backbone.** The engine emits and consumes on the bank's event backbone (broker chosen in [ADR-001](../integration_concepts/adrs/ADR-001-event-backbone-message-broker.md)). The engine has a contract with the broker's interface, not an opinion about the broker.
- **Schema format and registry.** Event payloads use the format and registry from [ADR-002](../integration_concepts/adrs/ADR-002-schema-format-and-registry.md). Evolution follows [integration_concepts §09](../integration_concepts/09-long-term-schema-evolution.md).
- **Saga participation.** The constitution flow of a new product instance touches Core Banking + Compliance + CRM + Workflow + Notifications — a saga, not a request. The orchestrator is from [ADR-003](../integration_concepts/adrs/ADR-003-saga-orchestrator.md); the walkthrough is in [integration_concepts §05](../integration_concepts/05-constitution-saga-walkthrough.md). The engine participates as a saga step (commands + compensations); it does not run the saga.
- **Outbox emission.** Every state-changing operation produces a domain event; events leave via the outbox pattern (per [ADR-004](../integration_concepts/adrs/ADR-004-outbox-pattern-mechanism.md), [integration_concepts §04](../integration_concepts/04-plumbing-patterns.md)). Exactly-once-effectively semantics. Event store and outbox are co-located so event-append and outbox-write commit atomically.
- **Anti-corruption layer.** The engine talks to Core Banking through the ACL (per [integration_concepts §02](../integration_concepts/02-anti-corruption-layer.md)). The ACL handles translation, idempotency, indeterminate state, and the rest of its seven responsibilities; the engine sees clean domain primitives.
- **Observability.** OpenTelemetry tracing, structured logs, metrics (per [ADR-007](../integration_concepts/adrs/ADR-007-observability-stack.md), [integration_concepts §06](../integration_concepts/06-observability-and-tracing.md)). The engine instruments product semantics (*accrual computed*, *withholding applied*); the integration layer instruments transport semantics.
- **MCP server exposure.** Commands and queries are exposed to LLM agents via the MCP server (per [ADR-010](../integration_concepts/adrs/ADR-010-mcp-server-runtime-and-sdk.md), [integration_concepts §11](../integration_concepts/11-chat-agent-channel-strategy.md)). Agent-channel access is the same surface as the rest of the bank, gated by the same authorisation.

### Deployment

Self-hosted in the operating bank's infrastructure (typically private cloud or on-prem Kubernetes). One codebase, one set of images, one configuration grammar, one regulatory pack at a time. The event backbone and the saga orchestrator run in the same topology. Operational tooling (upgrade scripts, backup/restore, on-call runbooks) is part of the deliverable.

### Strangler-fig coexistence

The adoption motion from the [vision](./00-product-vision.md) is product-line at a time. Coexistence is a multi-year period with start, middle, and end phases during which two peer systems run the same product family concurrently — not a steady-state property the engine has. Full treatment of the seven dimensions of dual operation, the system-of-record map, the daily-batch-file shape, the unified-read-surface staleness asymmetry, reconciliation, regulatory reporting, cutover mechanics, and the end state: [coexistence](./feature-design-strangler-fig-coexistence.md).

The three integration-seam properties that make the period operable:

- **Per-product-line onboarding.** The bank turns on the engine for one product family (v1: term deposits) while the rest stays on the legacy core. The event topology must let one family flow through the new engine without forcing other families through it.
- **API coexistence with legacy system-of-record.** Products on the new engine are SoR'd there; products on the legacy core are SoR'd there. Both are queryable through a unified read surface, which CQRS (per [integration_concepts §03](../integration_concepts/03-cqrs-and-read-models.md)) makes possible — one read model spans both, with different staleness profiles per source.
- **Event contract that lets legacy and engine react to each other.** When a new-engine term deposit matures and settles to a legacy current account, the legacy core reacts to the settlement event. When a legacy term deposit auto-renews onto the engine, the two events are linked by correlation_id across the SoR transition. The event catalogue (per [integration_concepts §08](../integration_concepts/08-event-catalog-governance.md)) governs the contracts.

The integration architecture was designed to support this coexistence; it was not designed for one specific product. The engine fits in as a participant, not as a new layer. The extension from event-driven peers to a batch-file peer (the legacy daily extract is the engine's only feed of legacy state) is what the design notes specify in detail.
