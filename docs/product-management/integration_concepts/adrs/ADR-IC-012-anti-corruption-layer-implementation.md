# ADR-IC-012: Anti-Corruption Layer Implementation Approach

| Field | Value |
|---|---|
| Status | Accepted |
| Date | 2026-05-18 |
| Deciders | jhosm |
| Common criteria | [ADR-IC-000](./ADR-IC-000-common-evaluation-criteria.md) |
| Depends on | [ADR-IC-001](./ADR-IC-001-event-backbone-message-broker.md), [ADR-IC-003](./ADR-IC-003-saga-orchestrator.md), [ADR-IC-004](./ADR-IC-004-outbox-pattern-mechanism.md), [ADR-IC-005](./ADR-IC-005-cqrs-read-model-storage.md) |

---

## Context

[Document 02](../02-anti-corruption-layer.md) names the ACL as the only place where translation between the domain and Core Banking is permitted, lists its eight concrete responsibilities (semantic translation, protocol translation, adapted idempotency, ID mapping, error translation, latency adaptation, periodic reconciliation, authentication to Core), and prescribes its internal pieces (port, translator, protocol client, state store, reconciler). It also names the antipatterns — wrapper-that-doesn't-translate, business logic in the ACL, ACL shared across consumers, stateless ACL, error-hiding ACL — and the indeterminate-state protocol that separates a robust ACL from a naive one.

What document 02 leaves open is how to *materialise* those responsibilities. Three classes of decision remain:

1. **Where does the ACL run?** As a library in the Deposits service, as a sidecar, or as its own service.
2. **How does it talk to the Core?** What outbound protocol client; what fallback when the Core uses MQ or batch.
3. **How does the Core talk back?** Webhook callbacks, ACL polling, or an MQ bridge — the answer depends on the Core's capabilities, but the ACL design must accommodate all three without rewriting the translator each time.

Two further decisions are downstream of (1)–(3): where to place circuit breakers and bulkheads, and how the ACL's state store relates to the domain outbox ([ADR-IC-004](./ADR-IC-004-outbox-pattern-mechanism.md)).

### What this ADR decides

| # | Decision | Options evaluated |
|---|---|---|
| D1 | **Deployment topology** | In-process library; sidecar; dedicated service |
| D2 | **Outbound adapter pattern** | Hand-rolled per-operation client; EIP framework (Apache Camel / Spring Integration) |
| D3 | **Inbound trigger from Core** | Webhook-only; polling-only; MQ-bridge-only; pluggable adapter for all three |
| D4 | **Failure isolation placement** | Single circuit breaker at the port; per-adapter circuit breaker + bulkhead |
| D5 | **ACL state and outbox relationship** | Share the domain database; own database per ACL with its own outbox |

The decisions are coupled. D1 (dedicated service) is what makes D5 (own database) possible. D3 (pluggable inbound adapters) only matters if the ACL has a stable internal "Core event" abstraction, which D1 makes easier to enforce. D2 narrows once D1 is settled. D4 is the only decision whose outcome is largely independent of the others.

### Scope boundary

This ADR covers the implementation pattern of the ACL that protects the Deposits domain from a legacy Core Banking system. It does not cover:

- **The specific Core vendor's API surface.** This ADR assumes the Core may speak SOAP/XML, REST, MQ, or batch files (often in combination) and the ACL must absorb whichever applies. Specific WSDLs, queue names, and operation codes belong in the ACL's implementation repository.
- **Authentication to Core Banking.** Document 02 §8 already establishes the rules (dedicated service account, secrets manager, separate identity for reconciliation, mTLS to the saga). This ADR honours those rules but does not redecide them.
- **Reconciliation job scheduling and tooling.** A separate decision point; document 02 §7 prescribes the discipline, but the cron mechanism, batch query shape, and divergence triage workflow are deferred to a future ADR if reconciliation surfaces tool-selection questions.

---

## Evaluation

### D1 — Deployment topology

**Option A: In-process library** consumed by the Deposits service directly. The ACL is a module: port, translator, protocol client, and state-store DAO compiled into the same binary as the saga orchestrator and the aggregates. Lowest operational footprint; one process to deploy, one set of logs, one set of metrics.

**Option B: Sidecar** — a separate process running on the same pod (or host) as the Deposits service, communicating via localhost over HTTP or a Unix domain socket. The ACL has its own runtime independent of the application language and can be updated without redeploying the application. Common pattern for service-mesh proxies; uncommon for stateful adapters.

**Option C: Dedicated service** in its own deployment unit, called by the saga orchestrator over the network (REST or gRPC) with mTLS. The ACL has its own scaling, its own database, its own service-account identity, and its own failure domain.

**Chosen: Option C — dedicated ACL service.**

Three forces push past the operational simplicity of the library mode.

**First, document 02 §8 requires identity separation that a library cannot provide.** The ACL holds the most privileged credentials in the system — it can instruct the Core to move real money. The saga orchestrator authenticates *to* the ACL via mTLS; the ACL authenticates to the Core with a dedicated service account; the reconciliation job has a distinct identity from the write path. A library-mode ACL collapses all three into the saga orchestrator's process and identity. The transport-layer enforcement that "no other service — even one with network access — can instruct the ACL to move money" disappears, because there is no transport layer between them. Document 10's Boundary 9 (and equivalent boundaries for Core access) require a process-level break. A library cannot create one.

**Second, the ACL needs its own database for its own state.** Document 02 lists idempotency keys, ID mappings, in-flight operations, and the indeterminate-state dead-letter as ACL-owned state. If the ACL is a library in the domain process, it either shares the domain's database (mixing two aggregates' state in one schema, violating the bounded-context property that ADR-IC-004 P6 makes explicit) or maintains a separate connection to a separate database from the same process (which is the dedicated-service pattern with extra steps and no isolation). Separating the runtime is the cleaner expression.

**Third, the reading test from document 02 — "if the Core vendor were replaced tomorrow, how many files would change?" — is structurally enforced by a deployment boundary.** In a library mode, the discipline of keeping Core types out of domain code is a code-review obligation. In a dedicated-service mode, the only types that cross the boundary are the domain types the saga sends and the domain types the ACL emits — Core types cannot leak because they are in a different process with a different repository.

**Why not sidecar (Option B)?** A sidecar adds a process per pod without adding the identity or database separation that drives Option C. It is useful when the application language cannot host the adapter (e.g., the application is in a niche runtime and the Core SDK is JVM-only). At POC scale with no such constraint, it is the worst of both worlds: a separate process to operate, but co-located with the application — meaning a misbehaving sidecar still affects the application pod's resource budget, and the identity boundary is weaker than C.

**Why not library (Option A)?** Rejected for the three reasons above. The operational saving is real (one fewer deployment unit) but is paid for at the boundaries the architecture commits to defend.

**Scope of "one ACL service":** one ACL service per bounded context that consumes the Core. The Deposits ACL is *not* shared with CRM, Compliance, or any other consumer — document 02 names "ACL shared across multiple consumers" as an antipattern. If other contexts in this architecture later need Core access, each gets its own ACL deployment. This is the right kind of duplication.

---

### D2 — Outbound adapter pattern

**Option A: Hand-rolled per-operation client.** For each Core operation the domain needs, the ACL has a thin client method that builds the request payload (SOAP envelope, REST body, MQ message, batch line), sends it, parses the response, and returns a domain-shaped result. WSDL-to-stubs code generation may produce the request/response classes, but the orchestration code is hand-written and lives in the translator.

**Option B: Enterprise Integration Patterns framework** — Apache Camel, Spring Integration, or equivalent. The ACL is expressed as a set of EIP routes (from, to, transform, choice, aggregate) backed by component connectors for SOAP, MQ, JDBC, FTP. Routes are declarative; transformations are pluggable processors.

**Chosen: Option A — hand-rolled per-operation client.**

The EIP framework approach is genuinely useful when an integration involves dozens of disparate Core operations, complex content-based routing across multiple back-ends, or a steady evolution of transformation rules driven by non-engineers. None of these conditions hold for the Deposits ACL at POC scale or at realistic Portuguese-bank scale:

- **Operation count is small.** A term deposit's Core surface is roughly: constitute (open the deposit), reverse-constitute (compensation), interest-payment, early-mobilisation (partial or full), maturity-payout. Five to ten operations, each with one outbound shape and one async confirmation shape. A framework's declarative routing is overkill for a problem of this size.
- **The Core is a single back-end.** EIP shines in scenarios where one inbound message fans out to multiple back-ends with content-based routing. The ACL talks to one Core. There is no routing problem to solve.
- **The transformation rules are not configuration-grade.** Each Core operation has subtle semantic translations (e.g., a `Deposit` constitution becomes N accounting movements; an `EarlyMobilization` becomes a reversal plus interest adjustment plus release). Expressing these as Camel processors trades a clear function in the host language for opaque XML or DSL configuration that is harder to test, debug, and review.

The hand-rolled client is also strictly inside the ADR-IC-000 budget: no additional dependency beyond the SOAP/REST client library the runtime ships with, and the application's existing test harness covers it.

The translator's structure is small enough to enumerate in the implementation principles below. The frame is: one function per Core operation, each function with three parts (build request, send, parse response into domain result). The framework saves nothing here.

**Where this approach requires explicit discipline:**

- **WSDL-to-stub generation:** if the Core exposes SOAP, the request/response types are generated from the WSDL at build time. The generated types are confined to the protocol-client layer and never cross into the translator's domain-facing API. The translator imports domain types only; it depends on stub types only inside the protocol-client module.
- **Error catalogue:** the error translation table (document 02 §5) is hand-maintained code in the translator, not configuration. Each Core error code maps to one of three domain error categories: recoverable business (`InsufficientBalance`, `LimitExceeded`), non-recoverable business (`AccountBlocked`, `ProductRetired`), and transient technical (`CoreUnavailable`, `Timeout`). Unknown codes default to "non-recoverable, escalate to human review" — the failure mode that errs on the side of refusing to retry an ambiguous state.
- **WSDL versioning:** when the Core publishes a new WSDL version, the regeneration step is part of an explicit ACL release, not a build-system auto-update. The ACL's pinned WSDL hash is part of its release notes.

---

### D3 — Inbound trigger from Core

Document 02 §6 (Latency Adaptation) names three concrete mechanisms by which the Core can confirm an asynchronous operation: **webhook callback** (Core POSTs to the ACL), **polling** (ACL queries the Core periodically by external reference), and **MQ / CDC bridge** (the ACL consumes from a Core-owned queue or change stream and translates to domain events). The Core's capabilities determine which is available — this is an external constraint, not a choice the architecture makes.

The decision is therefore not "which one" but **how the ACL is structured so any of the three can be the active mechanism without changing the translator or the saga**.

**Option A: Webhook-only design.** Assume the Core can POST to an HTTPS endpoint the ACL exposes. Simplest single-mechanism implementation. Fails the day the architecture meets a Core that only supports MQ.

**Option B: Polling-only design.** Assume the Core supports a "query by external reference" API and the ACL polls. Universal compatibility (any Core can be queried) but the highest latency and the most expensive at scale.

**Option C: MQ-bridge-only design.** Assume the Core writes to an IBM MQ (or equivalent) queue the ACL consumes. Common in older Portuguese banking Cores but a hard dependency on a specific Core capability.

**Option D: Pluggable inbound adapter** — the ACL defines a single internal `CoreInboundEvent` abstraction. Three adapter implementations (webhook receiver, poller, MQ consumer) all produce events of this shape. The translator and saga see only the abstraction, never the wire format. The deployed adapter is selected by configuration based on what the target Core supports.

**Chosen: Option D — pluggable inbound adapter.**

The reasoning is simple: in Portuguese banking, the Core's inbound mechanism is rarely a single one even within one bank. A modern Core may support webhooks for some operations and MQ for others; a Core in the middle of a vendor migration may have both old and new mechanisms active simultaneously; reconciliation (document 02 §7) is essentially a scheduled poll regardless of which mechanism is primary. Designing for a single mechanism forces a rewrite at the first encounter with reality.

The cost of the pluggable design is one internal interface and three small adapter implementations — far smaller than the cost of any rewrite. The cost is paid once at ACL bootstrap and amortised across every operation thereafter.

**The abstraction:**

```
CoreInboundEvent {
  external_reference      // TD-{deposit_id} or equivalent
  core_correlation_id     // Core's own trace ID, for audit chain
  operation               // "ConstitutionDebitConfirmed" | "ReversalConfirmed" | ...
  outcome                 // success | business_error | technical_error
  core_payload            // raw fields for the translator
  received_at             // when the adapter materialised it
}
```

The webhook adapter populates this from an HTTP request body; the poller populates it from a Core query result; the MQ adapter populates it from a queue message. The translator consumes the abstraction, looks up the matching in-flight operation by `external_reference`, applies the error-translation table to `outcome`, and emits a domain event (e.g., `DebitConfirmedInCore`) to the ACL's own outbox.

**Adapter-specific concerns (paid by the adapter, not the translator):**

- **Webhook adapter:** authentication of the Core (mTLS client certificate validation; the Core's certificate is pinned at the ACL's TLS terminator); idempotency at the HTTP level (the Core may retry; the adapter dedupes by Core-supplied delivery key before invoking the translator); replay-resistance (signed timestamp if the Core supports it; otherwise documented as a residual risk).
- **Poller adapter:** the polling schedule (typically aligned with the Core's processing cycle — every few minutes during the day, more aggressive around the daily settlement window); a bounded query (`SELECT … WHERE external_reference IN (…in-flight set…)`) rather than a "since" cursor (avoids missing operations that change status retroactively); the in-flight set is read from the ACL state store (see D5).
- **MQ adapter:** consumer group identity (one consumer per ACL instance is allowed — partition-equivalent semantics in IBM MQ are by queue, not by group); poison-message handling (a message the translator cannot parse goes to a dead-letter queue, not back into the input queue, to prevent head-of-line blocking).

All three adapters share the same back-end: the translator and the ACL's outbox.

---

### D4 — Failure isolation placement

The ACL has three distinct outbound dependencies (the Core for commands, the Core for inbound events, the saga orchestrator that calls in) and one local dependency (the ACL's own database). Each can fail independently.

**Option A: Single circuit breaker at the domain-facing port.** One breaker around all calls into the ACL. When tripped, every Core operation returns "ACL unavailable" to the saga.

**Option B: Per-adapter circuit breaker + bulkheaded thread pools.** Separate circuit breakers on (i) outbound Core client, (ii) inbound poller (the poller, not the webhook receiver — a slow Core that times out queries needs a breaker; a webhook receiver that gets no events needs a different signal), (iii) ACL state-store. Each adapter runs on its own bounded thread pool (or equivalent concurrency primitive in non-JVM runtimes); a saturated outbound Core call cannot starve the inbound adapter or the state-store writes.

**Chosen: Option B — per-adapter circuit breaker + bulkhead.**

A single port-level breaker has the wrong granularity. The most common ACL failure mode is "the Core is slow on writes" — a circuit breaker that trips on the outbound write path should not also block the inbound adapter from processing confirmations of operations already in flight. Conversely, "the Core inbound queue is backed up" should not block the saga from initiating new operations, because the new operations may be against Core paths that are healthy.

Bulkheading is the structural defence: if the outbound pool exhausts, the inbound pool continues; if the inbound pool exhausts, the reconciler pool continues. Each is sized to its expected load with a generous ceiling, so a runaway component cannot consume the others' capacity.

**Placement:**

| Adapter | Breaker | Bulkhead | Fallback |
|---|---|---|---|
| Outbound Core client (D2) | Yes — open after sustained timeout or `5xx` rate | Yes — bounded HTTP/SOAP client pool | Return `CoreUnavailable` to the saga; saga enters its own backoff state |
| Inbound poller (D3) | Yes — open after sustained timeout | Yes — separate pool from outbound | Skip this poll cycle; next cycle retries; alert if breaker stays open beyond N minutes |
| Inbound webhook receiver (D3) | No — receiving incoming requests; breaker does not apply | Yes — bounded receiver pool | HTTP `503` to the Core if pool exhausted; Core retries per its own schedule |
| Inbound MQ adapter (D3) | No — consumer with explicit ack; breaker does not apply | Yes — bounded consumer pool | Stop consuming; messages accumulate in Core's queue (Core-side concern); alert via consumer-lag |
| ACL state store | No — local DB; failure is fatal to all operations | N/A — implicit in the database connection pool | Health check fails; the ACL stops accepting saga calls until DB returns |
| Reconciler (document 02 §7) | Yes — its own breaker; isolated from runtime path | Yes — own pool, scheduled, not request-driven | Skip this reconciliation cycle; alert if breaker stays open across two scheduled cycles |

**Library choice is deferred to the host runtime.** This ADR specifies the placement; the implementation picks the idiomatic resilience library (Resilience4j on the JVM; Polly on .NET; semaphore-based isolation in Go or Node). All three families are open-source and inside the ADR-IC-000 budget. The principle that matters is "one breaker and one bulkhead per failure-class adapter," not which library expresses it.

---

### D5 — ACL state and the outbox relationship

The ACL has substantial state — document 02 lists idempotency keys (per Core operation), ID mappings (`deposit_id ↔ core_txn_id`, persistent), in-flight operations (with their `IN_FLIGHT`/`INDETERMINATE`/`CONFIRMED` lifecycle), and a dead-letter for ambiguous outcomes. The question is where this state lives in relation to the domain's outbox (ADR-IC-004).

**Option A: ACL state in the domain database.** The ACL service writes its state to the same PostgreSQL instance the Deposits domain uses (different schema, same database). The ACL's "Core confirmed" outbox row and the ACL's state mutation can be in one transaction. But the ACL service no longer owns its database — every schema migration, every replication tuning decision, every backup strategy is shared with the domain.

**Option B: ACL state in its own database.** The ACL service owns a PostgreSQL instance. ACL state mutations and ACL outbox writes are in one local transaction (the outbox pattern from [document 04](../04-plumbing-patterns.md) and ADR-IC-004 applies inside the ACL exactly as it applies inside the domain). The ACL publishes confirmation events to Redpanda; the domain consumes them through the standard inbox pattern.

**Chosen: Option B — ACL owns its database; events cross via Redpanda.**

Option A collapses the bounded-context boundary the dedicated-service decision (D1) was meant to enforce. If the ACL writes to the same database as the domain, the deployment boundary is real but the data boundary is leaky — schema changes coordinate, foreign keys may form, query patterns drift across the seam. The architectural intent of D1 is undercut.

Option B costs one more PostgreSQL instance (still within the ADR-IC-000 budget — PostgreSQL is open source and a small instance is unremarkable to operate). In exchange, every ACL responsibility from document 02 is local: idempotency keys, ID mappings, and the in-flight state machine are read and written in the same database transaction as the outbox row that publishes the domain-visible event. ADR-IC-004's invariants apply unchanged.

**Flow on a confirmed Core operation:**

1. Inbound adapter (webhook / poller / MQ — D3) constructs a `CoreInboundEvent` and hands it to the translator.
2. Translator looks up the matching in-flight operation by `external_reference` in the ACL state store.
3. Translator applies the error-translation table to `outcome`; produces a domain event (e.g., `DebitConfirmedInCore` with `correlation_id` and `causation_id` from the originating saga step).
4. In one local transaction: state-store row transitions `IN_FLIGHT → CONFIRMED`; ID mapping written if the Core returned a new reference; outbox row inserted with the domain event payload.
5. The ACL's outbox publisher (ADR-IC-004's custom polling publisher) picks up the row and publishes to Redpanda.
6. The saga consumes the event via its own inbox (Primitive 5 from [document 01](../01-the-six-primitives.md)) and advances.

**Indeterminate-state flow** (the case document 02 highlights):

1. Outbound adapter (D2) sends a debit to the Core; before sending, writes `(idempotency_key, status=IN_FLIGHT)` to the state store in its own transaction.
2. Network drops; the call times out.
3. The state store row is updated to `status=INDETERMINATE`. No immediate retry. No outbox event yet — the saga is not told the operation succeeded or failed.
4. A clearance task (separate scheduled job within the ACL) queries the Core by `external_reference` to find out whether the operation actually executed. (This reuses the poller adapter's code from D3 with a different trigger — it is not a fresh poll loop.)
5. On clearance: if the Core has the operation, transition to `CONFIRMED` and publish the event via the outbox path above. If the Core does not have the operation, transition to `RETRY_PERMITTED` and let the saga's compensation logic decide whether to reissue (with the same `idempotency_key`).
6. While in `INDETERMINATE`, the saga's view of the step is `AwaitCoreClearance` — published from the ACL's outbox the moment the state moves out of `IN_FLIGHT`. The saga does not see `INDETERMINATE` as a silent state; it is a modelled step.

The outbox is the only mechanism by which ACL state changes become visible to the domain. The saga never reads the ACL's database directly; the ACL never writes to the saga's database directly. Redpanda is the single seam.

---

## Decision

Summary of all five choices:

| Decision | Chosen |
|---|---|
| D1 — Deployment topology | Dedicated ACL service per bounded context (Deposits ACL is not shared); identity separation from the saga at the mTLS boundary |
| D2 — Outbound adapter | Hand-rolled per-operation client; WSDL-to-stub code generation for SOAP only; error catalogue is hand-maintained code |
| D3 — Inbound trigger | Pluggable adapter (webhook, polling, MQ) behind a single `CoreInboundEvent` abstraction; the translator is adapter-agnostic |
| D4 — Failure isolation | Per-adapter circuit breaker and bounded bulkhead pool; outbound, inbound poller, and reconciler are isolated failure classes; library choice deferred to runtime |
| D5 — State and outbox | ACL owns its own PostgreSQL database; ACL outbox publishes domain events to Redpanda; ADR-IC-004's invariants apply inside the ACL unchanged |

---

## Consequences

**What this choice makes easier:**

- The reading test from document 02 — "if the Core vendor were replaced tomorrow, how many files would change?" — is structurally enforceable. The deployment boundary, the database boundary, and the event-payload boundary all line up; Core-vendor types do not exist outside the ACL service repository.
- The ACL is independently deployable, scalable, and operable. A change to the SOAP wire format requires an ACL release; the domain is unaffected. A schema migration in the ACL's state store is invisible to the domain.
- Failure isolation is granular. A slow Core write path does not block inbound confirmations; a stalled inbound queue does not block new saga steps; a misbehaving reconciler does not consume capacity meant for the runtime path.
- The pluggable inbound adapter pattern absorbs Core-vendor heterogeneity without translator changes. A Core migration that swaps webhooks for an MQ bridge changes one adapter implementation, not the translator or the saga.

**What this choice makes harder or impossible:**

- **A second runtime to operate.** The ACL is its own service with its own database, its own deployment pipeline, its own observability configuration, and its own on-call surface. For a 1–2 person team this is not free; it is the operational cost of the boundary.
- **Cross-aggregate atomicity with the domain is impossible.** The ACL and the domain are separate transactional units. Any consistency between them is eventual, mediated by Redpanda. This is the correct architecture, but it forecloses any temptation to "just write to both tables in one transaction" when an operational shortcut would be welcome.
- **Latency between the saga and the Core grows by one network hop.** The saga calls the ACL (mTLS over network), the ACL calls the Core. Compared to a library-mode ACL, every operation pays an extra round-trip. At Portuguese banking volumes this is immaterial; at higher volumes the boundary holds and the trade-off is the right one.
- **Synchronous WSDL stubs concentrate runtime risk in the protocol-client module.** A breaking WSDL change from the Core breaks ACL builds before it reaches production. This is the intended behaviour — surface the change at build time, not at first error — but it requires the ACL release process to handle WSDL regeneration as a first-class step.

**Residual risks:**

- **State-store divergence from the Core.** The ACL's state store records what the ACL believes the Core has. The reconciler (document 02 §7) is the only mechanism that corrects divergence. The reconciler is mandatory; an ACL without an active reconciler is a partial implementation. The reconciler must run at least daily and must produce a divergence report even on a zero-divergence day (silence on the reconciler indicates the reconciler is not running, not that nothing diverges).
- **Webhook adapter SSRF / replay surface.** The webhook receiver is the only inbound HTTP endpoint of the ACL. mTLS-pinned to the Core is the structural defence; if the Core does not support mTLS, the ACL must require a signed delivery (HMAC with a Core-issued secret, see [ADR-IC-011](./ADR-IC-011-async-saga-completion-notification.md) D3) and reject anything else. A webhook adapter that accepts unauthenticated POSTs is unacceptable in banking.
- **Indeterminate-state backlog.** The `INDETERMINATE` queue can grow if the clearance task is slow. The architecture must alert on `INDETERMINATE` queue depth (a separate SLI from outbox lag); a sustained backlog means the clearance task or the Core's query interface is unhealthy, and the bank is sitting on operations whose Core-side reality is unknown. This is one of the most dangerous states the system can enter and must be observable.
- **ACL outbox lag.** Because the ACL has its own outbox (ADR-IC-004), it has its own `outbox_publish_lag_seconds` SLI. The same warning/critical thresholds from ADR-IC-004 P4 apply, scoped to the ACL's outbox.
- **Single-vendor Core lock-in is unaffected by this ADR.** A dedicated-service ACL with a clean translator does not make the Core easier to replace; it makes the *domain* easier to preserve when the Core is replaced. The Core itself remains a single point of dependency, mitigated only by the reconciler's audit trail and the ACL's structured error catalogue.

---

## Implementation Principles

The ACL is the most concentrated repository of cross-system risk in the architecture. The following principles define the minimum discipline. They are not optional refinements; an ACL implementation that violates them is not the ACL this ADR describes.

---

### P1 — One ACL service, one bounded context, one database

Each bounded context that consumes the Core owns its ACL. The Deposits ACL serves only the Deposits domain. If Compliance later needs Core access, it gets its own ACL deployment with its own database — not a multi-tenant ACL. This is the structural enforcement of document 02's "ACL Shared Across Multiple Consumers" antipattern.

The ACL's database is not shared with any other service. The schema includes: `idempotency_keys`, `id_mappings`, `in_flight_operations` (with the state machine described in D5), `inbound_event_dedup` (per-adapter delivery dedup), `outbox` (per ADR-IC-004 P1 columns), and `reconciliation_runs` (per-run divergence reports).

---

### P2 — Domain vocabulary on the inside; Core vocabulary only in the protocol-client module

The translator's public API speaks domain language: `debitForConstitution(deposit)`, `reverseConstitution(deposit_id)`, `payInterest(deposit_id, amount, value_date)`. Domain types in; domain results out. The fact that a `debitForConstitution` becomes N accounting movements at the Core is invisible to the saga.

Core types — WSDL-generated stubs, MQ message classes, REST DTOs — live only in the protocol-client module. They are not imported by the translator's public API and not imported anywhere in the saga, the aggregates, or any other service. This is enforced by module dependency rules (build-system level), not by code review alone.

---

### P3 — Every Core call carries `correlation_id`

Document 02 §8 already commits to this; this principle states the implementation form. Every outbound call from the ACL to the Core carries the originating saga's `correlation_id` in whichever field the Core's protocol supports (SOAP header, REST `X-Correlation-Id`, MQ message property, batch line metadata column). If the Core's protocol has no such field, the ACL records the `(core_request_id, correlation_id)` pair in its own state store at send time, so the ACL's logs alone form a cross-boundary audit trail.

Inbound `CoreInboundEvent` records the Core's `core_correlation_id` (or equivalent) for the same audit purpose. The translator joins the two at confirmation time and propagates `correlation_id` into the domain event published via the outbox.

---

### P4 — Idempotency is per Core operation, not per saga

The ACL's idempotency key is *the Core's idempotency contract*, not the saga's. A retry from the saga (same saga step, same logical operation) must produce the same idempotency key at the ACL boundary so that the Core sees one operation regardless of how many times the saga retried. The key is derived deterministically from `(operation_type, saga_step_id, external_reference)` — stable across retries, unique per operation.

The `idempotency_keys` table stores `(key, core_reference, status, first_seen_at, last_seen_at)`. A second send with the same key returns the recorded `core_reference` without contacting the Core. This is what makes the Core *appear* idempotent to the domain (document 02 §3).

---

### P5 — `INDETERMINATE` is a first-class state, not an error

The state machine for an in-flight operation is `IN_FLIGHT → CONFIRMED | INDETERMINATE | REJECTED`. From `INDETERMINATE`, the clearance task transitions to `CONFIRMED` or `RETRY_PERMITTED`. The ACL never silently retries an `INDETERMINATE` operation; it queries the Core for ground truth first. The saga sees `INDETERMINATE` as the domain event `CoreOperationAwaitingClearance` and parks itself in `AwaitCoreClearance` — a modelled state, not a stuck step.

A retry against an `INDETERMINATE` operation without clearance is the canonical "double debit" bug. The state machine prevents it by construction: outbound D2 refuses to send if an in-flight row with the same `idempotency_key` exists in any state other than `RETRY_PERMITTED`.

---

### P6 — Outbox is local; cross-service consistency is via Redpanda

The ACL's outbox (per ADR-IC-004) is in the ACL's own database. The saga's outbox is in the saga's own database. Neither service writes to the other's database. Confirmed Core operations cross the boundary as Redpanda events; saga commands cross the boundary as synchronous mTLS calls into the ACL's port.

This is the standard ADR-IC-004 invariant scoped to the ACL: the outbox write and the state-store mutation are in the same local transaction, ensuring an operation cannot be recorded as confirmed without the corresponding domain event being durably queued for publication.

---

### P7 — The reconciler is mandatory and self-evidencing

A reconciliation job runs at least daily, aligned with the Core's settlement cycle. It produces a `reconciliation_runs` row on every execution — even on zero-divergence days. Divergences become exception records that block the next day's processing for the affected operation until investigated.

Three classes of divergence the reconciler must detect:

1. **ACL says confirmed, Core has no record.** The most dangerous — the domain has been told an operation succeeded that the Core never executed. Critical alert; manual reissue or compensation required.
2. **Core has the operation, ACL has it as `INDETERMINATE`.** Clearance has fallen behind. The reconciler transitions the row to `CONFIRMED` and emits the deferred event.
3. **Core balances diverge from the domain's expected balances aggregated across confirmed operations.** Long-tail data drift. Daily-scoped alerts; investigation queue.

A silent reconciler is a broken reconciler. The reconciliation job's heartbeat (one record per run, success or failure) is monitored at the same severity as the outbox lag SLI.

---

### P8 — Circuit-breaker and bulkhead boundaries are visible in the topology

The deployment manifest (Helm chart, Compose file, or equivalent) names the bulkhead pools explicitly: `outbound-core-pool`, `inbound-poller-pool`, `inbound-webhook-pool`, `inbound-mq-pool`, `reconciler-pool`. The configuration is part of the ACL's release notes, not buried in code. Each pool has a documented size and a documented saturation alarm.

This is a documentation principle as much as a code principle: a reader of the ACL deployment should be able to see, without reading code, where the failure-isolation seams are. An ACL whose bulkhead structure is implicit in thread-pool defaults has the wrong shape, even if it works.

---

## Verifiable commitments

This decision's load-bearing commitments are fitness functions in the [commitment catalogue](../../product_concepts/adrs/commitment-catalogue.md) — the single source of truth for each commitment's exact claim, gate (pyramid level), and `Live`/`Planned`/`Gap` status ([ADR-PC-020 §P5–§P7](../../product_concepts/adrs/ADR-PC-020-llm-toolchain-and-conformance-governance.md)):

- The ACL idempotency key is derived deterministically from `(operation_type, saga_step_id, external_reference)`, stable across saga retries; a second send with the same key returns the recorded `core_reference` from the `idempotency_keys` table without contacting the Core (§P4).
- Double-debit is prevented by construction: the outbound D2 client refuses to send when an in-flight row with the same idempotency key exists in any state other than `RETRY_PERMITTED`, so an `INDETERMINATE` operation is never silently re-issued without Core clearance (§P5).
- The ACL outbox write and the state-store mutation commit in one local transaction — the ADR-IC-004 invariant scoped to the ACL — so an operation cannot be recorded as confirmed without its domain event being durably queued for publication (§P6).
- Core types (WSDL stubs, MQ message classes, REST DTOs) are confined to the protocol-client module, enforced by build-system module-dependency rules rather than code review alone — an architecture/dependency-assertion gate analogous to `ENGINE_FAMILY_AGNOSTIC` (§P2).
- The reconciler is mandatory and self-evidencing: it writes a `reconciliation_runs` row on every execution, including zero-divergence days, so reconciler silence is a detectable fault rather than a clean result (§P7).

None of these invariants is wired to a Test ID in the catalogue yet — they are deliberate, visible gaps to be added under the catalogue's growth provision when the ACL service is implemented (ADR-PC-020 §P5). The §P2 build-time module-boundary check is an analyser-class gate (in the family of the engine's `BENG` analysers); the §P5 double-debit guard and the §P4 idempotency-key derivation are the most testable. IC-012 is the originating contract for the idempotency and indeterminate-state machinery that [ADR-PC-016](../../product_concepts/adrs/ADR-PC-016-legacy-current-account-adapter.md) inherits verbatim; that contract's own Test IDs, when catalogued, will exercise these same invariants from the engine side.
