# ADR-001: Event Backbone — Message Broker Choice

| Field | Value |
|---|---|
| Status | Accepted |
| Date | 2026-05-17 |
| Deciders | jhosm |
| Common criteria | [ADR-000](./ADR-000-common-evaluation-criteria.md) |

---

## Context

The integration series (documents 00–10) assumes an event-driven backbone from the outset. Documents 00 and 04 name Kafka explicitly: document 00 recommends it for greenfield contexts alongside a schema registry; document 04 builds the outbox, inbox, and ordering guarantees entirely around Kafka semantics (partitioning by `aggregate_id`, `acks=all`, topic ACLs, at-least-once delivery with idempotent consumers).

This ADR makes that assumption explicit, evaluates the realistic alternatives, and records why Kafka (specifically in a Redpanda-compatible form) is the right choice for this architecture at zero budget and 1–2 people.

**Candidates evaluated:**

| # | Candidate | Notes |
|---|---|---|
| A | **Apache Kafka** (KRaft mode) | The assumed baseline throughout the series |
| B | **Redpanda** (Community Edition) | Kafka-API-compatible; C++ single binary; Apache 2.0 |
| C | **Apache Pulsar** | Alternative with BookKeeper-backed tiered storage |
| D | **RabbitMQ Streams** | Streams feature added in RabbitMQ 3.9 |
| E | **NATS JetStream** | Embedded in NATS Server; C-based; Apache 2.0 |

Candidates A and B are evaluated as a pair — Redpanda is not a separate broker but a wire-compatible implementation of the Kafka protocol. The decision is for "the Kafka ecosystem"; the implementation recommendation (Redpanda vs Apache Kafka) follows from the soft criteria.

---

## Evaluation

### Hard filter results

#### F1 · Cost / licensing

| Candidate | Licence | Assessment | Proceeds? |
|---|---|---|---|
| Apache Kafka | Apache 2.0 | Fully open source; self-hosted | **Pass** |
| Redpanda Community | Apache 2.0 | Open source community edition covers all POC features | **Pass** |
| Apache Pulsar | Apache 2.0 | Fully open source; self-hosted | **Pass** |
| RabbitMQ | MPL 2.0 | Mozilla Public Licence — permissive for use; no financial-services restriction | **Pass** |
| NATS JetStream | Apache 2.0 | Fully open source; self-hosted | **Pass** |

No candidate fails F1. All are self-hostable at zero cost. Redpanda Enterprise and Confluent Cloud both have paywalled tiers, but neither is required here.

*Date of licence assessment: 2026-05-17. Free-tier limits and licence terms can change; verify before production hardening.*

#### F2 · Regulatory fit

The Portuguese banking context imposes GDPR, DORA, and PSD2. The most structurally relevant constraint is **GDPR right-to-erasure on immutable event streams**: once an event is published with a data subject's PII, it cannot be deleted in the traditional sense.

| Candidate | GDPR (right-to-erasure) | DORA | PSD2 (audit trail) | Proceeds? |
|---|---|---|---|---|
| Kafka / Redpanda | Native log compaction with null-payload tombstones; crypto-erasure also possible | Replication factor, ISR, configurable RTO/RPO; chaos tooling (Chaos Monkey, Pumba) well-tested against Kafka | Ordered, durable, immutable-by-default log per topic | **Pass** |
| Pulsar | Compaction supported via Pulsar's ledger compaction | HA via BookKeeper ensemble; RTO/RPO documentable; resilience tooling less mature | Durable ledger; same audit properties | **Pass** |
| RabbitMQ Streams | No key-based log compaction. Streams support size/time retention and per-message TTL, but **not tombstone-based selective erasure**. Requires crypto-erasure (encrypt payload with per-subject key; delete the key). Valid DPIA approach — but adds application-layer complexity not required by Kafka. | Quorum queues provide HA; resilience tooling limited compared to Kafka | Classic AMQP audit trail; streams retain ordered log | **Pass (conditional)** — crypto-erasure required as the right-to-erasure mechanism (alternative accepted per ADR-000 F2) |
| NATS JetStream | Same gap as RabbitMQ Streams: no key-based compaction. Crypto-erasure is the only compliant path. | Single-binary simplicity aids resilience drills; JetStream clustering (RAFT) is straightforward | Durable JetStream subjects retain message history | **Pass (conditional)** — crypto-erasure required as the right-to-erasure mechanism (alternative accepted per ADR-000 F2) |

Kafka/Redpanda and Pulsar pass both hard filters unconditionally. RabbitMQ and NATS pass F2 only on the condition that crypto-erasure is committed as the right-to-erasure mechanism, with the key-management discipline that implies. Kafka/Redpanda and Pulsar provide the cleaner compliance path via native compaction.

---

### Soft criteria

#### Apache Kafka / Redpanda (treated together)

**S1 · Operational complexity:** Apache Kafka in KRaft mode (no ZooKeeper, stable since Kafka 3.3) is a meaningful operational simplification over older Kafka deployments, but it remains a JVM application with non-trivial configuration surface (broker configs, JVM heap, log segment sizes). For a 1–2 person team, JVM tuning and garbage collection surprises are real risks. **Redpanda eliminates this entirely**: it is a single C++ binary with no JVM, no ZooKeeper, no controller quorum complexity beyond the broker itself, and strong defaults requiring almost no tuning for a single-node POC. Redpanda's developer mode (`--mode dev-container`) starts in seconds. On operational burden alone, Redpanda is a better fit for this team size than Apache Kafka directly.

**S2 · Ecosystem coherence:** Kafka's ecosystem is the strongest of all candidates by a wide margin. Kafka Connect provides hundreds of production-grade connectors (JDBC, Debezium for outbox CDC relay, Elasticsearch, S3, and more). The schema registry (Confluent Schema Registry — Apache 2.0 community edition) integrates natively. Kafka Streams and ksqlDB provide stream processing. OpenTelemetry instrumentation is available via the OpenTelemetry Kafka instrumentation. Redpanda is wire-compatible with Kafka's protocol, meaning every Kafka client, connector, and tool works without modification. The entire ecosystem coherence argument applies to Redpanda directly.

**S3 · Exit cost:** High. The Kafka wire protocol is de facto standard, but it is proprietary in origin. Application code using Kafka client APIs (`KafkaProducer`, `KafkaConsumer`, Kafka Streams) would require significant rework to migrate to a different broker. Topic data is exportable via connectors or MirrorMaker. For an event sourcing architecture where the log is the system of record, exit cost is inherently high regardless of tool — this is a property of the architectural choice, not uniquely of Kafka. Redpanda's compatibility actually reduces lock-in relative to Apache Kafka, since another Kafka-compatible broker (or Apache Kafka itself) is a valid migration target.

**S4 · Community and longevity:** Kafka is a top-level Apache project with foundation governance, a very large contributor base, and a multi-vendor commercial ecosystem (Confluent, Redpanda, AWS MSK, Azure Event Hubs, Aiven). The probability of Kafka becoming unmaintained in the next decade is negligible. Redpanda (founded 2020) is younger and single-vendor controlled, but its Apache 2.0 licence and Kafka compatibility mean the community edition is not a lock-in risk — the application can switch to Apache Kafka if Redpanda's commercial direction diverges.

---

#### Apache Pulsar

**S1 · Operational complexity:** Pulsar's three-tier architecture — ZooKeeper (or etcd), Apache BookKeeper ensemble, and Pulsar brokers — is operationally prohibitive for a 1–2 person team. A minimal highly-available cluster requires at least three BookKeeper nodes and two ZooKeeper nodes, plus the broker tier. Even a single-node "all-in-one" Pulsar deployment (using the `bin/pulsar standalone` mode) embeds all three components in one process, which is usable for development but not representative of any production topology. Operationally, Pulsar trades Kafka's JVM complexity for a significantly more complex multi-component architecture. This is the decisive disqualifier.

**S2 · Ecosystem coherence:** Pulsar IO provides connectors, and Pulsar has a Kafka compatibility layer (KoP — Kafka-on-Pulsar) that allows some Kafka clients to connect. However, the connector ecosystem is substantially smaller than Kafka Connect's. Schema registry support is built in (Pulsar Schema), but Avro/Protobuf toolchains from the Kafka world do not integrate without the KoP shim. OpenTelemetry instrumentation exists but is less mature than Kafka's.

**S3 · Exit cost:** Moderate. Pulsar's multi-topic, namespace, and tenant model differs structurally from Kafka's flat topic model. Migration to or from Pulsar requires topic restructuring, not just client code changes.

**S4 · Community and longevity:** Pulsar is a top-level Apache project backed commercially by StreamNative. Community is healthy but substantially smaller than Kafka's. The architectural bet on BookKeeper as a separate storage tier has not become an industry default, and Pulsar's market position has not grown as fast as Kafka's between 2020 and 2026 (CNCF survey trend at the time of this ADR).

---

#### RabbitMQ Streams

**S1 · Operational complexity:** RabbitMQ has the best operational story of the traditional candidates. It is a mature Erlang application with excellent defaults, a management UI, and decades of battle-testing. The streams feature (stable since 3.9) extends it with a durable log model without requiring separate components. Single-node setup is simple. For teams that already operate RabbitMQ, the upgrade path to streams is low-friction.

**S2 · Ecosystem coherence:** RabbitMQ's traditional AMQP ecosystem is excellent for point-to-point and pub/sub messaging. For event streaming specifically, the streams feature is newer and the ecosystem is immature: there is no equivalent to Kafka Connect for outbox CDC relay or sink connectors; no stream processing framework comparable to Kafka Streams; and schema registry integration is not a solved problem for the streams protocol. The architecture described in this series (schema-versioned Avro/Protobuf events, compaction-based read model rebuilding, outbox relay) does not have well-worn paths on RabbitMQ Streams as of 2026.

**S3 · Exit cost:** AMQP protocol interoperability is a genuine advantage for point-to-point workloads. For the streaming workloads in this architecture, the exit cost is moderate — the streams protocol is proprietary, but data can be migrated via the management API.

**S4 · Community and longevity:** RabbitMQ has strong longevity for its traditional use case. The Broadcom acquisition of VMware (which owned RabbitMQ) created uncertainty about commercial priorities in 2024, though the project remains open source and actively maintained. The streams feature specifically is less certain to receive sustained investment.

---

#### NATS JetStream

**S1 · Operational complexity:** NATS JetStream has the simplest operational surface of all candidates. NATS Server is a single Go binary with JetStream enabled via a single config flag. Single-node setup takes minutes. Clustering (three-node RAFT) is straightforward. There is no separate ZooKeeper, no BookKeeper, no JVM. For a 1–2 person team, NATS is genuinely operationally comfortable.

**S2 · Ecosystem coherence:** NATS is well-suited to microservice-to-microservice communication and has a growing ecosystem of client libraries. For the specific workloads in this series — Avro/Protobuf schema-versioned events, outbox relay (polling or CDC), sink connectors to PostgreSQL and Elasticsearch — the ecosystem is significantly thinner than Kafka Connect. There is no NATS Connect equivalent. Schema registry integration is not standardised. OpenTelemetry instrumentation is available via community libraries. NATS JetStream's key-value and object store are useful primitives, but they do not replace a schema registry.

**S3 · Exit cost:** Moderate. NATS protocol is proprietary, but client libraries are available for most languages. JetStream-specific patterns (consumer groups, durable subscriptions, stream subjects) are conceptually different from Kafka's consumer groups and topic partitions, so migration involves meaningful application code changes.

**S4 · Community and longevity:** NATS is a CNCF Incubating project (as of 2026), which provides foundation governance. The primary commercial backer is Synadia. Community is active and growing, with strong uptake in cloud-native and IoT use cases. The trajectory is positive. However, NATS's positioning is more "messaging fabric for microservices" than "durable event log for financial systems" — the JetStream persistence layer is newer and less battle-tested for the retention periods and throughput profiles that banking architectures require.

---

## Decision

**Chosen: Kafka-compatible ecosystem, implemented as Redpanda Community Edition**

The Kafka ecosystem is the only candidate with a complete, production-proven toolchain for every component this architecture requires: schema registry, outbox relay (polling to start, Debezium CDC as an upgrade path for the outbox table), sink connectors, stream processing, and a native right-to-erasure path via log compaction. No other candidate matches this breadth.

Between Apache Kafka and Redpanda as implementations: Redpanda removes the JVM entirely (eliminating the primary operational risk for a 1–2 person team), is wire-compatible with the Kafka protocol (so every Kafka client, connector, and tool works unchanged), and its Apache 2.0 community edition covers all POC requirements. If Redpanda's commercial trajectory becomes a concern, migration to Apache Kafka KRaft is a broker replacement, not an application rewrite.

---

**Rejected: Apache Pulsar**

The three-tier architecture (ZooKeeper + BookKeeper + brokers) is operationally prohibitive for a 1–2 person team. Pulsar's functional advantages (multi-tenancy, geo-replication, tiered storage) address problems this architecture does not have at POC scale.

**Rejected: RabbitMQ Streams**

The streams ecosystem is too immature for event sourcing workloads in 2026. There is no equivalent to Kafka Connect for outbox CDC relay or sink connectors, no standard schema registry integration for streams, and no stream processing framework. RabbitMQ's classic AMQP use case is well-served by the existing toolchain; streams specifically is not.

**Rejected: NATS JetStream**

Operationally the strongest alternative, but the Kafka Connect ecosystem gap is decisive for this architecture. Banking integration pipelines depend on schema-versioned event contracts, outbox CDC relay capability, sink connectors, and a native compaction path for GDPR right-to-erasure — none of which have established solutions in the NATS ecosystem. NATS JetStream is a credible choice for a service mesh communication layer but not for a durable financial event log with structured schema governance.

---

## Consequences

**What this choice makes easier:**

- Document 04's outbox, inbox, and compaction patterns map directly to Redpanda/Kafka APIs — no translation layer.
- The outbox relay (document 04) starts with polling — simple, adequate for banking event volumes, no extra infrastructure. When volume justifies it, Debezium via Kafka Connect provides a CDC upgrade path for the outbox table specifically (CDC is only acceptable on the outbox table, where rows are already intentional domain events; see document 04 for the rationale). Kafka Connect's sink connectors (JDBC, Elasticsearch) are available for read model population.
- Schema registry (Confluent Schema Registry — open source) works unchanged against Redpanda's schema registry API.
- Any Kafka client library (Java, Python, Go, Rust) works without modification.
- Topic ACL enforcement (document 04, §Security) is supported via Redpanda's SASL/SCRAM and ACL API, which mirrors Kafka's.
- Transitioning to Apache Kafka (self-hosted or managed) is a broker swap, not an application rewrite, if operational or licensing requirements change.

**What this choice makes harder or impossible:**

- Redpanda's tiered storage (S3-backed extended retention) is an Enterprise feature. At POC scale, retention is bounded by local disk. This is acceptable; it must be revisited before production hardening.
- Log compaction provides a structural right-to-erasure path, but the application must discipline itself to use null-payload tombstones correctly — a partially implemented compaction strategy is worse than no compaction strategy (it creates a false sense of compliance). This must be documented in the event schema governance (see ADR-002).
- Topic count and partition count affect Redpanda performance differently from Apache Kafka (Redpanda has lower per-partition overhead). Capacity planning data from Kafka benchmarks is directionally useful but should be validated against Redpanda.

**Residual risks:**

- **Redpanda commercial trajectory:** Redpanda Inc. is a VC-backed company. If it is acquired or changes its licensing model, the community edition's scope could narrow. Mitigation: the Apache 2.0 licence protects the current community edition version; Apache Kafka KRaft is the fallback.
- **Kafka protocol version drift:** Redpanda tracks the Kafka protocol but may lag on very recent API versions. Verify client compatibility before upgrading either the broker or client libraries.
- **Single-node POC ≠ production topology:** A single-node Redpanda instance provides no replication. The architecture patterns in documents 00–10 assume at-least-once delivery with `acks=all`; on a single node, `acks=all` is equivalent to `acks=1`. This is acceptable for a POC; production requires a minimum three-broker cluster.

---

## Implementation Principles

Choosing the Kafka ecosystem means every service in the architecture shares a common broker. Without deliberate configuration conventions, individual services will diverge: different topic naming schemes, inconsistent retention policies, missing compaction configuration for GDPR-sensitive topics, and ad-hoc security. The following principles define the minimum shared discipline for Redpanda configuration and topic usage.

---

### P1 — POC topology is single-node but configuration must be production-structurally-honest

For POC, Redpanda runs as a single node in developer mode (`--overprovisioned --smp 1 --memory 1G --reserve-memory 0M --default-log-level=info`). Producers must still be configured with `acks=all` — on a single broker this is equivalent to `acks=1`, but the producer code must not be simplified to match the POC topology. When the cluster scales to three brokers, the configuration is correct without change. Any code that short-circuits `acks` or `enable.idempotence` "because it's just a POC" is a correctness debt, not a simplification.

---

### P2 — Topic naming encodes domain, aggregate type, and purpose

Topics must follow the pattern `{domain}.{aggregate_type}.{purpose}`, where:

| Segment | Values | Example |
|---|---|---|
| `domain` | Bounded context or service | `deposits` |
| `aggregate_type` | Aggregate root, snake_case | `term_deposit` |
| `purpose` | `events`, `commands`, or `dlq` | `events` |

Full example: `deposits.term_deposit.events`, `deposits.term_deposit.dlq`.

This naming convention is what makes document 04's topic ACL model workable: each service's producer ACL is scoped to its own `{domain}.` prefix, and consumer ACLs are scoped to the specific topic names the service explicitly subscribes to.

---

### P3 — Partition count is fixed at creation time; partition key is always `aggregate_id`

Redpanda partitions are immutable after topic creation. All event topics must be created with an explicit partition count before any producer connects. For POC, 12 partitions per event topic is the default — sufficient for local development without over-provisioning a single-node setup.

The producer must always set the record key to `aggregate_id` (the UUID of the aggregate root). Redpanda assigns partitions by hashing the record key; document 04's per-aggregate ordering guarantee depends on this — all events for a given `aggregate_id` must land in the same partition. The default round-robin or null-key partition assignment must not be used on domain event topics.

---

### P4 — GDPR-sensitive topics require log compaction configuration at creation time

Topics that carry events with PII fields must be created with `cleanup.policy=compact,delete`. This cannot be retrofitted after events have been published — adding compaction to an existing topic does not retroactively compact already-written segments, and null-payload tombstones cannot erase records in segments that predate the compaction policy.

The event catalog (ADR-008) is the authoritative list of which topics require compaction. Topics that carry no PII use `cleanup.policy=delete` with an explicit `retention.ms`. The default (no explicit policy) must not be relied upon — the correct policy must be set at `kafka-topics --create` time or the equivalent Redpanda Admin API call.

---

### P5 — Security baseline is SASL/SCRAM from the first deployment, not a production hardening step

Redpanda supports SASL/SCRAM-SHA-256 without external dependencies. Every service that produces or consumes must authenticate with a dedicated service account (not a shared superuser credential). ACLs must be configured per service at Redpanda startup:

- A producer has `WRITE` permission on its own domain prefix (`deposits.*`).
- A consumer has `READ` permission on the specific topics it subscribes to.
- No service has `ALTER`, `DELETE`, or `DESCRIBE CONFIGS` unless it is explicitly the cluster operator.

Deferring SASL/SCRAM to production means application code is never tested against an authenticated broker, and the ACL model from document 04 is never validated. The cost at POC scale is one SCRAM user creation per service; there is no justification for skipping it.
