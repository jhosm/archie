# ADR-004: Outbox Pattern Mechanism

| Field | Value |
|---|---|
| Status | Accepted |
| Date | 2026-05-17 |
| Deciders | jhosm |
| Common criteria | [ADR-000](./ADR-000-common-evaluation-criteria.md) |
| Depends on | [ADR-001](./ADR-001-event-backbone-message-broker.md), [ADR-002](./ADR-002-schema-format-and-registry.md) |

---

## Context

Document 04 establishes the outbox pattern as the non-negotiable primitive for dual-write safety: the domain state change and the integration event are written to the application database in the same local transaction, then a relay process reads the outbox table and publishes to Redpanda. This eliminates the scenario where the database is updated but the event is never published — the silent data corruption that in banking means Core never debits, Compliance never registers, and the client is never notified.

Document 04 also draws a hard line between acceptable and unacceptable CDC use: CDC on the outbox table is a delivery mechanism (reading rows the application explicitly wrote as domain events), while CDC on domain tables is an anti-pattern (inferring event semantics from storage mutations). This ADR is concerned only with the delivery mechanism.

What remains to be decided is the concrete relay mechanism: how the outbox table is polled or monitored, how events are published to Redpanda, how the relay is made safe for multiple concurrent instances, and how lag is observed and alarmed. This choice has implications for operational complexity, latency, and the JVM constraint established by ADR-001.

**Candidates evaluated:**

| # | Candidate | Notes |
|---|---|---|
| A | **Debezium + Kafka Connect** | Reads PostgreSQL WAL via logical replication; Outbox Event Router SMT routes to per-aggregate-type topics |
| B | **Custom polling publisher** | Application-owned worker; periodic `SELECT … FOR UPDATE SKIP LOCKED`; zero additional infrastructure |
| C | **Eventuate Tram** | Java microservices library with built-in transactional outbox (polling and CDC modes) |
| D | **Debezium Embedded Engine** | Debezium WAL reader embedded in the application process; no standalone Kafka Connect cluster |

---

## Evaluation

### Hard filter results

#### F1 · Cost / licensing

| Candidate | Licence | Assessment | Proceeds? |
|---|---|---|---|
| Debezium + Kafka Connect | Apache 2.0 (Debezium); Apache 2.0 (Kafka Connect, via Redpanda's Connect-compatible runtime) | Fully open source; self-hosted | **Pass** |
| Custom polling publisher | N/A — no additional tool | Zero incremental cost; uses the application database and Redpanda client already present | **Pass** |
| Eventuate Tram | Apache 2.0 (framework) | Open source; self-hosted | **Pass** |
| Debezium Embedded Engine | Apache 2.0 | Fully open source; embedded within the application JVM | **Pass** |

All candidates pass F1. No candidate introduces a paywalled feature required by this architecture.

*Date of licence assessment: 2026-05-17. Licence terms can change; verify before production hardening.*

#### F2 · Regulatory fit

The outbox table holds full integration event payloads — `client_id`, account numbers, amounts, IBANs. All candidates read from or interact with this data. The regulatory surface is the same across candidates: the outbox table is in the application database, which is already subject to the domain GDPR erasure strategy. The relay mechanism does not create a new persistent copy of the data (events are consumed from the outbox and published to Redpanda, which is governed by ADR-001 and ADR-002).

| Candidate | GDPR | DORA | PSD2 | Proceeds? |
|---|---|---|---|---|
| Debezium + Kafka Connect | WAL-based CDC reads logical replication slots in PostgreSQL. The WAL itself contains PII — but this is a transport path, not a new durable store. Debezium does not persist WAL data independently; it decodes and forwards. The Kafka Connect worker holds events in memory during transit; persistent offset storage (in a Kafka topic) records only the WAL offset, not the event payload. No new GDPR surface beyond the application database. | Kafka Connect is a stateless relay; its resilience is derived from PostgreSQL WAL durability and Redpanda's own guarantees (ADR-001). Resilience testing targets the application database and Redpanda, which are already DORA obligations. | Logical replication slot offsets provide an auditable delivery position. The Redpanda topic provides the ordered audit trail per ADR-001 guarantees. | **Pass** |
| Custom polling publisher | All data remains in the application database until published to Redpanda. No additional GDPR surface. | Resilience is entirely within the application database (already a DORA target) and Redpanda. | The outbox table itself is an ordered record of published events; the Redpanda topic is the durable audit trail. | **Pass** |
| Eventuate Tram | Same as the relay mechanism it uses under the hood (CDC or polling). No additional GDPR surface beyond the chosen relay mode. | No additional infrastructure beyond what the relay mode requires. | No difference from other approaches. | **Pass** |
| Debezium Embedded Engine | Same WAL-based CDC properties as Debezium + Kafka Connect. WAL data is decoded in-process and forwarded; no independent persistent store of event payloads. | Resilience depends on the host application process — the embedded engine fails if the application fails. No independent DORA target. | Same audit trail properties as standalone Debezium. | **Pass** |

All four candidates pass both hard filters.

---

### Soft criteria

#### Debezium + Kafka Connect

**S1 · Operational complexity:** Kafka Connect requires a Connect worker cluster (JVM) plus the Debezium PostgreSQL connector JAR and configuration. PostgreSQL must be configured with `wal_level = logical` and a logical replication slot. The replication slot is a PostgreSQL operational concern: a stalled or unacknowledged slot will cause WAL accumulation and disk pressure on the database host, which is a production incident risk if not monitored. Connector lifecycle (deployment, version upgrades, dead-letter queue configuration) is a separate operational surface from the application and from Redpanda. For a 1–2 person team, this is the second highest operational footprint after Conductor-OSS (ADR-003).

**S2 · Ecosystem coherence:** The Outbox Event Router SMT is a mature, purpose-built Debezium feature that maps outbox table columns (`aggregate_type`, `aggregate_id`, `event_type`, `payload`) to the correct Redpanda topic and partition key. This transforms Debezium from a generic CDC tool into an outbox-specific relay with first-class routing semantics. OpenTelemetry instrumentation is available via the JVM agent on the Connect worker. The integration is technically coherent but introduces a JVM Connect cluster as the delivery critical path — a decision ADR-001 deliberately avoided for the broker itself.

**S3 · Exit cost:** Medium-low. The outbox table schema is independent of Debezium — it is a standard application table and can be read by any relay mechanism. Connector configuration (SMT definitions, offset topic, schema registry references) is Debezium/Kafka-Connect-specific JSON and would need to be re-expressed for a different relay, but this is configuration, not application code. The exit cost is bounded to connector reconfiguration.

**S4 · Community and longevity:** Debezium is a Red Hat project with a large and active community, CNCF sandbox status, and wide production adoption in change-data-capture use cases beyond the outbox pattern. Longevity is strong. The PostgreSQL connector is one of Debezium's most mature connectors with a long release history.

---

#### Custom polling publisher

**S1 · Operational complexity:** Zero incremental infrastructure. The publisher is a lightweight module — either a background thread in the application or a thin standalone service — that executes a periodic `SELECT … FOR UPDATE SKIP LOCKED` against the application database and publishes resulting rows to Redpanda using the same Kafka client library used by every other service in the stack. No additional process to monitor independently of the application; no JVM dependency; no connector configuration; no replication slot lifecycle. The additional latency (one polling interval, configurable from 100ms to a few seconds) is acceptable for all known consumers at Portuguese banking volumes.

**S2 · Ecosystem coherence:** Maximum coherence. The publisher reads from the same PostgreSQL instance as the domain model using a standard JDBC or pg driver, and publishes via the same Kafka/Redpanda client library used by every producer in the system. Observability (OpenTelemetry spans, outbox lag metric, publisher heartbeat) uses the same instrumentation pipeline as the rest of the application. No new wire protocol, no new SDK, no adapter layer. The `outbox_lag` metric (age of the oldest `PENDING` row) is a standard application metric, not a connector metric, and is surfaced via the application's own Prometheus scrape endpoint.

**S3 · Exit cost:** Lowest possible. The publisher is application code with no vendor SDK or framework dependency beyond the Kafka client library already present. Replacing or modifying the publisher requires changing application code only.

**S4 · Community and longevity:** Not applicable — there is no external vendor. The approach depends on the application's own engineering, PostgreSQL's `SELECT FOR UPDATE SKIP LOCKED` guarantee (stable since PostgreSQL 9.5), and the Redpanda Kafka client. All three have multi-decade stability horizons.

**Where this approach requires more explicit implementation effort than the CDC alternatives:**

- **Polling interval tuning:** the interval must be short enough to meet the lag SLA (alerting threshold: oldest `PENDING` row older than a defined threshold) but not so short as to create unnecessary read pressure. The recommendation is 200ms as a starting point, backed by a `pg_stat_activity` check during load testing to verify the polling query does not appear in slow-query logs.
- **HA publisher coordination:** multiple publisher instances (for fault tolerance) must coordinate to avoid duplicate publication in the same polling window. The `SELECT … FOR UPDATE SKIP LOCKED` pattern is the standard PostgreSQL mechanism and is sufficient; the inbox deduplication at consumers absorbs any duplicate that slips through on reconnect.
- **Lag alerting:** the publisher must emit a `outbox_publish_lag_seconds` metric (age of the oldest `PENDING` row at each poll cycle). Alert threshold is a deployment-time decision; a suggested default is 30 seconds for normal operations and 5 minutes as a critical threshold.

---

#### Eventuate Tram

**S1 · Operational complexity:** Eventuate Tram is a Java library. Using it requires the application to run on the JVM (or at minimum that the outbox relay runs as a separate JVM service). This reintroduces JVM operational complexity — the same concern ADR-001 raised against Apache Kafka and ADR-003 raised against Axon Framework — for the component that, in the custom approach, requires no additional runtime at all. The CDC mode of Eventuate Local requires the same Kafka Connect + Debezium infrastructure as candidate A, giving it the worst combination: both JVM dependency and Kafka Connect operational surface.

**S2 · Ecosystem coherence:** Eventuate Tram is designed for a Spring Boot / Spring Data ecosystem and its abstractions (the `MessageProducer` interface, `@Transactional` annotations on outbox writes, the `SagaManager`) permeate the domain model's Spring wiring. In this architecture — where the domain model is not Spring-bound by any prior decision — adopting Eventuate Tram to solve the outbox relay problem is disproportionate: it introduces framework-level coupling across the producer's domain code to solve a problem that the polling publisher solves in a single infrastructure class.

**S3 · Exit cost:** Highest of the four candidates. Eventuate's annotations and abstractions appear in domain aggregate code (the outbox write is mediated by the framework rather than by a direct SQL insert), which means replacing Eventuate requires touching domain classes — not just infrastructure configuration.

**S4 · Community and longevity:** Single-vendor (eventuate.io). The community is substantially smaller than Debezium's. Recent release activity has slowed compared to the 2018–2020 period when the Eventuate pattern book was published. The longevity risk is non-trivial for a 1–2 person team that cannot absorb a framework going unmaintained.

---

#### Debezium Embedded Engine

**S1 · Operational complexity:** The embedded engine runs within the application process, eliminating the standalone Kafka Connect cluster. This is a meaningful reduction in operational surface compared to candidate A. However, the embedded engine still requires the application to be a JVM process (or a JVM sidecar) and still requires `wal_level = logical` on PostgreSQL and replication slot management. The embedded engine adds JVM heap pressure to the application process — the WAL decoder runs in the same JVM as the domain logic — which complicates GC profiling and heap sizing. The replication slot lifecycle risk (stalled slot → WAL accumulation → disk pressure) remains unchanged from candidate A.

**S2 · Ecosystem coherence:** Better than standalone Kafka Connect but still requires the Debezium API surface within the application. The embedded engine API (`EmbeddedEngine`, `ChangeEventSourceCoordinator`) is a secondary usage pattern compared to the standalone connector, and its documentation and community examples are thinner. The Outbox Event Router SMT is not directly usable in embedded mode — the application must implement equivalent routing logic, reducing the feature advantage over the polling publisher.

**S3 · Exit cost:** Medium. The CDC logic is in application code rather than connector configuration, but it depends on Debezium APIs. Replacement requires removing the Debezium dependency and rewriting the relay logic — more work than replacing a connector configuration, but less than an Eventuate migration.

**S4 · Community and longevity:** Debezium's main community investment is in the standalone connector model. The embedded engine is a supported feature, but its trajectory follows Debezium's overall direction — which is positive — while its specific API surface receives less community testing and documentation than the connector path.

---

## Decision

**Chosen: Custom polling publisher**

Debezium (candidate A) is the strongest rejected candidate and deserves an honest assessment before the positive case for the polling publisher is made.

Debezium would be the right call if: the system operates at a volume where a 200ms–1s polling interval creates a measurable, operational problem — that is, if consumers downstream of the outbox have latency requirements that make a 200ms relay lag unacceptable, or if the read pressure of the polling query appears in slow-query analysis at production volumes. For Portuguese banking at term deposit scale (thousands of operations per day, not millions per second), neither condition holds. The throughput argument for CDC is not triggered.

The operational overhead is not yet amortized for three reasons.

**First, the JVM constraint from ADR-001 applies here.** ADR-001 rejected Apache Kafka specifically to eliminate the JVM from the event backbone. Adopting Debezium + Kafka Connect reintroduces a JVM process — and a more operationally complex one than Kafka, because the Connect worker, connector lifecycle, and SMT configuration form their own operational surface. At large scale, this cost is shared across many connectors and justified. At POC scale with a single outbox table, it is paid in full to replace a 30-line polling loop.

**Second, the replication slot lifecycle is a production risk that the polling publisher does not introduce.** A PostgreSQL logical replication slot that stalls — because the Connect worker is restarting, or because the slot is not being consumed — causes WAL segments to accumulate on the database host until disk fills. This is a well-known operational hazard for Debezium in production. Monitoring and alerting for slot lag is standard practice, but it is an additional operational obligation with a failure mode (disk full → database unavailable) that does not exist for the polling publisher.

**Third, the Outbox Event Router SMT's value is reduced for a small schema.** The SMT provides routing logic for multi-aggregate outbox tables (routing to different Redpanda topics based on `aggregate_type`). The same logic is trivially implemented in the polling publisher's topic-selection code. The SMT's value is proportional to the number of aggregate types sharing an outbox table; at POC scale, this is not a differentiating advantage.

The upgrade path is clean: when the system operates at a volume where polling lag is operationally significant, adding Debezium (standalone Kafka Connect) does not require changing the outbox table schema or the producer logic. The outbox table is the interface; the relay mechanism is replaceable behind it.

---

**Rejected: Debezium + Kafka Connect**

The operational overhead — JVM Connect cluster, replication slot lifecycle, connector configuration management — is not justified at POC volumes where a polling interval of 200ms delivers adequate relay latency. The replication slot failure mode (stalled slot → WAL accumulation → disk pressure) introduces a new production risk not present in the polling approach. Debezium is the natural upgrade when polling lag becomes a measured operational problem; the outbox table schema is relay-agnostic.

**Rejected: Eventuate Tram**

Reintroduces JVM operational complexity and introduces Spring/framework coupling into domain aggregate code to solve a problem the polling publisher solves with a standard SQL query and a Kafka producer. The longevity risk (single-vendor, slowing release activity) and the highest exit cost of the four candidates combine to make this the clearest rejection.

**Rejected: Debezium Embedded Engine**

Reduces the operational surface compared to standalone Kafka Connect but retains the JVM dependency in the application process and the replication slot lifecycle risk. The embedded engine API is less documented than the connector API, and the Outbox Event Router SMT is not usable in embedded mode, which removes the main functional advantage of the Debezium family over the polling publisher at this scale.

---

## Consequences

**What this choice makes easier:**

- The relay is part of the application codebase — readable, testable, and modifiable without touching connector configuration in a separate runtime. Integration tests can start the publisher as part of the test harness alongside the application, with no stub or mock.
- Observability is uniform: the publisher emits spans and metrics using the same OpenTelemetry setup as the rest of the application. The `outbox_publish_lag_seconds` metric is a first-class application metric, not a connector JMX metric requiring a separate scrape configuration.
- The outbox table schema is relay-agnostic. The columns the publisher reads (`event_id`, `aggregate_id`, `event_type`, `payload`, `status`, `created_at`) are standard SQL; no CDC-specific metadata columns are required.
- No additional infrastructure to provision, monitor, or upgrade. The publisher is a service module, not a platform.

**What this choice makes harder or impossible:**

- **Sub-100ms relay latency** is not achievable with a polling interval approach. If a consumer SLA requires near-real-time event delivery (relay lag < 50ms), this mechanism cannot deliver it without a polling interval that imposes read load the database team may reject. The assumption is that all consumers in this architecture can tolerate the outbox relay latency; if a synchronous-latency use case arises, the answer is a direct synchronous channel, not a faster relay.
- **Zero-overhead CDC for very high write rates** is not available. At Portuguese banking volumes this is not a constraint, but the team must revisit if write rates grow by two or more orders of magnitude.
- **Automated topic creation** via the Outbox Event Router SMT's routing rules is not available. The polling publisher's topic-selection logic must be kept in sync with new aggregate types as the domain grows. This is not complex, but it is a manual discipline.

**Residual risks:**

- **Publisher HA coordination:** multiple publisher instances polling concurrently are safe because `SELECT … FOR UPDATE SKIP LOCKED` serializes access at the row level. However, if two instances publish the same row (possible in a crash-at-the-moment-of-status-update window), the consumer inbox must absorb the duplicate. The inbox idempotency pattern from Document 04 is mandatory — not optional — for every consumer in this architecture, and the dual-publish window is the mechanism that makes this non-negotiable.
- **Polling query degradation:** as the outbox table grows (if cleanup is not running), the `SELECT … FOR UPDATE SKIP LOCKED WHERE status = 'PENDING'` query degrades without a partial index. A partial index on `(created_at, event_id) WHERE status = 'PENDING'` must be created at schema initialization time, not added after the table has grown.
- **Upgrade path coordination:** when the team upgrades from polling to Debezium CDC, both mechanisms must not run simultaneously against the same outbox table — the polling publisher's status update (`UPDATE outbox SET status = 'PUBLISHED'`) conflicts with Debezium's append-only WAL consumption model (Debezium does not depend on the `status` column — it reads WAL offsets). The migration sequence is: deploy Debezium, drain the polling publisher, disable the polling publisher, verify CDC delivery via consumer lag metrics, then optionally repurpose or drop the `status` column.

---

## Implementation Principles

The outbox table and its publisher are shared infrastructure: every domain service that writes integration events depends on them. Without explicit constraints, implementations will diverge in schema conventions, retry semantics, and monitoring contracts. The following principles define the minimum shared discipline.

---

### P1 — Outbox table schema is a contract, not an implementation detail

The outbox table must have the following columns across every service that implements one:

| Column | Type | Purpose |
|---|---|---|
| `event_id` | UUID, PK | Stable message identifier; carried into the Redpanda record header as `message_id` |
| `aggregate_type` | VARCHAR | Topic routing key (e.g. `term_deposit`) |
| `aggregate_id` | UUID | Partition routing key; determines Redpanda topic partition |
| `event_type` | VARCHAR | Event name as registered in the schema registry (ADR-002) |
| `payload` | BYTEA | Avro-serialized event payload |
| `schema_id` | INTEGER | Schema registry ID embedded at write time; re-validated at publish time |
| `status` | VARCHAR | `PENDING` or `PUBLISHED`; indexed via partial index on `PENDING` rows |
| `created_at` | TIMESTAMPTZ | Write time; used for `ORDER BY` and lag calculation |
| `published_at` | TIMESTAMPTZ, nullable | Set when the relay successfully produces to Redpanda and receives an ack |

Services may add columns (e.g. `correlation_id`, `causation_id` from Document 01 Primitive 4), but must not omit the above. The publisher reads only the above columns; additional columns are invisible to the relay.

---

### P2 — Publish order within an aggregate is a hard constraint

The publisher must read with `ORDER BY created_at, event_id` and publish sequentially per `aggregate_id`. Redpanda partitions by `aggregate_id`; partition order is guaranteed only if the publisher respects creation order within the partition. The publisher must not publish rows for the same `aggregate_id` concurrently. `SKIP LOCKED` resolves the HA coordination problem (multiple publisher instances) but does not guarantee order across instances for the same aggregate — the publisher must acquire a lock granularity that prevents concurrent in-flight rows for the same `aggregate_id`.

The simplest correct implementation: poll with `LIMIT N ORDER BY created_at, event_id`, publish each row synchronously (produce + await ack) before marking `PUBLISHED` and moving to the next row. Throughput is a secondary concern at banking volumes; order is not.

---

### P3 — The `schema_id` is embedded at write time, not resolved at publish time

The Avro schema ID (from the schema registry, ADR-002) must be resolved and stored in the outbox row at the time the domain transaction writes the event. The publisher reads the `schema_id` from the row and constructs the Confluent wire-format envelope (`magic byte + schema_id + avro payload`) directly, without a schema registry lookup at publish time. This eliminates a runtime dependency on the schema registry in the publish path and ensures that a schema registry outage cannot stall the relay.

At publish time, the publisher must validate that the `schema_id` in the row is still registered (not deleted or superseded) — a defensive check that can be satisfied by a cached registry client with a short TTL. This check is advisory at POC scale; it becomes mandatory before any production hardening that involves schema deletion.

---

### P4 — Lag is a first-class SLI, not an afterthought

The publisher must emit a `outbox_publish_lag_seconds` gauge at every polling cycle — the age in seconds of the oldest `PENDING` row at the time of the poll. This metric must be scraped by the application's Prometheus endpoint and alarmed at two thresholds:

- **Warning:** oldest `PENDING` row is older than 30 seconds. Indicates the publisher is running but Redpanda is slow or backpressured.
- **Critical:** oldest `PENDING` row is older than 5 minutes. Indicates the publisher is not running or Redpanda is unavailable.

The polling interval itself is not a metric that consumers can observe; `outbox_publish_lag_seconds` is the correct SLI because it captures end-to-end delivery health, not just publisher liveness.

---

### P5 — Cleanup is part of the schema, not a deferred task

A nightly batch job or rolling retention window must move or delete `PUBLISHED` rows beyond the retention horizon (recommended: 7 days as default, 30 days for products where audit windows require it). The partial index on `status = 'PENDING'` keeps the polling query fast even as the total table size grows — but only if the partial index exists from day one. The cleanup job must `DELETE WHERE status = 'PUBLISHED' AND published_at < NOW() - INTERVAL '7 days'` — it must never touch `PENDING` rows.

---

### P6 — The outbox write and the domain transaction are always the same transaction boundary

No service may write to the outbox table outside a transaction that also writes the corresponding domain state. The outbox table must be in the same PostgreSQL database as the domain state it records (not a shared outbox database). Cross-database transactions violate the local-atomicity guarantee that makes the outbox pattern correct. If a service uses a separate database for read models or process managers, events for those state changes must be written via the appropriate local outbox in the same transaction, not forwarded to a shared outbox.

---

### P7 — The publisher treats Redpanda unavailability as backpressure, not failure

If Redpanda is unavailable, the publisher must retry with exponential backoff up to a configured ceiling, then enter a wait loop — polling the outbox table, failing to produce, and alerting via the lag SLI. It must never mark rows as `FAILED` or abandon them; the outbox is the source of truth, and rows remain `PENDING` until they are successfully published. The practical consequence is that during a Redpanda outage, the application's domain database absorbs the event backlog (events accumulate as `PENDING` rows). This is the intended behavior: the outbox table is the durability buffer. The lag SLI alert (P4) notifies the operator before the buffer grows to a problematic size.
