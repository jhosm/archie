# ADR-IC-008: Event Catalog Governance Tooling

| Field | Value |
|---|---|
| Status | Accepted |
| Date | 2026-05-17 |
| Deciders | jhosm |
| Common criteria | [ADR-IC-000](./ADR-IC-000-common-evaluation-criteria.md) |
| Depends on | [ADR-IC-001](./ADR-IC-001-event-backbone-message-broker.md), [ADR-IC-002](./ADR-IC-002-schema-format-and-registry.md) |

---

## Context

[Document 08](../08-event-catalog-governance.md) establishes that the event catalog is not decorative infrastructure — it is the mechanical enforcement layer for governance. Without it, the four pillars (ownership, conventions, review, discoverability) exist only as goodwill and wiki pages that rot. The catalog makes governance enforceable: no integration event may be published in production without a catalog entry, and CI fails if the entry is absent or inconsistent with the registered schema.

Two interrelated decisions are required:

**Decision A — Specification format:** What machine-readable format describes the business contract of each integration event? This format is the source of truth that tooling, validators, and CI automation will consume. It is distinct from the Avro schema managed by the schema registry (ADR-IC-002), which describes the wire format — the catalog format describes the *who*, *why*, and *when* of an event alongside its payload structure.

**Decision B — Catalog portal:** What tool renders and navigates the catalog for human consumers? This is the interface through which engineers, event stewards, and new team members discover, review, and understand the event landscape.

Both decisions are evaluated here; Decision A is settled first as it constrains Decision B.

### On the specification format

AsyncAPI (a CNCF project, Apache 2.0) is the established standard for documenting event-driven APIs. It describes channels (topics), operations (publish/subscribe), and message schemas in a machine-readable YAML or JSON file, and its scope maps directly onto the integration architecture in this series: each Redpanda topic is a channel, each integration event is a message on that channel. AsyncAPI files can reference Avro schemas by URI or embed schema definitions inline. The AsyncAPI CLI provides diff, validation, and compatibility commands. Tooling that generates documentation, mock consumers, or SDK stubs from an AsyncAPI file is widely available.

No credible alternative specification format exists for this use case at equivalent maturity. OpenAPI is designed for synchronous REST; CloudEvents defines a metadata envelope standard (complementary, not alternative); custom markdown formats provide no tooling or validation leverage. **AsyncAPI is adopted as the canonical specification format without a comparative evaluation.** Each integration event has exactly one corresponding AsyncAPI specification file, kept under version control alongside the schema registry Avro schema.

The catalog portal decision is what follows.

### GDPR tombstone contract requirement

ADR-IC-002 established that compaction-based GDPR erasure uses null-payload tombstone records. ADR-IC-001 established that this is the Redpanda-native erasure mechanism. A direct consequence is that any consumer subscribing to a compacted topic must configure its Avro SerDe to tolerate null payloads — a consumer that enforces a non-null schema will crash when it receives a tombstone. This is a per-topic behavioral contract that must be documented in the catalog entry for every event published on a compacted topic. The catalog tooling must support a structured field for this contract (see P3).

**Candidates evaluated:**

| # | Candidate | Notes |
|---|---|---|
| A | **Git-native AsyncAPI** | AsyncAPI YAML files in a Git repository; no portal; validation via AsyncAPI CLI in CI; rendered by GitHub file browser |
| B | **EventCatalog** | Open-source event catalog static site generator; AsyncAPI import; CI integration; hosted on GitHub Pages or equivalent |
| C | **Backstage** | CNCF graduated internal developer portal; AsyncAPI catalog plugin; Node.js runtime service with PostgreSQL backend |
| D | **Confluent Stream Governance** | Commercial; integrated schema registry + catalog; part of Confluent Platform |

---

## Evaluation

### Hard filter results

#### F1 · Cost / licensing

| Candidate | Licence | Assessment | Proceeds? |
|---|---|---|---|
| Git-native AsyncAPI | AsyncAPI spec: Apache 2.0; AsyncAPI CLI: Apache 2.0 | No tool to license beyond the CLI | **Pass** |
| EventCatalog | Apache 2.0 (core) | Open-source static site generator; self-hosted or GitHub Pages; commercial features available as paid tier but core portal is open source | **Pass (conditional)** — required features must be verified to fall within the open-source tier at implementation time; free-tier boundaries can shift |
| Backstage | Apache 2.0 | CNCF project; open source; self-hosted | **Pass** |
| Confluent Stream Governance | Proprietary; part of Confluent Platform | The event catalog and schema lineage features are paywalled in Confluent Platform; no self-hosted open-source equivalent | **Fail** — event catalog features require Confluent Platform licence |

*Date of licence assessment: 2026-05-17.*

#### F2 · Regulatory fit

The catalog portal is a documentation tool, not a data-processing system. It stores event schemas and human-readable descriptions — not event payloads, not PII. The regulatory surface is therefore narrow compared to ADRs that handle event data directly.

| Candidate | GDPR | DORA | PSD2 | Proceeds? |
|---|---|---|---|---|
| Git-native AsyncAPI | AsyncAPI files contain schema definitions and field descriptions. If a field description example contains PII (e.g., a sample payload with real `client_id` values), that is a governance antipattern, not a structural risk of the format. No new GDPR data surface. | The catalog is not on the operational critical path. Its unavailability does not affect event production or consumption. No DORA obligation applies to the catalog portal itself. | No PSD2 implication for documentation tooling. | **Pass** |
| EventCatalog | Same as Git-native. EventCatalog renders the AsyncAPI files; it does not store separate event data. The built static site may be publicly accessible — operators must verify that no event description example embeds real PII. | Same as Git-native. | Same as Git-native. | **Pass** |
| Backstage | Backstage persists catalog metadata (service ownership, component relationships) in a PostgreSQL database. The catalog entries themselves (AsyncAPI files) contain no PII, but Backstage's user identity and team membership data (for ownership and access control) introduces a new GDPR surface for the personnel data it holds. This is standard HR data, not financial PII, but it must be in the data inventory. | Backstage is an internal tooling service. Its availability does not affect production event infrastructure. However, if Backstage is the authoritative catalog that CI gates reference, a Backstage outage could block deployments — a DORA-adjacent concern to manage by decoupling the CI gate from the live Backstage instance. | No PSD2 implication. | **Pass (conditional)** — user/team identity data held by Backstage must be entered in the GDPR data inventory |

All three passing candidates proceed.

---

### Soft criteria

#### Git-native AsyncAPI (no portal)

**S1 · Operational complexity:** Zero incremental infrastructure. AsyncAPI files are YAML files in a Git repository. The AsyncAPI CLI is a Node.js binary invoked in CI (`npx @asyncapi/cli@latest validate`, `npx @asyncapi/cli@latest diff`). No service to deploy, no database, no portal to keep running. This is the absolute operational floor.

**S2 · Ecosystem coherence:** Maximum coherence at the validation layer — the AsyncAPI CLI integrates with any CI system. The gap is at the human layer: event discovery is limited to `git grep`, GitHub file search, and reading raw YAML. For a 1–2 person team where both people know the codebase, this gap is tolerable. It becomes a real problem when a new team member joins or when the event count exceeds ~20 — at which point the lack of a searchable, cross-referenced portal means governance fails in practice even if it holds on paper.

**S3 · Exit cost:** Lowest possible. The AsyncAPI files are the output — they are the same files that every portal candidate imports. Adopting EventCatalog or Backstage later requires zero file format migration: the same AsyncAPI files become the import source.

**S4 · Community and longevity:** AsyncAPI CLI is a CNCF project with strong community momentum. The format itself is format-stable; tooling evolves around it. No longevity concern for the format; the "portal" here is GitHub's file renderer, which is not going anywhere.

**Where this approach fails the governance goal:**

Document 08 states that the catalog must be *discoverable* and *navigable* — "documentation as a product." A Git repository of raw YAML files is neither. The governance failure mode for this option is not technical; it is behavioral: engineers stop consulting a catalog that is hard to use, the review discipline erodes, and the month-18 swamp scenario reasserts itself. This is the failure mode the Decision section's headline principle is targeting.

---

#### EventCatalog

**S1 · Operational complexity:** EventCatalog is a static site generator. Running it produces HTML/CSS/JS files that can be served from GitHub Pages, Netlify, or any static hosting with a free tier — no server process, no database, no cluster. The CI integration is: on merge to main, run `npx @eventcatalog/cli@latest build`, deploy the output to GitHub Pages. The catalog portal is always in sync with the repository; its "availability" is GitHub Pages availability, which is not a team operational responsibility. This is not meaningfully different from a documentation site generated by MkDocs or Docusaurus. The operational overhead argument that applied to runtime services (ADR-IC-003, ADR-IC-004, ADR-IC-005) does not apply here.

**S2 · Ecosystem coherence:** EventCatalog is designed exactly for this use case — nothing else, nothing more. It imports AsyncAPI files natively, renders schemas, displays changelogs, links producers to consumers, and provides full-text search across event descriptions. The CI gate (`npx @eventcatalog/cli@latest validate`) enforces that every referenced Avro schema exists in the specification. The AsyncAPI file format is the single source of truth; EventCatalog renders it without requiring a separate catalog-specific configuration beyond a `catalog.config.js` that maps AsyncAPI files to services. The schema registry reference in each AsyncAPI file (the Avro schema ID from ADR-IC-002) is surfaced in the rendered catalog entry alongside the human-readable documentation.

**S3 · Exit cost:** Low. EventCatalog consumes AsyncAPI files; it does not produce a new proprietary format. If EventCatalog is replaced by Backstage or any other portal that imports AsyncAPI, the same files are the input. The only EventCatalog-specific artefact is the `catalog.config.js` configuration file. Exit from EventCatalog does not require touching the AsyncAPI specifications.

**S4 · Community and longevity:** EventCatalog was started by David Boyne and is maintained under the event-catalog GitHub organization. The community has grown substantially since the v2 launch in 2024, with contributions from Confluent, AWS, and individual engineers at financial services firms. The Apache 2.0 licence (core) is clean. The risk is single-vendor dependence: there is no foundation governance, and the commercial tier creates incentive pressure that could shift features from the open-source tier over time. This risk is bounded by the low exit cost — the AsyncAPI files are not hostage to EventCatalog.

---

#### Backstage

**S1 · Operational complexity:** Backstage requires a Node.js backend service, a PostgreSQL database for catalog state, and a deployment infrastructure to keep it running. This is not a static site — it is an application server. For a 1–2 person team, this is a significant operational commitment for what is, at POC scale, an event catalog with 10–20 entries. Backstage is designed as a full internal developer portal serving hundreds of engineers across dozens of teams. Deploying it for two people to navigate 15 events is using a freight elevator to go up one floor. The PostgreSQL requirement also introduces a second operational database instance alongside the application database — the same concern ADR-IC-003 raised about Temporal's second persistence tier.

**S2 · Ecosystem coherence:** Backstage has an AsyncAPI plugin, but its native model is a general service/component catalog. Integrating event-specific semantics (producer/consumer relationships, schema versions, deprecation lifecycle, GDPR tombstone contract fields) requires custom plugin development or manual curation that EventCatalog provides out of the box. Backstage's power is its extensibility — which is also what makes it heavy. At POC scale, the features that justify Backstage's operational cost are not present: there are no multiple teams, no service dependency graph spanning dozens of services, no need for integrated TechDocs, no Kubernetes plugin integration.

**S3 · Exit cost:** Moderate. Backstage stores catalog relationships in its own PostgreSQL schema. The AsyncAPI files remain portable, but the Backstage-specific metadata (team ownership mappings, Backstage annotations in AsyncAPI files) adds friction to migration. Replacing Backstage with EventCatalog requires removing Backstage annotations from AsyncAPI files and re-creating the EventCatalog configuration.

**S4 · Community and longevity:** Backstage is a CNCF graduated project, originally developed by Spotify, now maintained by a large community. Longevity is the strongest of all candidates. The risk here is not longevity but fit: Backstage is a mature, battle-tested tool being evaluated for a use case where most of its features are unused.

---

## Decision

**Chosen: EventCatalog**

The decisive principle: **governance tooling that nobody uses is not governance.** A catalog that is technically complete on paper but practically unread fails its purpose — the events are documented in the formal sense and undiscoverable in the operational sense, which is the same outcome as having no catalog at all.

Confluent Stream Governance is disqualified by F1. The choice is between Git-native AsyncAPI (no portal), EventCatalog, and Backstage.

The operational overhead argument that determined ADR-IC-003, ADR-IC-004, and ADR-IC-005 does not govern this decision. Those ADRs rejected runtime services because operating them adds a permanent production obligation. EventCatalog generates a static site — it adds a build step to CI and a GitHub Pages deployment. There is no service to monitor, no database to back up, no cluster to tune. The operational cost is zero at runtime.

The meaningful question is therefore: does Git-native AsyncAPI (raw YAML files, GitHub file browser) provide sufficient discovery for the governance goals document 08 sets out? The answer is no. Document 08 is explicit that the catalog must be *discoverable as a product*, and that "without a catalogue, governance lives in the heads of a few people." Raw YAML files in a repository satisfy the *existence* requirement (the event is documented) without satisfying the *discoverability* requirement (engineers find and use the documentation), and the decisive principle above resolves which side of that distinction matters.

EventCatalog satisfies both requirements. It renders the AsyncAPI files into a searchable, cross-referenced portal where the producer/consumer relationships, schema versions, changelog, and lifecycle status of each event are navigable without reading YAML. It is purpose-built for exactly this use case. Its static-site delivery model means the operational cost argument against it is moot.

Backstage is rejected for this decision on fit grounds, not quality grounds. For a 1–2 person team at POC scale with 10–20 events, Backstage's full developer portal infrastructure is disproportionate. The upgrade path is clear: when the team grows, when service count makes a full internal portal worth its operational cost, Backstage with the AsyncAPI plugin is the natural next step. The AsyncAPI files EventCatalog consumes are the same files Backstage imports — migration is a portal change, not a specification change.

---

**Rejected: Confluent Stream Governance**

The event catalog features are paywalled in Confluent Platform. No open-source self-hosted equivalent is available that provides the same integrated catalog+registry capability. The commercial dependency is inconsistent with the zero-budget constraint and the self-hosted posture of the entire stack.

**Rejected: Git-native AsyncAPI (no portal)**

The specification format decision (AsyncAPI) is correct and adopted unconditionally. The portal decision is separate. Raw YAML files in a Git repository satisfy the machine-readable governance requirement but fail the human discoverability requirement. Discovery via `git grep` and raw YAML reading is a portal that engineers will not use. Governance tooling that is not used is not governance. EventCatalog provides the portal layer at zero additional runtime cost.

**Rejected: Backstage**

The right tool for the wrong scale. Backstage's operational footprint (Node.js service + PostgreSQL backend) is justified when it serves dozens of teams and hundreds of services. At POC scale with 1–2 people and a small event inventory, it is disproportionate infrastructure for the benefit it delivers. Backstage is the explicit upgrade path when team scale makes a full internal developer portal worthwhile.

---

## Consequences

**What this choice makes easier:**

- The CI gate enforces catalog completeness without requiring a running service. `npx @eventcatalog/cli@latest validate` in the PR pipeline rejects any merge that introduces an event without a corresponding AsyncAPI catalog entry.
- AsyncAPI diff in CI (`npx @asyncapi/cli@latest diff old.yaml new.yaml`) produces a structured report of breaking changes on every modification to an AsyncAPI file. Breaking changes are surfaced before merge, not after deployment.
- EventCatalog renders the schema registry Avro schema alongside the AsyncAPI documentation in a single catalog entry view. Engineers consult one URL, not two systems.
- The static site is always in sync with the main branch. No cache invalidation, no background sync job, no eventual consistency between the live catalog and the repository.
- The exit path to Backstage requires no specification migration — only a portal change.

**What this choice makes harder or impossible:**

- **Real-time consumer tracking** (who is currently consuming which event in production) is not available from a static site. Knowing the live consumer topology requires instrumenting the consumers themselves ([document 06](../06-observability-and-tracing.md)'s observability layer) and querying Redpanda consumer group offsets — the catalog records *who may consume* (governance), not *who is consuming* (operational state). These are different concerns, and conflating them in the catalog is a design error.
- **Self-service onboarding flows** (Backstage templates, scaffolding new services from the portal) are not available. At POC scale this is not a requirement.
- **Cross-catalog federation** (sharing events across organizational units with separate EventCatalog instances) requires EventCatalog federation features that may be in the commercial tier. At POC scale with a single repository, this is not a concern.

**Residual risks:**

- **License drift:** EventCatalog's commercial tier boundaries can shift. **Baseline (as of 2026-05-17):** EventCatalog Core (open source, Apache 2.0) provides the static-site build, AsyncAPI import, schema rendering, producer/consumer relationship visualization, changelog rendering, full-text search across event descriptions, and the CLI commands (`validate`, `generate`, `build`) used by the CI gate (P2). The commercial EventCatalog Pro / Enterprise tiers add Backstage and Confluent UI integrations, federated catalogs across repositories, asset management workflows, governance scorecards, custom-domain hosting, and SLA-backed support. The critical features for this architecture all sit in Core today. If they migrate to a paid tier, the exit path is Backstage (no specification change — Backstage imports the same AsyncAPI files) or Git-native AsyncAPI (portal removed, CI validation retained via the AsyncAPI CLI). Re-assess the Core feature surface at implementation time against the features required.
- **CI gate fragility:** The "no event without catalog entry" gate must be implemented carefully to avoid false positives that block unrelated PRs. The gate must validate only the AsyncAPI file set against the schema registry, not require the entire EventCatalog build to succeed on every PR. Separate the validation step (fast, runs on every PR) from the build step (runs on merge to main for deployment).
- **Example payload PII:** EventCatalog renders example payloads from AsyncAPI `examples:` blocks. These examples must use synthetic data only. An example block containing a real `client_id`, IBAN, or name is a GDPR incident embedded in version-controlled documentation.

---

## Implementation Principles

### P1 — AsyncAPI file is the governance source of truth

Every integration event has exactly one AsyncAPI specification file, located at `catalog/events/<event-name>.asyncapi.yaml`. This file is the authoritative contract: the schema registry Avro schema is its structural validation layer; the CI gate is its enforcement mechanism; EventCatalog is its rendering layer. No part of the governance record lives outside this file and the schema registry.

The minimum required fields in each AsyncAPI file. Events use CloudEvents 1.0 Binary Content Mode for Kafka: CloudEvents attributes are Kafka headers; the message value is the Avro-encoded business payload.

```yaml
asyncapi: '3.0.0'
info:
  title: <EventName>
  version: '<ce_schemaversion>'
  description: |
    <Business meaning: what business fact does this event represent?>
    <When emitted: exact triggering conditions, including conditions under which it is NOT emitted>
  x-owner: <bounded-context-name>
  x-owner-contact: <team-email or Slack channel>
  x-status: active | deprecated | sunset
  x-gdpr-legal-basis: <e.g. CONTRACT_PERFORMANCE, AML_OBLIGATION, LEGITIMATE_INTEREST — required>
  x-authorized-consumers:
    - <bounded-context-name>  # who MAY subscribe (governance input to Kafka ACLs)
channels:
  <topic-name>:
    x-compacted: true | false   # required; drives tombstone contract field (see P3)
    messages:
      <EventName>:
        $ref: '#/components/messages/<EventName>'
components:
  messages:
    <EventName>:
      bindings:
        kafka:
          # CloudEvents 1.0 Binary Content Mode — attributes in Kafka headers
          headers:
            type: object
            required:
              - ce_specversion
              - ce_id
              - ce_source
              - ce_type
              - ce_time
              - ce_correlationid
              - ce_causationid
              - ce_aggregatetype
            properties:
              ce_specversion:      { type: string, const: "1.0" }
              ce_id:               { type: string, format: uuid, description: "Unique event ID" }
              ce_source:           { type: string, description: "URI of the producing service" }
              ce_type:             { type: string, description: "Reverse-DNS event type, e.g. com.bank.deposits.DepositConstituted" }
              ce_time:             { type: string, format: date-time }
              ce_datacontenttype:  { type: string, const: "application/avro" }
              ce_dataschema:       { type: string, format: uri, description: "Schema registry subject URI" }
              ce_subject:          { type: string, description: "aggregate_id — the specific resource this event is about" }
              # Domain extension attributes:
              ce_correlationid:    { type: string, description: "Originating correlation ID (Primitive 4, doc 01)" }
              ce_causationid:      { type: string, description: "ce_id of the triggering message (Primitive 4, doc 01)" }
              ce_aggregatetype:    { type: string, description: "Aggregate type, e.g. Deposit" }
              ce_schemaversion:    { type: integer, description: "Schema version" }
              ce_producerversion:  { type: string, description: "Semantic version of the producing service" }
              # W3C Trace Context (injected by OTel SDK — not CloudEvents attributes):
              traceparent:         { type: string }
              tracestate:          { type: string }
      payload:
        schemaFormat: 'application/vnd.apache.avro+json;version=1.9.0'
        schema:
          $ref: '<schema-registry-url>/subjects/<subject-name>/versions/latest/schema'
```

Services may add additional `x-` extension fields. They may not omit the above.

---

### P2 — CI gate: no event without a catalog entry

The PR pipeline must include a validation step that:

1. Identifies all Redpanda topic configurations in the repository.
2. Verifies that every topic marked as carrying integration events has a corresponding `catalog/events/<event-name>.asyncapi.yaml`.
3. Runs `npx @asyncapi/cli@latest validate <file>` on every modified AsyncAPI file.
4. Runs `npx @asyncapi/cli@latest diff <old> <new>` on every modified AsyncAPI file and fails the build if breaking changes are detected without an explicit `x-breaking-change-approved: true` annotation in the file header (see P4).

This step must be fast (< 30 seconds). It must run on every PR that touches `catalog/`, `topics/`, or schema files. It must not require a running EventCatalog instance.

---

### P3 — GDPR tombstone contract field is mandatory on compacted topics

ADR-IC-002 established that GDPR erasure uses null-payload tombstone records on compacted topics. Any consumer subscribing to a compacted topic must configure its Avro SerDe to tolerate null payloads or it will crash on tombstone receipt.

Every AsyncAPI file for an event on a compacted topic (`x-compacted: true`) must include:

```yaml
channels:
  <topic-name>:
    x-compacted: true
    x-tombstone-contract: |
      This topic uses null-payload tombstone records for GDPR erasure (ADR-IC-001, ADR-IC-002).
      Consumers MUST configure their Avro SerDe to tolerate null values on this topic.
      A consumer that enforces a non-null schema will throw a deserialization exception
      on tombstone receipt. Concrete configuration depends on the SerDe library version
      and is intentionally not pinned in this contract — verify against the consumer's
      actual library at implementation time. As a starting point: for the Confluent Java
      Avro SerDe, the consumer must accept null `ConsumerRecord.value()` and the SerDe
      must not enforce a non-null schema; the corresponding property keys (and their
      defaults) vary across `kafka-avro-serializer` major versions.
```

The CI gate (P2) must validate that any AsyncAPI file with `x-compacted: true` also has `x-tombstone-contract` set. A compacted topic without this field fails the gate.

---

### P4 — Breaking-change detection is automated, approval is explicit

A breaking change in an AsyncAPI file is any modification that would fail the `BACKWARD` compatibility check in the schema registry (removing a field, changing a field type, renaming a required field) or any semantic change to the `description`, `x-owner`, `x-authorized-consumers`, or `x-status` fields without an RFC.

The CI gate runs `asyncapi diff` on every modified file. If the diff report contains breaking changes (classified as `BREAKING` by the AsyncAPI CLI), the build fails unless the file contains:

```yaml
info:
  x-breaking-change-approved: true
  x-breaking-change-rfc: <link to RFC document>
  x-breaking-change-consumers-notified:
    - <bounded-context-name>  # each authorized consumer, confirmed notified
```

These three fields together are the machine-readable record of the RFC approval from document 08 (Pillar 3). They do not replace the human RFC process — they enforce that the human process produced an output before the code merged.

---

### P5 — Deprecation lifecycle has machine-readable state

An event being deprecated must have its `x-status` updated to `deprecated` with additional fields:

```yaml
info:
  x-status: deprecated
  x-deprecated-date: 'YYYY-MM-DD'
  x-sunset-date: 'YYYY-MM-DD'  # minimum 6 months after x-deprecated-date
  x-deprecation-notice: <link to RFC or migration guide>
  x-replacement-event: <EventName> | null  # null if no direct replacement
```

The CI gate must reject any `x-sunset-date` less than 180 days after `x-deprecated-date`. The EventCatalog build surfaces deprecated events with a warning banner and their sunset date in the portal. A separate CI job (running on a schedule, not on PRs) checks for events whose `x-sunset-date` has passed and `x-status` is still `deprecated`, and opens a tracking issue.

---

### P6 — Catalog entry schema reference reconciles with the schema registry

Each AsyncAPI file's `schema:` block must reference the canonical Avro schema by its registry subject name and version:

```yaml
components:
  messages:
    DepositConstituted:
      payload:
        schemaFormat: 'application/vnd.apache.avro+json;version=1.9.0'
        schema:
          $ref: '<schema-registry-url>/subjects/term_deposit.DepositConstituted-value/versions/latest/schema'
```

The CI gate validates that the referenced subject exists in the schema registry at pipeline time. A catalog entry that references a subject that does not exist in the registry fails the gate — this prevents the catalog from documenting events that are not yet registered, or that had their registry subject deleted without updating the catalog.
