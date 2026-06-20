# ADR-IC-006: Edge API Gateway and Synchronous Layer

| Field | Value |
|---|---|
| Status | Accepted |
| Date | 2026-05-17 |
| Deciders | jhosm |
| Common criteria | [ADR-IC-000](./ADR-IC-000-common-evaluation-criteria.md) |
| Depends on | [ADR-IC-001](./ADR-IC-001-event-backbone-message-broker.md), [ADR-IC-005](./ADR-IC-005-cqrs-read-model-storage.md) |

---

## Context

[Document 00](../00-introduction-and-decisions.md) draws a fundamental cut between two worlds: a synchronous edge that accepts requests within 500ms and an asynchronous backbone where orchestration happens. The edge is not a transport layer — it is a security boundary, a rate-limiting surface, a payload validation checkpoint, and a PSD2 pre-condition enforcer. [Document 10](../10-security-and-threat-model.md) makes this explicit at Boundary 1 (External Clients → API Gateway): everything outside the gateway is untrusted; everything inside has been authenticated, rate-limited, and validated before touching internal services.

[Document 05](../05-constitution-saga-walkthrough.md) materialises this boundary in concrete HTTP terms. At Step 0, the edge completes authentication and authorisation checks, PSD2 SCA validation, synchronous idempotency lookup, payload schema validation, and initial aggregate creation — all within 150ms of its 500ms budget. It then returns a `202 Accepted` with a `stream_url` pointing to an SSE endpoint where the client subscribes for real-time saga progress:

```http
HTTP 202
{
  "deposit_id": "DEP-2026-00012345",
  "process_id": "PROC-2026-00098765",
  "status": "PROCESSING",
  "stream_url": "/api/v1/processes/PROC-2026-00098765/stream"
}
```

The SSE stream is not a short HTTP response. It is a long-running HTTP/1.1 streaming connection that stays open until the saga reaches a terminal state — including sagas that contain a workflow-approval step, which may remain in `HUMAN_DECISION_PENDING` for minutes or longer.

The gateway must satisfy five distinct obligations:

| # | Obligation | Specific requirement |
|---|---|---|
| 1 | **Authentication** | Validate OAuth 2.0 bearer tokens: signature (RS256/ES256), expiry, issuer |
| 2 | **PSD2 SCA enforcement** | For financial operations (`POST /deposits/constitute`, `POST /deposits/:id/mobilise`), confirm the token carries a valid SCA completion claim before routing. Absent or expired SCA → `403 SCA_REQUIRED`; the orchestrator never starts |
| 3 | **Rate limiting** | Per client identity (JWT `sub`), per source IP, and per operation type — both a resilience and a fraud-prevention control |
| 4 | **Payload schema validation** | Reject structurally invalid requests at the edge; the application never receives them |
| 5 | **SSE proxy** | Long-running HTTP/1.1 streaming connections for saga status updates; connections stay open for the full saga duration, unbounded by a hard infrastructure timeout |

### Candidate naming clarification — the BFF question

The Backend for Frontend (BFF) pattern produces a channel-specific aggregation layer (separate thin services for web, mobile, and branch terminal) that composes data from multiple downstream services into the shape each UI needs. BFFs are not API gateways: they do not handle token validation, rate limiting, or circuit breaking — those are cross-cutting edge concerns that apply uniformly across channels.

In the candidate evaluation below, "custom BFF per channel" is evaluated as the option of inlining all gateway-layer concerns (auth, rate limiting, SCA enforcement, schema validation) into channel-specific application services, eliminating a dedicated gateway component. This is architecturally distinct from running channel-specific BFFs behind a shared gateway — that option remains valid regardless of which gateway is chosen.

**Candidates:**

| # | Candidate | Notes |
|---|---|---|
| A | **Kong Gateway CE** | Self-hosted; Apache 2.0; plugin-based; largest open-source API gateway community |
| B | **AWS API Gateway (HTTP API)** | Managed SaaS; free tier (1M req/month for 12 months); proprietary |
| C | **Apache APISIX** | Self-hosted; Apache 2.0; CNCF project; OpenResty/nginx base |
| D | **Custom BFF per channel** | Gateway concerns embedded in channel-specific application services; no dedicated gateway component |

---

## Evaluation

### Hard filter results

#### F1 · Cost / licensing

| Candidate | Licence / tier | Assessment | Proceeds? |
|---|---|---|---|
| Kong Gateway CE | Apache 2.0 (Community Edition) | Self-hosted; all features required by this architecture are in CE; no paywalled plugins | **Pass** |
| AWS API Gateway | AWS service; HTTP API free tier: 1M calls/month for 12 months on new accounts | Free tier requires a credit-card-registered AWS account (standard cloud practice). The 12-month limit means the tier expires before the POC is fully hardened. Post-free-tier cost is $1.00/million requests for the REST layer plus ALB minimum charge (~€16/month) for the SSE routes — cost model diverges from zero-budget after month 12. | **Pass (conditional)** — free tier expires 12 months after account creation; post-expiry cost must be re-evaluated. Date of assessment: 2026-05-17 |
| Apache APISIX | Apache 2.0 (Apache Software Foundation top-level project) | Self-hosted; all features required by this architecture are in the open-source distribution | **Pass** |
| Custom BFF per channel | N/A (application code) | Uses open-source JWT validation, rate-limiting, and schema validation libraries under permissive licences. No gateway licence. | **Pass** |

*Date of licence assessment: 2026-05-17.*

#### F2 · Regulatory fit

| Candidate | GDPR | DORA | PSD2 | Proceeds? |
|---|---|---|---|---|
| Kong Gateway CE | Self-hosted; EU region under full operator control; no cross-border data transfer. TLS termination at the gateway processes headers and bodies that may contain client identifiers — Kong must be included in the GDPR data residency scope. Request logging must be classified per document 10, Principle 4: `correlation_id` and `process_id` are operational; account identifiers and client sub values are not access log data at any level except a tightly controlled forensic log. | DORA chaos injection at the gateway layer is fully under operator control. Kong can be degraded, rate limits tightened, upstreams failed, and health check behaviour modified without involving a third party. Kong emits its own Prometheus metrics — observable via the same stack as ADR-IC-007. | Kong CE can enforce PSD2 SCA pre-conditions via its `pre-function` plugin: a Lua snippet that reads decoded JWT claims in the access phase and returns `403 SCA_REQUIRED` if the SCA completion claim is absent or expired. SCA enforcement is a configuration decision, not a plugin purchase. | **Pass** |
| AWS API Gateway | EU regions available (eu-west-1 / eu-central-1). Deployed in-region, GDPR data residency is satisfied by configuration. CloudWatch access logs (bearing client identifiers) must be RBAC-controlled — this is a separate configuration concern from application RBAC. | DORA chaos injection at the gateway tier is not possible: AWS manages the gateway infrastructure; operators cannot inject faults at the gateway layer, only at the Lambda authorizer or upstream application layers. At POC scale this is an accepted constraint; a production system would require an explicit resilience testing plan that works around the managed-service boundary. | SSE connections: AWS API Gateway HTTP API enforces a 29-second maximum integration timeout that cannot be overridden by configuration. The saga status stream in document 05 must stay open for the full saga duration — including workflow-approval steps that can wait for minutes. This is a structural incompatibility with the optimistic-acceptance + status-push model established in document 00, not a performance concern. Workaround: serve SSE from an Application Load Balancer while routing REST calls through API Gateway — but this splits the edge into two separately configured managed-service tiers, doubling configuration surface and CloudWatch / Grafana observability fragmentation. | **Pass (conditional)** — GDPR: EU region and CloudWatch RBAC must be explicitly configured. DORA: gateway-layer chaos is outside operator control; accepted at POC scale. PSD2/SSE: the 29-second timeout requires an ALB alongside API Gateway for SSE routes, splitting the edge into two tiers. |
| Apache APISIX | Same self-hosted, EU-region, operator-controlled properties as Kong CE. APISIX's access logging must be classified under the same rules as Kong CE. | Full DORA operator control; same as Kong CE. | APISIX's `openid-connect` plugin (Apache 2.0, first-party) handles JWT validation with automatic JWKS endpoint polling — public key rotation from the IAM propagates without manual re-configuration. PSD2 SCA claim enforcement: same `serverless-pre-function` Lua approach as Kong CE. | **Pass** |
| Custom BFF per channel | Operator-controlled, EU-deployable; same GDPR data classification rules as any application service. | Full DORA operator control: BFF processes can be terminated, degraded, and recovered by the operator. | PSD2 SCA enforcement is application code in each BFF. Each channel must independently implement and test the same pre-condition check. Correctness is a function of code quality per channel, not a shared configuration. | **Pass** |

All four candidates pass both hard filters.

---

### Soft criteria

#### Kong Gateway CE

**S1 · Operational complexity:** Kong CE's DB-less declarative mode (managed with the `deck` CLI) eliminates the PostgreSQL configuration store that Kong's traditional DB mode requires. Configuration is a YAML file committed to git; changes deploy via `deck sync`. A new route, plugin, or upstream is a pull request, not a database mutation. The operational surface at POC is one process, one config file. The `rate-limiting` plugin runs in `local` mode at single-node POC scale (in-memory counters); graduating to Valkey-backed distributed counters — when needed — is a configuration-only change, and Valkey is already in the upgrade path from ADR-IC-005.

**S2 · Ecosystem coherence:** Kong sits cleanly in front of the Deposits API, validating bearer tokens with its `jwt` plugin, enforcing rate limits with `rate-limiting`, validating payloads with `request-validator`, and routing with mTLS via upstream certificate configuration. The `opentelemetry` plugin (Kong CE v3.x) emits traces and metrics in OTel-native format — the same instrumentation model as every other service in the stack (ADR-IC-007). SSE proxying requires no additional configuration: Kong's nginx base does not buffer streaming responses when the upstream sends `Content-Type: text/event-stream`. An `X-Accel-Buffering: no` header from the upstream disables nginx buffering for that connection; long-running saga streams flow through without a timeout at the gateway layer.

**S3 · Exit cost:** Medium. Kong's declarative config maps closely to universal API gateway concepts (services, routes, plugins). Migrating to a different gateway requires translating `deck` YAML to the target system's format — the concepts are portable, the syntax is not. Estimated translation effort: 2–5 days of configuration work. Application services behind Kong are unaffected by a gateway migration.

**S4 · Community and longevity:** Kong is the most widely deployed open-source API gateway by GitHub stars and production installations. Community Edition has maintained Apache 2.0 licensing since initial release. Kong Inc.'s commercial incentives are aligned with CE health — their enterprise sales depend on community adoption. The contributor base is broad; the majority of commits come from contributors outside Kong Inc. The user base is large enough that most configuration and plugin questions have documented answers on Stack Overflow and the Kong forum. CNCF member project.

---

#### AWS API Gateway (HTTP API)

**S1 · Operational complexity:** Zero infrastructure to operate — it is a managed service. Configuration is Terraform (AWS provider) or CDK. For a 1–2 person team, the absence of a gateway process to monitor, patch, and upgrade is a genuine advantage. However: the 29-second SSE timeout requires a separate ALB for the `stream_url` routes. An ALB has a minimum monthly cost (~€16/month always-on); combined with API Gateway costs post-free-tier, the zero-budget constraint fails after month 12. The dual-tier edge (API Gateway for REST, ALB for SSE) means two Terraform resource sets, two CloudWatch log groups, and two sets of health-check configurations to maintain.

**S2 · Ecosystem coherence:** AWS API Gateway is the only managed-cloud component in an otherwise entirely self-hosted stack. Its access logs land in CloudWatch; every other service's logs land in Loki (ADR-IC-007). Its metrics land in CloudWatch; every other service's metrics land in Prometheus. Correlating a gateway-level `403` with the downstream trace in Grafana Tempo requires crossing the CloudWatch / Grafana boundary manually. Incident response for a 1–2 person team at 2am is measurably harder when the edge's observability signal is in a different toolchain. The SSE ALB adds a third log destination (ALB access logs, also in CloudWatch, in a different log group with a different field schema). The architecture established in ADR-IC-001 through ADR-IC-005 is consistently self-hosted; a managed-cloud edge is an anomaly that fragments every operational workflow.

**S3 · Exit cost:** High. Every route, JWT authorizer configuration, throttling setting, and method integration is an AWS-specific resource with no portable format. The Lambda authorizer for SCA claim checking is application code structured as a Lambda function — not portable to Kong or APISIX without rewriting the handler and its deployment packaging. Configuration investment in CloudFormation / Terraform AWS provider is gateway-specific. Migrating to a self-hosted gateway means rebuilding the edge configuration from scratch and restructuring the Lambda authorizer as a plugin.

**S4 · Community and longevity:** AWS will not disappear. However, the HTTP API (v2) is the second revision of API Gateway — the original REST API (v1) is already in maintenance mode despite widespread use. Managed services can be deprecated, repriced, or significantly changed with 12–24 months notice; the product roadmap is under AWS sole control with no community governance. The 29-second timeout — the decisive constraint in this evaluation — is an AWS platform decision that cannot be lobbied against by the user community.

---

#### Apache APISIX

**S1 · Operational complexity:** APISIX's standalone mode (configuration from a `apisix.yaml` file, no etcd) is comparable in simplicity to Kong's DB-less mode. The full etcd-backed mode — required for dynamic routing updates without restarts in a multi-node deployment — adds etcd as an operational dependency. At POC scale, standalone mode is sufficient and eliminates etcd. APISIX's configuration format (YAML + Lua plugins) is well-documented; the learning curve exists but is shorter than Kubernetes-native alternatives.

**S2 · Ecosystem coherence:** APISIX's `openid-connect` plugin (Apache 2.0) handles JWT validation with automatic JWKS endpoint polling — public key rotation from the IAM propagates to APISIX without manual configuration changes, a meaningful operational advantage over Kong CE's `jwt` plugin (which requires manual JWKS reload or a `pre-function` workaround to poll automatically). The OTel plugin is available. SSE proxying works natively on the same OpenResty/nginx base as Kong. APISIX's Admin API (for programmatic config changes) uses etcd as backing store in the full mode; standalone mode replaces the Admin API with a file-render-and-reload cycle, similar to Kong's `deck sync`.

**S3 · Exit cost:** Low to medium. APISIX's configuration format maps to the same universal API gateway concepts as Kong's. Route, plugin, and upstream definitions are similarly structured. Migration effort is comparable to Kong's.

**S4 · Community and longevity:** APISIX is an Apache Software Foundation top-level project (graduated 2021) under Apache 2.0. API7.ai (the founding company) provides the commercial tier, analogous to Kong Inc.'s relationship with Kong CE. The community is growing but meaningfully smaller than Kong's — roughly one-third of the GitHub contributor count and a fraction of the Stack Overflow question volume at the time of this ADR. For a 1–2 person team, community size matters operationally: unusual edge cases (plugin interaction bugs, nginx directive conflicts, Lua sandbox behaviour under specific load patterns) are more likely to have documented resolutions in Kong's ecosystem. APISIX's English-language documentation quality is improving but uneven. Apache Foundation governance is a positive longevity signal comparable to Kong's CNCF membership.

---

#### Custom BFF per channel (gateway concerns inline)

**S1 · Operational complexity:** Each channel (web, mobile, branch terminal) introduces a BFF service that independently implements JWT validation, SCA enforcement, rate limiting, schema validation, and SSE proxying. For three channels, that is three separate implementations of the same cross-cutting concerns. Updates to the SCA claim name, rate-limit thresholds, or JWT issuer require coordinated releases across all BFFs. There is no centralised place to change edge policy; it is replicated across the BFF fleet. For a 1–2 person team, this is the highest ongoing maintenance cost of any candidate: the gateway concerns are solved problems in dedicated gateway software, and reimplementing them delays work on the banking domain itself.

**S2 · Ecosystem coherence:** The custom BFF approach yields maximum per-channel flexibility: each BFF can implement exactly the edge behaviour its UI requires, with no plugin system intermediating. SSE is trivially supported — it is a standard HTTP streaming response from the BFF process. OTel instrumentation lives directly in the BFF code. The coherence cost is in the cross-cutting concerns replicated across channels, not in the BFF-to-downstream integration. Edge policy in a regulated banking system is predominantly uniform across channels: PSD2 SCA requirements, rate limits, and JWT validation rules do not differ between web, mobile, and branch terminal. Flexibility that is never exercised is a maintenance surface, not a benefit.

**S3 · Exit cost:** Zero — it is your code. Replacing the BFF-as-gateway approach with a dedicated gateway is additive: gateway concerns migrate from the BFFs to the new gateway; the BFFs lose that responsibility. No proprietary format to translate.

**S4 · Community and longevity:** N/A — the BFF is application code. The JWT, rate-limiting, and schema-validation libraries used within it are governed by their own licences and communities.

---

## Decision

**Chosen: Kong Gateway CE — single shared gateway**

The decisive reason is operational convergence: Kong CE provides JWT validation, rate limiting, payload schema validation, and SSE proxying from a single configuration point that a 1–2 person team can understand, change, and audit in one place. The DB-less declarative mode removes the only meaningful operational overhead Kong CE introduces — there is no gateway database, no Admin API to protect at runtime, no migration to run on upgrade. The configuration is a YAML file in git. Every edge policy change — a new SCA-protected route, a tightened rate limit, a new JWT issuer — is a pull request on that file.

The BFF pattern that the "custom BFF per channel" candidate addresses — channel-specific aggregation and data shaping for each UI — remains valid regardless of this decision. BFFs that sit behind Kong and compose data for specific channels are complementary to Kong, not an alternative to it. What this ADR rejects is the option of inlining gateway-layer concerns into those BFFs.

---

**Rejected: AWS API Gateway (HTTP API)**

The 29-second integration timeout is the decisive constraint. It is a hard platform limit that cannot be overridden by configuration or by a support request — it is a property of the HTTP API product tier. The saga status stream in document 05 must stay open for the full saga duration, including sagas containing workflow-approval steps that wait for human decisions. Serving SSE from an ALB alongside API Gateway REST routes splits the edge into two managed-service tiers: two separate Terraform resource hierarchies, two CloudWatch log groups in a different format from the Loki/Prometheus observability stack chosen in ADR-IC-007, and ALB minimum cost that conflicts with the zero-budget constraint once the API Gateway free tier expires after 12 months. Every other component in this architecture is self-hosted; a managed-cloud gateway is an anomaly that fragments the operational model without providing a benefit that the self-hosted candidates cannot match.

---

**Rejected: Apache APISIX**

APISIX's built-in `openid-connect` plugin with automatic JWKS polling is a genuine advantage over Kong CE's manual `jwt` key management. The gap is real but bounded: at POC scale, a `pre-function` Lua snippet in Kong CE can poll the IAM's JWKS endpoint on a schedule, closing the operational gap without requiring APISIX. The decisive rejection reason is community scale. APISIX's smaller English-language community increases the cost of resolving unusual configuration problems for a 1–2 person team — the configuration edge cases that arise in a regulated banking gateway (SCA claim enforcement, streaming response buffering, mTLS to internal services) are more likely to have documented resolutions in Kong's ecosystem. The JWKS polling advantage does not offset the community support gap at this scale. APISIX remains the preferred upgrade path if Kong CE's JWT key management becomes operationally burdensome at production scale, or if multiple channels require significantly different OIDC configurations that benefit from APISIX's more flexible `openid-connect` plugin.

---

**Rejected: Custom BFF per channel (gateway concerns inline)**

Replicating JWT validation, SCA enforcement, rate limiting, and schema validation across N channel-specific services is the highest ongoing maintenance cost of any candidate for a 1–2 person team. Every edge policy change becomes a multi-service coordinated release. The flexibility argument does not materialise in practice: PSD2 SCA requirements, JWT validation rules, and rate limit thresholds are uniform across channels — the regulated banking edge has no meaningful per-channel variation to exploit. Flexibility that is never exercised is a maintenance surface, not a benefit.

---

## Consequences

**What this choice makes easier:**

- Edge policy — SCA-protected routes, rate limit thresholds, JWT issuer list, schema validation rules — is configured in one `kong.yaml` file in git. Changes deploy through `deck sync` in CI, with `deck diff` providing a human-readable change preview before merge.
- SSE streams are proxied natively from Kong's nginx base. No hard timeout. The only configuration needed is `X-Accel-Buffering: no` from the upstream and an extended `read_timeout` on the SSE upstream definition.
- OTel traces span the gateway boundary. Kong's `opentelemetry` plugin propagates `traceparent` to all upstream requests, creating a distributed trace that begins at the edge and continues through the Deposits API, the saga orchestrator, and downstream services — unifying the signal described in ADR-IC-007.
- Rate limiting can graduate from in-memory `local` mode (single-node POC, resets on restart) to Valkey-backed distributed counters without changing application code — only the plugin configuration changes. Valkey is already in the upgrade path from ADR-IC-005.
- PSD2 SCA enforcement is a route-level configuration decision. Adding a new financial operation that requires SCA is a `pre-function` plugin attachment to its route in `kong.yaml`, not an application code change.

**What this choice makes harder or impossible:**

- **Channel-specific edge behaviour** (e.g., mobile clients receiving a different rate-limit budget than branch terminals) requires Kong Consumer configuration or route tagging — achievable but adds configuration complexity. At POC inception, uniform edge policy across channels is assumed.
- **Kong CE's `jwt` plugin requires public key pre-registration**: the IAM's signing keys must be imported into Kong's JWT key store. Automatic JWKS rotation requires a `pre-function` workaround until Kong CE adds native JWKS auto-rotation. Key rotation events require a deliberate `deck sync`.
- **Kong CE does not manage the OIDC flow**: the gateway validates tokens but does not handle OAuth 2.0 authorization code redirects or token issuance. Channel clients manage the OIDC flow against the IAM directly; the resulting bearer token is what Kong validates.

**Residual risks:**

- **SCA enforcement drift:** The `pre-function` Lua snippet that checks SCA claims is configuration code in the gateway, not in the application's test suite. If the IAM changes its claim names (e.g., `amr` becomes `acr_values`), SCA enforcement silently breaks if the gateway configuration is not updated in lockstep. Mitigation: a contract test in CI must assert that a request without the SCA claim receives `403 SCA_REQUIRED` from the gateway — this test catches claim-name drift before it reaches any environment beyond local development. See document 07 for the testing strategy.
- **Rate-limit state loss on restart (local mode):** In `local` mode, Kong's in-memory rate-limit counters reset on process restart. A restart mid-attack window allows a burst through the limit. At POC scale this is theoretical. Mitigation: Valkey-backed rate limiting (configuration-only change) before any load test or security assessment.
- **Kong CE plugin availability by version:** The `opentelemetry`, `request-validator`, and upstream mTLS features cited in this ADR became available in Kong CE v3.x releases. The Kong CE release used at implementation must be v3.0 or later; the 2.x plugin matrix differs significantly.

  *Revised 2026-06-14: this premise is partly wrong — `request-validator` is a Kong **Enterprise** plugin and is **not** in the Kong Gateway CE (Apache-2.0) bundled-plugin set the Decision selects (verified against the `kong:3.9.1` image; `opentelemetry` and upstream mTLS are confirmed CE-bundled). The §4 edge payload-validation obligation ("reject structurally invalid requests at the edge") is therefore realised in `infra/kong/kong.yml` with the CE-bundled `pre-function` body check — the same CE mechanism this ADR mandates for SCA enforcement (§P2) — and not with `request-validator`. The declarative JSON-schema `request-validator` returns on the Kong Enterprise / APISIX upgrade path (§S4). This is an explicit-drift acknowledgement (ADR-PC-020 §D3) for a code change that honours the §4 obligation on the selected edition; the Decision is unchanged.*

---

## Implementation Principles

### P1 — DB-less declarative mode; all configuration in git

Kong must be operated in DB-less mode (`database = "off"` in `kong.conf`). All configuration — services, routes, plugins, consumers, upstreams — is expressed in a `kong.yaml` file managed with the `deck` CLI and committed to the infrastructure repository alongside application code. The `deck diff` command is the gate in CI: a PR that modifies `kong.yaml` produces a human-readable diff of the gateway configuration change before merge. No configuration changes are made directly through the Admin API on running instances; all changes are applied by rendering and syncing the declarative config.

### P2 — SCA enforcement is a route-level plugin, not an application concern

For every route that represents a financial operation (at minimum: `POST /api/v1/deposits/constitute`, `POST /api/v1/deposits/:id/mobilise`), a `pre-function` plugin running in the access phase must verify that the bearer token carries a valid SCA completion claim before the request reaches the Deposits API. The Deposits API must not implement this check redundantly — the gateway enforces it at the boundary; the application trusts the gateway assertion (Boundary 2 from document 10).

The specific claim name and value agreed with the IAM implementation must be documented alongside this ADR. A contract test (document 07) must assert at the gateway level — not the application level — that a request without the SCA claim receives `403 SCA_REQUIRED`.

### P3 — Rate limiting: per-consumer identity with IP fallback

Two `rate-limiting` plugin configurations are applied:

1. **Consumer-level:** After JWT authentication, rate limiting applies against the JWT `sub` claim mapped to a Kong Consumer. This limits requests per client identity regardless of source IP (covering mobile clients behind NAT or shared Wi-Fi).
2. **IP-level fallback:** For unauthenticated requests or pre-auth paths, an IP-based rate limit applies as a DDoS mitigation control.

POC-inception limits (to be calibrated against observed traffic before production hardening):
- `POST /deposits/constitute`: 5 requests/minute per consumer — prevents accidental duplicate constitution attempts
- General authenticated API: 200 requests/minute per consumer
- Unauthenticated paths: 30 requests/minute per IP

### P4 — SSE proxy: no buffering; read timeout spans saga duration

For every route that proxies the saga status stream (`/api/v1/processes/:process_id/stream`):

1. The upstream Deposits API response must include `Content-Type: text/event-stream` and `X-Accel-Buffering: no`.
2. The Kong upstream definition for this path sets `read_timeout` to a value that exceeds the expected maximum saga duration. Recommended: 1800 seconds (30 minutes), covering workflow-approval steps with a generous buffer. This overrides Kong's default 60-second read timeout for this upstream only.
3. Response buffering must be disabled at the route level via nginx directive passthrough (`proxy_buffering off`).

The SSE endpoint's per-process authorisation check — that the token's `client_id` matches the process's owning client — is enforced at the Deposits API level. Kong validates the token signature and SCA claim; the application enforces the process-ownership check. This two-layer separation is the mitigation for the authorisation note in document 05: the `process_id` in the URL is not a capability token.

### P5 — mTLS to all upstream services

Kong's connection from the gateway to internal services uses mTLS. Each internal service presents a TLS certificate signed by the internal CA; Kong is configured with the internal CA as its trusted root for upstream connections. Requests arriving at internal services without a valid client certificate are rejected at the transport layer. This enforces Boundary 2 (API Gateway → Internal Services) from document 10 mechanically — not by convention, not by network policy alone.

### P6 — OTel traces span the gateway boundary

The `opentelemetry` plugin must be enabled globally in `kong.yaml`. It propagates the `traceparent` header to all upstream requests, creating a distributed trace that begins at the gateway and continues through the Deposits API, the saga orchestrator, and downstream services. The `X-Correlation-Id` header from the client request (per document 05) is injected as a span attribute at the gateway and is available in every downstream log line via the OTel log injection described in ADR-IC-007.

### P7 — JWKS key rotation is a deliberate operational step

Kong CE's `jwt` plugin validates token signatures against keys registered in Kong's key store. Key rotation events — when the IAM rotates its signing keypair — require:

1. The new public key is added to Kong's key store (`deck sync` with the new key in `kong.yaml`).
2. The old key is retained until all tokens signed with it have expired (typically one token-lifetime after the rotation).
3. The old key is removed from the key store in a subsequent `deck sync`.

This three-step rotation prevents token rejection during the overlap window. The rotation procedure must be documented and tested before any production hardening. If automatic JWKS rotation becomes operationally burdensome (multiple IAM key rotations per year, or multiple JWT issuers), Apache APISIX's `openid-connect` plugin with automatic JWKS polling is the documented upgrade path.

---

## Amendment — 2026-06-20: §P2's gateway-403 SCA model is scoped to non-agent routes; the MCP money-movers enforce SCA in the engine (attest-not-deny)

In plain English: §P2 says the gateway (Kong) should be the one place that checks whether a customer recently passed strong authentication (SCA) for a money-moving request — it returns `403 SCA_REQUIRED` at the edge, and the application behind it does not re-check. That model is right for the routes a human or a saga drives. It does **not** work for the AI-agent channel's irreversible money-movers (maturing a deposit, paying a coupon), because there the bank wants to *prompt* the human to do a fresh step-up challenge mid-call — and a `403` at the gateway would kill the call *before* the agent could ever show that prompt. So for those two endpoints the SCA check moves one step inward: the gateway still validates the token and **attests** the SCA claims to the engine, but it does **not** deny; the **engine** is the one that refuses (with `422 SCA_REQUIRED`) when the proof is missing or stale, which lets the agent run the step-up and retry. This amendment records that scoping so the divergence from §P2 is explicit, not silent (the [ADR-PC-020 §D3](../../product_concepts/adrs/ADR-PC-020-llm-toolchain-and-conformance-governance.md) drift gate). It is additive: §P2 holds unchanged for every route it already governs.

This is the gateway-side companion to [ADR-IC-010 §P8 Amendment 2026-06-20 (A7–A10)](./ADR-IC-010-mcp-server-runtime-and-sdk.md) (Q-BE resolved, [bd babelstone-ziu3.5](../../product_concepts/04-open-questions.md)).

### A1 · §P2's gateway-enforced-403 model governs the non-agent financial routes — unchanged

§P2 is **binding as written** for the routes it names and their class: the constitute front door (`POST /api/v1/deposits/constitute`) and the existing-instance SoR money-movers (`POST /api/v1/sor/instances/{id}/operations`) keep the `pre-function` gateway `403 SCA_REQUIRED` gate, the application-trusts-the-gateway posture, and the gateway-level contract test ([bd babelstone-6imx / babelstone-abig](../../product_concepts/04-open-questions.md)). Nothing about those routes changes.

### A2 · The MCP agent-channel money-movers enforce SCA in the engine; the gateway attests, it does not deny

For the irreversible **agent-channel** money-movers — the MCP tools `mature_deposit` / `pay_interest`, which map to the engine commands `POST /v1/deposits/{id}/maturity` and `POST /v1/deposits/{id}/interest` — the SCA-completion *decision* lives in the **engine** (`ScaPrecondition`, returning `422 SCA_REQUIRED`), and the `/mcp` Kong route **attests** the AS-signed `acr`/`auth_time` to the engine as `X-SCA-Acr` / `X-SCA-Auth-Time` (the same `set_header` overwrite-from-the-token anti-spoof attestation §P4 / the route already does for `X-Client-Id`) **without denying**. The reason is structural and is the §P8-recommended path (ADR-IC-010 §P8 A7): a gateway `403` on `/mcp` would terminate the agent's `tools/call` *before* the MCP server could issue the URL-mode step-up elicitation, making the human-in-the-loop step-up impossible. Moving the refusal to the engine lets the tool catch the `422`, run the step-up, and retry with a refreshed token. The trust anchor is still the AS signature the gateway's `jwt` plugin validated — the engine reads only gateway-attested headers, never the raw token — so Boundary 2 (the application trusts the gateway's attestation, Document 10) is preserved; only the *deny point* moves from the gateway to the engine for these two endpoints.

### A3 · The freshness contract test for the agent path is at the engine, by construction

§P2 requires the SCA contract test to assert *at the gateway level*. For the agent-channel money-movers the assertion is necessarily at the **engine** level — the gateway no longer denies, so there is no gateway `403` to assert; the enforceable behaviour is the engine's `422 SCA_REQUIRED` (absent/stale SCA → 422; fresh attested SCA → settle), tested in `engine/tests/Babelstone.Engine.Api.Tests/DepositsApiIntegrationTests.cs`. The gateway-level §P2 contract test stays the authoritative gate for the constitute / SoR routes (A1).

### A4 · This amends §P2's scope; it does not supersede this ADR

The Decision (Kong CE, DB-less declarative, behind one shared edge) and §P1, §P3–§P7 remain binding as written. §P2 is **unchanged** for its existing routes; this amendment only *scopes* it — naming the agent-channel money-movers as the one class where SCA enforcement is engine-side (`422`) with the gateway attesting-not-denying, and recording why (the elicitation flow). The mTLS attestation trust model (§P5 / Boundary 2) is unaffected.
