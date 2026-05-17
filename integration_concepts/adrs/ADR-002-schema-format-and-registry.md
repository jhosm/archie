# ADR-002: Schema Format and Registry

| Field | Value |
|---|---|
| Status | Accepted |
| Date | 2026-05-17 |
| Deciders | jhosm |
| Common criteria | [ADR-000](./ADR-000-common-evaluation-criteria.md) |
| Depends on | [ADR-001](./ADR-001-event-backbone-message-broker.md) |

---

## Context

The integration series assumes schema-versioned events from the outset: document 00 names Avro and Protobuf alongside a schema registry; document 04 specifies compatibility modes (BACKWARD, FORWARD, FULL) enforced by the registry at publish time; document 09 builds the entire schema evolution discipline on the assumption of a mechanical CI/CD gate.

This ADR makes two coupled choices explicit:

1. **Serialization format** — the wire format for all integration events published to Redpanda topics.
2. **Schema registry** — the system that stores, versions, and enforces compatibility for all event schemas.

These choices are coupled: the format determines which registry API surface is most natural, and the registry determines how schemas are looked up at runtime.

**Format candidates evaluated:**

| # | Candidate | Notes |
|---|---|---|
| A | **Apache Avro** | Binary; schema stored separately in registry; Apache 2.0 |
| B | **Protocol Buffers (Protobuf)** | Binary; `.proto` IDL; code generation required; BSD-3 |
| C | **JSON Schema** | Text-based; schema validation, not binary serialization |

**Registry candidates evaluated:**

| # | Candidate | Notes |
|---|---|---|
| X | **Confluent Schema Registry** (standalone) | De facto standard; Apache 2.0; self-hosted JVM app |
| Y | **Apicurio Registry** | Apache 2.0; Red Hat backed; implements Confluent SR API |
| Z | **AWS Glue Schema Registry** | Managed; free tier; AWS-native |

A fourth option — **Redpanda built-in schema registry** — is not a separate candidate but a deployment note: Redpanda Community Edition ships with a built-in SR that exposes the Confluent Schema Registry REST API (`/subjects`, `/schemas`, `/config`). It is the zero-overhead POC implementation of whichever registry API is chosen.

---

## Evaluation

### Hard filter results

#### F1 · Cost / licensing

| Candidate | Licence | Assessment | Proceeds? |
|---|---|---|---|
| Apache Avro | Apache 2.0 | Open source; self-hosted serialization library | **Pass** |
| Protobuf | BSD-3 | Effectively open; no financial-services restriction | **Pass** |
| JSON Schema | Open specification; open source tooling | All relevant libraries are open source | **Pass** |
| Confluent Schema Registry | Apache 2.0 | Open source community edition; self-hosted | **Pass** |
| Apicurio Registry | Apache 2.0 | Open source; self-hosted | **Pass** |
| AWS Glue Schema Registry | Proprietary (free tier) | Managed service; free tier available | **Pass** (conditional — see F2) |

*Date of assessment: 2026-05-17. Licence terms and free-tier limits can change; verify before production hardening.*

#### F2 · Regulatory fit

**Format:**

| Candidate | GDPR | DORA | PSD2 | Proceeds? |
|---|---|---|---|---|
| Avro | Binary encoding — PII fields are not human-readable without schema lookup. Null-payload tombstones (compaction-based erasure, ADR-001) require consumers to tolerate null values; the Avro SerDe must not enforce a non-null schema on compacted topics. | Format is independent of broker resilience. | Schema versioning provides an auditable contract trail. | **Pass** |
| Protobuf | Same binary properties as Avro. | Same. | Same. | **Pass** |
| JSON Schema | Payloads are plain-text JSON — readable without schema lookup. PII is not structurally protected by the wire format. Weaker data minimization than binary formats. | Same. | Same. | **Pass** (structurally weaker on GDPR, not a hard fail) |

**Registry:**

| Candidate | GDPR | DORA | PSD2 | Proceeds? |
|---|---|---|---|---|
| Confluent SR | Self-hosted — data residency in EU under operator control. SR is cached client-side; SR downtime does not break consumers reading previously-seen schema IDs. | Resilience testing is under operator control. | Schema version history provides audit trail of contract evolution. | **Pass** |
| Apicurio Registry | Same as Confluent SR. | Same. | Same. | **Pass** |
| AWS Glue Schema Registry | Schemas stored in AWS — data residency depends on region. Requires explicit EU region configuration for GDPR compliance. Cloud dependency is inconsistent with ADR-001's self-hosted posture. DORA resilience testing depends on AWS availability, not under operator control. | Not under operator control. | Same audit properties. | **Fail** — data residency not self-controlled; DORA resilience testing not under operator control; inconsistent with ADR-001. |

AWS Glue fails F2 and is eliminated.

---

### Soft criteria

#### Apache Avro

**S1 · Operational complexity:** Avro requires no build toolchain change. A schema is a JSON document (`.avsc`) registered in the SR at deploy time. At runtime, producers and consumers use a Confluent Avro SerDe (available for Java, Python, Go, Rust) that resolves the schema from the SR via a schema ID embedded in the message header. No `protoc`, no generated code, no per-language plugin management. For a 1–2 person team, this is a significant operational advantage — adding a new event means writing a `.avsc` file and registering it, not modifying a build pipeline.

**S2 · Ecosystem coherence:** Avro and the Confluent Schema Registry were designed together for the Kafka ecosystem. Every Kafka / Redpanda documentation example, every Kafka Connect converter, and every stream processing framework uses Avro as the default format. The Redpanda built-in SR has first-class Avro support. Compatibility mode enforcement (BACKWARD, FORWARD, FULL, NONE) is deeply documented and battle-tested for Avro specifically. The integration is effectively seamless.

**S3 · Exit cost:** Avro is most common in the Kafka / Hadoop ecosystem. Migrating to a different format would require re-serialising all events in existing topics — non-trivial but scriptable. The `.avsc` schema files themselves are portable. The registry exit cost is low: the Confluent SR API is implemented by multiple tools and switching is an endpoint URL change.

**S4 · Community and longevity:** Apache Avro is a top-level Apache project, widely used in data engineering (Hadoop, Spark, Flink) and event streaming. Well-maintained. No abandonment risk.

---

#### Protocol Buffers

**S1 · Operational complexity:** Protobuf requires a code generation step (`protoc`) and per-language plugins (e.g. `protoc-gen-java`, or the modern `buf` toolchain). This adds a build pipeline dependency to every service that produces or consumes events. For a 1–2 person team maintaining multiple services, managing protoc versions and plugin compatibility is a real ongoing cost. Protobuf's primary advantage — strongly typed generated classes — pays off most in large polyglot teams where compile-time safety across languages is critical.

**S2 · Ecosystem coherence:** Protobuf has good Kafka support via a Confluent Protobuf SerDe, and Confluent SR supports Protobuf schemas natively. However, Kafka Connect converters default to Avro, and most Kafka ecosystem documentation requires additional configuration for Protobuf. Redpanda's built-in SR supports Protobuf but the tooling integration is measurably thinner than for Avro.

**S3 · Exit cost:** `.proto` files are highly portable — Protobuf is used in gRPC, REST, and many non-Kafka contexts. Lower exit cost than Avro if the architecture ever extends significantly beyond Kafka.

**S4 · Community and longevity:** Google-backed, part of the gRPC ecosystem. Excellent longevity. The `buf` toolchain has significantly modernised the developer experience.

---

#### JSON Schema

**S1 · Operational complexity:** The lowest barrier to start — write a JSON schema document, no serialization configuration. However, the Confluent SR JSON Schema mode validates payloads but does not provide binary serialization. Messages remain full JSON text: no wire efficiency (a binary Avro message is typically 5–10× smaller than the equivalent JSON); no schema-directed binary decoding; deserialization relies on application-level JSON parsing. These properties make JSON Schema appropriate for REST API contracts and configuration validation — not for a durable event stream.

**S2 · Ecosystem coherence:** JSON is universal, but JSON Schema is not idiomatic for Kafka event streaming. Kafka Connect converters, stream processing frameworks, and SR compatibility enforcement all have more mature paths for Avro and Protobuf. The compaction-based GDPR erasure pattern (ADR-001) also interacts more cleanly with binary formats.

**S3 · Exit cost:** Lowest exit cost — JSON is readable without schema lookup.

**S4 · Community and longevity:** Well-maintained IETF draft standard. Not at risk of abandonment. Wrong tool for this context regardless.

---

#### Confluent Schema Registry (standalone)

**S1 · Operational complexity:** A JVM application that stores schemas in a dedicated Kafka / Redpanda topic (`_schemas`). At POC scale, an additional process to operate and monitor. This overhead is unnecessary when **Redpanda Community Edition already includes a built-in SR that exposes the identical Confluent SR API** — the standalone SR adds operational surface for no additional capability at this stage. The standalone SR is the correct choice when migrating to Apache Kafka or when a governance UI is needed in production.

**S2 · Ecosystem coherence:** The de facto standard. Every Kafka library, tool, CI gate, and documentation example targets the Confluent SR API. Everything that works against the standalone SR works identically against Redpanda's built-in SR, because they speak the same protocol.

**S3 · Exit cost:** The Confluent SR API is the de facto standard, implemented by Redpanda, Apicurio, and others. Switching between SR implementations is an endpoint URL configuration change — no code change.

**S4 · Community and longevity:** Apache 2.0, maintained by Confluent. Widely adopted. No abandonment risk for the community edition.

---

#### Apicurio Registry

**S1 · Operational complexity:** Self-hosted; requires a backing store (PostgreSQL, Kafka topics, or in-memory). More components to operate than the standalone Confluent SR, with no additional capability relevant to this architecture at POC scale. Implements the Confluent SR compatibility API, so all Kafka Avro tooling works without modification.

**S2 · Ecosystem coherence:** Apicurio supports Avro, Protobuf, JSON Schema, OpenAPI, AsyncAPI, and GraphQL schemas — a broader surface than Confluent SR, most of which is irrelevant here. The Confluent SR compatibility layer means no client-side changes. The native Apicurio API adds cognitive overhead without benefit in a pure Kafka context.

**S3 · Exit cost:** Same as Confluent SR — URL swap.

**S4 · Community and longevity:** Red Hat / IBM backed, Apache 2.0. Healthy community, actively developed. A credible production alternative, particularly in OpenShift / Kubernetes environments or where AsyncAPI catalog integration is wanted.

---

## Decision

**Chosen format: Apache Avro**

Avro is the native serialization format of the Kafka ecosystem. It requires no build toolchain beyond a schema JSON file and a standard SerDe library — the decisive advantage for a 1–2 person team. Its compatibility mode enforcement is the most deeply documented and battle-tested of any format in the Confluent SR / Redpanda ecosystem. All documents in the integration series that describe schema evolution (doc 04, doc 09) implicitly assume Avro's schema resolution model.

**Chosen registry API: Confluent Schema Registry API**

The de facto standard for Kafka-native schema governance. All tooling, CI gates, and documentation in the Kafka ecosystem target it. This is a choice of API contract, not a specific deployment.

**Chosen registry implementation (POC): Redpanda built-in schema registry**

Redpanda Community Edition includes a built-in SR that exposes the Confluent SR REST API. No separate process, no separate storage, no extra configuration — full schema governance at zero additional operational cost. The built-in SR is API-compatible with the standalone Confluent SR; switching to standalone Confluent SR or Apicurio (when migrating to Apache Kafka, or when governance UI features are needed) is an endpoint URL change, not a code change.

---

**Rejected: Protocol Buffers**

The code generation step (`protoc` + language plugins) adds build toolchain complexity not justified at 1–2 people. Protobuf's primary advantage — strongly typed generated classes across languages — matters most in large polyglot teams. The Kafka ecosystem is measurably more mature for Avro.

**Rejected: JSON Schema**

Not a binary serialization format. Payloads remain plain-text JSON — no wire efficiency, no schema-directed binary decoding. Appropriate for REST API contracts; wrong for a durable Kafka event stream.

**Rejected: Confluent Schema Registry (standalone) for POC**

Redpanda's built-in SR provides the identical API at zero additional operational surface. Standalone Confluent SR is the correct choice when this architecture graduates to Apache Kafka or when governance UI features are needed in production.

**Rejected: Apicurio Registry for POC**

Same reasoning as standalone Confluent SR. More components to operate for no additional capability at POC scale. A credible production alternative when Apicurio's broader schema format support (AsyncAPI, OpenAPI alongside Avro) becomes relevant.

---

## Consequences

**What this choice makes easier:**

- Any Kafka / Redpanda client in any language uses a standard Confluent Avro SerDe with no custom serialization code.
- Schema registration is a CI/CD gate: the producer's pipeline registers (or validates) the schema against the Redpanda built-in SR before deployment. Incompatible schemas fail the build, not production (document 09).
- Compatibility mode defaults: **BACKWARD** for most events (producer evolves first; old consumers can read new data). **FULL** for events with many known consumers where coordinated rollout is not feasible. Both are enforced mechanically by the SR.
- GDPR tombstones: null-payload tombstone messages on compacted topics must be tolerated by consumers — the Avro SerDe must be configured to accept null values on compacted topics rather than enforcing a non-null schema. This is a producer/consumer contract requirement to be documented in the event catalog (document 08).
- CloudEvents envelope: the Confluent wire-format Avro value is the `data` of a CloudEvents 1.0 event in Binary Content Mode. CloudEvents attributes (including domain extensions `ce_correlationid`, `ce_causationid`, `ce_aggregatetype`) are carried in Kafka message headers. The schema registry manages only the business payload schema — not the envelope. The outbox publisher (ADR-004) constructs the CloudEvents headers from outbox table columns at publish time.
- Migrating from Redpanda built-in SR to standalone Confluent SR or Apicurio is a configuration change (SR endpoint URL), not a code change.

**What this choice makes harder or impossible:**

- No compile-time type safety on event schemas without a separate code generation step (Avro SpecificRecord generation from `.avsc` files). GenericRecord provides runtime schema access but no IDE autocompletion on event fields.
- Avro's JSON-based IDL (`.avsc` files) is less ergonomic than Protobuf's `.proto` syntax for complex nested types. Tooling for `.avsc` editing is thinner than the `buf` ecosystem for Protobuf.
- Topics serialised with Avro cannot be consumed without SR access: the schema ID in the Avro message header is meaningless without the registry. SR availability is a dependency of every consumer. Mitigation: Confluent Avro SerDe libraries cache schemas locally after first lookup; SR downtime does not break consumers reading previously-seen schema IDs.

**Residual risks:**

- **Avro null / union verbosity:** Avro represents nullable fields as a union type (`["null", "string"]`). This is verbose and a common source of schema authoring errors. A schema authoring convention (document 08) should standardise nullable field patterns before the first event is published.
- **Redpanda built-in SR feature surface:** The built-in SR implements the core Confluent SR API but may not support all advanced features (context-namespaced schemas, schema export). Verify before relying on any non-standard SR capability.
- **Schema registry backup:** Redpanda's built-in SR stores schemas in an internal topic (`_schemas`). This topic must be included in the backup and restore strategy — loss of this topic makes all persisted Avro events unreadable.

---

## Implementation Principles

Choosing Avro and the Confluent Schema Registry API means every event schema in the architecture is governed by the same registry and serialized by the same SerDe conventions. Without explicit rules, schemas will diverge in nullable field handling, file naming, compatibility mode choices, and the boundary between what the registry governs and what it does not. The following principles establish the minimum shared discipline.

---

### P1 — One schema file per event type; naming mirrors the registry subject

Every event type must have exactly one `.avsc` file. Files follow the naming pattern `{aggregate_type}.{EventName}.avsc` (e.g. `term_deposit.DepositInitiated.avsc`). The Avro `namespace` in the schema must be `{domain}.{aggregate_type}` (e.g. `deposits.term_deposit`) and the `name` must be the PascalCase event name (`DepositInitiated`). The registry subject is derived from the fully qualified name: `deposits.term_deposit.DepositInitiated-value`.

This naming makes schema registry subjects predictable and avoids collisions when multiple aggregates in the same domain publish events with similar names.

---

### P2 — Nullable fields always use `["null", "type"]` with null listed first

Avro represents optional fields as a union. The project convention is null first in the union:

```json
{ "name": "cancellation_reason", "type": ["null", "string"], "default": null }
```

Null must be listed first because Avro's default value must match the first type in the union. If null is listed second, the field cannot have a `null` default — which in practice means every producer must explicitly supply the field even when absent, and BACKWARD compatibility checks behave unexpectedly. Fields that are logically required (never null) must use a non-null type with no null union; they must not have a `null` default.

---

### P3 — Schema registration is a CI gate, never a runtime operation

No service may register a new schema version at runtime in the producer startup path. Schema registration must happen in the CI/CD pipeline, before the service container image is deployed. The pipeline step must:

1. Resolve the SR endpoint from the deployment environment configuration.
2. Attempt to register the schema for the subject; if it already exists at an identical hash, the step is a no-op.
3. Check the SR compatibility enforcement result — a BACKWARD or FULL violation must fail the build, not be suppressed.

At runtime, the Avro SerDe resolves the schema ID by subject name lookup only. A schema registry outage must not stall a deployment and must not stall an already-running producer whose subject was registered in a prior CI run. This is the primary reason registration is a pipeline step: the schema is durably registered before the code that depends on it ships.

---

### P4 — Consumers on compacted topics must handle null-payload tombstones explicitly

Redpanda log compaction produces null-payload tombstone messages as the GDPR right-to-erasure mechanism (ADR-001 P4). Every consumer on a compacted topic must:

1. Check whether the record value is null before passing it to the Avro SerDe.
2. If null: treat the record as a deletion signal — remove the corresponding projection, cache entry, or materialized state for the key.
3. Never attempt to deserialize a null payload as an Avro record; the Confluent SerDe will throw a `NullPointerException` or equivalent before reaching the application.

This is not an edge-case defensive check — it is part of the consumer contract for any topic with `cleanup.policy=compact`. A consumer that crashes on null payloads is not GDPR-compliant, regardless of what the schema says.

---

### P5 — The schema registry governs the `data` field only; envelope attributes are not schema-versioned

The `.avsc` schema file and its registry subject describe the CloudEvents `data` field — the business payload. The schema must not include CloudEvents envelope attributes (`id`, `source`, `type`, `time`, `datacontenttype`, `specversion`) or domain extension attributes (`ce_correlationid`, `ce_causationid`, `ce_aggregatetype`). These are carried as Kafka/Redpanda message headers, written by the outbox publisher (ADR-004), and consumed directly from the record header map.

Embedding envelope fields in the Avro schema conflates the wire envelope with the business payload. It means every schema version carries envelope fields that are already mandated by the CloudEvents spec, creates schema churn when envelope conventions evolve, and makes the schema registry subject misleadingly describe the full message structure rather than just the business event.
