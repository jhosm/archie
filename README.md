# babelstone — Banking Ecosystem Reference Library

A collection of documentation series covering different dimensions of a banking ecosystem. Each series is self-contained and addresses a distinct concern; they share a common example domain — a Portuguese retail banking environment — but can be read independently.

The three series answer three distinct questions:

- [financial_concepts/](./docs/product-management/financial_concepts/banking_products_financial_mathematics.md) — **what math is correct**
- [product_concepts/](./docs/product-management/product_concepts/README.md) — **what configurable product implements that math**
- [integration_concepts/](./docs/product-management/integration_concepts/00-introduction-and-decisions.md) — **how that product integrates with the bank**

> **New here?** The series above are organised by *concern*. If you'd rather start from *your role* — integrator, family developer, pack author, agent-channel consumer, operator — begin at the [**reading paths**](./docs/product-management/reading-paths/README.md), which sequence the docs, [task guides](./docs/product-management/guides/README.md), and generated [reference](./docs/product-management/reference/README.md) for each. The organising architecture is [ADR-PC-022](./docs/product-management/product_concepts/adrs/ADR-PC-022-product-documentation-architecture.md).

---

## Series

### financial_concepts/ — Financial Mathematics of Banking Products

A conceptual reference for the financial mathematics underlying retail banking products. It establishes a unifying framework — sequences of cash flows, present value, IRR — and develops it across the main product families: term deposits, loan amortization (French, German, and constant-amortization systems), current accounts, and credit cards.

The document is aimed at engineers and architects who need to reason about the financial behaviour of the products their systems manage, without requiring an accounting or finance background. It is not a regulatory or accounting source; real implementations must respect Banco de Portugal conventions and IFRS 9.

| Document | What It Covers |
|---|---|
| [Financial Mathematics of Banking Products](./docs/product-management/financial_concepts/banking_products_financial_mathematics.md) | Cash flow framework, present value, the three amortization systems, term deposits, IRR/TAEG, composite and irregular cases, cross-family synthesis, glossary |

---

### product_concepts/ — Core Banking Product Engine

A documentation series defining a configurable core banking product engine: a product brief, not a system design. The engine takes the cash-flow primitive from [financial_concepts §9.2](./docs/product-management/financial_concepts/banking_products_financial_mathematics.md) as its single architectural insight and uses it to collapse every retail product family — deposits, credits, mortgages, current accounts, cards — into one engine with a swappable configuration surface and a swappable regulatory pack. The integration backbone is inherited from `docs/product-management/integration_concepts/`, not redefined.

The customer is an incumbent Portuguese bank modernising on a strangler-fig adoption path; geography expands PT → ES → EU; deployment is SaaS multi-tenant and self-hosted from a single codebase. The v1 slice is *depósito a prazo* (Portuguese term deposit) — the smallest surface that exercises both the engine and the PT regulatory pack end-to-end.

| Document | What It Covers |
|---|---|
| [README](./docs/product-management/product_concepts/README.md) | Series intro, positioning relative to the other two series, document map |
| [00 — Product Vision](./docs/product-management/product_concepts/00-product-vision.md) | The one-pager: customer, problem, wedge, in/out of scope, strategic frame |
| [01 — Product Architecture](./docs/product-management/product_concepts/01-product-architecture.md) | Architectural thesis: cash-flow primitive, configuration surface, two families, regulatory pack, integration seam |
| [02 — v1 Scope: Term Deposits](./docs/product-management/product_concepts/02-v1-scope-term-deposits.md) | Why term deposits first, in-scope features, PT regulatory features, subledger outputs, event contract, coexistence with legacy DDA |
| [03 — Roadmap](./docs/product-management/product_concepts/03-roadmap.md) | Sequenced expansion (PT term deposits → PT credit → PT mortgage → PT current accounts/cards → ES → EU) plus continuous pack maintenance |
| [04 — Open Questions](./docs/product-management/product_concepts/04-open-questions.md) | Deferred decisions register: competitive positioning, pricing, licensing, coexistence targets, multi-tenancy, IFRS 9 signal boundary, time-travel, configurability depth, primary economic buyer, founding team credibility, split-brain reconciliation |
| [v1 Build Backlog](./docs/product-management/product_concepts/v1-build-backlog.md) | Execution spec: the v1 build as bd epics + child issues (platform, engine core, financial-math kernel, pack toolchain, projections, walking skeleton, term-deposit content, integration estate, observability, load, security/DR, CI/CD), with deferred ACL/notification/IFRS9 reserved |

---

### integration_concepts/ — Integration Architecture

A documentation series covering integration architecture patterns for complex banking ecosystems. The series captures the full design reasoning — from the initial constraints that shaped the architecture, through the conceptual primitives it rests on, down to the concrete patterns, flows, testing strategy, and long-term governance.

A Portuguese term deposit management system serves as the running example throughout: specific enough to make every pattern concrete, complex enough to exercise all of them. The architecture itself is not tied to term deposits — it is the integration backbone for a banking ecosystem, equally applicable to loans, savings accounts, investment products, or any other application that integrates with the same Core Banking, CRM, Compliance, and Workflow infrastructure.

The documents are ordered to follow the logic of the design. They should be read in sequence.

#### The Three Constraints That Shaped Everything

Before any patterns were chosen, three constraints were fixed. Every architectural decision in the series is traceable to one or more of these.

**Sub-500ms edge response.** When a client initiates a high-value operation — in the example, constituting a term deposit — they see confirmation within 500ms. Coordinating Core + Compliance + CRM + Workflow synchronously within that budget is physically impossible, so the system uses an optimistic acceptance model: validate what fits, persist the request, return `202 Accepted`, run the saga asynchronously.

**Hybrid saga — orchestration + choreography.** Multi-step flows with complex compensation use a stateful orchestrator. Fan-out of side-effects without coordination requirements uses choreography.

**Compensation, not transactionality.** Classical 2PC/XA distributed transactions kill flexibility and are often unavailable in Core Banking systems. Compensation is the right trade-off — but how it is implemented determines whether the system is actually robust under failure.

The full reasoning is in [Document 00](./docs/product-management/integration_concepts/00-introduction-and-decisions.md).

#### Document Map

| # | Title | What It Covers |
|---|---|---|
| [00](./docs/product-management/integration_concepts/00-introduction-and-decisions.md) | Introduction and Foundational Decisions | Context, the three driving constraints, high-level architectural shape |
| [01](./docs/product-management/integration_concepts/01-the-six-primitives.md) | The Six Primitives | Command vs Event, Domain vs Integration Event, Bounded Context + Aggregate, Identity Trio, Idempotency Key, Compensating Action |
| [02](./docs/product-management/integration_concepts/02-anti-corruption-layer.md) | Anti-Corruption Layer | Seven ACL responsibilities, internal structure, the indeterminate-state problem, antipatterns |
| [03](./docs/product-management/integration_concepts/03-cqrs-and-read-models.md) | CQRS and Read Models | Read/write model separation, projectors, eventual consistency management, the greenfield-pragmatic starting point |
| [04](./docs/product-management/integration_concepts/04-plumbing-patterns.md) | Plumbing Patterns | Outbox, Inbox, Schema Registry, delivery guarantees — the mechanics that make events reliable |
| [05](./docs/product-management/integration_concepts/05-constitution-saga-walkthrough.md) | Constitution Saga Walkthrough | All primitives and patterns materialized in a real constitution flow, with concrete IDs, timings, and compensation paths |
| [06](./docs/product-management/integration_concepts/06-observability-and-tracing.md) | Observability and Distributed Tracing | Three pillars (logs/metrics/traces), OpenTelemetry, the concrete trace of a constitution, what to instrument and alert on |
| [07](./docs/product-management/integration_concepts/07-testing-strategy.md) | Testing Strategy | Adapted test pyramid for event-driven systems: aggregate unit tests, integration with testcontainers, contract tests (Pact), saga tests, selective E2E |
| [08](./docs/product-management/integration_concepts/08-event-catalog-governance.md) | Event Catalog Governance | Four governance pillars, ownership model, naming conventions, review process, the living catalogue |
| [09](./docs/product-management/integration_concepts/09-long-term-schema-evolution.md) | Long-term Schema Evolution | Taxonomy of compatible/incompatible changes, concrete techniques for each, antipatterns, real scenarios |
| [10](./docs/product-management/integration_concepts/10-security-and-threat-model.md) | Security and Threat Model | Trust boundaries, assets worth protecting, six security principles, regulatory obligations (PSD2, GDPR, BdP, DORA) |
| [11](./docs/product-management/integration_concepts/11-chat-agent-channel-strategy.md) | Chat Agent Channel Strategy | Bank as MCP server; LLM-agent channel; tool/resource/prompt mapping onto commands and CQRS; async completion patterns (tasks, polling, out-of-band callback); trust model for untrusted agents |
