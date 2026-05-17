# ADR-003: Saga Orchestrator

| Field | Value |
|---|---|
| Status | Accepted |
| Date | 2026-05-17 |
| Deciders | jhosm |
| Common criteria | [ADR-000](./ADR-000-common-evaluation-criteria.md) |
| Depends on | [ADR-001](./ADR-001-event-backbone-message-broker.md) |

---

## Context

The integration series describes a hybrid saga model (document 00): orchestration for complex multi-step flows (constitution, early mobilization, maturity renewal), choreography for side-effect fan-out. The orchestrated flows require a mechanism that:

- Maintains saga state across crashes and restarts
- Drives compensations as first-class business operations
- Handles long-running waits (hours to days, e.g. manual workflow approval)
- Coordinates parallel and sequential steps with retry and timeout semantics
- Integrates with the Redpanda event backbone (ADR-001) and the application database

Document 05 (Constitution Saga Walkthrough) illustrates one saga in one application — `ConstitutionProcess` — with explicit business states (`STARTED`, `PARALLEL_VALIDATION`, `VALIDATIONS_COMPLETE`, `APPROVED`, `COMPENSATE_VALIDATIONS`, `COMPENSATE_POST_DEBIT`, `HUMAN_INTERVENTION_REQUIRED`, `AWAIT_CORE_CLEARANCE`, `AWAIT_WORKFLOW_APPROVAL`, `COMPLETED`, `CANCELLED`). This is one example; the ecosystem described in document 00 will have many sagas across many applications (term deposits, loans, savings products). The orchestrator choice must be evaluated for that general case, not just for one illustrated flow.

**Candidates evaluated:**

| # | Candidate | Notes |
|---|---|---|
| A | **Temporal** | Durable execution engine; workflow-as-code; MIT licence |
| B | **Conductor-OSS** | JSON DSL state machine; Apache 2.0; community fork of Netflix Conductor |
| C | **Axon Framework** | Java CQRS/ES/Saga framework; Apache 2.0 |
| D | **Event-driven application orchestrator** | Application-level state machine using Redpanda + application database; no additional tool |

---

## Evaluation

### Hard filter results

#### F1 · Cost / licensing

| Candidate | Licence | Assessment | Proceeds? |
|---|---|---|---|
| Temporal | MIT (server), Apache 2.0 (SDKs) | Open source; self-hosted | **Pass** |
| Conductor-OSS | Apache 2.0 | Community fork; self-hosted | **Pass** |
| Axon Framework | Apache 2.0 (framework); Axon Server SE is proprietary freeware | The framework itself is open source and can run without Axon Server using JDBC-backed saga and event stores. Axon Server Standard Edition (free) is a proprietary binary with a feature ceiling below the Enterprise tier. This ADR evaluates Axon in the JDBC-only (fully open source) configuration. | **Pass** (JDBC-only configuration; Axon Server SE paywalled features flagged) |
| Event-driven orchestrator | N/A — no additional tool | Uses Redpanda (Apache 2.0, ADR-001) and the application database already required by the domain | **Pass** |

*Date of licence assessment: 2026-05-17. Licence terms can change; verify before production hardening.*

#### F2 · Regulatory fit

The critical regulatory consideration for saga orchestrators is where saga state and workflow inputs/outputs are stored, and whether that storage introduces a new GDPR surface beyond the application database.

| Candidate | GDPR | DORA | PSD2 | Proceeds? |
|---|---|---|---|---|
| Temporal | Workflow history stores all workflow arguments and activity return values in plain text by default. For a constitution saga, workflow inputs include `client_id`, IBAN, amount — PII by GDPR definition. Temporal v1.20+ provides a custom data converter API for payload encryption, but this requires application-layer key management in addition to Temporal's own PostgreSQL-backed storage. The history store must be included in the GDPR key rotation and erasure protocol. | Self-hosted PostgreSQL-backed Temporal can document RTO/RPO from PostgreSQL guarantees. Resilience testing is under operator control. | Temporal history provides an ordered, durable audit trail of all workflow executions. Strong PSD2 fit. | **Pass** (GDPR note: custom data converter required for PII in workflow inputs) |
| Conductor-OSS | Task inputs and outputs are stored in the Conductor backend (Redis or Elasticsearch). Same PII surface as Temporal — any task input or output containing client data must be masked or encrypted at the application layer. Achievable, but adds complexity not present for the application-database-native approaches. | Resilience depends on the chosen backend (Redis Sentinel or Elasticsearch HA). Operator-controlled. DORA requirements documentable. | Task execution history provides an ordered audit trail. | **Pass** (GDPR note: PII masking or encryption required for task payloads) |
| Axon Framework (JDBC) | Saga state persisted in the application PostgreSQL database — the same database already subject to the domain GDPR handling strategy. No additional GDPR surface. Events in the Axon event store must comply with the same tombstone/compaction discipline as Redpanda topics (ADR-001). | JVM application; resilience testing under operator control. RTO/RPO from PostgreSQL guarantees. DORA-compatible. | Domain events in the Axon event store provide a strong audit trail. | **Pass** |
| Event-driven orchestrator | No additional GDPR surface. `ConstitutionProcess` aggregate is stored in the application database alongside domain state — already subject to the domain GDPR handling strategy. Events on Redpanda topics are governed by ADR-001 and ADR-002. | No infrastructure beyond Redpanda (ADR-001) and the application database. DORA resilience testing targets both. | `ConstitutionProcess` state transitions are persisted in the application database and published as domain events on Redpanda — full audit trail. | **Pass** |

All four candidates pass both hard filters.

---

### Soft criteria

#### Temporal

**S1 · Operational complexity:** Self-hosted Temporal requires the Temporal Server — multiple services in production (Frontend, History, Matching, Worker, Internal Frontend), though collapsible to a single process for development (`temporal server start-dev`) — and a PostgreSQL database for workflow history and visibility state. For a 1–2 person team already operating Redpanda and an application database, Temporal introduces a second persistence layer and a new service with its own operational, monitoring, and upgrade surface. The developer experience is polished; the production operational surface is non-trivial.

**S2 · Ecosystem coherence:** Temporal's workflow-as-code model is powerful: sagas are expressed as Go, Java, Python, or TypeScript functions, with activities representing individual saga steps. Retries, timeouts, and long-running pauses are first-class language constructs. However, Temporal's native communication model is **synchronous activity calls** — the workflow calls an activity and awaits its return. Integrating with a Redpanda event backbone requires building Kafka-consumer activities, so the saga is partially event-driven (via the backbone) and partially Temporal-signal-driven (within the workflow). This impedance mismatch means the orchestration model and the event model do not share a clean seam. OpenTelemetry instrumentation is available.

**S3 · Exit cost:** HIGH. Temporal owns workflow execution history in its own data store. The workflow-as-code SDK is tightly coupled to Temporal's runtime — activity functions, workflow continuations, and signal handlers all use Temporal-specific APIs. Migrating away requires extracting in-flight saga state from Temporal history and rewriting all orchestration logic without the SDK. The operations console must either query Temporal's API or maintain a separate projection of saga state derived from Temporal — neither of which is trivial.

**S4 · Community and longevity:** Temporal was spun out of Uber's Cadence project and has a strong, growing community with good VC backing and wide cloud-native adoption. The MIT licence protects the self-hosted edition. Single-vendor-controlled without foundation governance; longevity prospects are good but not foundation-anchored.

---

#### Conductor-OSS

**S1 · Operational complexity:** Conductor-OSS requires Elasticsearch (for workflow visibility and search) and a backend store (Redis in the typical configuration). This is the heaviest operational footprint of all candidates — more moving parts than Temporal. The original Netflix Conductor was designed for Netflix's operational scale and engineering depth; the community edition inherits this footprint without inheriting the ops team.

**S2 · Ecosystem coherence:** Conductor defines workflows as JSON DSLs. Workers (which execute tasks) are lightweight HTTP or message-queue consumers that Conductor coordinates. The model is language-agnostic and includes a Kafka publish/consume task type. However, the JSON DSL is insufficiently expressive for complex conditional compensation trees — the multi-path, stateful compensation logic described in Document 05 (retry with backoff, escalation to `HUMAN_INTERVENTION_REQUIRED`, two-movement reversal in Core) is significantly clearer in code than in nested JSON configuration. Compensation paths are a domain-modelling decision; Conductor forces them into a configuration artefact.

**S3 · Exit cost:** Moderate-high. Conductor workflow definitions are JSON in Conductor's own DSL, not portable to other orchestration systems. Workflow execution state lives in Elasticsearch and the backend store. Migration requires extracting definitions and re-implementing them in the replacement system.

**S4 · Community and longevity:** The original Netflix Conductor received reduced investment from Netflix from approximately 2023. Orkes (the commercial company) maintains conductor-oss as the community fork and offers Orkes Conductor (paid) as its commercial product. The community edition's trajectory depends on Orkes's commercial incentives, creating a non-trivial risk of features progressively moving to the paid tier. This is the most uncertain longevity profile of the four candidates.

---

#### Axon Framework (JDBC-backed)

**S1 · Operational complexity:** Axon Framework is a Java library, which reintroduces JVM as an orchestrator runtime dependency. ADR-001 identified JVM operational complexity — GC tuning, heap sizing, JVM version management — as a meaningful risk for a 1–2 person team, which is why Redpanda (C++) was chosen over Apache Kafka (JVM). Adding a JVM orchestration service reintroduces this risk for the component with the most complex state management in the system. The JDBC-backed configuration (without Axon Server) avoids the proprietary server but requires more manual wiring of event store, saga store, and command bus, with less community documentation than the Axon Server path.

**S2 · Ecosystem coherence:** Axon's saga model — Java classes annotated with `@Saga`, reacting to events via `@SagaEventHandler` — is conceptually aligned with the event-driven architecture in this series. However, Axon's native communication model (via the Axon Message Bus) competes with rather than integrates natively into Redpanda. The Axon Kafka Extension provides a bridge, but adds another adapter layer and its own configuration surface. The heavy Spring coupling (Spring Boot, Spring Data) creates framework-level opinions across the domain model.

**S3 · Exit cost:** HIGHEST. Axon's patterns deeply couple the application to the framework — `@Aggregate`, `@CommandHandler`, `@EventSourcingHandler`, `@Saga`, `@SagaEventHandler` annotations on domain classes. Migrating away from Axon would require rewriting domain aggregates, saga logic, and the event/command dispatch infrastructure simultaneously. The JVM constraint also limits the team's language options for the orchestrator service.

**S4 · Community and longevity:** AxonIQ (Dutch company) maintains Axon Framework (Apache 2.0). Community is strong within the Java CQRS/ES niche. The commercial Axon Server Enterprise tier funds AxonIQ's development. Longevity is good for Java shops; the JVM-only constraint limits relevance for polyglot or JVM-averse teams.

---

#### Event-driven application orchestrator

**S1 · Operational complexity:** No additional infrastructure. The orchestrator is a dedicated service (or a module within an existing service) that: subscribes to domain event topics on Redpanda; reads, updates, and persists the `ConstitutionProcess` aggregate in the application database; publishes command messages to Redpanda topics. All infrastructure was already committed to by prior ADRs. No new process to operate, no new database schema to manage, no new observability target to wire up independently.

**S2 · Ecosystem coherence:** Maximum coherence. The orchestrator speaks the same language as every other service in the stack: Redpanda topics, Avro-serialized messages, application database, outbox pattern, inbox idempotency, correlation and causation IDs. Saga state is a table in the application database — persisted, queryable, surfaced by the operations console API without a vendor adapter. Long-running waits (`AWAIT_WORKFLOW_APPROVAL`, `AWAIT_CORE_CLEARANCE`) are rows in a state column that survive crashes because the database is the source of truth. No communication model mismatch: the orchestrator is a Redpanda consumer like every other service. OpenTelemetry instrumentation is uniform across the stack.

**S3 · Exit cost:** LOWEST. There is no external vendor state to migrate. The orchestration logic is in the application code, modifiable with standard software practices. The "orchestrator" is a service, not a framework, and its replacement or refactoring does not require extracting state from a vendor system.

**S4 · Community and longevity:** N/A — there is no external vendor. The approach depends on the team's own engineering, Redpanda (ADR-001), and the application database. The patterns involved (state machine, event-driven, outbox, inbox) are well-documented, widely practiced, and not proprietary.

**Where this approach requires more explicit implementation effort than the dedicated tools:**

- **Timeout scheduling:** timeouts (compliance hold expiry, clearance job check interval) must be implemented explicitly. The recommended mechanism: a `saga_timers` table in the application database (`id`, `process_id`, `fire_at`, `event_type`, `processed`), polled by a timer worker that publishes a "timer fired" event to Redpanda at the scheduled time. Alternatively, a delay-capable Redpanda topic or a lightweight scheduler library. Neither requires new infrastructure.
- **Retry with backoff:** consumer retry loops with exponential backoff must be built explicitly. This is standard Kafka consumer code — not a framework feature, but not novel work either.
- **Workflow introspection:** Temporal and Conductor provide out-of-the-box workflow search and visualization UIs. The custom approach exposes saga state through the `ConstitutionProcess` aggregate via the application's own API. Building this API is necessary regardless (Document 05 describes the operations console as a first-class security boundary), so the incremental effort is modest.

---

## Decision

**Chosen: Event-driven application orchestrator**

Temporal is the strongest rejected candidate and deserves an honest assessment of why it loses at this scale before the positive case for the custom approach is made.

Temporal would be the right call if: there are many saga types across many applications, the team grows beyond 2 people, and the cost of each new saga team reinventing timers and retry infrastructure outweighs Temporal's operational overhead. That is not the situation here. At POC scale with a 1–2 person team proving the patterns across a small number of saga types, the balance tips the other way for three concrete reasons.

**First, operational overhead is not yet amortized.** Temporal requires a dedicated server cluster and its own PostgreSQL schema — a second persistence tier on top of the application database. At large scale (many applications, many saga types), this cost is paid once and shared. At POC scale, it is paid in full for the benefit of a handful of sagas. The custom approach adds zero infrastructure.

**Second, the communication model mismatch is a real first-day cost.** This architecture's backbone is Redpanda. Temporal's native model is synchronous activity calls, not event-driven messaging. Combining them requires either bypassing Redpanda for saga coordination (Temporal calls activities directly, events are only published as side-effects at saga completion) or building a Signal-bridge that sits between Redpanda consumers and Temporal workflow signals. Neither option is free — one undermines the event backbone, the other adds plumbing. The custom approach has no mismatch: it is a Redpanda consumer like every other service.

**Third, the GDPR banking constraint is mandatory, not optional.** Temporal stores all workflow inputs and activity outputs in plain text in its history store. For any saga in this ecosystem, those inputs contain PII — client identifiers, IBANs, amounts. A custom data converter (Temporal's encryption plugin) is required from day one, before a single saga runs. This adds key management complexity that the custom approach does not introduce.

The custom approach is not a permanent answer. When saga count grows and the cost of each team owning its own timer and retry infrastructure becomes visible, introducing Temporal (or a similar durable-execution engine) is a clear upgrade path. The event-driven patterns proved here — state machine persisted in the application DB, commands on Redpanda topics, outbox for reliable publishing — translate directly into Temporal activities and signals. The migration is an addition, not a rewrite.

---

**Rejected: Temporal**

The operational overhead is not justified at POC scale: a second PostgreSQL schema and Temporal Server processes add meaningful complexity for a 1–2 person team. The synchronous activity-call model creates a genuine impedance mismatch with the Redpanda event backbone that requires either bypassing the backbone for saga coordination or building a Signal-bridge adapter. In a banking GDPR context, payload encryption via a custom data converter is mandatory from day one, not an optional hardening step. Temporal is the recommended upgrade path when saga count and team size make shared orchestration infrastructure worth its cost.

**Rejected: Conductor-OSS**

The JSON DSL is insufficiently expressive for the multi-path, stateful compensation trees described in Document 05. The operational footprint (Elasticsearch + backend store) is the heaviest of all candidates. The community longevity risk — trajectory dependent on Orkes's commercial incentives — is the highest of the four.

**Rejected: Axon Framework**

Reintroduces JVM operational complexity for the component with the most complex state management in the system, after ADR-001 deliberately eliminated the JVM by choosing Redpanda over Apache Kafka. The deep Spring and framework coupling (`@Aggregate`, `@Saga`, Axon Message Bus) creates the highest exit cost of any candidate and constrains the team's language choices for the orchestrator service.

---

## Consequences

**What this choice makes easier:**

- Saga state (e.g. `ConstitutionProcess`) lives in the application database — one place to persist, one place to query, one place to audit. The operations console reads directly from the application database without a vendor API.
- Long-running waits (`AWAIT_WORKFLOW_APPROVAL`, `AWAIT_CORE_CLEARANCE`) are first-class aggregate states persisted in the database. The saga resumes when the triggering event arrives from Redpanda, regardless of how long it waited. No external timer primitives to manage.
- Every observability, retry, and idempotency pattern in documents 04 and 06 applies directly to the orchestrator service — no separate instrumentation model, no new operational target.
- No new infrastructure to provision, monitor, or upgrade. The orchestrator is a service, not a platform. Temporal or a similar durable-execution engine is the natural upgrade path when saga count and team size make shared orchestration infrastructure worth its cost.

**What this choice makes harder or impossible:**

- **Timeout scheduling** requires explicit implementation: a `saga_timers` table polled by a timer worker, or equivalent. The worker must be treated as at-least-once (duplicate timer fires must be handled idempotently by the saga via inbox check). The timer worker requires its own liveness monitoring and alert.
- **Durable activity retries** with exponential backoff are the application's responsibility. Standard Redpanda consumer retry patterns handle this adequately, but there is no framework-provided abstraction.
- **Workflow introspection** is limited to what the application's own API exposes on `ConstitutionProcess`. There is no built-in workflow search or execution graph visualization. Ensure the operations console roadmap includes saga state visibility from day one, not as an afterthought.
- Each saga type (constitution, early mobilization, maturity renewal) must be implemented and tested independently. The orchestration engine's behavior is owned by the team.

**Residual risks:**

- **Concurrent writer race:** multiple instances of the orchestrator service can observe the same domain event and both attempt to drive the same saga forward. Mitigation: the saga state row must carry an optimistic-concurrency version field. The orchestrator must update the row with a `WHERE version = current_version` predicate; the losing writer retries by re-reading the current aggregate state. The database serializes writers; the inbox idempotency check ensures duplicate event processing is safe.
- **Timer worker as SPOF:** the `saga_timers` worker is a critical path for any saga that depends on timeouts. A crashed timer worker that fails to restart leaves affected sagas in a state they cannot escape without manual intervention or the timer worker's recovery. Deploy with liveness probes and alert on timer lag exceeding a defined threshold.
- **Observability gap:** without a dedicated orchestration UI, saga state is visible only via the application database and the saga API. Document 06's OpenTelemetry instrumentation must attach `process_id` and `correlation_id` as span attributes for every span emitted by the orchestrator, so saga execution is fully traceable in the distributed trace without needing a separate workflow visualization tool.

---

## Implementation Principles

Choosing a custom orchestrator means the team owns the orchestration infrastructure. Without deliberate constraints, each new saga will diverge: different state table shapes, different retry conventions, different timer implementations. The following principles define the minimum shared discipline required to keep the implementation reusable, minimalist, and coherent with the rest of this architecture.

---

### P1 — Separate shared infrastructure from saga-specific logic

There is a clear line between what belongs in a shared library or module (usable by every saga in every application) and what belongs in the saga itself.

**Shared infrastructure — build once:**
- Optimistic-concurrency state persistence: the pattern of `UPDATE saga_state SET state = ?, version = version + 1 WHERE id = ? AND version = ?`, with a retry loop on conflict.
- Inbox deduplication: the same deduplication table and check described in document 04, applied to saga event consumption.
- `saga_timers` table and worker: a single timer worker shared across all applications, polling for `fire_at <= NOW()` and publishing timer events to the relevant Redpanda topic.
- Outbox for commands: saga-emitted commands use the same outbox mechanism as all other services (document 04), not a separate publish path.

**Saga-specific — never shared:**
- The state enumeration and valid transition table for each saga type.
- The business compensation logic and its ordering.
- The specific events the saga consumes and the specific commands it emits.

Do not let saga-specific concerns leak into shared infrastructure, and do not copy the shared infrastructure into each saga. These are the only two failure modes.

---

### P2 — The state machine is the specification

Each saga must define its states and transitions as an explicit, inspectable data structure — not as implicit control flow buried in a sequence of `if` statements. A transition table of the form `(current_state, event_type) → (next_state, commands_to_emit)` serves as both the implementation and the documentation.

Any transition that is not in the table is rejected with an error, not silently ignored. This makes illegal state transitions impossible by construction and makes the saga's behavior auditable from the table alone, without reading the surrounding code.

---

### P3 — States model business reality, not technical mechanics

Saga states must be named for what the business situation is, not for what the system is doing internally. `AWAIT_WORKFLOW_APPROVAL` and `HUMAN_INTERVENTION_REQUIRED` are business states — an operator or manager can read them and understand what is happening. `RETRYING_COMPLIANCE_CALL` or `POLLING_CORE` are technical states that belong in the activity implementation, not in the saga's state machine.

This principle is what makes the operations console possible without a specialized orchestration UI: the saga state column in the database is directly meaningful to a human operator.

---

### P4 — Long waits are states, not blocked threads

A saga that must wait hours or days (workflow approval, Core clearance) must express that wait as a named state in the state machine and return control immediately. The saga resumes when the expected event arrives from Redpanda, not when a timer expires or a thread unblocks.

No saga may hold a thread, connection, or lock across an external wait. The infrastructure cost of a long-running saga is one row in the database, nothing else.

---

### P5 — Apply the reversibility-ordering principle from Primitive 6

The ordering principle documented in document 01 (Primitive 6) and demonstrated in document 05 is not optional for orchestrated sagas: reversible steps first, irreversible steps last. This is what makes partial failure recoverable. Any saga design that schedules an irreversible effect before all reversible preconditions have succeeded requires an explicit justification — it is an architectural exception, not a default.

---

### P6 — Compensation is always a domain action, never a technical rollback

Compensations are modelled as explicit saga states (`COMPENSATE_VALIDATIONS`, `COMPENSATE_POST_DEBIT`) and emit domain commands (`ReleaseBalanceReservation`, `ReverseCoreDebit`). They are never implemented as database transaction rollbacks or silent no-ops. The reason: compensation in a distributed saga is itself a business operation with its own failure modes, its own retry logic, and its own escalation path. A compensation that fails must produce an `INDETERMINATE` or `HUMAN_INTERVENTION_REQUIRED` state, not a swallowed exception.

---

### P7 — Carry the identity trio on every message

Every command and event emitted by the orchestrator must carry the full identity trio from document 01 (Primitive 4): `correlation_id` (unchanged from the originating request), `causation_id` (the `message_id` of the event that triggered this emission), and a new `message_id`. This is what makes saga execution traceable as a single chain in the distributed trace without requiring a dedicated orchestration UI.
