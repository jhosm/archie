# Product Architecture

> The architectural thesis. Five sections, each one a load-bearing piece of the engine's identity. The engine **inherits** the integration architecture from [integration_concepts/](../integration_concepts/00-introduction-and-decisions.md) — it does not redefine it.

---

## 1. The Cash-Flow Primitive

Every retail banking product obeys the same equation. [financial_concepts §9.2](../financial_concepts/banking_products_financial_mathematics.md) states the unification:

```
S(t + Δt) = S(t) × (1 + r × Δt) − payments(Δt) + drawdowns(Δt)
```

Where `S(t)` is the outstanding balance, `r` is the rate matched to the unit of `Δt`, and `payments` and `drawdowns` are the cash flows crossing the account boundary in the interval.

A term deposit. A Price loan. A SAC loan. A bullet credit. A mortgage with grace period and quarterly variable-rate revision. A current account. A revolving credit card. Pick any of them; substitute the right shape for `payments` and `drawdowns`, the right `r` and `Δt`, and the equation produces the right answer. The proof is in §9.2 of the financial concepts series; the consequence is architectural.

The consequence is this: a product engine that takes this equation as its primitive is **structurally** one engine. Not "one engine that special-cases each product family," nor "a shared library that each product module reuses." One engine, one runtime, one balance evolution function — invoked with different parameters for different products. The product-per-module pattern that haunts legacy cores is a *modelling* accident. Unification dissolves it.

This is the single architectural insight on which everything else in this document depends. The configuration surface, the two-families story, the regulatory pack, and the integration seam are all consequences of taking the cash-flow primitive seriously.

---

## 2. The Configuration Surface

The engine's runtime is fixed; the **configuration surface** is the variable part. [financial_concepts §2.2](../financial_concepts/banking_products_financial_mathematics.md) names the three irreducible dimensions:

1. **Cash-flow shape** — fixed (Price installments), variable (Euribor-revised mortgage), irregular (current-account movements).
2. **Day-count convention** — Act/360, Act/365, 30/360, and the geographic conventions layered on top.
3. **Compounding frequency** — daily, monthly, quarterly, annual.

Three dimensions are not enough on their own to specify a sellable banking product. A product also carries:

- **Charges** — opening fees, maintenance fees, early-repayment penalties, currency-conversion margins. Some are mandatory for the TAEG calculation (financial_concepts §6.2); some are optional. The engine treats charges as first-class cash flows, not metadata.
- **Insurance** — mandatory life insurance on a mortgage (PT *seguro de vida*), optional payment-protection insurance on a personal loan. Premiums are cash flows; coverage events are state transitions. Both belong inside the product configuration, not bolted on outside.
- **Regulatory pack** — the geography-specific bundle covered in section 4 below. The pack is part of the configuration, but it is swapped at deployment time, not at product-design time. New product configurations layer on top of a chosen pack.

The agility wedge from the [vision](./00-product-vision.md) lives concretely here. **A new product is a new configuration, not a new module.** A new variant of *depósito a prazo* with a different compounding rule is a parameter change. A new credit line with a balloon at the end is a new cash-flow shape attached to existing day-count and compounding settings. The product engine's job is to be the runtime; the product team's job is to fill in the configuration surface. The legacy product-per-module pattern dies because it has nothing left to do.

The falsifiable target from the [vision](./00-product-vision.md) ("under 5 working days from configuration commit to first booked instance") translates into specific properties of this surface: the configuration must be **declarative** (no engine code change required to ship a new variant within an existing family), the validation must be **synchronous at commit time** (so the product team learns within minutes that a configuration is well-formed and pack-compliant, not hours), and the deployment must be **safe-by-default** (a new configuration cannot break configurations already running in production). The depth question — templates only, DSL only, or both — is genuinely open and tracked in [04-open-questions](./04-open-questions.md); whichever depth is chosen, it must satisfy these three properties or the 5-day claim fails.

The configuration surface is also where the discipline of the brief lives. The engine **does not** ship with a configuration for "anything imaginable." It ships with a deliberately bounded surface that covers the product families in scope. Expanding the surface is a roadmap decision, not a runtime extension point that customers can stretch beyond recognition.

---

## 3. Two Families Inside One Engine

Even with a unified equation, retail banking products split cleanly into two **operating modes**. [financial_concepts §9.1](../financial_concepts/banking_products_financial_mathematics.md) calls these *prospective* and *retrospective*; this document calls them by what they do to cash flows.

**With-a-plan (forecast cash flows).** Term deposits and credits. The schedule of cash flows is computed *ex ante* from the product configuration plus the constituting parameters (principal, rate, term). The engine produces an amortisation schedule (for credits) or an accrual + maturity schedule (for deposits). Actual events on the account either match the schedule or trigger a known set of deviations (`amortização antecipada`, `prestação extraordinária`, early termination of a deposit). [financial_concepts §4, §5, and §7](../financial_concepts/banking_products_financial_mathematics.md) cover the math.

**Irregular (observed cash flows).** Current accounts and credit cards. There is no schedule. Movements happen; the engine observes them; balance and interest are computed *ex post* by integration over the realised balance path. [financial_concepts §8](../financial_concepts/banking_products_financial_mathematics.md) covers the operational formula — `J(period) = (TAN / base) × Σ S(d)`, the sum-of-daily-balances method that PT current-account practice uses.

The same equation governs both — that is what §9.2 proves. The operational differences (fixed vs variable `Δt`, forecast vs observed cash flows) translate into two **modes** of the same engine, not two engines. A single product runtime supports both: it accepts events when they arrive (irregular mode) *or* it generates a schedule and reconciles events against it (with-a-plan mode). The subledger semantics are the same. The reporting hooks are the same. The lifecycle state machine differs in detail but not in structure.

The mathematical sameness does not erase an operational asymmetry worth naming. The with-a-plan family has predictable ingest: one or two events per account per period, schedulable in advance. The irregular family has unpredictable, high-volume ingest: every card swipe, every direct debit, every salary credit is an event the engine has to absorb, accrue, and reconcile within tight timing. The runtime is the same; the *operational profile* (throughput, latency, batch-window behaviour, peak handling) is materially different. The engine architecture has to be built with the irregular profile as the upper-bound design point, even if the irregular mode lands later in the [roadmap](./03-roadmap.md). Sizing for with-a-plan only and retrofitting irregular is one of the ways "one engine, two modes" turns into two engines under the same name.

This is what "one engine across product families" actually means, with that caveat. Not "we have one engine and two completely separate code paths inside it." One engine, two modes, one cash-flow primitive, two operational profiles that the runtime has to absorb without forking.

---

## 4. The Regulatory Pack

The regulatory pack is the **third dimension of the wedge** — alongside agility and unification. Without it, the engine works only in Portugal. With it, the engine works in any EU country once the pack is filled in.

A regulatory pack bundles, for one geography:

- **Rate and day-count conventions.** PT retail deposits use Act/360; PT retail credit uses TAN with proportional periodic rate; *taxa equivalente* vs *taxa proporcional* defaults vary by product family.
- **Tax treatment.** PT applies 28% IRS withholding tax on interest paid to resident individuals; the engine must split TANB and TANL ([financial_concepts §5.4](../financial_concepts/banking_products_financial_mathematics.md)) and apply withholding flow-by-flow for multi-period deposits.
- **Mandatory disclosures.** PT credit products require the FINE (*Ficha de Informação Normalizada Europeia*) for mortgages and the SECCI for consumer credit; PT term deposits require a *Ficha de Informação Normalizada* under BdP rules.
- **Reporting hooks.** Banco de Portugal reporting (Central de Responsabilidades de Crédito for credit, deposit-guarantee-fund reporting for deposits) — the engine emits the signals; the reports themselves are downstream consumers.
- **Product-class rules.** What can legally be called a "depósito a prazo"; what compounding rules apply to which product types; what counts as a *prazo* eligible for the deposit guarantee scheme.

**The pack at v1 — PT.** *Decreto-Lei* 133/2009 (consumer credit, transposing CCD 2008/48/EC), *Decreto-Lei* 74-A/2017 (mortgage credit, transposing MCD 2014/17/EU), Banco de Portugal *Avisos* and *Instruções* on retail banking conduct, the 28% IRS withholding rule for resident individuals, and the Act/360 deposit day-count convention. v1 implements exactly what depósito a prazo needs from this list; subsequent products in the [roadmap](./03-roadmap.md) fill in the rest of the pack.

**The pack at v5+ — ES.** TBD. Spain transposes the same EU directives but with its own administrative rules, tax treatment, and reporting agencies (Banco de España, AEAT). The pack abstraction means filling in those rules without rewriting the engine.

**The EU baseline.** CCD 2008/48/EC (consumer credit) and MCD 2014/17/EU (mortgage credit) form the common floor for every EU pack. PSD2 (Directive 2015/2366), GDPR, and DORA cut across packs and feed into the integration architecture rather than the product engine itself ([integration_concepts/10](../integration_concepts/10-security-and-threat-model.md) covers them).

**The non-negotiable.** The pack is swappable from day one. If regulation is hardcoded anywhere in the engine — a `if country == "PT"` somewhere, a hardcoded `0.28` for withholding tax, a hardcoded "Act/360" for day-count — geographic expansion becomes a rewrite, not a configuration change. The wedge dies. The architecture has to make the pack a first-class swap point, and the engine has to read the pack at runtime, not bake it in.

---

## 5. The Integration Seam

The product engine does not live alone. It lives inside a bank's ecosystem, which means it inherits an integration architecture. That architecture is fully documented in [integration_concepts/](../integration_concepts/00-introduction-and-decisions.md). This section names the seam, not the architecture — the engine's job is to **inherit, not redefine**.

**Events on Redpanda.** The engine emits and consumes events on the bank's event backbone. The choice of broker is [ADR-001](../integration_concepts/adrs/ADR-001-event-backbone-message-broker.md) (Redpanda). The engine does not have an opinion about the broker; it has a contract with the broker's interface.

**Schema format and registry.** Event payloads use the schema format and registry chosen in [ADR-002](../integration_concepts/adrs/ADR-002-schema-format-and-registry.md). Schemas evolve under the long-term rules in [integration_concepts/09](../integration_concepts/09-long-term-schema-evolution.md).

**Saga participation.** The constitution flow of a new product instance touches Core Banking + Compliance + CRM + Workflow + Notifications — that is a saga, not a request. The saga orchestrator is the one in [ADR-003](../integration_concepts/adrs/ADR-003-saga-orchestrator.md); the canonical walkthrough is the constitution saga in [integration_concepts/05](../integration_concepts/05-constitution-saga-walkthrough.md). The engine participates as a saga step (commands + compensations), it does not run the saga.

**Outbox emission.** Every state-changing operation in the engine produces a domain event; events leave the engine via the outbox pattern from [ADR-004](../integration_concepts/adrs/ADR-004-outbox-pattern-mechanism.md) and [integration_concepts/04](../integration_concepts/04-plumbing-patterns.md). Exactly-once-effectively semantics, not at-most-once and not at-least-once. The subledger and the outbox are co-located so the event-and-write commit atomically.

**Anti-corruption layer.** The engine talks to Core Banking through the ACL described in [integration_concepts/02](../integration_concepts/02-anti-corruption-layer.md). The ACL handles the seven responsibilities listed there (translation, idempotency, indeterminate state, etc.); the engine sees clean domain primitives. Translation lives in the ACL, not in the engine.

**Observability.** Distributed tracing, structured logs, and metrics are emitted via OpenTelemetry per [ADR-007](../integration_concepts/adrs/ADR-007-observability-stack.md) and [integration_concepts/06](../integration_concepts/06-observability-and-tracing.md). The engine instruments product-level semantics (e.g. "accrual computed", "withholding applied"); the integration layer instruments transport-level semantics.

**MCP server exposure.** The engine's commands and queries are exposed to LLM agents via the MCP server described in [ADR-010](../integration_concepts/adrs/ADR-010-mcp-server-runtime-and-sdk.md) and [integration_concepts/11](../integration_concepts/11-chat-agent-channel-strategy.md). Agent-channel access is the same surface as the rest of the bank — a request, a saga, a status push — gated by the same authorisation.

### Deployment modes

The engine ships in two deployment modes from a single codebase:

- **SaaS multi-tenant.** Hosted by the vendor; tenants isolated at a level determined by the [open-questions](./04-open-questions.md) review.
- **Self-hosted.** Deployed into the customer bank's infrastructure (typically a private cloud or on-prem Kubernetes), operated by the customer or co-operated under a managed-service agreement.

Both modes use the same images, the same configuration grammar, and the same regulatory packs. The integration architecture supports both — Redpanda runs equally well in either topology, and the saga orchestrator is environment-agnostic.

The single-codebase commitment is not free. SaaS multi-tenancy needs tenant routing, per-tenant rate limits, and shared observability that names every signal by tenant; self-hosted needs none of those but needs operational tooling (upgrade scripts, backup/restore, on-call runbooks) that the SaaS team operates centrally. Vendors who promise "same code, two modes" frequently arrive at a *de facto* fork — a separate branch for self-hosted, with selective backports from SaaS. The engineering response is to treat the seam (tenant scoping, deployment topology) as a configuration point inside the codebase, not as a branch — and to require every feature to ship in both modes before it lands in either. A future ADR will document the specific mechanism; the architectural commitment in this brief is that the seam exists at runtime, not at the source-tree level.

### Strangler-fig coexistence

The adoption motion from the [vision](./00-product-vision.md) is product-line at a time. That requires three coexistence properties from the integration seam:

- **Per-product-line onboarding.** The bank turns on the engine for one product family (v1: term deposits) while every other product family stays on the legacy core. The event topology must let one product family flow through the new engine without forcing other families through it.
- **API coexistence with legacy system-of-record.** For products on the new engine, the engine is the system of record. For products on the legacy core, the legacy is. Both must be queryable through a unified read surface, which is what [integration_concepts/03](../integration_concepts/03-cqrs-and-read-models.md) (CQRS) makes possible — a read model spans both.
- **Event contract that lets legacy and new engine react to each other.** When a term deposit on the new engine matures and settles to a current account on the legacy core, the legacy core has to react to the settlement event. When a current account on the legacy core is closed, the new engine has to react and terminate any deposits it holds. The event catalogue ([integration_concepts/08](../integration_concepts/08-event-catalog-governance.md)) governs the contracts that make this work.

The integration architecture in integration_concepts/ was designed to support exactly this coexistence — it was not designed for one specific product. The product engine fits into it as a participant, not as a new layer.
