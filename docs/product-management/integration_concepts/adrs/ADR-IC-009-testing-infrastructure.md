# ADR-IC-009: Testing Infrastructure and Contract Testing

| Field | Value |
|---|---|
| Status | Accepted |
| Date | 2026-05-17 |
| Deciders | jhosm |
| Common criteria | [ADR-IC-000](./ADR-IC-000-common-evaluation-criteria.md) |
| Depends on | [ADR-IC-001](./ADR-IC-001-event-backbone-message-broker.md), [ADR-IC-002](./ADR-IC-002-schema-format-and-registry.md), [ADR-IC-003](./ADR-IC-003-saga-orchestrator.md), [ADR-IC-005](./ADR-IC-005-cqrs-read-model-storage.md), [ADR-IC-007](./ADR-IC-007-observability-stack.md) |

---

## Context

[Document 07](../07-testing-strategy.md) defines a five-level adapted pyramid for this architecture: unit tests of pure aggregates, integration tests with real infrastructure, consumer-driven contract tests, saga tests (happy path, fault injection, chaos/property-based), and selective E2E. Each level requires tooling choices that must hold at 1–2 person scale.

**One decision is already settled by ADR-IC-002:** schema-level compatibility for integration events is enforced by the Redpanda built-in schema registry using BACKWARD/FORWARD/FULL compatibility modes. This is a hard CI gate that prevents structurally incompatible schema changes from reaching any consumer. Document 07 (Level 3) names this the "schema contract" layer. It is not re-evaluated here.

**What ADR-IC-006 already covers:** Kong's `request-validator` plugin enforces the OpenAPI schema for all inbound REST requests at the edge. External-facing REST contract enforcement is a consequence of the gateway choice.

**What remains to be decided, across four interdependent areas:**

| Area | Question |
|---|---|
| **Integration test container management** | How do tests spin up and tear down real Redpanda, PostgreSQL, and dependent services — reproducibly, per-class, in CI? |
| **Behavioural contract testing** | How are per-consumer expectations about event payload semantics enforced in CI, beyond what the schema registry can verify? How are HTTP contracts between bounded contexts enforced? |
| **External service simulation** | How do saga tests and integration tests simulate Core Banking (SOAP), Compliance (REST), and Workflow (REST) without a live environment? |
| **Resilience injection** | How are DORA-relevant failure scenarios (Redpanda unavailable, Core Banking unresponsive, connection timeouts) injected into saga tests and game days in a controllable, repeatable way? |

### The behavioural gap that motivates Area 2

The schema registry enforces that `correlation_id` is a string field present in the Avro schema. It does not enforce that `correlation_id` is non-null in every production message. It does not enforce that `deposit.amount` is positive, or that the identity trio from [document 01](../01-the-six-primitives.md) (Primitive 4) is populated on every message — only that the fields exist in the schema. These behavioral gaps are where production incidents happen after a schema-valid but semantically incorrect change: a field silently nullified in a refactoring, a required attribute dropped under a code path not covered by unit tests.

Consumer-driven contract (CDC) testing is the mechanism that catches this class of change in the producer's CI before it reaches any consumer. Document 07 (Level 3) identifies both schema contracts and CDC as required, not optional.

### The DORA obligation that shapes Area 4

[Document 10](../10-security-and-threat-model.md) identifies Core Banking as the critical third-party provider in this architecture. DORA requires that the ACL's indeterminate-state handling and the reconciliation job are tested under real failure conditions, not assumed to work. The game days recommended in [document 06](../06-observability-and-tracing.md) are DORA-relevant activities. The resilience injection tooling selected in this ADR is the mechanism that makes game days repeatable as automated test cases, not just periodic manual drills.

### The GDPR constraint that shapes test data

[Document 07](../07-testing-strategy.md) identifies test data as a problem frequently underestimated in banking systems. Real client data cannot be used; synthetic data must be realistic enough to exercise financial edge cases; and synthetic client records must be coherently propagated across all service mocks. This is addressed in the Implementation Principles (P4) rather than as a tool selection, because the constraint shapes practice, not a specific tool purchase.

---

### Candidate overview

**Area 1 — Integration test container management:**

| # | Candidate | Notes |
|---|---|---|
| A | **Testcontainers** | Programmatic Docker container lifecycle tied to JUnit test execution; Apache 2.0; modules for Redpanda (Kafka-compatible), PostgreSQL, WireMock, Toxiproxy |
| B | **Docker Compose test profile** | Compose file with a test profile; containers started externally before the test suite; no programmatic per-class lifecycle |
| C | **Embedded / in-process alternatives** | Spring Kafka Test's embedded Kafka, H2 in-memory SQL |

**Area 2 — Behavioural contract testing:**

| # | Candidate | Notes |
|---|---|---|
| D | **Pact** | Consumer-driven contract testing; Apache 2.0; supports async message contracts (PactV4 messages, covering Kafka events) and HTTP interactions (covering REST APIs between bounded contexts); multilingual; Pact Broker (MIT, self-hosted) |
| E | **Spring Cloud Contract** | JVM-centric; Apache 2.0; HTTP + messaging stubs via DSL; no separate broker service; tighter Spring ecosystem coupling |
| F | **Schema registry only** | Avro compatibility enforcement as the sole contract gate; no behavioural CDC |

**Area 3 — External service simulation:**

| # | Candidate | Notes |
|---|---|---|
| G | **WireMock** | HTTP/SOAP mock server; Apache 2.0; standalone Docker image; Testcontainers module; XPath body matching; built-in fault modes (EMPTY_RESPONSE, CONNECTION_RESET) |
| H | **Hoverfly** | HTTP/HTTPS proxy; Apache 2.0; Go-based; simulation/capture/spy modes |
| I | **MockServer** | HTTP/HTTPS mock; Apache 2.0; Java; API-first stub definition |

**Area 4 — Resilience injection:**

| # | Candidate | Notes |
|---|---|---|
| J | **Toxiproxy** | TCP proxy that injects network conditions (latency, bandwidth limit, timeout, disconnect) between specific connections; MIT; Go-based; Testcontainers module |
| K | **Pumba** | Docker-level chaos (container kill, pause, `tc`-based network impairment at container level); MIT |
| L | **Chaos Mesh** | Kubernetes-native chaos engineering; Apache 2.0; requires Kubernetes |

---

## Evaluation

### Area 1 — Integration test container management

#### F1 · Cost / licensing

| Candidate | Licence | Assessment | Proceeds? |
|---|---|---|---|
| Testcontainers | Apache 2.0 (library); Docker runtime (Docker Engine on Linux: Apache 2.0 and free; Docker Desktop: free for personal use and teams < 250 people / < $10M revenue) | The library is Apache 2.0. Docker Engine on Linux CI is free regardless of team size — the standard CI runtime. Docker Desktop licence restrictions apply to developer workstations; personal use remains free. A 1–2 person POC team is within the free tier on both counts. | **Pass** |
| Docker Compose test profile | Apache 2.0; same Docker runtime considerations | No additional licence beyond the Docker runtime. | **Pass** |
| Embedded / in-process | Spring Kafka Test: Apache 2.0; H2: LGPL-2.1 | Both open source; no financial-services restriction. | **Pass** |

*Date of licence assessment: 2026-05-17.*

#### F2 · Regulatory fit

| Candidate | Assessment | Proceeds? |
|---|---|---|
| Testcontainers | Tests run against real Redpanda and real PostgreSQL — not simulated behaviour. GDPR: test environments must use synthetic data (P4); Testcontainers does not constrain what data the tests use. DORA: real infrastructure makes resilience scenarios credible — a test that verifies outbox recovery after Redpanda restart is evidence of resilience, not a simulation of it. PSD2: real PostgreSQL allows the idempotency `ON CONFLICT DO NOTHING` path, the saga state transition persistence, and the outbox transactionality to be tested against the actual production code path. | **Pass** |
| Docker Compose test profile | Same regulatory properties as Testcontainers; the infrastructure is identically real. The difference is operational (lifecycle management), not regulatory. | **Pass** |
| Embedded / in-process | H2's SQL dialect diverges from PostgreSQL in the idioms this architecture depends on: `ON CONFLICT DO NOTHING` (idempotency), advisory locks (outbox publisher coordination), and range partition queries (maturity projections) are not available in H2. A test that passes against H2 does not validate the production code path for these idioms. This is a DORA risk: the evidence of tested resilience is not credible if the test environment does not match production behaviour. Embedded Kafka is closer to Redpanda but differs in consumer group re-balance behaviour and topic ACL enforcement — relevant when testing inbox deduplication under concurrent consumers. | **Fail** — the H2 / PostgreSQL behavioural divergence is structural, not addressable by test-writing discipline; the production idempotency and transactionality paths cannot be verified |

#### Soft criteria

**Testcontainers (A):**

S1 · Operational complexity: zero external setup. A `@Testcontainers` annotation and a static container field declaration is the entire configuration. The Testcontainers Kafka module (which wraps Redpanda via the standard Kafka API) and the PostgreSQL module start containers before the first test method and stop them after the last. On warm Docker caches, container startup adds 3–6 seconds per test class — negligible relative to saga test execution time. Parallel test class execution creates isolated containers per class; no shared-state contamination between suites.

S2 · Ecosystem coherence: every component in this decision's Area 3 (WireMock) and Area 4 (Toxiproxy) has a first-party Testcontainers module. This means the entire test infrastructure — broker, database, HTTP mocks, resilience proxy — shares one container lifecycle mechanism. A saga test that needs Redpanda, PostgreSQL, WireMock, and Toxiproxy needs one `@Testcontainers` class and four container field declarations. No shell scripts, no external `docker compose up`.

S3 · Exit cost: low. If Docker Compose is later preferred for saga environments (where containers persist across many test files), extracting the container configuration from Testcontainers to Compose is straightforward. The test assertion code is unaffected.

S4 · Community and longevity: acquired by Docker Inc. in 2023; CNCF landscape member; Apache 2.0; very large community. Active module development across all components in this stack.

**Docker Compose test profile (B):**

S1 · Operational complexity: requires a CI step to `docker compose up -d --profile test` before the test suite and `down` after. Test isolation between parallel runs requires unique Compose project names per CI job. Compared to Testcontainers, lifecycle management shifts from the test framework to CI configuration — more CI YAML, less Java.

S2 · Ecosystem coherence: Compose is not aware of individual test class boundaries. Container state from one test class can leak into the next unless each test cleans up after itself. For saga tests with complex state, this discipline is hard to maintain at scale. Testcontainers' per-class container lifecycle solves this at the framework level; Docker Compose requires explicit workarounds (table truncation, schema reset) that Testcontainers makes unnecessary.

The Docker Compose test profile is the right tool for saga test environments that run as long-lived environments with multiple test suites sharing the same container set — a deployment concern rather than a per-class concern. The two approaches are not mutually exclusive.

---

### Area 2 — Behavioural contract testing

#### F1 · Cost / licensing

| Candidate | Licence | Assessment | Proceeds? |
|---|---|---|---|
| Pact | Apache 2.0 (pact-jvm library); MIT (Pact Broker, self-hosted) | The self-hosted Pact Broker (MIT) covers all features required by this architecture: contract publishing, producer verification, tag/branch management, pending-pact feature (allows verifying unverified pacts without breaking the build during initial rollout). PactFlow (commercial SaaS) adds WebUI features not required here. | **Pass** |
| Spring Cloud Contract | Apache 2.0 | Open source; no broker service required — contracts publish as Maven/Gradle stub jars to a repository already present in the build system. | **Pass** |
| Schema registry only | Apache 2.0 (Redpanda + Avro) | No additional tool. | **Pass** |

*Date of licence assessment: 2026-05-17.*

#### F2 · Regulatory fit

| Candidate | Assessment | Proceeds? |
|---|---|---|
| Pact | Pact contracts are JSON files in version control and the Pact Broker — no personal data, no financial data. GDPR: no new data residency concern. DORA: Pact producer verification runs in CI on every commit, making contract validation a repeatable, auditable gate. PSD2: behavioral contracts can encode the identity trio requirements from [document 01](../01-the-six-primitives.md) — if a producer stops populating `correlation_id` in its messages, the Pact verification that asserts `correlation_id` is non-null will block the producer's CI. | **Pass** |
| Spring Cloud Contract | Same regulatory properties as Pact: contracts are test artifacts in version control. GDPR/DORA/PSD2 considerations are identical. | **Pass** |
| Schema registry only | The Avro compatibility gate enforces structural contracts. It cannot enforce that `correlation_id` is non-null in production messages — the Avro schema permits null (union type) even when the behavioral contract is never-null in practice. It cannot enforce that `deposit.amount` is positive, or that the `metadata.version` field matches the schema version used by the producer. These behavioral gaps are the class of change that causes consumers to break after a schema-valid but semantically incorrect producer update. Document 07 (Level 3) explicitly names both schema contracts and CDC as required. | **Pass** — proceeds as a first-layer contract gate that Pact supplements; structural and behavioral enforcement are complementary |

#### Soft criteria

**Pact (D):**

S1 · Operational complexity: the Pact library is a test dependency; no separate process is needed for writing consumer tests or producer verifications. The Pact Broker adds one Docker Compose service (the official `pact-foundation/pact-broker` image, which embeds its own PostgreSQL). For a 1–2 person team, the Pact Broker's PostgreSQL is one additional database container to operate — at POC scale this is acceptable, and the Pact Broker container can share the Compose network with the application stack.

S2 · Ecosystem coherence: Pact covers both the Kafka event contracts and the HTTP REST contracts between bounded contexts in a single framework. A consumer's Pact message test says "when I receive a `DepositConstituted` event, I expect these fields with these constraints"; a consumer's Pact HTTP interaction test says "when I call `GET /deposits/:id`, I expect this response shape". Both verify against the producer in CI using the same Pact Broker. This unified framework means one set of tooling, one Broker, one verification workflow — regardless of whether the contract is over Kafka or HTTP.

S2 · Ecosystem coherence, continued: the Pact producer verification test uses the same Avro serializer as production, so the verification validates real serialization against real consumer expectations — not a mock or a hand-crafted stub. The schema registry check (structural) and the Pact verification (behavioral) compose naturally: both run in the producer's CI pipeline.

S3 · Exit cost: moderate. The Pact consumer test DSL is Pact-specific; migration to Spring Cloud Contract would require rewriting consumer tests. The Pact contract files (JSON) are portable data and re-runnable. The Pact Broker's database is reconstructable by re-running all consumer test suites.

S4 · Community and longevity: maintained by the Pact Foundation, a non-profit multi-vendor open governance body. Broad adoption in banking and fintech contexts where multi-language service ecosystems are common. The PactV4 async message specification is the current version, actively maintained.

**Spring Cloud Contract (E):**

S1 · Operational complexity: no broker service required — contracts are published as stub JARs to a standard Maven repository. This is operationally simpler than the Pact Broker for a pure JVM team. Contracts live in the producer's codebase (or a separate contracts repository) as Groovy/YAML DSL files.

S2 · Ecosystem coherence: Spring Cloud Contract's messaging support is idiomatic within the Spring ecosystem (Spring Cloud Stream, Spring Integration). ADR-IC-003 chose a custom event-driven orchestrator, not a Spring-based orchestrator. If the orchestrator does not use Spring messaging abstractions, wiring Spring Cloud Contract's messaging stub mechanism requires additional adapter code. The more natural fit is for a team building Spring Boot microservices end-to-end; less natural for architecture where the orchestrator manages its own Redpanda consumer loop outside Spring Cloud Stream.

S2, continued: Spring Cloud Contract does not unify Kafka and HTTP contracts in a single framework in the same way Pact does — its HTTP stubs and messaging stubs are separate mechanisms. Pact's single framework for both is the coherence advantage.

S3 · Exit cost: moderate. Contracts are in the producer codebase; migrating requires republishing as Pact files.

S4 · Community and longevity: maintained by the Spring team at Broadcom; active, well-documented, large community.

---

### Area 3 — External service simulation

#### F1 · Cost / licensing

| Candidate | Licence | Assessment | Proceeds? |
|---|---|---|---|
| WireMock | Apache 2.0 | Open source library and standalone Docker image; Testcontainers module; self-hosted | **Pass** |
| Hoverfly | Apache 2.0 | Open source; self-hosted Go binary and Docker image | **Pass** |
| MockServer | Apache 2.0 | Open source; self-hosted; Testcontainers module | **Pass** |

*Date of licence assessment: 2026-05-17.*

#### F2 · Regulatory fit

All three candidates are HTTP mock servers that simulate external service responses in test environments. They are self-hosted, store no personal data (only synthetic test stubs), and impose no cross-border data transfer. All three can simulate SOAP/XML responses, which is required for the Core Banking ACL ([document 02](../02-anti-corruption-layer.md)).

| Candidate | Proceeds? |
|---|---|
| WireMock | **Pass** |
| Hoverfly | **Pass** |
| MockServer | **Pass** |

#### Soft criteria

**WireMock (G):**

S1 · Operational complexity: the `testcontainers-wiremock` module starts WireMock as a container alongside Redpanda and PostgreSQL, with stubs loaded from the test classpath. No external process or script. Stubs are JSON files committed in `src/test/resources/wiremock/mappings/` — human-readable, reviewable, version-controlled.

S2 · Ecosystem coherence: WireMock's XPath body matching is the critical feature for Core Banking SOAP simulation. The ACL ([document 02](../02-anti-corruption-layer.md)) translates internal commands into SOAP calls to `HoldsService.create` and `HoldsService.confirm`. WireMock can match on the SOAP action header and the relevant XPath expression in the body, returning the expected XML response — or a fault. WireMock's built-in fault modes are the mechanism for testing ACL indeterminate-state paths:

| ACL scenario (document 07 Level 4) | WireMock fault |
|---|---|
| Core Banking returns empty body (connection reset mid-response) | `CONNECTION_RESET` fault on the SOAP endpoint stub |
| Core Banking returns malformed XML | `MALFORMED_RESPONSE_CHUNK` fault |
| Core Banking responds 500 | HTTP 500 stub with Retry-After header |
| Core Banking times out | Stub with `fixedDelayMilliseconds` exceeding the ACL's timeout threshold |

These faults are injected by swapping the active stub before the triggering event in the saga test — no application code change, no Testcontainers restart.

S2, continued: WireMock's record-and-replay mode allows capturing real Core Banking SOAP responses against a controlled environment (integration testing against the bank's sandbox) and replaying them in CI indefinitely. The capture date is stored in the stub file and should be refreshed when the Core Banking API version changes.

S3 · Exit cost: low. WireMock stubs are JSON files; the mock definitions are test data, not production code.

S4 · Community and longevity: WireMock is the most widely adopted HTTP mock framework in the JVM ecosystem. Commercial backing via WireMock Ltd. (which maintains the Apache 2.0 open-source core and a commercial enterprise tier). Very large community; extensive SOAP simulation documentation.

**Hoverfly (H):**

S1 · Operational complexity: Go binary or Docker image; simulation mode requires a prior "capture" pass against a live service. This two-step workflow is less natural for test authoring than WireMock's stub-first approach — you define what the mock returns before writing the test, which is the right development order.

S2 · Ecosystem coherence: Hoverfly's primary differentiator over WireMock is network condition simulation (latency, packet loss, rate limiting). However, this capability is redundant given Toxiproxy (Area 4) is purpose-built for network condition injection and has a Testcontainers integration. Hoverfly's unique value proposition does not add to this architecture.

**MockServer (I):**

S1 · Operational complexity: Testcontainers module available; broadly similar to WireMock. API-first stub definition (Java DSL or REST API only; no JSON file format), which is less reviewable than WireMock's JSON stubs in version control.

S2 · Ecosystem coherence: equivalent to WireMock for most use cases; smaller community documentation for SOAP/XML simulation specifically.

---

### Area 4 — Resilience injection

#### F1 · Cost / licensing

| Candidate | Licence | Assessment | Proceeds? |
|---|---|---|---|
| Toxiproxy | MIT | Open source; Go binary and Docker image; Testcontainers module (`testcontainers-toxiproxy`) | **Pass** |
| Pumba | MIT | Open source; Go binary | **Pass** |
| Chaos Mesh | Apache 2.0 | Open source; self-hosted on Kubernetes | **Pass** |

*Date of licence assessment: 2026-05-17.*

#### F2 · Regulatory fit

| Candidate | Assessment | Proceeds? |
|---|---|---|
| Toxiproxy | Toxiproxy operates at the TCP proxy layer between services. It intercepts a specific connection and applies programmable conditions (latency, disconnect, bandwidth limit, timeout) to the byte stream without modifying data — only timing and connectivity. GDPR: no data persistence; Toxiproxy is a transparent proxy. DORA: Toxiproxy enables the fault scenarios from document 07 (Level 4 saga test table) to run as automated, repeatable, committed test cases — this is the concrete evidence of operational resilience that DORA requires. The test that verifies "ACL enters INDETERMINATE when Core Banking connection times out after 3 retries" is a DORA compliance artifact, not just a developer convenience. | **Pass** |
| Pumba | Pumba applies chaos at the Docker container level: kill containers, pause them, or apply `tc` (Linux traffic control) network rules to the container's network interface. The `tc`-based network impairment (latency, packet loss) requires `CAP_NET_ADMIN` in the container runtime, which must be verified against the CI provider. Pumba is the right tool for "what happens when the entire Redpanda node crashes mid-saga?" — a coarser, whole-component failure that complements Toxiproxy's per-connection surgical control. | **Pass** |
| Chaos Mesh | Requires a running Kubernetes cluster. The POC stack (ADR-IC-001 through ADR-IC-007) is Docker Compose-based. Standing up a Kubernetes cluster solely for chaos testing is a significant operational addition at 1–2 person scale. | **Pass (conditional)** — Kubernetes dependency is disproportionate at POC scale; re-evaluate when the stack moves to a Kubernetes deployment model |

#### Soft criteria

**Toxiproxy (J):**

S1 · Operational complexity: one Docker container; the `testcontainers-toxiproxy` module starts it as part of the test class. Services under test connect through Toxiproxy's proxy ports instead of directly to Redpanda or WireMock. Adding a toxic (e.g., `new LatencyToxic().setLatency(300).setJitter(50)`) is a single API call from the test code; removing it restores the connection. No `sudo`, no kernel capabilities required — Toxiproxy is a software TCP proxy, not a kernel-level traffic shaper.

S2 · Ecosystem coherence: Toxiproxy's Testcontainers module integrates into the same Docker network as Redpanda, PostgreSQL, and WireMock. A single test can have:
- Toxiproxy injecting a timeout on the Core Banking WireMock connection (simulating ACL indeterminate-state path)
- While Redpanda and PostgreSQL remain fully available (so the outbox and saga state transitions proceed normally)
- And then remove the toxic to simulate Core Banking recovery

This surgical granularity — inject failure on one connection, leave all others healthy — is exactly what the fault injection scenarios in document 07's Level 4 table require. A test that kills the entire Core Banking container cannot verify that the ACL correctly handles a connection timeout as distinct from a container-level failure.

S3 · Exit cost: low. Toxiproxy configuration is test code; the application code is not affected. Switching to a different chaos tool requires changing test setup only.

S4 · Community and longevity: created and maintained by Shopify; MIT licence; active maintenance; Testcontainers module is community-maintained and active.

**Pumba (K):**

S1 · Operational complexity: CLI or Go library; Docker socket access required; `tc`-based network impairment requires `CAP_NET_ADMIN` — verify against the CI provider before committing to `tc` scenarios. Container kill and pause work without any elevated capabilities.

S2 · Ecosystem coherence: Pumba complements Toxiproxy for "kill the whole node" scenarios: "what happens when the Redpanda broker crashes mid-saga?" requires killing the Redpanda container, not inserting a latency toxic. Toxiproxy cannot kill a container. The two tools address different granularities of the same failure space.

**Chaos Mesh (L):**

Kubernetes-only. Not evaluated further for POC; noted as the production evolution when the stack is containerized on Kubernetes.

---

## Decision

### Area 1 — Integration test container management

**Chosen: Testcontainers**

The decisive reason is behavioural fidelity at the production code path. H2 diverges from PostgreSQL in the SQL idioms this architecture depends on (`ON CONFLICT DO NOTHING` for idempotency, advisory locks for outbox publisher coordination), and embedded Kafka diverges from Redpanda in consumer group rebalance behaviour. Docker Compose test profiles achieve the same infrastructure fidelity but at the cost of external lifecycle management. Testcontainers provides programmatic, per-class lifecycle management of real infrastructure within the test runner — zero additional setup for the developer and deterministic isolation across parallel test classes.

**Rejected: Embedded / in-process alternatives**

The PostgreSQL / H2 behavioural divergence is disqualifying. The idempotency primitive in this architecture (`ON CONFLICT DO NOTHING`) cannot be verified against H2 — a test that passes against H2 does not constitute evidence of correct production behaviour.

**Rejected: Docker Compose test profile as the primary mechanism**

Not rejected on quality grounds — it achieves the same infrastructure fidelity. Testcontainers' programmatic lifecycle is strictly superior for per-class isolation and parallel test execution. Docker Compose remains valid for saga-level test environments that span multiple test files with shared container state, but even those environments are best started via Testcontainers rather than external scripts.

---

### Area 2 — Behavioural contract testing

**Chosen: Pact (pact-jvm + self-hosted Pact Broker)**

The decisive reasons are multi-language forward compatibility and unified coverage across both Kafka message contracts and HTTP contracts.

The bounded contexts in this ecosystem are not contractually required to be JVM services — the Core Banking ACL adapters and future integrations may use different runtimes. Spring Cloud Contract's messaging stub mechanism is idiomatic only within the Spring ecosystem. Pact's PactV4 message consumer specification is language-neutral: a consumer in any language can write a Pact message test, publish the resulting Pact file to the Pact Broker, and have it verified by the JVM producer without any framework dependency. For a banking ecosystem that will grow beyond its initial JVM services, Pact's multi-language support is the right structural bet.

Pact also unifies event contracts (PactV4 async messages) and HTTP contracts (Pact HTTP interactions) in one framework and one Broker — the same verification workflow covers both. This means one set of tooling and one contract registry, regardless of transport.

**The Avro schema registry compatibility gate is retained as the first contract layer.** Pact supplements it, enforcing behavioral semantics (non-null requirements, value ranges, identity trio presence) that schema compatibility modes cannot verify. These two mechanisms are complementary; neither replaces the other.

**Rejected: Spring Cloud Contract**

Not rejected on quality grounds — the Spring Cloud Contract DSL is well-designed and operationally simpler (no Pact Broker). The rejection is structural: non-JVM consumers cannot participate in Spring Cloud Contract's stub mechanism without significant shim code. In an architecture where the schema-level contract (ADR-IC-002) is already language-neutral (Avro), the behavioral contract layer should be equally language-neutral.

**Rejected: Schema registry only**

Schema compatibility enforces structural contracts — field presence and type. It cannot encode the behavioral invariant that `correlation_id` is non-null in every production message, or that `deposit.amount` is always a positive integer, or that the saga state transition events carry the `process_id` attribute that the saga test harness depends on for search. Document 07 (Level 3) is explicit: schema contracts and consumer-driven contracts are complementary, not substitutable. A schema-only contract strategy leaves the behavioral gap open.

---

### Area 3 — External service simulation

**Chosen: WireMock**

The decisive reasons are SOAP/XML simulation maturity and built-in fault modes.

The Core Banking ACL ([document 02](../02-anti-corruption-layer.md)) calls legacy SOAP endpoints. WireMock's XPath body matching and fault injection modes are the most capable and best-documented of the three candidates for this use case. WireMock's `CONNECTION_RESET`, `EMPTY_RESPONSE`, and `MALFORMED_RESPONSE_CHUNK` fault modes map directly onto the ACL indeterminate-state scenarios in document 07's Level 4 fault injection table — the most financially consequential code paths in the architecture. This is not an accidental alignment; it is the design WireMock's fault injection was built for.

WireMock's record-and-replay mode provides a migration path from simulated stubs to captured real responses as the Core Banking test environment becomes available, without changing the test assertion code.

**Rejected: Hoverfly**

Hoverfly's primary differentiator is network condition simulation, which is redundant given Toxiproxy (Area 4) provides that capability with more surgical granularity and a first-class Testcontainers integration. Without that differentiator, Hoverfly's two-step capture-then-simulate workflow is a less natural fit for stub-first test authoring.

**Rejected: MockServer**

Broadly equivalent to WireMock but with smaller SOAP/XML community documentation and no JSON file format for stubs (API-first only), making stub definitions less reviewable in version control.

---

### Area 4 — Resilience injection

**Chosen: Toxiproxy (primary) + Pumba (secondary, for container-kill scenarios)**

Toxiproxy is the primary tool for automated saga test fault injection. The decisive reason is surgical granularity: Toxiproxy injects conditions on a specific TCP connection, leaving all other connections in the test environment unaffected. A saga test that verifies "ACL enters INDETERMINATE when Core Banking times out after 3 retries" requires the Core Banking connection to time out while Redpanda and PostgreSQL remain available — so the outbox event persists, the saga state is stored, and the compensation scheduler can fire when the connection is restored. Toxiproxy achieves this; container-level tools cannot.

Pumba is retained for game-day exercises and whole-component failure scenarios ("what happens when the Redpanda broker crashes mid-saga?"). These two tools address different granularities of the same failure space and are complementary.

**Rejected: Chaos Mesh (at POC scale)**

Kubernetes dependency is the disqualifying constraint. The POC stack is Docker Compose-based. Chaos Mesh is the right successor when the stack moves to Kubernetes for production hardening.

---

## Consequences

**What this combination makes easier:**

- Every layer of the document 07 pyramid has a corresponding tool that integrates with the same Docker-based runtime. A saga test that exercises a fault injection scenario needs: Testcontainers (orchestrates everything), Redpanda (event backbone), PostgreSQL (saga state + outbox), WireMock (Core Banking + Compliance mocks), Toxiproxy (network fault injection) — all started by one `@Testcontainers` class with five container field declarations. No shell scripts, no CI configuration beyond "run tests".
- The schema registry + Pact two-layer contract gate catches two distinct classes of breaking change: the registry catches structural incompatibility at schema registration time; Pact catches behavioral incompatibility (silent nullification, missing identity fields) at the producer's next CI run. Together they make the feedback loop for contract-breaking changes a matter of minutes, not production incidents.
- Toxiproxy's per-test programmability enables the DORA game-day scenarios to run as committed, automated test cases in CI. The test that verifies "compensation executes after Core Banking timeout" is the DORA compliance artifact — it proves the resilience behavior works, reproducibly, on every commit.
- WireMock's SOAP simulation means the ACL (the highest-risk component, because it moves real money) is fully testable without a live Core Banking environment. The Core Banking dependency does not block test coverage of the most financially consequential code paths.

**What this combination makes harder or impossible:**

- **Docker-in-Docker CI environments:** Testcontainers requires a Docker socket. CI environments that run the build inside a container (without DinD or Docker socket passthrough) require the `TESTCONTAINERS_RYUK_DISABLED=true` workaround and socket mounting configuration. This is a CI setup concern, not a blocker, but it must be addressed explicitly when configuring the CI pipeline.
- **Pumba `tc` network impairment in restricted CI:** Pumba's `tc`-based network conditions require `CAP_NET_ADMIN` in the container runtime. Not all CI providers grant this capability. Container kill and pause (the Pumba scenarios most relevant to game days) work without elevated capabilities. Verify `tc` availability against the CI provider before committing to Pumba network scenarios in automated tests.
- **Non-JVM consumer Pact maturity:** pact-jvm is the most complete Pact implementation. Pact implementations in Python, Go, and JavaScript support PactV4 async messages, but trail pact-jvm in edge case coverage. When non-JVM services are introduced, verify the PactV4 async message specification support in the relevant language implementation before relying on it in CI.
- **WireMock SOAP stub drift:** WireMock stubs simulating Core Banking SOAP responses must be refreshed when the Core Banking API changes. A stub that drifts from the real response causes tests to pass in CI while the production ACL fails. The record-and-replay workflow (P3) mitigates this; the mitigation requires discipline.

**Residual risks:**

- **Pact Broker database:** the Pact Broker's PostgreSQL backend holds the current set of published Pact contracts. If the database is lost, all contracts are lost — but every consumer's CI run re-publishes its Pact files, so the database is reconstructable by re-running all consumer test suites. The Pact Broker is a distribution and verification cache, not the source of truth; the Pact files generated by consumer tests are the source.
- **Test container startup time in CI:** saga tests with six containers (Redpanda, PostgreSQL, WireMock × 2, Toxiproxy, OTel Collector) add 10–20 seconds of startup overhead per test class on cold Docker caches. On CI with pre-pulled images (Docker layer cache), this drops to 3–5 seconds. Pre-pull the canonical container images as a CI setup step to keep saga test startup within acceptable bounds.
- **Chaos Mesh gap in production:** the POC uses Toxiproxy and Pumba for resilience testing. When the stack moves to Kubernetes, Toxiproxy and Pumba are supplemented by Chaos Mesh for production-grade DORA compliance (network partition simulation, pod failure injection, node drain scenarios). This gap is accepted at POC scale and must be addressed in production hardening.

---

## Implementation Principles

### P1 — All test containers share a single Testcontainers-managed Docker network

For integration and saga tests, a single Docker network managed by the `@Testcontainers` class extension hosts Redpanda, PostgreSQL, WireMock, Toxiproxy, and the OTel Collector. Every inter-service call in the test environment goes through this network. Services use container-aliased hostnames rather than `localhost` — the Redpanda bootstrap address is the container alias, not a mapped local port.

The recommended test container topology for a saga test class:

```
Test application (in-process)
  → Toxiproxy:8474/proxy/redpanda → Redpanda:9092           (events)
  → Toxiproxy:8474/proxy/core     → WireMock:8080 (SOAP)    (Core Banking ACL)
  → Toxiproxy:8474/proxy/comply   → WireMock:8081 (REST)    (Compliance ACL)
  → PostgreSQL:5432 (direct — DB failures tested via Pumba)
  → OTel Collector:4317 (in-memory exporter mode, for telemetry assertions)
```

Proxying through Toxiproxy for Redpanda and WireMock connections (but not PostgreSQL) enables per-connection fault injection without affecting the database. DB-level failures (connection pool exhaustion, transaction timeouts) are tested separately via Pumba container-level pause.

---

### P2 — The Pact contract gate covers every integration event and every inter-service HTTP API

For every integration event published by a bounded context, there is at least one Pact message consumer test that encodes the behavioral contract:

- All fields in the identity trio (`correlation_id`, `causation_id`, `message_id` from [document 01](../01-the-six-primitives.md)) are non-null.
- All numerical amounts are positive integers (cents, never floats).
- All state fields match the documented state machine enumeration.
- The `schema_version` field is present and matches the Avro schema version in the registry.

For every REST API call between bounded contexts (e.g., the saga orchestrator polling the read model, the ACL checking its idempotency store), there is a Pact HTTP interaction test that encodes the expected response shape and status code for the happy path and the primary error codes.

The Pact producer verification test runs in the producer's CI pipeline on every commit. It downloads all consumer Pacts from the Pact Broker and verifies them against messages produced by the real Avro serializer. A failing Pact verification blocks the pipeline.

The two-layer contract gate:

| Gate | What it catches | When it runs |
|---|---|---|
| Schema registry compatibility check | Structurally incompatible schema change (field removed, type changed) | At schema registration (producer publish time) |
| Pact producer verification | Behaviorally incompatible change (required field nullified, identity trio absent, amount sign inverted) | In the producer's CI pipeline on every commit |

---

### P3 — WireMock stubs are versioned and capture-dated

WireMock stubs that simulate Core Banking SOAP responses are JSON files in `src/test/resources/wiremock/mappings/core-banking/`. Every stub file contains a comment field with the capture date and the Core Banking API version it was recorded against:

```json
{
  "metadata": {
    "capturedFrom": "Core Banking SOAP v4.2",
    "captureDate": "2026-05-17",
    "captureEnvironment": "sandbox.corebanking.internal"
  },
  "request": { ... },
  "response": { ... }
}
```

When the Core Banking API version changes, the stubs must be refreshed using WireMock record mode against the updated sandbox. Stale stubs (older than one Core Banking API version) are a test validity risk and should be flagged in code review.

The Compliance and Workflow REST stubs follow the same convention, with their respective API version and capture date.

---

### P4 — Test data is synthetic, consistent across all service mocks, and structurally non-personal

Three tiers of test data with distinct generation strategies:

| Tier | What | Strategy |
|---|---|---|
| Unit test fixtures | Aggregate states, domain events, value objects | Fluent builders in test code: `aDeposit().forClient("CLI-TEST-001").ofAmount(100000).build()` |
| Integration and saga test personas | Client profiles consistent across CRM mock, Core mock, Compliance mock | Stable persona constants (`PERSONA_CLIENT_VIP`, `PERSONA_CLIENT_NEW`, `PERSONA_CLIENT_BLOCKED`, `PERSONA_CLIENT_INSUFFICIENT_FUNDS`) with fixed synthetic attributes |
| Pact contract examples | Sample event payloads in message consumer tests | Same fluent builders as unit tests; persona constants for client references |

Stable personas use synthetic Portuguese NIF values in the range `900-000-000` to `999-999-999` — a range reserved for testing by the Portuguese Tax Authority, structurally valid but not assignable to real persons. IBAN mock values use the prefix `PT50 0000 0000` followed by a sequential account number — visually distinct from real IBANs. These values are hardcoded constants in a `TestPersonas` class, not generated at runtime.

WireMock stubs for Core Banking are keyed by these synthetic NIFs. If a test uses `PERSONA_CLIENT_VIP.nif`, the Core Banking stub that returns the expected account balance is also keyed by `PERSONA_CLIENT_VIP.nif`. Adding a new persona requires: (1) a new constant in `TestPersonas`, (2) corresponding WireMock stubs for Core Banking and Compliance, (3) a matching record in the PostgreSQL saga test schema.

No real client data is used at any test tier. GDPR Article 25 (data protection by design) is satisfied by construction: the test data generation functions produce values that are structurally valid for the system but legally non-personal by definition.

---

### P5 — Fault injection scenarios are enumerated in the saga test suite as first-class test cases

For each saga (ConstitutionProcess, MobilizationProcess, MaturityProcess), the saga test suite contains one test per row of document 07's Level 4 fault injection table. Each test is self-contained: it injects the fault, triggers the saga, and asserts the expected compensating behavior.

Reference mapping for the ConstitutionProcess:

| Scenario (document 07) | Toxiproxy toxic | WireMock fault | Expected outcome |
|---|---|---|---|
| Core Banking times out (INDETERMINATE) | `timeout` toxic on Core Banking proxy port; timeout < ACL retry interval | — | ACL transitions to INDETERMINATE; clearance job retries; saga awaits |
| Core Banking empty response | — | `CONNECTION_RESET` fault on `/holds` stub | Same INDETERMINATE path as above |
| Compliance fails after Core debit | — | WireMock priority stub: 3 × 500, then 200 | Saga enters COMPENSATE_POST_DEBIT; Core reversal credit is called |
| Redpanda producer timeout | `latency` toxic on Redpanda proxy port with `latency=30000ms` (> producer timeout) | — | Outbox publisher retries; saga persists; no duplicate events after recovery |
| Compensation fails 3× | — | WireMock priority stub: 3 × 500, no 200 | Saga enters HUMAN_INTERVENTION_REQUIRED; alert fires in observability stack |
| Service restart mid-flow | Pumba `kill` on the application container; restart | — | Saga resumes from persisted PostgreSQL state; no duplicate effects |

Each test asserts the final saga state, the events published to Redpanda, and the calls made to WireMock. For the INDETERMINATE scenarios, the assertion includes the ACL state store entry (state = INDETERMINATE, retry count, last attempt timestamp).

---

### P6 — Observability is verified as a first-class test output

Following the relationship between documents 07 and 06 (observability facilitates testing; testing instruments observability), each saga test includes assertions on the telemetry signals produced. The OTel Collector runs in the Testcontainers network using the in-memory exporter, exposing collected spans and metrics for test assertions.

Minimum telemetry assertions per saga test:

- A trace with the expected `correlation_id` attribute exists in the in-memory span exporter.
- The trace contains the manually-created spans in the naming convention from ADR-IC-007 P2 (`aggregate.deposit.create`, `saga.constitution.transition`, `outbox.publish`, etc.).
- The business metric `deposits_constituted_total` (or `deposits_cancelled_total` for compensation paths) increments by exactly 1.
- No span carries a raw Portuguese NIF or IBAN as a span attribute (GDPR assertion — P4 of ADR-IC-007).

This prevents a class of silent regression where a refactoring removes a span or a metric that an alert (from ADR-IC-007 P6) depends on. The observability signal is a product output tested like any other, not an assumed side-effect.
