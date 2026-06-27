# Architectural Decision Records

This folder holds the Architectural Decision Records (ADRs) that materialise the integration architecture documented in `integration_concepts/` (documents 00–11) into concrete tool choices. The concept documents describe **what** the architecture does; the ADRs decide **which tool** does each piece, under the constraints of a 1–2 person team, a zero-cost budget, and a Portuguese banking regulatory context (GDPR, DORA, PSD2).

[ADR-IC-000](./ADR-IC-000-common-evaluation-criteria.md) defines the shared evaluation framework: two hard filters (F1 cost, F2 regulatory fit) and four soft criteria (S1 operational complexity, S2 ecosystem coherence, S3 exit cost, S4 community longevity). Every other ADR applies that framework to one decision.

---

## ADR index

| # | Title | Chosen | Supports docs |
|---|---|---|---|
| [000](./ADR-IC-000-common-evaluation-criteria.md) | Common Evaluation Criteria for All Tool Selections | Hard filters (F1, F2) + soft criteria (S1–S4); verdict format `Pass` / `Pass (conditional)` / `Fail` | all |
| [001](./ADR-IC-001-event-backbone-message-broker.md) | Event Backbone — Message Broker Choice | **Redpanda Community Edition** (Kafka-compatible, JVM-free) | [04](../04-plumbing-patterns.md) |
| [002](./ADR-IC-002-schema-format-and-registry.md) | Schema Format and Registry | **Apache Avro** + **Confluent SR API**, implemented by **Redpanda's built-in SR** | [04](../04-plumbing-patterns.md), [09](../09-long-term-schema-evolution.md) |
| [003](./ADR-IC-003-saga-orchestrator.md) | Saga Orchestrator | **Event-driven application orchestrator** (in-house; no third-party orchestration engine) | [05](../05-constitution-saga-walkthrough.md) |
| [004](./ADR-IC-004-outbox-pattern-mechanism.md) | Outbox Pattern Mechanism | **Custom polling publisher** (PostgreSQL `SELECT … FOR UPDATE SKIP LOCKED` → Redpanda) | [04](../04-plumbing-patterns.md) |
| [005](./ADR-IC-005-cqrs-read-model-storage.md) | CQRS Read Model Storage | **PostgreSQL** as the sole read-model store at POC inception | [03](../03-cqrs-and-read-models.md) |
| [006](./ADR-IC-006-edge-api-gateway.md) | Edge API Gateway and Synchronous Layer | **Kong Gateway CE** as the single shared gateway | [10](../10-security-and-threat-model.md), [11](../11-chat-agent-channel-strategy.md) |
| [007](./ADR-IC-007-observability-stack.md) | Observability Stack | **Grafana LGTM** (Loki + Grafana + Tempo + Prometheus) via the **OpenTelemetry Collector** | [06](../06-observability-and-tracing.md) |
| [008](./retired/ADR-IC-008-event-catalog-governance-tooling.md) | Event Catalog Governance Tooling | **[Superseded by ADR-IC-015]** EventCatalog with AsyncAPI as the contract format — the EventCatalog portal was replaced by Backstage after the License-drift risk realised (2026-06-07); the AsyncAPI format and §P1–§P6 carry forward | [08](../08-event-catalog-governance.md), [09](../09-long-term-schema-evolution.md) |
| [009](./ADR-IC-009-testing-infrastructure.md) | Testing Infrastructure and Contract Testing | **Testcontainers** + **Pact** + **WireMock** + **Toxiproxy** (with Pumba secondary) | [07](../07-testing-strategy.md) |
| [010](./ADR-IC-010-mcp-server-runtime-and-sdk.md) | MCP Server Runtime, SDK, Transport, and Authorization | **Python MCP SDK**, **Streamable HTTP**, hosted **behind Kong**, reusing the **existing IAM** as the OAuth 2.1 authorisation server | [10](../10-security-and-threat-model.md), [11](../11-chat-agent-channel-strategy.md) |
| [011](./ADR-IC-011-async-saga-completion-notification.md) | Async Saga Completion Notification — Out-of-Band Callback Wire Format | Pre-registered subscription endpoint; HMAC-SHA256 signing; exponential backoff with jitter; **dedicated notification service** subscribed to saga terminal events; SSE and callbacks **coexist** | [11](../11-chat-agent-channel-strategy.md) |
| [012](./ADR-IC-012-anti-corruption-layer-implementation.md) | Anti-Corruption Layer Implementation Approach | **Dedicated ACL service** per bounded context, hand-rolled outbound clients, pluggable inbound adapter (webhook / poller / MQ bridge), per-adapter circuit-breaker + bulkhead, ACL owns its own database with its own outbox | [02](../02-anti-corruption-layer.md) |
| [013](./ADR-IC-013-in-house-estate-build-and-repository-placement.md) | In-House Estate — Build Provenance and Repository Placement | **Five in-house estate components** (orchestrator [003], outbox [004], MCP [010], notification [011], ACL [012]) **co-located in the product monorepo** ([ADR-PC-019](../../product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md)) as extraction-ready subtrees, estate-repo split reserved; classifies all twelve IC decisions by build provenance (in-house vs consumed vs convention). Not a runtime tool — a placement/classification decision | [feature-design-c4-architecture](../../product_concepts/feature-design-c4-architecture.md) |
| [014](./ADR-IC-014-static-analysis-and-supply-chain-scanning.md) | Static Analysis and Supply-Chain Scanning | **GitHub-native trio** (free on the public repo): **CodeQL** SAST → Security tab (C#/Go; Python when `mcp-server` lands) + **Dependabot** (SCA + Actions pinning) + **GitHub secret scanning + push protection**. F1 is visibility-dependent (private-no-GHAS fallback = CodeQL-artifact + gitleaks, documented); report-only first, gating deferred to Q.7 | [07](../07-testing-strategy.md), [10](../10-security-and-threat-model.md) |
| [015](./ADR-IC-015-event-catalog-governance-tooling-backstage.md) | Event Catalog Governance Tooling — Backstage | **Backstage** (Apache-2.0, CNCF) with **AsyncAPI** as the contract format, **superseding [ADR-IC-008](./retired/ADR-IC-008-event-catalog-governance-tooling.md)** — the EventCatalog AsyncAPI generator went AGPL-3.0/commercial (License-drift risk realised, 2026-06-07), so ADR-IC-008's own Backstage exit path is taken; the AsyncAPI files stay the source of truth, the hermetic gate is unchanged, descriptors ship now and the Backstage host is deferred | [08](../08-event-catalog-governance.md), [09](../09-long-term-schema-evolution.md) |
| [016](./ADR-IC-016-service-identity-and-mtls.md) | Service Identity, Transport Authentication, and Observability-Plane Access Control | A **security-posture** decision (not a tool bake-off) selecting the mechanism for doc-10 boundaries 2–7 across three planes: **(i) service-to-service mTLS** (Kong-originated; ACL command port accepts only the orchestrator identity; certs from the OpenBao boundary), **(ii) Kafka SASL/SCRAM + topic ACLs** (distinct per-client identities via the existing `ISecretProvider` seam; ACLs as deployment config), **(iii) observability-plane RBAC + structural-only/pseudonymous span attributes** (`OBS_NO_PII_ATTRS`). Sequences M.3 by reachable-today (boundary-3 SASL, boundary-7 span pseudonymization) vs blocked (mTLS on the un-scaffolded ACL; Grafana RBAC on K.2) | [10](../10-security-and-threat-model.md) |
| [017](./ADR-IC-017-integration-event-promotion-criterion.md) | Integration-Event Promotion — Catalog-Gated Relay + Explicit Domain-vs-Integration Criterion | An **integration-boundary governance** decision (not a tool bake-off): the outbox relay publishes an event **iff** it has a catalog/AsyncAPI entry (**fail-closed**, `INTEGRATION_EVENT_CATALOG_GATED`), making a catalog entry the single enforced record of a deliberate promotion; operationalises [Primitive 2](../01-the-six-primitives.md)'s domain-vs-integration line as a written criterion (coarse business fact + named external consumer + stable contract); adds the **reverse** orphan check (`NO_UNCATALOGUED_EVENT_ON_BUS`) to [ADR-IC-015](./ADR-IC-015-event-catalog-governance-tooling-backstage.md)'s. Closes the silent-drift hole where every appended event publishes by accident; the engine⇄saga ingress topology is flagged open, not decided | [01](../01-the-six-primitives.md), [08](../08-event-catalog-governance.md) |
| [018](./ADR-IC-018-family-owned-saga-modules.md) | Saga Orchestrator — Family-Owned Saga Modules over a Family-Agnostic Substrate | A **structural/engineering** decision (residual class; F1/F2 do not discriminate), the integration-estate analog of [ADR-PC-021](../../product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md): concrete, product-family-specific sagas (`ConstitutionProcess`, the renewal saga) move out of `Babelstone.Orchestrator` into per-family `Babelstone.Families.<X>.Orchestration` **saga modules**, leaving a **family-agnostic substrate** (the runtime + `ISagaStateMachine`/`IResultEventBridge`/`ISagaCommandRouter` ports + a new `ISagaModule` plug-in). Dissolves the central `SagaState` enum into family-owned string states; the host composes modules (and MAY name a family; the substrate MAY NOT). Operationalises [ADR-IC-003 §P3](./ADR-IC-003-saga-orchestrator.md) with a fitness gate (`ORCHESTRATOR_FAMILY_AGNOSTIC`); first realised by a behavior-preserving `ConstitutionProcess` extraction, then the renewal saga is born as a family module | [05](../05-constitution-saga-walkthrough.md) |
| [019](./ADR-IC-019-family-agnostic-notification-platform.md) | Notification Service — Family-Owned Contributions over a Family-Agnostic Notification Core | A **structural/engineering** decision (residual class; F1/F2 do not discriminate), the notification-estate analog of [ADR-IC-018](./ADR-IC-018-family-owned-saga-modules.md) / [ADR-PC-021](../../product_concepts/adrs/ADR-PC-021-application-layer-family-owned-deciders.md): the notification service becomes a **family-agnostic core** (worker host + scheduler + per-service outbox + delivery/subscription stores) that reads family data over the **storage-opaque** [ADR-PC-027](../../product_concepts/adrs/ADR-PC-027-deposit-read-surface-canonical-resource.md) read contract — never by binding the engine kernel + a family's internal projection types (the shape bd `babelstone-60n8.1` stood up). Family-specific content/scheduling is a family-owned contribution composed at the host edge (host MAY name a family; the core MAY NOT). Adds the `NOTIFICATION_FAMILY_AGNOSTIC` fitness gate — the last platform service to get the engine's discipline | [11](../11-chat-agent-channel-strategy.md) |
| [020](./ADR-IC-020-openapi-second-catalogue-format-and-rest-governance-tooling.md) | OpenAPI as a Second Catalogue Specification Format — Spectral + oasdiff for REST Governance | **OpenAPI 3.1** adopted as a *second* canonical specification format alongside AsyncAPI/Avro (the synchronous REST plane behind Kong, [ADR-IC-006](./ADR-IC-006-edge-api-gateway.md), gets the governed-contract discipline the event plane already has); **Spectral** (`@stoplight/spectral-cli`, Apache-2.0) chosen for lint + governance-field enforcement and **oasdiff** (Apache-2.0) for breaking-change diff — both hermetic and free, the **F1 licence hard filter re-run** to avoid the EventCatalog → Backstage drift ([ADR-IC-015](./ADR-IC-015-event-catalog-governance-tooling-backstage.md)); **Redocly CLI rejected** (breaking-diff behind a paid tier). Amends [ADR-IC-015](./ADR-IC-015-event-catalog-governance-tooling-backstage.md) to carry `spec.type: openapi` API entities; kept **disjoint** from [ADR-IC-017](./ADR-IC-017-integration-event-promotion-criterion.md)'s event-promotion biconditional (a REST contract is not a bus promotion) | [08](../08-event-catalog-governance.md), [10](../10-security-and-threat-model.md) |
| [021](./ADR-IC-021-iam-oauth-authorization-server.md) | IAM — OAuth 2.1 / OIDC Authorization Server — Logto | The **token issuer** that [doc 10](../10-security-and-threat-model.md) Boundaries 1/6/9 assume but never selected (Kong [ADR-IC-006](./ADR-IC-006-edge-api-gateway.md) only *validates*; the MCP server [ADR-IC-010](./ADR-IC-010-mcp-server-runtime-and-sdk.md) is only the *resource server*; [ADR-IC-016](./ADR-IC-016-service-identity-and-mtls.md) scoped to internal-hop identity, Boundaries 2–7). Decisive finding: **no self-hosted OSS IdP clears both MCP must-haves** — and RFC 8707 resource-bound `aud` (the un-fakeable anti-replay MUST) outranks DCR (doc 10 makes DCR *recommended*, deferrable at curated staging). **Logto** chosen (MPL-2.0, Node-on-`node-oidc-provider`, Postgres-reuse, non-JVM) as the **single** AS for Boundaries 1/6/9 — the **only** survivor with *native, adversarially-confirmed* RFC 8707 + project-DNA fit; the DCR gap is accepted-and-tracked for curated agents. **Authelia not adopted** (it is itself a second IdP with its own user store — redundant beside Logto; a stateless oauth2-proxy→Logto is reserved only if a non-OIDC surface ever needs forward-auth). **Keycloak rejected** (RFC 8707 experimental-flag-only + JVM, the [ADR-IC-001](./ADR-IC-001-event-backbone-message-broker.md) anti-pattern); **Zitadel/Ory/Authentik** fail RFC 8707 natively; **Auth0/Cognito (F1), WorkOS-Clerk (F2 US residency), Zitadel-Cloud (managed)** eliminated at the filters | [10](../10-security-and-threat-model.md), [11](../11-chat-agent-channel-strategy.md) |

---

## Chosen tools and how they relate

The chosen tools form one coherent runtime topology, not twelve independent choices. The decisive force across the series is the ADR-IC-000 hard filter F1 (zero cost) combined with S1 (1–2 person operability): every tool is open-source, self-hostable, and either JVM-free at runtime or scoped to a single auxiliary process.

### Runtime topology

```
                                  ┌────────────────────────────────┐
                  Browser /       │  Kong Gateway CE (ADR-IC-006)     │
                  Mobile / SDK ──▶│  JWT / mTLS / rate-limit / SCA │
                                  │  Routes: REST, SSE, MCP        │
                                  └──────────┬─────────────────────┘
                                             │
            MCP agents ──── Streamable HTTP ─┤
                                             │
                          ┌──────────────────┴───────────────────┐
                          │  Deposits domain service             │
                          │  ┌─────────────────────────────────┐ │
                          │  │ Aggregates + event-driven saga  │ │
                          │  │ orchestrator (ADR-IC-003)          │ │
                          │  └────────────┬────────────────────┘ │
                          │               │ writes outbox in TX  │
                          │       ┌───────▼──────────┐           │
                          │       │ PostgreSQL       │           │
                          │       │ (domain state +  │           │
                          │       │  outbox table)   │           │
                          │       └───────┬──────────┘           │
                          │               │                      │
                          │   Custom polling publisher (ADR-IC-004) │
                          │               │                      │
                          └───────────────┼──────────────────────┘
                                          │
                                  Avro on the wire (ADR-IC-002)
                                          │
                          ┌───────────────▼──────────────────────┐
                          │  Redpanda CE (ADR-IC-001)               │
                          │  + built-in Confluent-API SR         │
                          │                                      │
                          │  Topics: deposits.integration.events │
                          └───┬───────────────────┬──────────────┘
                              │                   │
              ┌───────────────┘                   └──────────────┐
              │                                                  │
   ┌──────────▼──────────┐                          ┌────────────▼──────────────┐
   │ Read-model projector│                          │ Notification service      │
   │ (ADR-IC-005)           │                          │ (ADR-IC-011)                 │
   │ → PostgreSQL CQRS   │                          │ → webhook HMAC delivery   │
   └─────────────────────┘                          └───────────────────────────┘

   ┌──────────────────────────────────────────┐
   │ ACL service (ADR-IC-012) — dedicated, own DB│
   │  ┌──────────────────────────────────┐    │
   │  │ Outbound: hand-rolled clients ───┼────┼──── Core Banking (SOAP/REST/MQ)
   │  │ Inbound:  webhook / poll / MQ ◀──┼────┼──── Core Banking
   │  │ State store + own outbox ────────┼────┼──── Redpanda (events back to domain)
   │  └──────────────────────────────────┘    │
   └──────────────────────────────────────────┘

   ┌─────────────────────────────────────────┐  ┌──────────────────────────────┐
   │ OpenTelemetry Collector → Grafana LGTM  │  │ Backstage (ADR-IC-015)          │
   │ (ADR-IC-007) — logs, traces, metrics       │  │ + AsyncAPI specs             │
   │ scraped from all services above         │  │ (governance, not runtime)    │
   └─────────────────────────────────────────┘  └──────────────────────────────┘
```

### How the tools fit together

- **The backbone is one broker.** Redpanda CE (ADR-IC-001) is the only message broker. Its built-in schema registry (ADR-IC-002) speaks the Confluent SR REST API, so every producer and consumer uses the same wire format (Avro) and the same governance contract without a second deployment.

- **State lives in PostgreSQL.** Both the domain (write side, ADR-IC-004 outbox) and the read side (ADR-IC-005 CQRS projections) use PostgreSQL. The ACL (ADR-IC-012) also uses PostgreSQL, but its own instance — the bounded-context boundary is also a database boundary.

- **Three outbox publishers, one pattern.** The domain, the ACL, and the notification service each have their own outbox table and their own polling publisher (ADR-IC-004). The pattern's invariants — same-transaction write, `SELECT FOR UPDATE SKIP LOCKED`, lag SLI — apply identically in every service.

- **The edge is one gateway.** Kong CE (ADR-IC-006) terminates JWT validation, mTLS, rate limiting, SCA enforcement, and OTel context propagation for every external surface — REST, SSE (ADR-IC-006 / ADR-IC-011 D6), and MCP Streamable HTTP (ADR-IC-010). No service implements these at its own ingress.

- **One observability pipeline.** Every service emits OpenTelemetry signals to the OTel Collector, which fans out to Grafana LGTM (ADR-IC-007). The orchestrator's saga spans, the outbox publisher's lag metric (ADR-IC-004 P4), the ACL's circuit-breaker state (ADR-IC-012 D4), and the notification service's delivery attempts (ADR-IC-011 P3) all surface in the same dashboards.

- **Testing fidelity comes from real infrastructure.** Testcontainers (ADR-IC-009) spins up PostgreSQL and Redpanda — the same ones the production stack uses — per test class. Pact verifies message contracts (Avro events) and HTTP contracts. WireMock simulates the Core's SOAP surface for tests that cannot afford a Testcontainer. Toxiproxy injects per-connection faults so saga tests can verify the indeterminate-state path (ADR-IC-012 D5) without taking down shared infrastructure.

- **Governance is offline.** The event catalogue (ADR-IC-015, superseding ADR-IC-008) reads AsyncAPI specs from source control; the portal (Backstage) does not sit on the runtime path. Schema evolution (doc 09) is enforced by the schema registry (ADR-IC-002) at publish time, not by the catalogue.

- **The agent channel adds two components, not a rewrite.** ADR-IC-010 (Python MCP server behind Kong) and ADR-IC-011 (notification service for async completion delivery) layer onto the existing event backbone — both subscribe to events the orchestrator already emits. The synchronous REST/SSE surface and the agent-channel surface coexist behind the same gateway.

---

## ADR conventions

### Verdict format (defined in ADR-IC-000)

Hard filters use three values:

- **Pass** — the candidate satisfies the filter without qualification.
- **Pass (conditional)** — the candidate satisfies the filter only if a specific mitigation is documented in the same table cell and restated in Consequences or Residual Risks. The mitigation is committed as part of the decision.
- **Fail** — the candidate is disqualified by this filter. A waiver requires explicit justification.

Soft criteria are expressed in prose, not numerical scores — two readers may legitimately weight the criteria differently. Each ADR's `## Decision` section names the decisive reason rather than restating every positive attribute of the chosen tool.

### Status lifecycle

- **Proposed** — the ADR is drafted and open for review. No commitment yet.
- **Accepted** — the decision is committed and binds downstream work. New code and infrastructure conform.
- **Superseded by ADR-NNN** — the decision has been replaced. The superseded ADR remains in the folder as historical record; readers follow the link to the current ADR.
- **Rejected** — the proposal was considered and not adopted. Kept as evidence that the option was evaluated.

A change to an Accepted ADR is rare and requires either an amendment (a dated entry appended to the ADR explaining the narrow change) or supersession (a new ADR with a different number). Editing an Accepted ADR's `## Decision` section in place is not the supported workflow.

### Numbering

ADR numbers are sequential and never reused. When picking a new number, check both the on-disk filenames (`ls integration_concepts/adrs/`) and the planned-but-unwritten ADRs in the issue tracker (`bd list | grep ADR-`); the two share one number space. This convention exists because a real numbering collision occurred between an on-disk ADR-IC-010 and a tracker-reserved ADR-IC-010, which had to be resolved by renumbering.

### File naming

`ADR-NNN-short-kebab-case-slug.md`. The slug is the chosen tool or the decision topic, not the alternatives considered. Example: `ADR-IC-001-event-backbone-message-broker.md`, not `ADR-IC-001-kafka-vs-redpanda.md`.

### Cross-linking

Links from one ADR to another use relative paths (`./ADR-NNN-…md`). Links from an ADR back to a concept document use `../NN-name.md`. This matches the convention in [CLAUDE.md](../../../../CLAUDE.md).
