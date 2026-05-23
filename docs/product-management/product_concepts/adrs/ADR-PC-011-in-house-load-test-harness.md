# ADR-PC-011: Synthetic v4-Scale Load-Test Harness — In-House .NET Harness on the Production Boundary

| Field | Value |
|---|---|
| Status | Accepted |
| Date | 2026-05-23 |
| Deciders | jhosm |
| Shape | Tool-selection |
| Common criteria | [ADR-IC-000](../../integration_concepts/adrs/ADR-IC-000-common-evaluation-criteria.md) (reused per [ADR-PC-000](./ADR-PC-000-namespace-and-contract-shape-framework.md) D2) |
| Depends on | [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) (C# / .NET 9 hand-rolled engine — fixes the harness language and the injected-clock seam), [ADR-PC-001](./ADR-PC-001-event-store-technology.md) (PostgreSQL event store — the topology under test), [ADR-IC-001](../../integration_concepts/adrs/ADR-IC-001-event-backbone-message-broker.md) (Redpanda — the ingest boundary the harness drives), [ADR-IC-002](../../integration_concepts/adrs/ADR-IC-002-schema-format-and-registry.md) (Avro + Confluent SR — the envelope the harness reuses), [ADR-IC-006](../../integration_concepts/adrs/ADR-IC-006-edge-api-gateway.md) (Kong — the control-plane boundary), [ADR-IC-007](../../integration_concepts/adrs/ADR-IC-007-observability-stack.md) (OpenTelemetry / Grafana LGTM — the measurement plane), [ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md) (Testcontainers — the fixture tooling) |
| Resolves | bd `archie-10r.12`; the tooling residual of Q-AK ([two-modes §8](../feature-design-two-modes-asymmetry.md)) |

---

## Context

[two-modes §5.6](../feature-design-two-modes-asymmetry.md) makes a synthetic v4-scale load test a **v1 acceptance gate**: it runs green at the v1 release candidate, or v1 does not ship. [two-modes §8](../feature-design-two-modes-asymmetry.md) fully specifies the *test* — workload pattern and event mix (§8.2), p50/p95/p99 latency bands per projection class, sustained 250 TPS for 24h, burst 1000 TPS for 15min, replay budgets and reliability invariants (§8.3), infrastructure ownership (§8.4), and determinism via seeded RNG + injected clock (§8.5). [Q-AK in 04-open-questions](../04-open-questions.md) marks the spec SPECIFIED and defers only the operator-calibration numbers (§8.1) and **the tooling**. This ADR picks the tooling.

This is also the resolution of [ADR-PC-010 Open Action #1](./ADR-PC-010-dotnet-hand-rolled-engine.md) ("the Q-AK synthetic v4-scale load test is a v1 acceptance gate for the hand-rolled append/replay path") and the gate [ADR-PC-001](./ADR-PC-001-event-store-technology.md) names against the chosen PostgreSQL topology.

**Candidates** (per bd `archie-10r.12`): [k6](https://k6.io), [Gatling](https://gatling.io), [Locust](https://locust.io), [JMeter](https://jmeter.apache.org), in-house harness. The bd issue framed the in-house option as *Go/Rust*; that framing predates [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) (issue created 2026-05-21; the engine language was decided C# / .NET 9 on 2026-05-23). The in-house option is therefore evaluated as **.NET**, for the engine-code-reuse reasons the Decision sets out.

### Two production surfaces, not one

§8.4 requires the harness to "drive the engine through the same APIs production channels use, not via internal entry points." The engine has **two** such surfaces, and neither is a conventional HTTP request/response endpoint:

1. **Event ingest — Redpanda.** Per §8.2, ~85% of `E_year` arrives as externally-ingested events (card transactions ~70%, transfers/direct-debits ~15%). Production card schemes and payments rails publish Avro messages onto the [ADR-IC-001](../../integration_concepts/adrs/ADR-IC-001-event-backbone-message-broker.md) backbone; the engine consumes them. The harness must be a **Kafka producer** that emits the same Avro envelopes ([ADR-IC-002](../../integration_concepts/adrs/ADR-IC-002-schema-format-and-registry.md)) with the correct `partition_key` ([two-modes §5.3](../feature-design-two-modes-asymmetry.md)).
2. **Control plane — Kong-fronted REST + the injected clock.** Operator-initiated and treasury-gated operations (rate-sheet deploy per [ADR-PC-008](./ADR-PC-008-rate-sheet-storage-and-deploy-api.md), pack adoption per [ADR-PC-009](./ADR-PC-009-per-instance-version-pinning.md), `AccountFrozen`/`FundsHeld`) go through the [ADR-IC-006](../../integration_concepts/adrs/ADR-IC-006-edge-api-gateway.md) gateway, and the **test clock** (§8.5) is driven through a production control API.

The remaining ~10% engine-generated lifecycle events (`DailyAccrualClosed`, `StatementCycleClosed`, `FeeAssessed`) and the ~3% cross-mode flow (`DepositMaturedSettlement`) are **not produced by the harness** — the engine emits them itself when the injected clock advances past a simulated month-end. Driving them by faking the events directly would violate §8.4's "not via internal entry points." The harness advances the clock; the engine generates its own internal events; the harness asserts they fired correctly. This is the cleanest demonstration that the test exercises the production path.

### Why the hard filters do not decide this

[ADR-IC-000](../../integration_concepts/adrs/ADR-IC-000-common-evaluation-criteria.md)'s F1/F2 hard filters are built to *eliminate* candidates. Here they eliminate none: every candidate is free OSS at the scale required, and a load harness generates synthetic data from seeds — it stores no production PII and processes no real customer data. The decision therefore lives in the soft criteria, and specifically in **S2 (ecosystem coherence)**, where the §8 functional requirements bite. The framework is still run in full below (per [ADR-PC-000 D2](./ADR-PC-000-namespace-and-contract-shape-framework.md)), but the load-bearing reasoning is S2.

### The four functional gates §8 imposes

These are not soft preferences; they are the test specification's hard requirements, and they discriminate where F1/F2 do not:

- **G1 · Production boundary.** Drive Redpanda end-to-end and the Kong control plane — not internal entry points (§8.4).
- **G2 · Boundary-measured, async latency.** §8.3 measures *event-receipt-at-engine-boundary → projection-committed*. For async projections there is no response to time; the latency is read from the engine's OpenTelemetry traces/metrics ([ADR-IC-007](../../integration_concepts/adrs/ADR-IC-007-observability-stack.md), [integration_concepts §06](../../integration_concepts/06-observability-and-tracing.md)), not the load tool's send clock. §8.4 also forbids test-only instrumentation that "disappears at production cutover."
- **G3 · End-to-end determinism.** Seeded RNG produces a reproducible event sequence (§8.5); the injected clock makes month-end lifecycle fire at *simulated* month-end. The clock is an engine seam ([ADR-PC-010 §P5](./ADR-PC-010-dotnet-hand-rolled-engine.md)); a failure must reproduce from `(seed, code revision)`.
- **G4 · Engine-team ownership, production-shaped hardware.** Reproducible from version-controlled config; one archived pass/fail artefact per RC; runs every RC through v3 (§8.4).

---

## Evaluation

### Hard filter results

#### F1 · Cost / licensing

| Candidate | Licence (tool + the Kafka path it needs) | Verdict |
|---|---|---|
| In-house .NET harness | Confluent.Kafka .NET (Apache 2.0), OpenTelemetry .NET SDK (Apache 2.0), engine code (engine-team-owned). No third-party load tool. | **Pass** |
| k6 | k6 core AGPL-3.0; `xk6-kafka` extension Apache 2.0 (requires a custom-built k6 binary). | **Pass** |
| Gatling | Gatling OSS Apache 2.0; community `gatling-kafka` plugin. Gatling Enterprise paywalled but not required. | **Pass** |
| Locust | Locust MIT; Kafka via `confluent-kafka-python` (Apache 2.0) in user code. | **Pass** |
| JMeter | Apache 2.0; Kafka via community plugins (Pepper-Box, Apache 2.0). | **Pass** |

All pass F1.

#### F2 · Regulatory fit (GDPR / DORA / PSD2)

A load harness fabricates synthetic data from seeds, runs on EU-resident test infrastructure, and is itself the instrument that discharges **DORA's operational-resilience-testing obligation** ([ADR-IC-000 F2](../../integration_concepts/adrs/ADR-IC-000-common-evaluation-criteria.md)). None of the candidates is structurally disqualified; the choice is regulatory-positive regardless of tool.

| Candidate | Verdict | Note |
|---|---|---|
| In-house .NET harness | **Pass** | Synthetic data only; the harness *is* the DORA performance/resilience test; reliability invariants (§8.3) assert the PSD2 audit trail (no event loss, per-`partition_key` ordering). |
| k6 / Gatling / Locust / JMeter | **Pass** | Same — synthetic data, EU-resident test rig; F2 does not discriminate. |

F1 and F2 leave all five candidates standing. The decision is made on soft criteria, dominated by S2 against gates G1–G4.

### Soft criteria

#### In-house .NET harness — **CHOSEN**

**S1 · Operational complexity for 1–2 people.** One additional .NET project inside the engine solution, run through the same `dotnet` + Testcontainers .NET tooling the team already operates ([ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md)). No separate tool runtime, scripting language, or management plane to learn. The cost is *build* effort (a synthetic generator, a rate-shaping driver, an assertion harness) rather than *operational* surface. Crucially, 250 sustained / 1000 burst TPS is a single-well-sized-producer workload, not a million-RPS distributed-load problem — the harness needs no distributed-load control plane, removing the one thing the generic tools' clustering modes are good at.

**S2 · Ecosystem coherence.** Decisive, and the reason this is chosen. The harness **reuses the engine's own Avro schemas and event-envelope construction** (the `partition_key`, identity trio `correlation_id`/`causation_id`/`message_id`, `pack_version`/`schema_version` columns per [ADR-PC-001 §P1](./ADR-PC-001-event-store-technology.md)) and the same Confluent.Kafka .NET producer — so the bytes the test puts on Redpanda are produced by the *same code* production uses, with zero schema drift between test and production serialization (G1). Boundary latency (G2) is read from the engine's OpenTelemetry .NET spans/metrics via Grafana LGTM ([ADR-IC-007](../../integration_concepts/adrs/ADR-IC-007-observability-stack.md)) — the same telemetry that diagnoses production, satisfying §8.4's "no test-only instrumentation" clause. The injected test clock (G3) shares the engine's clock abstraction directly ([ADR-PC-010 §P5](./ADR-PC-010-dotnet-hand-rolled-engine.md)). The projection-rebuild drill and per-partition ordering assertions (§8.3, [event-store §7.2](../feature-design-event-store-projections.md)) are engine-internal checks the harness coordinates in-process. No glue, no bridge, no parallel re-implementation.

**S3 · Exit cost.** Lowest possible: the harness is engine-team code in the engine repo. "Exit" means deleting code, not migrating off a vendor or a proprietary test-plan format. The archived artefact (G4) is a plain pass/fail report plus raw OTel metrics — standard formats readable by any tool.

**S4 · Community and longevity.** Rides the longevity of .NET 9, Confluent.Kafka .NET, and OpenTelemetry .NET — all already assessed and accepted in [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) and [ADR-IC-007](../../integration_concepts/adrs/ADR-IC-007-observability-stack.md). There is no separate-tool abandonment risk, no community extension to keep current.

#### k6

**S1.** k6 itself is genuinely easy to run — a single Go binary, JavaScript scripting. But Kafka is not core: it needs the community `xk6-kafka` extension compiled into a **custom k6 binary** via `xk6`, which the team must build and keep current against k6 releases — net new operational surface for the one capability that matters most here (G1). **S2.** Weak. k6's native instrument is request/response latency timed from the VU's own clock — the wrong measurement for async event-in→projection-commit (G2), which still has to be read from engine telemetry, leaving k6's core value unused. The Avro envelope would be re-implemented in JavaScript inside the k6 script, reintroducing the schema-drift seam that the in-house path eliminates. The injected clock (G3) is not part of k6's model. **S3.** Low (OSS, JS scripts). **S4.** Healthy, Grafana-backed; AGPL core.

**Decisive reason for not choosing:** k6's strengths (HTTP request orchestration, built-in req/resp latency stats, distributed load) are precisely the parts this test does *not* need, while the parts it does need (Avro-on-Kafka with the engine's envelope, OTel-boundary latency, injected clock) all fall to bespoke script code that duplicates engine logic with no reuse.

#### Gatling

**S1.** Scala/Java DSL — a heavier learning curve for a .NET team, with Kafka via the community `gatling-kafka` plugin. **S2.** Weak, and worse on one specific axis: Gatling brings a **JVM** into an estate that deliberately removed it. Redpanda was chosen over Apache Kafka precisely to eliminate the JVM as the main operational risk for a 1–2 person team (bd memory `adr-001-decided-on-kafka-compatible-ecosystem-implemented`); introducing a JVM-based load tool reintroduces exactly that operational surface at the acceptance-test boundary. Same async-measurement mismatch (G2) and same parallel Avro re-implementation in Scala/Java as k6. **S3.** Low-to-medium. **S4.** Healthy OSS core; Enterprise paywalled.

**Decisive reason for not choosing:** reintroduces the JVM the estate intentionally shed, with no offsetting capability — the async-boundary measurement and engine-envelope reuse still cannot be done from inside Gatling.

#### Locust

**S1.** Python, approachable, distributed mode built in. Kafka requires `confluent-kafka-python` custom code in the locustfile. **S2.** Weak. Locust times its own task durations (a request/response model), again the wrong instrument for async-projection latency (G2). Although a Python sibling exists in the system (the MCP service per [ADR-IC-010](../../integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md)), the engine's event types are .NET — a Locust generator re-encodes the Avro envelope in Python independently, the same schema-drift seam. Locust's headline strength, simulating massive concurrent *users*, is irrelevant at 250/1000 TPS of *events*. **S3.** Low. **S4.** Healthy, MIT.

**Decisive reason for not choosing:** the async-measurement mismatch plus a parallel Python re-implementation of the event envelope, in exchange for a distributed-user-simulation capability the workload does not require.

#### JMeter

**S1.** GUI-and-XML test plans; Kafka via community plugins. Heavyweight, JVM-based. **S2.** Weakest of the field. Same JVM-reintroduction objection as Gatling; XML test plans are not the "reproducible from version-controlled config" that §8.4 (G4) wants — they are not readable diffs and resist code-review. Determinism and an injected clock (G3) are outside its model, and the async-boundary latency (G2) is again unreachable from the tool. **S3.** Medium (XML/GUI lock-in). **S4.** Apache, long-lived.

**Decisive reason for not choosing:** JVM reintroduction, XML test plans that fight the version-controlled-config and determinism requirements, and the same async-measurement mismatch — the worst fit against G2–G4.

---

## Decision

### Chosen: an **in-house .NET load-test harness** that drives the engine through Redpanda and the Kong control plane and measures latency from the engine's OpenTelemetry telemetry.

The decisive force is **S2 ecosystem coherence against gates G1–G4**. Because the engine's input boundary is Avro-on-Redpanda (not HTTP) and the §8.3 latency is an async, boundary-observed quantity read from OpenTelemetry (not a request/response time), the generic load tools' core value — HTTP request orchestration with built-in response-latency statistics and distributed-user simulation — is largely inapplicable. What this test actually needs is (a) a deterministic, domain-aware synthetic event generator producing the §8.2 mix and peak structure with correct `partition_key`s, (b) a producer that puts the engine's *own* Avro envelopes on the bus, (c) latency read from the engine's *own* OTel spans, and (d) coordination of the injected clock and the projection-rebuild drill. Every one of those is engine code or a reuse of it. Building it in .NET inside the engine solution reuses the envelope construction, the Confluent.Kafka producer, the clock seam, and the LGTM telemetry directly, with zero schema drift and zero JVM or extension to operate. This is the same hand-rolled, fully-owned posture [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) adopted for the engine core, applied to the engine's acceptance harness.

**On the bd issue's "Go/Rust" framing.** Superseded by [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md). A Go or Rust harness would re-encode the Avro event envelope and schemas in a second language, reintroducing exactly the parallel-implementation and schema-drift seam that the .NET-and-reuse argument exists to remove. The in-house decision stands; the language is .NET, to keep the harness inside the engine's own serialization and telemetry code.

**Rejected: k6** — its strengths (HTTP orchestration, req/resp latency, distributed load) are unused here; Avro-on-Kafka, OTel-boundary latency, and the injected clock all fall to bespoke JS that duplicates engine logic, plus a custom-built k6 binary to maintain.

**Rejected: Gatling** and **JMeter** — both reintroduce the JVM the estate deliberately removed ([ADR-IC-001](../../integration_concepts/adrs/ADR-IC-001-event-backbone-message-broker.md) / Redpanda), with no capability that offsets the async-measurement mismatch; JMeter additionally fights the version-controlled-config and determinism gates with its GUI/XML model.

**Rejected: Locust** — async-measurement mismatch and a parallel Python re-implementation of the event envelope, traded for distributed-user simulation the 250/1000 TPS workload does not need.

A generic tool may still be used freely in throwaway spikes (e.g., a quick k6 smoke test of a single REST endpoint); it does not become the v1 acceptance harness.

---

## Consequences

**What this choice makes easier:**

1. **Test bytes are production bytes (G1).** The harness emits events through the engine's own Avro envelope construction and Confluent.Kafka producer, so a passing test exercises the exact serialization path production uses — no class of "the test encoder and the prod encoder diverged" bug.
2. **One measurement plane (G2).** Latency, throughput, and backlog come from the same OpenTelemetry / LGTM telemetry that runs in production ([ADR-IC-007](../../integration_concepts/adrs/ADR-IC-007-observability-stack.md)), satisfying §8.4's "diagnosable from production-grade telemetry, no test-only instrumentation" clause by construction.
3. **Determinism is shared, not simulated (G3).** The harness drives the engine's injected clock seam ([ADR-PC-010 §P5](./ADR-PC-010-dotnet-hand-rolled-engine.md)) directly; `(seed, code revision)` reproduces a run, and month-end lifecycle events fire at simulated month-end because the engine — not the harness — emits them.
4. **Ownership is automatic (G4).** The harness *is* engine-team code in the engine repo, reproducible from version control, exercised by the same Testcontainers .NET fixtures ([ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md)) as the rest of the suite. No external tool to provision per RC.
5. **No JVM, no extension, no second language** in the test path — consistent with the Redpanda-over-Kafka rationale and ADR-PC-010's single-runtime posture.

**What this choice makes harder or impossible:**

1. **The team builds the generator, the rate driver, and the assertion harness.** No off-the-shelf TPS scheduler, distributed-load coordinator, or results dashboard. *Mitigation:* the peak-shaping and event-mix logic (§8.2) would be bespoke script code in *any* tool — in-house adds no net authoring cost there, it removes the impedance mismatch; the TPS driver is a bounded concern (a rate-scheduled producer with a peak-shape envelope), and 250/1000 TPS needs no distributed control plane. Results render in the existing Grafana ([ADR-IC-007](../../integration_concepts/adrs/ADR-IC-007-observability-stack.md)).
2. **No vendor's tuned high-throughput load engine.** A future need to exceed the §8.3 burst budget by an order of magnitude (the v4-time backpressure question [Q-AM](../04-open-questions.md), explicitly out of v1 scope per §8.6) could justify revisiting; the in-house producer would need scaling work a mature tool gives for free. *Mitigation:* Q-AM is a deliberate v1 non-goal; revisit at v4 when the real workload replaces the synthetic test anyway (§8.4 cadence).
3. **The harness is engine-version-coupled.** Reusing engine envelope/clock code means a breaking engine change can break the harness build. *Mitigation:* this coupling is the point — it is what guarantees test/prod parity; it surfaces drift at compile time rather than hiding it behind a tool boundary.

**Residual risk:**

- §8.1 operator-calibration numbers (`N_acct`, `N_card`, `E_year`) are still pending; the harness is built against the §8.2 *shape*, parameterised so the absolute size is config. This ADR does not unblock that calibration — it is tracked under [Q-AK](../04-open-questions.md).
- The three §8.6 non-goals (sharding [Q-AL](../04-open-questions.md), backpressure [Q-AM](../04-open-questions.md), cross-mode invariant under load [Q-AN](../04-open-questions.md)) are out of scope; the harness must not foreclose them but does not validate them.

---

## Implementation Principles

### P1 — The harness is three modules: generator, driver, observer

**Generator** — a deterministic, seeded function `(seed, calibration, simulated_window) → event stream` producing the §8.2 event mix with correct `partition_key`s ([two-modes §5.3](../feature-design-two-modes-asymmetry.md)) and the daily/monthly/annual peak envelope (§8.2). Different seeds exercise different data shapes (uniform vs clustered activity, normal vs heavy-tailed amounts). It emits only the ~85% externally-ingested classes plus the operational externals; engine-generated lifecycle (~10%) and cross-mode (~3%) are produced by the engine when the clock advances. **Driver** — a rate-scheduled Confluent.Kafka .NET producer that realises the generator's stream onto Redpanda at the target TPS/peak shape, plus a small Kong-fronted REST client for the control plane (rate-sheet deploy, pack adoption, freezes) and the test-clock advance. **Observer** — reads the engine's OpenTelemetry spans/metrics from LGTM and evaluates the §8.3 thresholds.

### P2 — Latency is asserted from OpenTelemetry, never from the driver's send clock

The §8.3 sync bands (`current_balance` p99 < 200 ms, etc.) are *event-receipt-at-boundary → projection-committed* (G2). The engine emits a span (or a histogram metric) covering exactly that interval ([ADR-IC-007](../../integration_concepts/adrs/ADR-IC-007-observability-stack.md), [integration_concepts §06](../../integration_concepts/06-observability-and-tracing.md)); the observer reads the p50/p95/p99 from that telemetry. The driver's own publish-confirm time is *not* the metric — for async projections there is no synchronous response at all. Async batch budgets (statement_cycle within 6h of cycle close, withholding within 4h of daily close) are asserted against the *simulated* close instant from the injected clock (§8.5), not wall-clock.

### P3 — The injected clock is the engine's seam, driven through a control API

Per [ADR-PC-010 §P5](./ADR-PC-010-dotnet-hand-rolled-engine.md) the engine accepts an injected clock for determinism. The harness advances simulated time through a production control operation so that `DailyAccrualClosed`, `StatementCycleClosed`, and `FeeAssessed` fire at simulated boundaries and the engine emits them itself (§8.4: not via internal entry points). A 24-hour sustained run with one simulated payday and the synthetic annual-peak day compresses to a bounded wall-clock run by clock injection, but the *throughput* dimensions (250 TPS sustained, 1000 TPS burst) run against real wall-clock rate on production-shaped hardware — the clock governs *domain* time, not the producer's emission rate.

### P4 — Reproducibility artefact: `(seed, code revision) → pass/fail report + raw metrics`

Every run names its RNG seed and the engine code revision; the archived artefact (§8.4) is a pass/fail report plus the raw OTel metric series, stored per RC. A failure that does not reproduce from `(seed, code revision)` is escalated above a deterministic failure (§8.5) — it implies engine-level non-determinism. The harness config (calibration numbers, hardware sizing, topology) is version-controlled alongside the engine.

### P5 — The projection-rebuild drill is the final assertion, run in-process

As the test's last step, the observer triggers a cold rebuild of every projection from the event log and asserts bit-for-bit equality with the running projection ([event-store §7.2](../feature-design-event-store-projections.md), §8.3). The per-`partition_key` ordering check (delivered order == event-store order) and the replay-budget checks (irregular instance < 30 s; 100k-account snapshot population < 1 h, [event-store §8.1–§8.2](../feature-design-event-store-projections.md)) run in the same harness, against the same Testcontainers-or-production-shaped fixture ([ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md)).

---

## Open Actions

1. **Operator calibration (§8.1)** — obtain `N_acct`, `N_card`, `E_year` from the operating bank and wire them into the harness config; the shape is independent of size but the thresholds run against real numbers. Tracked under [Q-AK](../04-open-questions.md).
2. **Engine latency-span contract** — confirm the engine emits the boundary-to-commit span/metric P2 reads (a small ADR-PC-010 / [ADR-IC-007](../../integration_concepts/adrs/ADR-IC-007-observability-stack.md) implementation detail), so the observer has a defined telemetry surface to assert against.
3. **Production-shaped sizing (§8.4)** — name the exact hardware profile in the harness config; it must match the v1 production deployment target, not a developer laptop or an oversized cluster.
4. **CI cadence wiring (§8.4)** — gate the v1 RC pipeline on a green run; schedule the every-minor-release re-run through v3.

---

## Cross-references

- [two-modes §8](../feature-design-two-modes-asymmetry.md) — the full test specification this ADR tools; [§5.6](../feature-design-two-modes-asymmetry.md) makes it a v1 acceptance gate.
- [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) — C# / .NET 9 hand-rolled engine; fixes the harness language and the injected-clock seam (§P5); this ADR resolves its Open Action #1.
- [ADR-PC-001](./ADR-PC-001-event-store-technology.md) — PostgreSQL event store and the §P1 envelope the harness reuses; the topology this test gates.
- [ADR-IC-001](../../integration_concepts/adrs/ADR-IC-001-event-backbone-message-broker.md) — Redpanda backbone; the ingest boundary the harness drives (and the no-JVM rationale that weighs against Gatling/JMeter).
- [ADR-IC-002](../../integration_concepts/adrs/ADR-IC-002-schema-format-and-registry.md) — Avro + Confluent SR; the envelope format the harness emits.
- [ADR-IC-006](../../integration_concepts/adrs/ADR-IC-006-edge-api-gateway.md) — Kong gateway; the control-plane boundary.
- [ADR-IC-007](../../integration_concepts/adrs/ADR-IC-007-observability-stack.md) / [integration_concepts §06](../../integration_concepts/06-observability-and-tracing.md) — OpenTelemetry / Grafana LGTM; the measurement plane (G2).
- [ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md) — Testcontainers; the fixture tooling the harness reuses.
- [04 Q-AK, Q-AL, Q-AM, Q-AN](../04-open-questions.md) — the resolved spec and the three deferred non-goals (§8.6).

---

*Decided 2026-05-23 by jhosm. The in-house option is .NET (not the bd issue's original Go/Rust framing), per [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md).*
