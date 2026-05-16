# Term Deposit System — Integration Architecture
## Document 00: Introduction and Foundational Decisions

---

## Context

This document series captures the architectural reasoning for the integration patterns of a Portuguese banking term deposit management system. The system is a specialized banking backend handling the complete operational lifecycle of term deposits, designed to be consumed by various UI/UX interfaces (web, chat, mobile apps) that exist as separate systems.

The system operates within Portugal's regulatory framework overseen by Banco de Portugal, including compliance with deposit guarantee schemes (FGD) and specific tax treatment requirements. A modular system design is preferred, where different concerns like workflow approvals, reporting, and documentation are handled by separate applications rather than creating a monolithic system.

The system integrates with: Core Banking, CRM, Compliance, Workflow, Documentation, Notifications, and Reporting.

---

## The Initial Question

> "Let's say I want to build this ecosystem. From a technical architecture perspective, I want to focus on the integration patterns. Given that we're in a very demanding and complex environment, the way the applications talk to each other must be as simple as possible while maximising three things: flexibility; performance; compensation mechanisms (not transactionality). What are your thoughts on this?"

---

## Initial Reading of the Three Principles

The combination "flexibility + performance + compensation (not transactionality)" points to a very specific architectural axis: event-driven as backbone, synchronous only where absolutely necessary (critical validations in the user flow), and sagas instead of 2PC/XA. Classical 2PC distributed transactions in banking typically kill exactly the flexibility and performance you want to preserve, and in systems like Core Banking they are often not even available.

Compensation is the pragmatic right choice — but **how** you implement it (choreography vs orchestration, outbox, idempotency) has serious trade-offs.

---

## Clarifying Questions Before Proceeding

Four questions were posed before the recommendation could be finalized, because each materially changes the architectural shape:

1. **Tolerable latency in the user flow.** When a client taps "Constitute Term Deposit" on mobile, what is the acceptable response time until they see confirmation? Sub-2 seconds (sync orchestration end-to-end), sub-500ms (event-driven with optimistic UI/pending status), or "show spinner and notify when done" (purely asynchronous at the edge with polling/push)? This defines whether the saga is synchronous request-reply or fully asynchronous at the boundary.

2. **Existing integration infrastructure.** Is there a corporate eventing/messaging platform in use at the bank (enterprise Kafka, IBM MQ, RabbitMQ, ESB like MuleSoft/WSO2)? Or do you have technological freedom? In banking we rarely start from scratch — and the Core Banking typically dictates much of the choice.

3. **Reality of Core Banking integration.** How does the Core speak? Modern REST/JSON, legacy SOAP/XML, asynchronous MQ, or mainframe with an adapter in the middle? This is usually the hardest constraint of the ecosystem and conditions whether you can really have pure event-driven or whether you need an anti-corruption layer with different semantics on each side.

4. **Compensation style.** When you say "compensation mechanisms", are you thinking of **orchestration** (an orchestrator service explicitly coordinates the saga steps — useful for complex flows like constitution that touch Core + Compliance + Workflow + Notifications) or **choreography** (each service reacts to events without a central coordinator — more decoupled but harder to observe and debug)? For banking, I normally recommend a hybrid, but I want to understand your preference.

---

## Answers Received

1. **Sub-500ms**
2. **Greenfield — starting from scratch**
3. **Hybrid (orchestration + choreography)**
4. (Implicit in 3 above)

Plus confirmation that the **Anti-Corruption Layer is needed**.

---

## High-Level Architectural Shape

Sub-500ms + greenfield + hybrid forces an architecture with a very clear cut between two worlds.

### The Fundamental Division: Synchronous Edge / Asynchronous Backbone

Sub-500ms for operations that touch Core + Compliance + CRM + Workflow is incompatible with distributed synchronous coordination — it is physically impossible to guarantee that SLA by summing latencies. Therefore, the design must be:

- **Edge (UI-facing)**: synchronous, but doing only what is strictly necessary within the budget
- **Backbone (system-facing)**: asynchronous, event-driven, where the real orchestration happens

For the user, the constitution operation does not "complete" in 500ms — it is **accepted** in 500ms. The model is **optimistic acceptance + status push**: the backend validates the critical things, returns `deposit_id` with `status=PROCESSING`, and pushes updates via SSE/WebSocket as the saga progresses.

### Patterns Applied in Layers

1. **At the edge** — REST/JSON synchronous for queries and pre-validations (KYC OK?, sufficient balance?, product eligibility?). Only calls that fit comfortably within the 500ms. State-change commands return immediately with status.

2. **Event backbone** — Kafka makes sense in greenfield (with schema registry, Avro or Protobuf). Events as first-class contracts, versioned from day one.

3. **Hybrid saga by nature of the flow**:
   - **Orchestration** for multi-step business flows with complex compensation: Constitution, Early Mobilization, Maturity with renewal. A dedicated orchestrator holds the saga state, knows the steps and their explicit compensations.
   - **Choreography** for fan-out of side effects without coordination: notifications, reporting, document generation, audit. The deposits service emits `DepositConstituted` and N consumers react independently.

4. **The three non-negotiable primitives for "compensation, not transactionality"**:
   - **Outbox pattern**: each service writes state and event in the same local transaction. Without this, you have dual-write and silently lose consistency.
   - **Idempotency keys** on all commands and handlers. Retry safe by design.
   - **Compensations modelled as domain actions**: `cancelConstitution()`, `reverseDebit()`, `releaseKycHold()`. They are not exceptions, they are part of the model.

5. **CQRS-lite to guarantee the read SLA** — you cannot hit 500ms if each query aggregates Core + CRM + Compliance at runtime. Materialize read models fed by backbone events. Screens (web, mobile, branch) read from those projections, not from source systems.

### Anti-Corruption Layer Confirmed

The Anti-Corruption Layer is required as a peripheral element between our domain and the Core Banking system, since the Core's contract is rarely event-native and its semantics would contaminate the rest of the system if there were no isolation.

---

## Approach: Work Through the Fundamental Primitives First

Before going into specific patterns (Outbox, Inbox, Schema Registry, ACL details, CQRS model, concrete saga orchestrator), we will work through the **fundamental primitives** that everything else builds upon.

There are six primitives. Naming them and explaining what each one protects — because the value of the primitive isn't in the name, it's in the discipline it imposes.

### The Six Primitives

1. **Command vs Event** — two semantics, not one
2. **Domain Event vs Integration Event**
3. **Bounded Context + Aggregate**
4. **Identity Trio**: Entity ID, Correlation ID, Causation ID
5. **Idempotency Key**
6. **Compensating Action as a domain operation**

These six sustain everything else. Outbox, inbox/dedup, sagas, ACL, read models, schema registry — these are *patterns built upon* these primitives, not primitives themselves.

### Order of Coverage

The order of coverage:
- (1+2) Message semantics
- (3) Boundaries
- (4+5) Identity and idempotency
- (6) Compensation

Then, on top of the primitives:
- ACL deep dive
- CQRS and read models
- Plumbing patterns (Outbox, Inbox, Schema Registry, delivery guarantees)
- Concrete saga of the Constitution
- Transversal topics: Observability, Testing, Governance, Schema Evolution

---

## Documents in This Series

- **00 — Introduction and Foundational Decisions** (this document)
- **01 — The Six Primitives**
- **02 — Anti-Corruption Layer**
- **03 — CQRS and Read Models**
- **04 — Plumbing Patterns**
- **05 — Constitution Saga Walkthrough**
- **06 — Observability and Distributed Tracing**
- **07 — Testing Strategy**
- **08 — Event Catalog Governance**
- **09 — Long-term Schema Evolution**
