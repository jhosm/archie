# ADR-IC-007: Observability Stack

| Field | Value |
|---|---|
| Status | Accepted |
| Date | 2026-05-17 |
| Deciders | jhosm |
| Common criteria | [ADR-IC-000](./ADR-IC-000-common-evaluation-criteria.md) |
| Depends on | [ADR-IC-001](./ADR-IC-001-event-backbone-message-broker.md) |

---

## Context

[Document 06](../06-observability-and-tracing.md) establishes that observability is not a post-hoc concern in a distributed saga architecture — it is the operational substrate that determines whether a production failure is diagnosed in minutes or in hours. The concrete scenario from document 06 (a client's deposit constitution disappeared, money already debited) illustrates what adequate observability must make possible: paste a `correlation_id`, see the complete trace of 47 operations across 8 systems, identify the failure point in 30 seconds.

Achieving this requires decisions across two separable layers:

**Layer 1 — Instrumentation standard:** How do services emit observability signals? This determines the vendor portability of the entire stack and the correlation model between traces, metrics, and logs. Document 06 is explicit: OpenTelemetry (OTel) is the standard. **OpenTelemetry is adopted as the instrumentation baseline without a comparative evaluation.** The reasons are structural: it is a CNCF graduated project (Apache 2.0 SDK, Apache 2.0 Collector), it provides vendor-neutral emission of all three signal types (traces, metrics, logs) with a single instrumentation model, it is the industry-converged standard that every major backend now natively receives, and its W3C Trace Context propagation (`traceparent` header) is the mechanism by which the identity trio from [document 01](../01-the-six-primitives.md) (correlation_id, causation_id, message_id) becomes distributed tracing. No alternative warrants evaluation.

**Layer 2 — Signal backends:** Where do traces, metrics, and logs land, and how are they queried and correlated? This is what this ADR decides.

### The integration requirement that shapes backend selection

Document 06 states that `trace_id` and `span_id` are injected automatically into every log line by the OTel logging integration. The result is bidirectional navigation: given a trace, navigate to its logs; given a log entry, navigate to its trace. This navigation must work in a unified UI — not by copying a `trace_id` out of a Jaeger tab and pasting it into a Kibana search box. A 1–2 person team debugging a saga failure at 2am cannot afford multi-tool friction in their observability workflow.

This requirement — unified cross-signal navigation — is the primary differentiator between backend candidates.

### GDPR surface of the observability backend

Document 06 identifies a direct consequence of the instrumentation model: span attributes include `deposit.amount`, `core.account`, and `deposit.client_id`. The distributed tracing backend therefore aggregates sensitive financial and operational data from every service in the ecosystem into one searchable place. It is a regulated data store (see also [document 10](../10-security-and-threat-model.md) — Trust Boundary 3 and Principle 4 for the full data classification taxonomy). Two concrete obligations follow:

- **RBAC is not optional.** NOC needs error rates and lag metrics; it does not need to query specific client transaction details. The access model must be designed before the stack is deployed.
- **Retention must be bounded.** Traces and logs containing financial attributes have a retention horizon driven by regulatory minimum (PSD2 audit trail: typically 5 years for payment operations) and GDPR maximum (data not retained beyond its stated purpose). These bounds must be expressed in the storage configuration, not left as infinite retention defaults.

**Candidates evaluated:**

| # | Candidate | Notes |
|---|---|---|
| A | **Grafana LGTM Stack** | Loki (logs) + Grafana (visualization) + Tempo (traces) + Prometheus (metrics); all from Grafana Labs; native cross-signal correlation in Grafana |
| B | **Jaeger + Prometheus + Grafana** | CNCF graduated trace backend; standard metrics stack; no native log backend; separate Jaeger UI for traces |
| C | **SigNoz** | Purpose-built OTel APM; Apache 2.0; unified UI; ClickHouse backend for all three signals |
| D | **Elastic APM stack** | Elasticsearch + Kibana + APM Server; Elasticsearch SSPL after v7.10 |
| E | **Datadog / commercial SaaS APM** | SaaS, paid |
| F | **Zipkin** | *Excluded before evaluation* — trace-only backend; no metrics or log integration; declining OTel investment relative to Jaeger; solves only one-third of the three-signal requirement (document 06) and cannot serve as the foundation |

---

## Evaluation

### Hard filter results

#### F1 · Cost / licensing

| Candidate | Licence | Assessment | Proceeds? |
|---|---|---|---|
| Grafana LGTM Stack | Grafana: AGPLv3; Tempo: Apache 2.0; Loki: AGPLv3; Prometheus: Apache 2.0 | AGPLv3 is explicitly in ADR-IC-000's acceptable list. Self-hosted; Grafana Cloud free tier available but not required. | **Pass** |
| Jaeger + Prometheus + Grafana | Jaeger: Apache 2.0 (CNCF); Prometheus: Apache 2.0; Grafana: AGPLv3 | Same licensing as components of candidate A. | **Pass** |
| SigNoz | Apache 2.0 (core); commercial tier available | Core platform is Apache 2.0; enterprise features are commercial. Self-hosted on ClickHouse. | **Pass (conditional)** — required features must be verified to fall within the Apache 2.0 tier at implementation time |
| Elastic APM stack | Elasticsearch: SSPLv1 (from v7.10+); Kibana: SSPLv1 (from v7.10+); APM Server: Apache 2.0 | Elasticsearch and Kibana changed to SSPL in January 2021. SSPL contains use restrictions that ADR-IC-000 explicitly flags as a hard fail even when currently free. OpenSearch (Apache 2.0, AWS fork) is a credible alternative, but OpenSearch APM maturity significantly lags the Elastic APM stack. | **Fail** — Elasticsearch and Kibana SSPL licence; OpenSearch APM variant not at equivalent maturity to justify evaluation |
| Datadog / commercial SaaS | Proprietary; SaaS subscription required | No free tier that covers the signal volume of this architecture without a payment commitment. | **Fail** — no free tier for POC-scale usage without payment method |

*Date of licence assessment: 2026-05-17.*

#### F2 · Regulatory fit

The observability backend is, per document 06, a regulated data store — it aggregates financial attributes from every service. This makes the F2 evaluation more demanding than for pure infrastructure tools.

| Candidate | GDPR | DORA | PSD2 | Proceeds? |
|---|---|---|---|---|
| Grafana LGTM Stack | Grafana's RBAC model (introduced in Grafana 8+) provides org-level and folder-level access control, allowing differentiated access between NOC, compliance, and engineering roles as document 06 requires. Loki's label-based log storage allows retention policies per log stream — enabling different retention horizons for operational logs vs. financial-attribute-bearing logs. Traces in Tempo can carry pseudonymous identifiers rather than raw `client_id` values (document 06 recommendation), reducing the GDPR surface of the trace store. Data residency: all self-hosted; no cross-border data transfer. | Self-hosted stack; resilience testing (chaos injection, failover drills) is under operator control. Grafana's alerting can target the observability stack's own health metrics (the "watchdog" pattern). Prometheus and Loki are designed for high availability; single-node deployment for POC is production-upgradeable. | Grafana's audit log captures dashboard access and query history, which satisfies the PSD2 requirement for an auditable trail of who accessed what financial data in the observability layer. | **Pass** |
| Jaeger + Prometheus + Grafana | Same RBAC and retention properties for the Prometheus+Grafana components. Jaeger's own access control is limited: it has no built-in RBAC in the open-source distribution — access to Jaeger UI is all-or-nothing (behind a reverse proxy). This is a regulatory gap if trace data includes financial attributes: every person who can access the Jaeger UI can query any trace. Mitigations exist (Jaeger behind an OAuth proxy, or using Grafana as the sole trace query UI via the Jaeger datasource plugin), but they require explicit architectural effort. | Same as Grafana LGTM. | Jaeger's query API has no audit log in the open-source version. Queries to trace data bearing financial attributes are not logged, which weakens the PSD2 accountability trail for data access at the observability layer. | **Pass (conditional)** — Jaeger OSS lacks native RBAC and query audit; access must be mediated by an OAuth proxy or by routing all trace queries through Grafana, from day one |
| SigNoz | SigNoz uses ClickHouse as its backend, with a standard RBAC model at the application layer. ClickHouse supports column-level access control which can enforce attribute-level restrictions on sensitive span fields (e.g., restricting queries on `deposit.amount` to authorized roles). Retention is configurable per ClickHouse table. Self-hosted; no cross-border transfer. | Self-hosted; resilience under operator control. ClickHouse has strong HA capabilities. | SigNoz's application-layer audit log is less mature than Grafana's. Access audit for PSD2 purposes requires additional instrumentation. | **Pass (conditional)** — audit log maturity must be validated for PSD2 access-audit purposes at implementation time, with supplementary instrumentation if needed |

All three passing candidates proceed.

---

### Soft criteria

#### Grafana LGTM Stack (Tempo + Prometheus + Loki + Grafana)

**S1 · Operational complexity:** Four components, but a Docker Compose file for single-node POC deployment is the standard getting-started path, and Grafana Labs maintains official compose configurations. Prometheus and Grafana are each lightweight; Tempo and Loki require storage configuration (local filesystem for POC, object storage for production). The operational surface is real but well-documented and widely practiced by small teams. A single-node LGTM stack for Portuguese banking POC volumes (thousands of operations per day, not millions per second) is within the operating capability of a 1–2 person team using the official quickstart.

**S2 · Ecosystem coherence:** Maximum. The four components are designed together by Grafana Labs. In Grafana's Explore view, you can: navigate from a metric anomaly to the traces that coincide with it (via Prometheus exemplars linking to Tempo trace IDs); navigate from a trace span to the logs that share its `trace_id` (via Loki's derived fields matching the OTel `trace_id`); navigate from a log entry back to its trace. This three-way correlation is not a third-party integration — it is a first-party feature of the stack. For the saga debugging scenario from document 06 (paste `correlation_id`, see everything), this is the native experience. OpenTelemetry's SDKs generate traces in OTLP format; Tempo natively receives OTLP; Loki natively receives OTel-formatted logs via the Grafana Alloy collector. Prometheus scrapes OpenMetrics endpoints. The stack requires minimal adapter glue.

**S3 · Exit cost:** Low-moderate. Grafana, Tempo, and Loki are components of the Grafana Labs ecosystem; replacing any one requires updating the Grafana datasource configuration and potentially the collector pipeline. The OTel Collector's pipeline is vendor-neutral by design — switching from Tempo to Jaeger requires changing one exporter in the collector config, not refactoring instrumentation code. Prometheus is a de facto standard with the widest ecosystem of alternatives. Grafana dashboards export as JSON and can be imported into any Grafana-compatible visualization tool. No proprietary data format.

**S4 · Community and longevity:** Grafana Labs is a commercial company (backed by significant VC and generating revenue from Grafana Cloud) that has not changed the core components' licenses. AGPLv3 and Apache 2.0 have been stable for these tools. Tempo and Loki were both started by Grafana Labs, but Prometheus (graduated CNCF) and Grafana (CNCF sandbox) have foundation-level governance. The risk is Grafana Labs' commercial trajectory affecting the open-source editions — historically, Grafana Labs has maintained the open-source tier rather than restricting it. Community size is very large for Grafana and Prometheus; growing for Tempo and Loki.

---

#### Jaeger + Prometheus + Grafana

**S1 · Operational complexity:** Three components for traces + metrics + visualization. Jaeger has multiple deployment modes: Jaeger all-in-one (suitable for POC, single binary), or the distributed mode (Collector + Query + Storage). All-in-one mode uses an in-memory store or local disk (Badger storage), which is appropriate for POC. This is marginally simpler than the LGTM stack (three services vs four). However, it leaves the log backend unsolved — document 06 requires structured log correlation with trace IDs. Adding a log backend (Loki, Vector, or a file-shipper configuration) immediately closes the operational simplicity gap.

**S2 · Ecosystem coherence:** Jaeger is the reference OTel trace backend — its CNCF graduated status, OTel-native SDK support, and wide production adoption make it technically excellent for traces. Grafana supports Jaeger as a datasource, which enables unified dashboards. The gap is that the trace-to-log and metric-to-trace correlations that are native to the Grafana-Tempo-Loki combination require explicit configuration when Jaeger is the trace backend. Specifically: the Loki derived-fields feature can be configured to link log entries to Jaeger traces by `trace_id`, but this is a manual datasource configuration step, not an automatic integration. The experience is coherent but not seamless.

**S3 · Exit cost:** Low. Jaeger is CNCF-governed, open-source, and its storage formats (Elasticsearch, Cassandra, Badger) are standard. The OTel Collector exports to Jaeger via the standard OTLP receiver — switching from Jaeger to Tempo requires changing one line in the collector config. Prometheus and Grafana exit costs are the same as in the LGTM stack.

**S4 · Community and longevity:** Jaeger is a CNCF graduated project, originally built by Uber. Strong community, extensive production adoption at large companies, excellent OTel support. Longevity is the strongest of all trace backend candidates — CNCF graduated projects have foundation-level protection and multi-vendor governance.

**Where Jaeger requires explicit additional effort:**

- **Log backend**: a separate log aggregation component is required to satisfy the structured-log + trace correlation requirement from document 06. Adding Loki to Jaeger+Prometheus+Grafana recreates the LGTM stack with a different trace backend — the operational simplicity advantage disappears.
- **Jaeger UI RBAC**: the open-source Jaeger UI has no native authentication or authorization. All Jaeger query access is unrestricted unless the operator adds an OAuth proxy. Given that trace attributes include financial data (document 06), this gap requires an explicit mitigation from day one, not a "we'll add auth later" assumption.

---

#### SigNoz

**S1 · Operational complexity:** SigNoz runs as a single Docker Compose application (SigNoz frontend + backend + ClickHouse). This is simpler to get started with than the LGTM stack. However, ClickHouse is a powerful but operationally non-trivial database: it has its own storage configuration, retention policies, and HA topology. For a 1–2 person team, ClickHouse adds a new database technology to the operational inventory that is not shared with any other component in the stack (the application uses PostgreSQL; the message broker is Redpanda). The LGTM stack's Tempo and Loki, while new technologies, are simpler operationally than ClickHouse.

**S2 · Ecosystem coherence:** SigNoz is purpose-built for OpenTelemetry — it receives OTLP natively and provides traces, metrics, and logs in a single UI. The cross-signal correlation (trace to log, metric to trace) is a first-class feature. However, SigNoz's visualization layer is its own custom UI (not Grafana), which means the team does not benefit from Grafana's mature dashboard ecosystem, alert management, and plugin library. For custom dashboards (persona-based dashboards from document 06 — operations dashboard, business dashboard, per-bounded-context dashboard), Grafana's mature ecosystem provides more tooling than SigNoz's younger UI.

**S3 · Exit cost:** Moderate. SigNoz stores data in ClickHouse, which is queryable via SQL — data is not locked in a proprietary format. However, the SigNoz UI and its alerting configuration are specific to SigNoz. Migrating dashboards and alerts to Grafana would require rebuilding them in Grafana's query language and dashboard DSL.

**S4 · Community and longevity:** SigNoz is maintained by SigNoz Inc., a Y Combinator-backed company. Apache 2.0 licence. Growing community, but substantially smaller than Grafana/Prometheus. The commercial trajectory (enterprise tier) is similar to Grafana Labs — core remains open source, enterprise features are paid. Longevity is good but not CNCF-anchored.

---

## Decision

**Chosen: Grafana LGTM Stack (Loki + Grafana + Tempo + Prometheus), deployed via OTel Collector**

The decisive reason is the unified cross-signal correlation, and it comes down to the 2am saga debugging scenario from document 06.

The scenario requires: paste a `correlation_id`, navigate seamlessly between the trace (which service, which span, which saga state transition), the correlated log entries (what the aggregate said when it failed), and the metrics (was this an isolated incident or was outbox lag high at the same time?). In the Grafana LGTM stack, this navigation is a first-party feature: trace-to-log correlation via OTel `trace_id` in Loki derived fields, metric-to-trace correlation via Prometheus exemplars pointing to Tempo trace IDs. It requires configuration but not custom integration code.

In the Jaeger candidate, the same navigation requires fronting Jaeger with an OAuth proxy (to satisfy the RBAC requirement for financial span attributes), configuring Loki as a separate component (closing the log gap), and wiring the Jaeger datasource to Grafana for unified dashboards. At that point, the operational surface of the Jaeger option equals the LGTM stack's surface — with the additional burden of the RBAC mitigation work — while providing a less seamless correlation experience.

Jaeger is the technically strongest trace backend in isolation — CNCF graduated, broadest production history, excellent OTel support. But "best trace backend in isolation" is the wrong criterion here. The architecture requires all three signals (traces, metrics, logs) to be correlated in one UI. Tempo + Grafana + Loki provides that natively. Jaeger achieves it with more explicit wiring and without resolving the access control gap out of the box.

SigNoz is an honest contender and deserves acknowledgement. Its unified OTel-native model and simpler deployment are genuine strengths. The reason it does not win is the immaturity of its visualization layer for the multi-persona dashboards document 06 requires, and the addition of ClickHouse as a new database paradigm to a stack that already operates PostgreSQL and Redpanda.

The operational overhead argument that determined ADR-IC-003, ADR-IC-004, and ADR-IC-005 applies here too, but points *toward* the LGTM stack rather than away from it: running Jaeger + an OAuth proxy + Prometheus + Grafana + a log backend is more pieces than running Loki + Grafana + Tempo + Prometheus as a single Compose application.

---

**Rejected: Elastic APM stack**

Elasticsearch and Kibana changed to SSPL in January 2021. SSPL is explicitly flagged in ADR-IC-000 as a licence that fails the F1 filter even when currently free. The OpenSearch fork (Apache 2.0) is a viable alternative for general log analytics but lacks the APM maturity of the Elastic stack.

**Rejected: Datadog / commercial SaaS**

No free tier that covers POC-scale usage without a payment commitment. A production Datadog deployment in a banking context would be an excellent choice — zero operational overhead, outstanding APM features, strong RBAC. At zero budget, it is not viable.

**Rejected: Jaeger + Prometheus + Grafana**

The strongest alternative candidate. Rejected not on quality grounds — Jaeger is excellent — but because the RBAC gap (no native access control in Jaeger UI) requires explicit mitigation for a stack that stores financial span attributes, and because satisfying the log correlation requirement forces adding Loki, recreating the LGTM stack's component count without its native inter-component integrations.

**Rejected: SigNoz**

Strong on OTel-native integration and operational simplicity. Loses on visualization maturity (Grafana's ecosystem is significantly more complete for multi-persona dashboard design) and on the addition of ClickHouse as a new database to operate.

---

## Consequences

**What this choice makes easier:**

- A single Grafana instance is the entry point for all observability signals. Engineers, ops, and compliance personnel all use one URL — differentiated by Grafana RBAC role, not by which tool they know.
- The OTel Collector pipeline is the single export configuration point. Switching a backend component (e.g., Tempo to a different trace store) requires changing the collector's exporter, not refactoring service instrumentation.
- `correlation_id` from Primitive 4 (document 01) maps directly onto OTel's `trace_id` in the Grafana correlation model. The identity trio (correlation, causation, message) becomes span attributes; Loki derived fields can surface them as clickable links in log queries.
- The Grafana alerting engine provides the SLO-based alerts (outbox lag, consumer lag, saga in `HUMAN_INTERVENTION_REQUIRED`) from document 06 in the same tool that provides dashboards, without a separate alerting platform.

**What this choice makes harder or impossible:**

- **ClickHouse-backed analytics** at OLAP scale is not available. Tempo and Loki are not designed for multi-month historical trace analytics (e.g., "show me all constitutions that entered compensation over the last 6 months"). For regulatory audit purposes requiring historical trace replay, a separate long-term storage strategy (object storage with Tempo's TraceQL over parquet exports, or a dedicated archive) must be designed separately.
- **Grafana's AGPLv3 copyleft applies if Grafana is modified and redistributed.** For internal self-hosted use, AGPL is not a practical concern — you are using, not distributing. However, if the team builds a custom Grafana plugin and distributes it externally, the AGPL obligation applies.

**Residual risks:**

- **Grafana Labs commercial trajectory:** Grafana Labs has historically kept the open-source core stable while commercializing cloud-managed features. The risk that observability-critical features (specific RBAC capabilities, new correlation features) migrate to Grafana Enterprise is real and precedented in the ecosystem. Monitor the Grafana changelog at each major version for open-source feature tier changes. **Baseline (as of 2026-05-17):** OSS Grafana ships basic role-based access control (Viewer / Editor / Admin), datasource permissions, dashboards-as-code provisioning, the unified alerting engine, and the Tempo / Loki / Prometheus correlation features used by this ADR. Grafana Enterprise adds: fine-grained RBAC down to dashboards and folders with team-based assignment, SAML/OAuth/LDAP enterprise SSO, audit logs of dashboard and query access, datasource query caching, recorded queries, and reporting / scheduled PDF exports. If the OSS RBAC tier later proves insufficient for the role split described in P6 below, the choice is to adopt Enterprise (which re-opens F1) or front Grafana with an external auth gateway that enforces dashboard-level ACLs upstream of the application.
- **Single-node Loki and Tempo:** for POC deployment, Loki and Tempo run in single-process mode. This is not HA — loss of the observability node means loss of observability. For a POC, this is acceptable (the application still runs). Before production hardening, both must move to a replicated configuration or cloud-managed storage backend (Grafana Cloud free tier is an option for trace and log storage without operating the backends).
- **Span attribute PII discipline:** the GDPR surface of the stack depends entirely on what teams put in span attributes. A single engineer who adds `client_name` or `client_email` as a span attribute creates a GDPR incident inside the tracing backend. P4 (attribute classification) below is the technical control; the cultural control is code review that treats span attributes with the same PII discipline as log messages.

---

## Implementation Principles

### P1 — OTel Collector is the mandatory pipeline boundary

No service may export telemetry directly to a backend (Tempo, Loki, Prometheus). Every service exports to the OTel Collector via OTLP (gRPC or HTTP). The Collector fans out to backends, applies sampling decisions, and provides the single reconfiguration point when backends change.

The Collector pipeline:
```
Service SDKs → OTLP → OTel Collector → Tempo (traces)
                                      → Prometheus remote_write (metrics)
                                      → Loki (logs via OTLP log receiver)
```

Sampling strategy for POC: tail-based sampling at the Collector with a 100% sample rate (no dropping). When volumes grow, introduce a head-based probabilistic sampler with a 10% default rate and a 100% rate for traces containing an error span.

---

### P2 — Span naming convention follows the document 06 model

Manual span names must follow the `<layer>.<entity>.<operation>` convention, with dot-separated components:

| Pattern | Examples |
|---|---|
| `aggregate.<entity>.<operation>` | `aggregate.deposit.activate`, `aggregate.deposit.cancel` |
| `saga.<process>.<transition>` | `saga.constitution.transition`, `saga.mobilization.compensation` |
| `acl.<system>.<operation>` | `acl.core.reserve_balance`, `acl.core.confirm_debit` |
| `outbox.<operation>` | `outbox.publish`, `outbox.poll` |
| `inbox.<operation>` | `inbox.dedup_check`, `inbox.process` |
| `projector.<projection>.<operation>` | `projector.deposits_by_client.upsert` |

OTel auto-instrumentation spans (HTTP, SQL, Kafka) follow their default naming conventions. Manual spans use this convention. Mixed trace — both types of span appear together — is expected and correct.

---

### P3 — Required span attributes for every manual span

Every manually created span must carry the identity trio from document 01 (Primitive 4):

| Attribute | Source | Required on |
|---|---|---|
| `correlation_id` | The originating `correlation_id` from the command or event | Every span |
| `causation_id` | The `message_id` of the message that triggered this operation | Every span that results from message processing |
| `process_id` | The `ConstitutionProcess` or equivalent saga ID | Every saga-related span |

These three attributes are what make `correlation_id`-based trace search possible from the Grafana Explore interface. Without them, the 30-second debugging scenario from document 06 is not achievable — the trace exists but cannot be located by the identifier the operations team knows.

OTel SDK automatic injection of `trace_id` and `span_id` is separate and complementary — it enables trace-to-log navigation. The domain attributes above enable business-level search by banking concepts.

---

### P4 — Span attributes are classified before use

Every span attribute is classified into one of three tiers before it is added to the instrumentation code, following the data classification taxonomy in [document 10](../10-security-and-threat-model.md) (Principle 4). The classification determines who can query it in Grafana:

| Tier | Examples | Grafana RBAC visibility |
|---|---|---|
| **Operational** | `process.state`, `saga.phase`, `inbox.deduplicated`, `event.type` | All roles (NOC, engineers, compliance) |
| **Financial-restricted** | `deposit.amount`, `deposit.product`, `core.txn_id` | Compliance and engineering roles only; not NOC |
| **Personal-restricted** | `deposit.client_nif` (raw Portuguese tax ID), `core.account` (full account number / IBAN) | Engineering only; pseudonymous reference (e.g. opaque `client_ref`) preferred over raw identifiers |

The **personal-restricted** tier must default to pseudonymous identifiers; raw identifiers require an explicit justification at the call site that survives code review. Instead of `deposit.client_nif = 234567891` (raw Portuguese tax ID) or `core.account = PT50.0033.0000.45161234567.05` (full IBAN), use an opaque short reference such as `client_ref = CLI-2026-007842` that resolves only in the authoritative Customer Data Store. This removes raw client identifiers from the trace backend, eliminating the GDPR erasure obligation for historical traces when a client exercises Article 17 rights.

Code review must reject any span attribute that adds a tier-2 or tier-3 attribute without a corresponding comment confirming its classification. This is not about blocking the attribute — it is about making the choice visible and intentional.

---

### P5 — Log retention is bounded and tiered

Structured logs are shipped to Loki with stream labels that encode retention tier. Loki's retention-per-stream feature enforces different retention horizons:

| Log stream label | Content | Retention |
|---|---|---|
| `tier=operational` | Service health, latency, infrastructure events | 30 days |
| `tier=business` | Saga transitions, deposit lifecycle events, compensation triggers | 5 years (PSD2 audit minimum for payment-related events) |
| `tier=debug` | DEBUG-level technical detail | 7 days; never enabled in production without explicit incident window |

The `business` tier retention aligns with PSD2's audit trail obligation for payment operations. Logs at this tier must not contain raw PII fields (names, NIBs, contact details) — `correlation_id`, `process_id`, and `deposit_id` are sufficient to reconstruct the event from authoritative source systems.

The `operational` tier is never written to an external archive. The `business` tier must be archived to object storage (S3-compatible, in an EU region for GDPR residency) before Loki's local retention window expires.

---

### P6 — RBAC is defined before the stack is deployed

The Grafana RBAC configuration must be provisioned as code (Grafana's provisioning YAML) before any financial span attributes are visible in the stack. The minimum role configuration:

| Role | Can query |
|---|---|
| `noc-viewer` | Operational metrics dashboards, alert state; no trace query access |
| `engineer` | All signals; full trace query, log query, metric query |
| `compliance-viewer` | Business metrics dashboard; log queries on `tier=business` stream only; no trace query |
| `admin` | Full access including Grafana configuration |

Grafana datasource permissions must restrict the Tempo datasource to `engineer` and `admin` roles. The `compliance-viewer` role accesses only the pre-built business dashboard and the Loki `business` stream — not ad-hoc trace exploration.

This configuration is not an optional hardening step. It must be in place before the first service emits spans carrying financial attributes.
