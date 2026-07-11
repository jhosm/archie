# ADR-IC-016: Service Identity, Transport Authentication, and Observability-Plane Access Control

| Field | Value |
|---|---|
| Status | Accepted |
| Date | 2026-06-11 |
| Deciders | jhosm |
| Common criteria | [ADR-IC-000](./ADR-IC-000-common-evaluation-criteria.md) |
| Depends on | [ADR-IC-001](./ADR-IC-001-event-backbone-message-broker.md) (the broker the SASL/ACL plane authenticates against), [ADR-IC-006](./ADR-IC-006-edge-api-gateway.md) (the gateway that terminates the edge and originates internal mTLS), [ADR-IC-007](./ADR-IC-007-observability-stack.md) (the observability plane this governs access to), [ADR-PC-004](../../product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md) (the OpenBao secret boundary + the no-PII-on-the-bus rule this plane reuses) |
| Implements | [doc 10 §Trust Boundaries](../10-security-and-threat-model.md#trust-boundaries) (Boundaries 2–7) and [§Six Security Principles](../10-security-and-threat-model.md#six-security-principles-for-this-architecture) (Principles 1, 2, 4, 6) |
| Resolves | bd `babelstone-c14p.1` |

---

## Context

Documents 01–09 carried an implicit trust model: internal services trust each other, Kafka is a trusted bus, authorization lives at the edge. [Document 10](../10-security-and-threat-model.md) names that assumption as the central security gap and replaces it with **nine trust boundaries** and **six principles** — but, like the integration documents before it, document 10 states the *requirement* (mTLS here, topic ACLs there, RBAC on the observability plane) without deciding **which concrete mechanism** delivers each, nor **which boundaries are reachable today** versus blocked on infrastructure that does not yet exist. That is the open question that blocks the M.3 implementation epic: the threat model is written, but its mitigations have no ADR selecting the tools and sequencing the work.

The tool selections for the surrounding estate already exist and constrain this decision rather than reopen it. The broker is **Redpanda CE** ([ADR-IC-001](./ADR-IC-001-event-backbone-message-broker.md)); the edge is **Kong CE** ([ADR-IC-006](./ADR-IC-006-edge-api-gateway.md)), which already terminates JWT validation, mTLS, rate-limiting and SCA at the edge; the observability stack is **Grafana LGTM via the OpenTelemetry Collector** ([ADR-IC-007](./ADR-IC-007-observability-stack.md)); the secret boundary is **OpenBao** ([ADR-PC-004 §A1](../../product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md)), and the engine already carries an `ISecretProvider` seam (`engine/src/Babelstone.Pii/ISecretProvider.cs`) whose own contract anticipates *"the database connection string today, Redpanda SASL credentials later."* This ADR does not re-pick any of those tools. It decides the **security posture** that binds them: how a service proves *who it is* on each internal hop, and how the highest-value aggregated data store — the observability plane — is access-controlled.

### Why this is a posture decision, not a tool bake-off

The ADR-IC house default is a tool-selection shape — F1/F2 hard filters, then S1–S4 soft criteria across candidate tools ([ADR-IC-000](./ADR-IC-000-common-evaluation-criteria.md)). That shape does not fit here. The mechanisms are not in contention: mTLS is *the* standard for service-to-service identity on a zero-trust network and is the one Kong already speaks; Kafka SASL/SCRAM + topic ACLs is *the* native authentication-and-authorization surface Redpanda exposes (it is Kafka-API-compatible by construction, [ADR-IC-001](./ADR-IC-001-event-backbone-message-broker.md)); Grafana's org/team/role model is *the* RBAC surface of the chosen observability stack ([ADR-IC-007](./ADR-IC-007-observability-stack.md)). There is no alternative to score that would not first require abandoning an already-Accepted ADR. What is genuinely undecided — and what this ADR fixes — is the **architecture posture**: the per-plane identity model, where each credential lives, and the *reachable-today vs blocked* split that sequences M.3. So this ADR uses the **posture shape** (Status / Context / Decision / Consequences / Residual Risks) the recent estate-posture ADRs use (e.g. [ADR-IC-013](./ADR-IC-013-in-house-estate-build-and-repository-placement.md)), and carries a `## Verifiable commitments` section per the [ADR-PC-000](../../product_concepts/adrs/ADR-PC-000-namespace-and-contract-shape-framework.md) template — because, unlike a pure classification, this posture has buildable mitigations an implementation can drift from.

### The three planes

Document 10's boundaries fall into three identity planes, each with a different mechanism and a different blocker profile:

- **(i) Service-to-service identity (mTLS).** Boundaries 2 (gateway → internal), 5 (orchestrator → ACL → Core), 6 (ops console → saga state). Every internal hop authenticates with a service identity; the ACL's command port accepts only the orchestrator's identity ([doc 10 Boundary 5](../10-security-and-threat-model.md#boundary-5-acl--core-banking), [Principle 6](../10-security-and-threat-model.md#principle-6-compensations-and-saga-commands-require-authorization)).
- **(ii) Kafka SASL/SCRAM + topic ACLs.** Boundaries 3 (producer → Kafka) and 4 (Kafka → consumer). Every Kafka client authenticates with a service identity; topic ACLs are deployment configuration, not convention ([doc 10 Boundary 3](../10-security-and-threat-model.md#boundary-3-deposits-service--kafka), [Principle 2](../10-security-and-threat-model.md#principle-2-kafka-is-a-shared-medium-not-a-trusted-bus)).
- **(iii) Observability-plane RBAC + PII-redaction.** Boundary 7 (observability backend → all system data). The aggregated trace/log store is a searchable database of all financial operations; access is role-scoped and span attributes carry no PII ([doc 10 Boundary 7](../10-security-and-threat-model.md#boundary-7-observability-backend--all-system-data), [Principle 4](../10-security-and-threat-model.md#principle-4-the-observability-plane-is-a-regulated-data-store)).

---

## Decision

The posture below is the live contract. Each plane states the mechanism, where the credential lives, and — decisively for sequencing — whether it is **reachable today** against the source and infrastructure that exist, or **blocked** on a named prerequisite. The reachable parts are the M.3 children that can start; the blocked parts are tracked, not silently skipped.

### Plane (i) — Service-to-service identity is mTLS, certificates from the secret boundary

1. **Every internal service-to-service hop is mutually authenticated with mTLS.** A service presents an X.509 client certificate that names its service identity; the callee verifies it against the shared trust root before accepting the connection. Plain HTTP between internal services is a configuration error, not a fallback ([doc 10 Boundary 2](../10-security-and-threat-model.md#boundary-2-api-gateway--internal-services)). Kong ([ADR-IC-006](./ADR-IC-006-edge-api-gateway.md)) is the originating mTLS endpoint at the edge; the internal mesh extends the same model hop-to-hop.

2. **The ACL command port accepts only the orchestrator's identity.** The most hostile authorization in the system — who may issue money-moving commands to the ACL — is enforced at the transport layer: the ACL's inbound command listener rejects any client certificate whose identity is not the saga orchestrator's, *before* the application layer runs ([doc 10 Boundary 5](../10-security-and-threat-model.md#boundary-5-acl--core-banking) / [Principle 6](../10-security-and-threat-model.md#principle-6-compensations-and-saga-commands-require-authorization)). An optional application-layer JWT signed by the orchestrator's key is the defence-in-depth second factor, not the primary control.

3. **Certificate material lives at the OpenBao boundary and never on a saga message or the bus.** Service certificates and their private keys are issued and rotated through the same secret boundary the engine already uses for application credentials ([ADR-PC-004 §A1](../../product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md)); a resolved credential lives only at a service's composition root and is never carried by a saga message (saga messages carry the identity trio only, [ADR-IC-003 §P7](./ADR-IC-003-saga-orchestrator.md)) nor placed on the durable integration bus (the no-secrets-on-the-bus rule, [ADR-PC-004 §P2](../../product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md)).

> **Reachable today: no.** mTLS between the orchestrator and the ACL needs *both* ends to have real listening source. `orchestrator/src` exists, but `acl/` is a Dockerfile + README with no service source yet (it is an in-house build reserved by [ADR-IC-012](./ADR-IC-012-anti-corruption-layer-implementation.md) / [ADR-IC-013](./ADR-IC-013-in-house-estate-build-and-repository-placement.md), not yet scaffolded). Plane (i) is therefore **blocked on the ACL (and the broader estate services) having real source** — the M.3 mTLS child is filed but cannot start until the ACL service exists.
>
> **Amended 2026-07-12 (bd babelstone-zla1.12.25).** The "no" above now scopes to the **orchestrator↔ACL** hop specifically. Among the services that *do* have real listening source — engine, orchestrator, notification, mcp-server, and the Mission Control BFF — plane (i) is now **code-complete**: the caller legs present a client cert and pin the internal CA (bd babelstone-zla1.12.10, `InternalMtls.BuildHandler`), and the engine + orchestrator now *validate* an inbound client cert against that same pinned CA in a Kestrel `ClientCertificateValidation` callback (`Babelstone.Engine.Api.InternalMtls` / `Babelstone.Orchestrator.InternalMtls`, the server-side mirror of the caller-side `ValidateAgainstInternalCa`). Enabling it on staging is a gated operator maintenance-window flip (`infra/k8s/overlays/staging/internal-mtls.patch.yaml` — uncomment + apply bootstrap certs + re-run `deck-sync`), **not** new code. The orchestrator↔ACL hop (commitment C2) remains blocked on the ACL having real source, unchanged.

### Plane (ii) — Kafka SASL/SCRAM authentication, topic ACLs as deployment config

4. **Every Kafka client authenticates with a distinct service identity via SASL/SCRAM.** The producer, each consumer, the outbox publisher, and the orchestrator each present their own SCRAM credential to Redpanda; the outbox publisher's identity is distinct from the Deposits API's, so a compromised publisher can publish only to the topics it is authorized for and cannot issue commands ([doc 10 Boundary 3](../10-security-and-threat-model.md#boundary-3-deposits-service--kafka)). (mTLS client certificates are the equivalent Kafka transport identity and an acceptable substitute; SASL/SCRAM is the chosen baseline because it is the simpler credential to issue, rotate, and reason about at 1–2-person scale.)

5. **Topic ACLs are deployment configuration, reviewed in the same PR as the service that uses them.** Only the Deposits service may produce to `deposits.integration.events` / `deposits.process.events`; no service may produce to another context's topics; each consumer subscribes only to the topics it needs ([doc 10 Boundary 4](../10-security-and-threat-model.md#boundary-4-kafka--each-consumer) / [Principle 2](../10-security-and-threat-model.md#principle-2-kafka-is-a-shared-medium-not-a-trusted-bus)). The ACL set is declarative infrastructure, not convention or documentation. Schema-registry write access is part of the same plane: registration is a CI/CD action, and `NONE`-compatibility changes require elevated authorization, never an ad-hoc individual action.

6. **SASL credentials resolve through the existing `ISecretProvider` seam.** The credential is fetched at the composition root via `ISecretProvider.GetSecretAsync` and refreshed on rotation via `RefreshAsync` — the exact second mode that seam's contract already anticipates ("Redpanda SASL credentials later", `engine/src/Babelstone.Pii/ISecretProvider.cs` / [ADR-PC-004 §A1](../../product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md)). No new secret abstraction is introduced.

> **Reachable today: partially.** Boundary-3 producer-side SASL/SCRAM is **reachable now** — the engine's Kafka clients already wire through `ISecretProvider`, and turning on SASL is a configuration + credential-issuance change with no new abstraction. Topic-ACL declarative config can be authored against the existing topics. The full fan-out (Boundary 4, six-plus consumers) lands incrementally as each consumer service exists.

### Plane (iii) — Observability is a regulated data store: RBAC roles + structural-only attributes

7. **The observability plane is access-controlled by role, not open to all engineers.** The Grafana org/team/role model ([ADR-IC-007](./ADR-IC-007-observability-stack.md)) scopes who sees what: NOC sees operational health (error rates, lag); compliance sees audit trails; developers see their own service's traces. Access to traces carrying financial attributes is itself logged ([doc 10 Boundary 7](../10-security-and-threat-model.md#boundary-7-observability-backend--all-system-data) / [Principle 4](../10-security-and-threat-model.md#principle-4-the-observability-plane-is-a-regulated-data-store)).

8. **No PII rides any telemetry signal; identifiers in spans are pseudonymous and structural.** Span and log attributes carry only the `babelstone.*` operational tier — structural identifiers (`partition_key`, `product_code`, `aggregate_type`), money as integer cents — never NIF, IBAN, account number, name, or e-mail. Where a client reference is needed for debugging, a pseudonym (a short hash resolved in the Customer Data Store) is used rather than the raw `client_id`, so the tracing backend never becomes a searchable personal-data index ([doc 10 Principle 4](../10-security-and-threat-model.md#principle-4-the-observability-plane-is-a-regulated-data-store) / [ADR-PC-004 §P2](../../product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md)). This is the already-catalogued `OBS_NO_PII_ATTRS` commitment (catalogue row OBS-3).

> **Reachable today: split.** The **no-PII / structural-only span-attribute** discipline is reachable now — it is implemented in `engine/src/Babelstone.Telemetry/BabelstoneAttributes.cs` (every key documented as operational-tier) and asserted by the `OBS_NO_PII_ATTRS` structural check (catalogue row OBS-3, `Planned` — the assertion rides inside `TelemetrySpanTests`, not yet a flipped gate). Span-attribute *pseudonymization* (a hashed client reference rather than a raw `client_id`) is a `Babelstone.Telemetry` addition that can start now. The **Grafana RBAC** half is **blocked on K.2** (the Grafana LGTM pipeline standing up) — there is no Grafana instance to scope roles against until then.

### Rejected / not taken

- **A service mesh (Istio/Linkerd) for plane (i).** The mesh would provide mTLS for free, but at 1–2-person scale on a self-hosted POC it is a heavyweight control plane the estate does not need yet — Kong already originates mTLS at the edge and the internal hop count is small. Reserved, not pre-built: revisited if the service count grows past hand-managed certificate rotation. This mirrors the project's "reserve, don't pre-build" discipline ([ADR-PC-009 §P5](../../product_concepts/adrs/ADR-PC-009-per-instance-version-pinning.md)).
- **Kafka `mTLS`-only (no SASL) for plane (ii).** Acceptable and noted as a substitute in §4, not chosen as the baseline: SCRAM credentials are simpler to issue and rotate through the existing `ISecretProvider` seam than per-client certificate lifecycles, and the two are not mutually exclusive (mTLS transport + SASL identity can coexist).
- **Per-service certificate authorities.** One shared trust root with per-service leaf certificates is simpler to operate than per-service CAs and meets the identity requirement; per-service CAs are an over-rotation of complexity for this scale.

---

## Consequences

**What this choice makes easier:**

- **M.3 is now sequenceable.** The reachable-vs-blocked split turns document 10's flat list of mitigations into an ordered backlog: boundary-3 SASL/SCRAM and boundary-7 span pseudonymization start now (against existing source and the `ISecretProvider` / `Babelstone.Telemetry` seams), while mTLS and Grafana RBAC are filed-but-blocked with named prerequisites — no mitigation is silently dropped.
- **No new secret abstraction.** Both the SASL credential (plane ii) and the service certificate (plane i) resolve through the same `ISecretProvider` boundary the engine already ships, so the rotation contract ([ADR-PC-004 §A1](../../product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md)) is reused, not reinvented.
- **The observability no-PII rule is already asserted in code.** Plane (iii)'s structural-only span discipline is the `OBS_NO_PII_ATTRS` commitment (catalogue row OBS-3, `Planned` — the structural no-PII assertion rides inside `TelemetrySpanTests`, not yet a flipped gate); this ADR makes the RBAC + pseudonymization extensions explicit additions to a posture the code already partly holds.

**What this choice makes harder or impossible:**

- **mTLS cannot land before the ACL service exists.** Plane (i)'s decisive control — the orchestrator-only ACL command port — is blocked on the ACL having real source. The mitigation is that the *application-layer* orchestrator-signed-JWT factor (§2) can be designed against the orchestrator source that exists, so the transport control is the only blocked half.
- **Certificate / credential rotation is hand-operated at this scale.** Without a mesh, leaf-certificate and SCRAM-credential rotation are operational procedures (KV v2 version bump + `RefreshAsync`), tested under M.4's DORA recovery drills rather than automated by a control plane. This is the deliberate "reserve the mesh" trade.

**Residual risks:**

- **Blocked planes drift into "later means never".** mTLS (plane i) and Grafana RBAC (plane iii RBAC half) are blocked on the ACL service and K.2 respectively; a blocker that lingers leaves a named gap in the threat-model coverage. Mitigation: each blocked plane is a filed M.3 child with its blocker named (see this ADR's bd grooming), and the umbrella M.3 (njt2) tracks the set so the gap stays visible, per the explicit-drift discipline ([ADR-PC-020 §D3](../../product_concepts/adrs/ADR-PC-020-llm-toolchain-and-conformance-governance.md)).
- **SASL credential on the wire vs in memory.** The resolved SASL secret is held in service memory to open the Kafka connection (the `ISecretProvider` KV mode explicitly *does* hold the credential, unlike the transit-key mode). It must never be logged or placed on a span attribute — which is exactly what plane (iii)'s `OBS_NO_PII_ATTRS` structural-only rule and the no-secrets-on-the-bus rule ([ADR-PC-004 §P2](../../product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md)) jointly forbid.
- **Pseudonym reversibility.** A span-attribute client pseudonym that resolves in the Customer Data Store is only as non-personal as the hash is non-reversible without that store; a weak or unsalted hash re-introduces the personal-data-index risk Principle 4 exists to remove. Mitigation: the pseudonym derivation is reviewed as a security-relevant parameter when the `Babelstone.Telemetry` pseudonymization child lands.

---

## Verifiable commitments

This decision's load-bearing observability commitment is **already catalogued centrally** ([commitment catalogue](../../product_concepts/adrs/commitment-catalogue.md), the [ADR-PC-000](../../product_concepts/adrs/ADR-PC-000-namespace-and-contract-shape-framework.md) reference form); the per-plane transport and authorization commitments do **not yet** have a gate and are stated inline as falsifiable claims (`Gap` — deliberate, visible holes per [ADR-PC-020 §P5](../../product_concepts/adrs/ADR-PC-020-llm-toolchain-and-conformance-governance.md)). They will be seeded as catalogue rows when their M.3 children are implemented.

| # | Commitment | Gate (pyramid level) | Test ID | Status |
|---|---|---|---|---|
| C1 (plane iii) | No PII in any telemetry signal — span/log attributes carry only the `babelstone.*` structural/operational tier; money rides as integer cents (§8). | unit / analyser | `OBS_NO_PII_ATTRS` (catalogue row OBS-3) | Planned (matches OBS-3 — rides inside `TelemetrySpanTests`, gate not yet flipped) |
| C2 (plane i) | The ACL command port rejects any client identity that is not the saga orchestrator's, at the transport layer, before the application runs (§2). | integration | `SVC_ACL_PORT_ORCHESTRATOR_ONLY` *(reserved)* | Gap — blocked on the ACL service having real source |
| C3 (plane ii) | Every Kafka client authenticates with a distinct SASL/SCRAM identity; topic ACLs reject cross-context produce (§4–§5). | unit + integration | `KAFKA_SASL_TOPIC_ACL` (catalogue row SEC-1) | Planned (unit leg Live — seeded as SEC-1 with the SASL child `babelstone-njt2.1`: `KafkaSaslOptionsTests` + `infra/redpanda/topic-acls.yaml`; broker-ACL integration leg still Planned) |
| C4 (plane iii) | The observability plane is role-scoped (NOC / compliance / developer); access to financially-attributed traces is logged (§7). | integration | `OBS_PLANE_RBAC` (catalogue row SEC-2) | Planned (config Live — K.2 `babelstone-o60b` is CLOSED, so the RBAC config landed with `babelstone-njt2.4`: `infra/grafana/rbac/` provisions the §P6 roles + Tempo datasource lock + dataproxy access log; the end-to-end enforcement integration test is Planned until a Grafana instance runs in CI) |
| C5 (plane i) | The engine + orchestrator command surfaces require a client cert and validate it against the pinned internal CA (not the container system trust store), so a cert-less or wrong-CA caller is rejected at the TLS handshake (§1). | unit (validator) + integration (handshake) | `SVC_ENGINE_ORCH_MTLS` *(reserved)* | Partial — the code-side pinned-CA validator is Live + unit-tested (`InternalMtls.BuildClientCertificateValidation` in `Babelstone.Engine.Api` / `Babelstone.Orchestrator`, bd babelstone-zla1.12.25); the live positive/negative handshake integration leg is Planned (gated staging flip, `internal-mtls.patch.yaml`) |

> The reference is one-way (ADR → catalogue): when C2/C3/C4 acquire gates, the catalogue becomes their single source of truth and this table's rows become references by Test ID, per [ADR-PC-000 Amendment 2026-05-24](../../product_concepts/adrs/ADR-PC-000-namespace-and-contract-shape-framework.md).

---

## Cross-references

- [doc 10 — Security and Threat Model](../10-security-and-threat-model.md) — the nine boundaries and six principles this ADR selects mechanisms for (Boundaries 2–7; Principles 1, 2, 4, 6).
- [ADR-IC-006](./ADR-IC-006-edge-api-gateway.md) — Kong CE, the edge mTLS / JWT / SCA terminator that originates the internal-identity model plane (i) extends.
- [ADR-IC-001](./ADR-IC-001-event-backbone-message-broker.md) — Redpanda CE, whose Kafka-API SASL/SCRAM + topic-ACL surface plane (ii) authenticates against.
- [ADR-IC-007](./ADR-IC-007-observability-stack.md) — Grafana LGTM + OTel Collector, the observability plane (iii) governs access to.
- [ADR-PC-004](../../product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md) — the OpenBao secret boundary (§A1 `ISecretProvider` KV mode) credentials resolve through, and the §P2 no-secrets/no-PII-on-the-bus rule all three planes honour.
- [ADR-IC-003](./ADR-IC-003-saga-orchestrator.md) / [ADR-IC-012](./ADR-IC-012-anti-corruption-layer-implementation.md) / [ADR-IC-013](./ADR-IC-013-in-house-estate-build-and-repository-placement.md) — the orchestrator and ACL whose identities plane (i) binds, and the in-house-estate placement that explains why the ACL has no source yet.

---

*Decided 2026-06-11 by jhosm.*
