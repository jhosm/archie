# babelstone — Banking Ecosystem Integration Architecture

A documentation series covering integration architecture patterns for complex banking ecosystems. The series captures the full design reasoning — from the initial constraints that shaped the architecture, through the conceptual primitives it rests on, down to the concrete patterns, flows, testing strategy, and long-term governance.

A Portuguese term deposit management system serves as the running example throughout: specific enough to make every pattern concrete, complex enough to exercise all of them. The architecture itself is not tied to term deposits — it is the integration backbone for a banking ecosystem, equally applicable to loans, savings accounts, investment products, or any other application that integrates with the same Core Banking, CRM, Compliance, and Workflow infrastructure.

The documents are ordered to follow the logic of the design, not alphabetical or historical order. They should be read in sequence.

---

## The Example System

The running example manages the complete operational lifecycle of term deposits in Portugal: constitution, maturity, early mobilization, interest payments, renewal. It integrates with Core Banking, CRM, Compliance, Workflow, Documentation, Notifications, and Reporting — the same ecosystem that any application built on this architecture would share.

The example operates within Portugal's regulatory framework (Banco de Portugal, FGD deposit guarantee schemes, specific tax treatment) and is designed for a greenfield stack — no legacy constraints on the integration infrastructure.

---

## The Three Constraints That Shaped Everything

Before any patterns were chosen, three constraints were fixed. Every architectural decision in the series is traceable to one or more of these.

**Sub-500ms edge response.** When a client initiates a high-value operation — in the example, constituting a term deposit — they see confirmation within 500ms. Coordinating Core + Compliance + CRM + Workflow synchronously within that budget is physically impossible, so the system uses an optimistic acceptance model: validate what fits, persist the request, return `202 Accepted`, run the saga asynchronously.

**Hybrid saga — orchestration + choreography.** Multi-step flows with complex compensation use a stateful orchestrator. Fan-out of side-effects without coordination requirements uses choreography.

**Compensation, not transactionality.** Classical 2PC/XA distributed transactions kill flexibility and are often unavailable in Core Banking systems. Compensation is the right trade-off — but how it is implemented determines whether the system is actually robust under failure.

The full reasoning behind these constraints and the architectural shape they force is in [Document 00](./integration/00-introduction-and-decisions.md).

---

## Document Map

| # | Title | What It Covers |
|---|---|---|
| [00](./integration/00-introduction-and-decisions.md) | Introduction and Foundational Decisions | Context, the three driving constraints, high-level architectural shape |
| [01](./integration/01-the-six-primitives.md) | The Six Primitives | Command vs Event, Domain vs Integration Event, Bounded Context + Aggregate, Identity Trio, Idempotency Key, Compensating Action |
| [02](./integration/02-anti-corruption-layer.md) | Anti-Corruption Layer | Seven ACL responsibilities, internal structure, the indeterminate-state problem, antipatterns |
| [03](./integration/03-cqrs-and-read-models.md) | CQRS and Read Models | Read/write model separation, projectors, eventual consistency management, the greenfield-pragmatic starting point |
| [04](./integration/04-plumbing-patterns.md) | Plumbing Patterns | Outbox, Inbox, Schema Registry, delivery guarantees — the mechanics that make events reliable |
| [05](./integration/05-constitution-saga-walkthrough.md) | Constitution Saga Walkthrough | All primitives and patterns materialized in a real constitution flow, with concrete IDs, timings, and compensation paths |
| [06](./integration/06-observability-and-tracing.md) | Observability and Distributed Tracing | Three pillars (logs/metrics/traces), OpenTelemetry, the concrete trace of a constitution, what to instrument and alert on |
| [07](./integration/07-testing-strategy.md) | Testing Strategy | Adapted test pyramid for event-driven systems: aggregate unit tests, integration with testcontainers, contract tests (Pact), saga tests, selective E2E |
| [08](./integration/08-event-catalog-governance.md) | Event Catalog Governance | Four governance pillars, ownership model, naming conventions, review process, the living catalogue |
| [09](./integration/09-long-term-schema-evolution.md) | Long-term Schema Evolution | Taxonomy of compatible/incompatible changes, concrete techniques for each, antipatterns, real scenarios |
| [10](./integration/10-security-and-threat-model.md) | Security and Threat Model | Trust boundaries, assets worth protecting, six security principles, regulatory obligations (PSD2, GDPR, BdP, DORA) |
