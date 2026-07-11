# ADR-IC-021: IAM — OAuth 2.1 / OIDC Authorization Server — Logto

| Field | Value |
|---|---|
| Status | Accepted |
| Date | 2026-06-27 |
| Deciders | jhosm |
| Shape | Tool-selection |
| Common criteria | [ADR-IC-000](./ADR-IC-000-common-evaluation-criteria.md) |
| Depends on | [ADR-IC-006](./ADR-IC-006-edge-api-gateway.md) (Kong edge that validates the tokens), [ADR-IC-010](./ADR-IC-010-mcp-server-runtime-and-sdk.md) (the MCP server is the OAuth resource server), [ADR-IC-016](./ADR-IC-016-service-identity-and-mtls.md) (service-to-service identity, scoped to the three internal-hop planes for Boundaries 2–7; the customer/agent IdP falls outside its scope), [ADR-IC-007](./ADR-IC-007-observability-stack.md) (Grafana, secured via Logto OIDC for the Boundary-7 RBAC surface), [ADR-PC-004 §A1](../../product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md) (the OpenBao secret boundary the `SECRET_VAULT_KEK` + engine anchors resolve through; the OIDC signing key is Logto-owned per the 2026-07-11 amendment) |
| Implements | [doc 10 Boundary 1](../10-security-and-threat-model.md#boundary-1-external-clients--api-gateway) (owned channels), [Boundary 6](../10-security-and-threat-model.md#boundary-6-operations-console--saga-state--core-banking) (ops console), [Boundary 9](../10-security-and-threat-model.md#boundary-9-agent--mcp-server) (MCP agents) |
| Resolves | bd `babelstone-54rf` |
| Relates to | `v1-build-backlog` Epic **J.1** (names "IAM" as the unselected dependency the Kong-fronted MCP edge assumes) |

---

## In plain English

This picks the piece of software that actually **logs users in and hands out the tokens** for babelstone's public staging environment — the login screen, the strong-customer-authentication (SCA) step, and the authority third-party AI agents talk to. Kong already *checks* tokens at the edge and the MCP server already *validates* them; what was missing is the **issuer**. The choice is **Logto** as the **single** OAuth 2.1 / OIDC Authorization Server serving every human-and-agent boundary: owned channels (Boundary 1), operators and the ops console (Boundary 6), and MCP agents (Boundary 9). A stateless **oauth2-proxy** is held *in reserve* as a forward-auth shim, to be added only if some future non-OIDC surface ever needs a perimeter gate — it is not part of this build.

The honest, load-bearing finding behind the choice: **no self-hostable open-source IdP today does both of the two hardest MCP requirements as a hardened feature.** One (RFC 8707 audience-binding, the anti-replay control) is security-critical and cannot be faked; the other (Dynamic Client Registration, letting unknown agents self-onboard) is, by document 10's own wording, *recommended* rather than mandatory, and is fine to defer for a curated staging cohort. Logto is the only candidate that gets the security-critical one right natively while matching the project's no-JVM / reuse-Postgres / self-host DNA — so we accept and track the DCR gap rather than take a candidate that ships the anti-replay control on an experimental flag.

## Context

[Document 10](../10-security-and-threat-model.md) fixes the *protocol* at the two customer-facing trust boundaries — OAuth 2.0 / OIDC at [Boundary 1](../10-security-and-threat-model.md#boundary-1-external-clients--api-gateway) (owned web/mobile/Mission-Control channels) and the full OAuth 2.1 contract at [Boundary 9](../10-security-and-threat-model.md#boundary-9-agent--mcp-server) (arbitrary third-party LLM agents over MCP) — but it states *"the IAM validates the token"* without ever **selecting the IAM**. [ADR-IC-006](./ADR-IC-006-edge-api-gateway.md) makes Kong the edge that *validates* JWTs and terminates mTLS/SCA; [ADR-IC-010](./ADR-IC-010-mcp-server-runtime-and-sdk.md) makes the MCP server the OAuth *resource server* (RFC 9728 protected-resource metadata, Bearer-token validation). [ADR-IC-016](./ADR-IC-016-service-identity-and-mtls.md) scoped itself to *service-to-service* identity across three internal-hop planes (Boundaries 2–7: mTLS, SASL, observability RBAC) — the customer/agent **token issuer** (Boundaries 1 and 9) falls outside those planes. That issuer — the Authorization Server + Identity Provider that mints the tokens, runs the login/SCA, and (ideally) lets agents register — is the hole this ADR fills. It is named, unselected, in `v1-build-backlog` Epic **J.1**.

A public staging environment now runs 24×7 on the internet, which turns "pick the IAM eventually" into "pick it now."

### What the MCP boundary mandates (the decisive filter)

The owned-channel boundary is ordinary OIDC. The decision is driven by the **hard case** — the MCP **2025-06-18 / 2025-11-25** authorization spec ([restated in ADR-IC-010](./ADR-IC-010-mcp-server-runtime-and-sdk.md#what-the-2025-11-25-spec-mandates)). Two requirements are **catastrophic if a candidate falsely claims them**:

- **RFC 8707 Resource Indicators** — the AS `MUST` bind an access token's `aud` to the requested resource (the MCP server's canonical URI), and servers `MUST NOT` accept tokens minted for another resource. This is the structural defence against token replay across MCP servers. *It cannot be approximated by an audience-mapper hack.*
- **Dynamic Client Registration (RFC 7591)** — so agents the bank does not pre-provision can self-onboard. Note the threat model's own hedge: doc 10's enrolment lifecycle says *"**If** Dynamic Client Registration is enabled — which Document 11 recommends"* — DCR is **recommended, not mandatory**, and the current MCP baseline has demoted it to a fallback behind Client ID Metadata Documents (CIMD).

Plus the supporting must-haves: OAuth 2.1 authorization-code + **PKCE (S256)**, no tokens in query strings; rotating refresh tokens with reuse-detection → whole-family revocation; RFC 7009 revocation + RFC 7662 introspection; custom per-tool scopes (`deposits:read` / `deposits:write` / `transfers:write`); and `acr` / `auth_time` claims so PSD2 SCA is a first-class saga signal. The operator-facing boundary (6) needs SSO + step-up MFA + role-based access — a standard OIDC capability, not a separate product.

### Constraints (per [ADR-IC-000](./ADR-IC-000-common-evaluation-criteria.md) + the estate's revealed preference)

Zero budget / self-hostable OSS or a genuinely-usable free tier (F1); EU data residency, GDPR erasure, DORA testability, PSD2 SCA (F2); operable by a 1–2 person team with no dedicated ops (S1). And the estate's **revealed preference**, weighed as tie-breakers: every prior ADR-IC chose self-hosted OSS (no managed SaaS for core infra); an explicit **anti-JVM** stance (Redpanda was chosen over Kafka precisely to remove the JVM as the small team's main operational risk — [ADR-IC-001](./ADR-IC-001-event-backbone-message-broker.md)); reuse of the **existing Postgres** substrate rather than a bespoke datastore; "reserve, don't pre-build"; and — decisive for the single-vs-two-component shape below — **minimise the number of identity sources a 1–2-person team must keep in sync.**

### Candidates evaluated

| Candidate | Class | Runtime | Store |
|---|---|---|---|
| **Logto** | self-hosted OSS | Node/TS on `node-oidc-provider` (OpenID-certified core) | PostgreSQL (reuses existing) |
| Authelia (+ oauth2-proxy) | self-hosted OSS | Go (single static binary) | PostgreSQL (reuses existing) |
| Keycloak | self-hosted OSS | Java / Quarkus (**JVM**) | PostgreSQL |
| Zitadel (self-hosted) | self-hosted OSS | Go | PostgreSQL |
| Ory Hydra + Kratos | self-hosted OSS | Go (headless AS + identity, **multi-service**) | PostgreSQL |
| Authentik | self-hosted OSS | Python/Django + Go + TS | PostgreSQL **+ Redis** |
| Zitadel Cloud | managed | SaaS (Google Cloud) | managed |
| Auth0 / Okta CIC | managed | SaaS | managed |
| AWS Cognito | managed | SaaS | managed |
| WorkOS / Clerk | managed | SaaS (US-operated) | managed |

The central finding, established below: **no self-hosted OSS candidate clears both RFC 8707 and DCR as hardened features.** The decision is therefore *which gap to accept* — and RFC 8707 (the security-critical, un-fakeable anti-replay MUST) outranks DCR (recommended, deferrable at curated scale).

## Evaluation

### Hard filter results

#### F1 · Cost / licensing

| Candidate | Licence / cost | Verdict |
|---|---|---|
| Logto | MPL-2.0 (OSI; financial-services use permitted; self-hosted edition fully functional, no required feature paywalled) | **Pass** |
| Authelia | Apache-2.0 (single OSS build, no open-core split) | **Pass** |
| Keycloak | Apache-2.0 (CNCF-held IP) | **Pass** |
| Zitadel (self-hosted) | AGPL-3.0-only core (OSI; permitted by F1) — **relicensed from Apache-2.0 on 2025-03-31** (an S4 signal, not an F1 fail) | **Pass** |
| Ory Hydra + Kratos | Apache-2.0 core; no decisive must-have sits behind the paid Ory Enterprise overlay | **Pass** |
| Authentik | MIT core + proprietary Enterprise; the core OAuth2/OIDC provider is not feature-gated | **Pass** |
| Zitadel Cloud | Apache-2.0 engine under a managed subscription; free tier real but on US-owned GCP | Pass (conditional) — free tier usable, but residency/DNA concerns push the verdict downstream |
| Auth0 / Okta CIC | Proprietary SaaS | **Fail** — DCR (a required must-have) is paywalled above the free tier (Professional/Enterprise + support ticket): the textbook open-core paywall [ADR-IC-000 §F1](./ADR-IC-000-common-evaluation-criteria.md) fails |
| AWS Cognito | Proprietary SaaS | **Fail** — managed SaaS contradicting F1's OSS/self-host intent; RFC 8707 binding gated to the paid Essentials/Plus tier |
| WorkOS / Clerk | Proprietary SaaS | Pass (conditional) — usable free tier, but closed SaaS fails the OSS arm; decided on F2 below |

#### F2 · Regulatory fit (GDPR / DORA / PSD2)

| Candidate | EU residency | GDPR erasure | DORA | PSD2 SCA | Verdict |
|---|---|---|---|---|---|
| Logto | self-hosted in operator EU infra | user store = plain Postgres rows, deletable | operator-run, testable | `acr`/`auth_time` supported; step-up maturity is the soft-criteria watch-item | **Pass** |
| Authelia | self-hosted EU | external user store (LDAP/file) | operator-run | SCA enforced by its **policy engine**, not surfaced cleanly via `acr` | Pass (conditional) — accept policy-engine SCA + external user store for erasure |
| Keycloak | self-hosted EU | deletable | operator-run | mature `acr`/step-up | **Pass** |
| Zitadel (self-hosted) | self-hosted EU | deletable | operator-run | `acr` partial | **Pass** |
| Ory Hydra + Kratos | self-hosted EU | Kratos store deletable | operator-run | `acr` step-up is self-built glue | **Pass** |
| Authentik | self-hosted EU | deletable | operator-run | `acr` is a static provider URI, not a per-LoA value | Pass (conditional) — model SCA as a flow-level step-up workaround |
| Zitadel Cloud | **US-owned GCP** | managed | contractual, not operator-run | `acr` partial | Pass (conditional) — managed, US cloud |
| Auth0 / Okta CIC | EU region (Frankfurt/Dublin) | managed | contractual | mature | Pass (conditional) |
| AWS Cognito | EU regions | managed | contractual | adequate | Pass (conditional) |
| WorkOS / Clerk | **US-controlled** (WorkOS has no EMEA region; Clerk EU is bytes-in-EU, US-operated → CLOUD Act, DPF not sovereignty) | managed | contractual | proprietary claims, not standard `acr`/`auth_time` | **Fail** — a US-controlled token issuer for an EU bank is a structural residency/sovereignty failure |

**Proceeds to soft criteria:** the six self-hosted OSS candidates (Logto, Authelia, Keycloak, Zitadel, Ory, Authentik). The four managed/proprietary options are eliminated at the filters — Auth0 and AWS Cognito on hard **F1** failures, WorkOS/Clerk on a hard **F2** failure, and Zitadel Cloud disqualified downstream as a managed dependency that contradicts the unbroken self-host/EU-residency precedent of every prior ADR-IC.

### The decisive filter — MCP Boundary 9

The whole decision exists to serve the hard case. Verdicts below reflect the **adversarial verification pass** (skeptic agents tasked to *refute* each top candidate's optimistic claims); where verification downgraded a dossier claim, the hardened verdict is shown.

| Candidate | DCR | RFC 8707 | OAuth2.1 / PKCE | rotating-refresh (reuse→family revoke) | revoke / introspect | custom scopes | `acr`/`auth_time` |
|---|---|---|---|---|---|---|---|
| **Logto** | **no** | **yes** (native) | yes | partial | yes / yes | yes | partial |
| Authelia | **no** | yes | yes | partial | yes / yes | yes | partial |
| Keycloak | partial | **no** (hardened) | yes | partial | yes / yes | yes | yes |
| Zitadel | **no** | **no** | yes | yes | yes / yes | partial | partial |
| Ory Hydra+Kratos | partial | **no** | yes | yes | yes / yes | yes | partial |
| Authentik | **no** | **no** | yes | partial | yes / yes | yes | partial |

**What the adversarial pass confirmed vs refuted:**

- **Logto RFC 8707 — confirmed native (the decisive confirmation).** Verification *tried to refute* the optimistic "yes" and failed: each API resource is a URI resource indicator; the request-time `resource` parameter is honoured across auth-code / token-exchange / client-credentials; the JWT `aud` is bound to it; mismatch yields `invalid_target`; backends reject cross-resource tokens. This is true RFC 8707, **not** an audience-mapper workaround. One operational footgun: if a client omits `resource`, Logto falls back to a default resource — so the MCP client/SDK **must** send it (the MCP-Auth SDK does).
- **Logto DCR — confirmed no, but bounded.** Logto's own MCP-Auth guide states DCR is unsupported and every client is hand-registered in the Console; not paywalled, just unimplemented (roadmap "yet"). This is the **lone decisive gap** — accepted-and-tracked for a curated staging cohort.
- **Keycloak RFC 8707 — refuted to "no (hardened)".** Keycloak's own MCP guide says verbatim *"Keycloak cannot recognize resource parameter"* and rates the MCP spec *"Partially Supported without Resource Indicators."* Real `aud`-binding exists only behind an **experimental, default-off** flag (`Profile.Feature.RESOURCE_INDICATORS`, merged 2026-03-17, undocumented in the MCP guide) or a custom audience-mapper hack — no hardened, supported path today. `mcpBlocking`.
- **Keycloak DCR — downgraded "yes"→"partial".** RFC 7591/7592 are listed "Supported", but *arbitrary*-agent registration is gated by a Trusted-Hosts allowlist whose guard is **spoofable via `X-Forwarded-For`** in k8s/multi-proxy setups (no fix merged), and the current MCP baseline prefers CIMD (experimental in Keycloak). Verification also surfaced a **current crash-loop regression (#48438)** on single-node Postgres in 26.6.0/26.6.1 — directly relevant to the 1–2-person ops claim.
- **Zitadel — confirmed both no, both blocking.** No released version ships DCR (no `registration_endpoint` in live discovery; only unmerged draft PRs, issue #9810). RFC 8707 returns `invalid_target` (issue #794); the only audience mechanism is a proprietary `urn:zitadel:…:aud` scope that the vendor's own guidance warns does **not** enforce resource-bound `aud`.
- **Ory Hydra RFC 8707 — confirmed no, blocking.** Hydra binds `aud` via a **static per-client allow-list**, not the request-time `resource` parameter; real MCP integrations disable audience validation outright as a stopgap. DCR is "partial" (needs a response-stripping proxy for Claude.ai's strict schema), atop the heaviest assembly burden in the field.

**Net:** no surviving candidate clears both DCR and RFC 8707 as hardened features. Keycloak / Zitadel / Ory / Authentik all fail the *replay-protection* MUST (RFC 8707) — the more security-load-bearing of the two. **Logto and Authelia are the only survivors that get RFC 8707 right natively**, and both lack DCR. Between them, Logto is a full Authorization Server (Authelia's OIDC provider is still open-beta and weak on standard `acr`), and — decisively for the single-component shape — pairing Authelia *with* Logto would mean running a second identity source (see §Decision). So Logto is selected as the sole AS.

### Soft criteria

**Logto — CHOSEN (sole Authorization Server for Boundaries 1, 6, 9).** *S1:* excellent — a single container bundling its own sign-in UI (no separate login/consent app to assemble, unlike Ory), Postgres-only (Redis optional, single-instance needs none), no JVM. Verification corrected two facts: the Node floor is **22.14+**, and upgrades require a **manual** `db alteration deploy` step per release (the one real recurring chore) — but confirmed the central "weekend stand-up, no specialist" profile holds. It covers the **operator boundary (6)** natively: SSO, TOTP + WebAuthn/passkey MFA for step-up, and roles — so the ops console and Grafana need no separate auth product (Grafana via its native `[auth.generic_oauth]`, mapping Logto roles → the NOC/compliance/developer org roles of [ADR-IC-007](./ADR-IC-007-observability-stack.md) §P6). *S2:* standard OIDC + RFC 8414 discovery means Kong's plugins "just work"; the RFC 9728 protected-resource flow on the MCP server interoperates cleanly. *S3:* low — portable OIDC JWTs, user store is plain Postgres exportable via SQL + Management API, MPL-2.0 source. *S4:* healthy independent OSS, no restrictive licence change, and notably *proactive* MCP investment (it authored the open MCP-Auth library) — but single-vendor (Silverhand), not foundation-governed, so bus-factor is the watch-item, insured by standard-OIDC portability (S3). Against project DNA it is the **best** fit: non-JVM, reuses the existing Postgres, fully self-host/EU-resident, low exit cost, and **one** identity source.

**Authelia — evaluated, NOT adopted.** A genuinely excellent, lightest-in-class forward-auth portal (single Go binary, config-as-code, Postgres-backed, OpenID-certified) — and one of only two survivors with correct RFC 8707. But it is **not adopted, and not paired with Logto**, for a decisive structural reason: Authelia is *itself* an identity provider, with its **own user store and its own 2FA enrolment**. Running it alongside Logto would mean **two identity sources to keep in sync** — precisely the operational duplication the 1–2-person constraint exists to avoid — with no compensating benefit, because every babelstone surface that needs protection (Grafana, Backstage, the Kong-fronted product API) speaks OIDC natively and can authenticate against Logto directly. Authelia's one distinctive capability — *forward-auth* for a surface that cannot speak OIDC — has almost no surface area in this estate; and where it is ever needed, a stateless **oauth2-proxy** delegating to Logto fills it without a second user store (see §Decision). It is therefore recorded as evaluated-and-rejected, not as a complement.

*(Keycloak, Zitadel, Ory, Authentik are not carried into soft-criteria weighting as primary because each fails the decisive RFC 8707 replay-protection must-have as a hardened feature; Keycloak additionally carries the anti-JVM DNA liability the project explicitly flagged via the Redpanda-over-Kafka precedent.)*

## Decision

**Chosen: Logto** as the **single** OAuth 2.1 / OIDC Authorization Server for the entire human-and-agent identity plane — Boundary 1 (owned channels), Boundary 6 (operators / ops console / Grafana SSO), and Boundary 9 (the curated MCP edge).

Decisive reason: Logto is the **only** surviving self-hostable, Postgres-backed, non-JVM candidate that implements the catastrophic-if-false MCP must-have — **RFC 8707 resource-bound `aud`** (verified native, adversarially confirmed) — *and* covers operator SSO + step-up MFA + RBAC natively, so the whole identity plane is served by **one** system that matches the project's anti-JVM / reuse-Postgres / self-host / minimise-identity-sources DNA.

**Rejected:**
- **Keycloak** — fails the RFC 8707 anti-replay MUST as a hardened feature, DCR guard is spoofable, and the JVM is the explicit anti-pattern of [ADR-IC-001](./ADR-IC-001-event-backbone-message-broker.md).
- **Zitadel** — fails *both* MCP must-haves (worse on the hard case than Logto) + an S4 relicence signal.
- **Ory Hydra + Kratos** — fails RFC 8707 natively and is the heaviest assembly for a 1–2-person team.
- **Authentik** — no DCR, no RFC 8707.
- **Auth0** (F1: DCR paywalled), **AWS Cognito** (F1: managed + RFC 8707 paid-tier), **WorkOS/Clerk** (F2: US-controlled issuer for an EU bank), **Zitadel Cloud** (managed-SaaS DNA + fails both MCP must-haves).

### Rejected / not taken

- **Pairing Logto with Authelia (or any second IdP).** Authelia is itself an identity provider with its own user store and 2FA; running it alongside Logto means two identity sources to keep in sync — the operational duplication the 1–2-person constraint exists to avoid — with no compensating benefit, since every babelstone surface that needs protection (Grafana, Backstage, the Kong-fronted product API) speaks OIDC natively and authenticates against Logto directly. Not adopted.
- **A stateless forward-auth perimeter gate now (oauth2-proxy).** Reserved, not pre-built: if a future *non-OIDC* internal surface ever needs gating, oauth2-proxy delegating to Logto provides forward-auth with a **single** identity source (no second user store, unlike Authelia). Unnecessary while every exposed surface is OIDC-aware or already behind Kong.
- **Waiting for a single candidate that does both DCR + RFC 8707.** Rejected: at curated staging scale DCR is deferrable (doc 10 makes it *recommended*, not mandatory), and the security-critical control (RFC 8707) must not wait. Reserve, don't pre-build.
- **A DCR-bridge/shim now (RFC 7591 → Logto Management API).** Reserved, not pre-built: a thin fronting registration proxy is the forward path to *open* Boundary-9 onboarding without switching IdPs, but it is unnecessary while the agent cohort is curated and hand-registered.

## Consequences

**What this makes easier:**
- Correct, **native RFC 8707 anti-replay** for MCP tokens from day one — the MCP server's existing `aud`-validation ([ADR-IC-010](./ADR-IC-010-mcp-server-runtime-and-sdk.md)) becomes *meaningful* (cross-resource replay actually rejected).
- A **single, single-container, Postgres-reusing, non-JVM** AS for the whole human/agent plane — **one user store, one MFA config, one thing to operate** — covering customers, operators, and agents; clean Kong / OTel / OpenBao composition over standard OIDC; **no ADR-IC-006 edge changes** (point discovery at Logto).
- Grafana SSO and the Boundary-7 RBAC split come "for free" via Logto's native OIDC integration, no extra gateway.
- Low exit cost — portable JWTs, SQL-exportable user store.

**What this makes harder or impossible:**
- **Arbitrary, un-pre-provisioned agents cannot self-onboard** until DCR ships (or a shim is built) — full open Boundary-9 production is gated on it.
- Manual per-upgrade DB alterations; a Node-22 runtime distinct from the engine's .NET.
- A surface that genuinely *cannot* speak OIDC would need an oauth2-proxy shim added later (a small, deferred cost — not built now).

**Residual risks** (the `Pass (conditional)` / watch-items to verify at implementation):
- **DCR gap (headline trade-off)** — fine for curated staging; the gate for open Boundary-9 production.
- **Refresh-family revocation unverified** — rotation confirmed; reuse-detection→whole-family-revoke is *not documented* and must be empirically verified before relying on it for token-theft response.
- **Default-resource fallback** — if a client omits `resource`, `aud`-binding silently weakens; assert the MCP SDK always sends it.
- **`acr`-driven SCA + operator step-up maturity** — Logto's step-up is less mature than Keycloak's; validate PSD2 SCA-as-first-class-`acr` for customer flows (Boundary 1/9) *and* the operator step-up for the ops console (Boundary 6), possibly via custom flows.
- **Bus-factor (S4)** — Logto is single-vendor (Silverhand), not foundation-governed; standard-OIDC portability is the insurance.

## Staging-first rollout

1. **Stand up Logto on k3s (weekend 1).** Single container + the **existing Postgres** (`DB_URL` to a new database; run `logto db init`), public Ingress on an auth subdomain via Traefik + cert-manager TLS, reverse-proxied so Kong's product routes stay the ADR-IC-006 edge. Store the `SECRET_VAULT_KEK` and OIDC signing keys in **OpenBao** (the engine's existing secret boundary — never on the bus, per the PII rule), injected at deploy; wire the manual `db alteration deploy` step into a one-shot k3s Job for upgrades; schedule the annual signing-key rotation (`rotate oidc.privateKeys`, grace-period enabled) as a cron Job. *(If any not-yet-OIDC-wired surface needs gating before its own wiring, front it with a stateless oauth2-proxy → Logto — reserved, not built by default.)*
2. **Wire Boundary 1 (owned channels).** Register the web/mobile/Mission-Control UI as Logto applications; enforce auth-code + PKCE (S256), no tokens in query strings. Configure WebAuthn/TOTP MFA for PSD2 SCA at login; emit `acr`/`auth_time`. Kong's OIDC/JWT plugins validate against Logto's `/.well-known/openid-configuration` + JWKS — no edge changes beyond pointing discovery at Logto.
3. **Wire Boundary 6 (operators / ops console / Grafana).** Register the operations console and **Grafana** as Logto OIDC clients; enforce SSO + step-up MFA (TOTP/WebAuthn) for operator access (doc 10 Boundary 6); map Logto roles → Grafana's NOC / compliance / developer org roles ([ADR-IC-007](./ADR-IC-007-observability-stack.md) §P6) via Grafana's native `[auth.generic_oauth]`. One identity source for staff, no separate gateway.
4. **Wire Boundary 9 (MCP, curated).** Register the **MCP server as a Logto API resource** whose identifier *is* the canonical MCP-server URI (trailing slash per the MCP-Auth SDK). Define `deposits:read` / `deposits:write` / `transfers:write` as that resource's scopes for fine-grained scope-to-tool mapping. The MCP server (already the RFC 9728 resource server) advertises protected-resource metadata; agent SDKs send the `resource` parameter so Logto binds `aud` and rejects cross-resource replay. **Until DCR ships, hand-register each third-party agent client in the Logto Console** — acceptable for a curated staging cohort.
5. **Verify the security-critical behaviours at implementation (gate before any production promotion):** (a) a token minted for the MCP resource is rejected at any other resource — RFC 8707 end-to-end; (b) refresh-token **reuse-detection → whole-family revocation** actually fires (dossier "partial", undocumented); (c) the MCP SDK *always* sends `resource` so the default-resource fallback never silently un-binds `aud`; (d) `acr`/step-up enforced for customer SCA *and* operator console access; (e) load-test the manual upgrade-alteration Job.

## Verifiable commitments

These commitments are catalogued in the [commitment catalogue](../../product_concepts/adrs/commitment-catalogue.md) — the single source of truth for each commitment's exact claim, gate (pyramid level), and `Live`/`Planned`/`Gap` status ([ADR-PC-020 §P5–§P7](../../product_concepts/adrs/ADR-PC-020-llm-toolchain-and-conformance-governance.md)). They were seeded as catalogue rows **IAM-1…IAM-5** when the IAM security verification landed (bd `babelstone-zla1.10.5`); this section is now the one-way ADR→catalogue reference by Test ID per [ADR-PC-000 Amendment 2026-05-24](../../product_concepts/adrs/ADR-PC-000-namespace-and-contract-shape-framework.md) — it names the claim, not the mutable status:

- `IAM_TOKEN_AUD_RESOURCE_BOUND` (C1) — **the AS binds an access token's `aud` to the requested MCP resource (RFC 8707); a token minted for the MCP-server URI is rejected at any other resource**. The wrong-resource rejection is the enforcement leg (shared with catalogue row MCP-1); the AS-binds-aud leg was proven live against Logto (slice 1).
- `IAM_OAUTH21_PKCE_ENFORCED` (C2) — **authorization-code + PKCE (S256) is enforced; no token is ever accepted from a URI query string**. Realised as a static discovery-contract (S256-only, no `plain`); the interactive "a `code_challenge`-less request is refused" leg stays Planned.
- `IAM_REFRESH_REUSE_FAMILY_REVOKE` (C3) — **refresh-token rotation with reuse-detection revokes the whole token family** (the §Residual-risks item to verify empirically). Proven empirically on live Logto v1.41.0 (slice 3) — the residual resolved positively; stays Planned for CI (no live Logto in CI).
- `IAM_SCA_ACR_AUTH_TIME` (C4) — **issued tokens carry a fresh `auth_time` and SCA-strength step-up is enforceable as a precondition for irreversible operations (PSD2), un-bypassable client-side**. The freshness gate is the enforcement leg (shared with catalogue rows MCP-2 / MOVEMENT-3). The AS-emits-native-`acr` half is a documented watch-item: the deployed Logto emits **no native `acr`** — step-up strength is a synthesised non-`acr` claim, freshness rides native `auth_time` ([ADR-IC-010 §A16](./ADR-IC-010-mcp-server-runtime-and-sdk.md), slice 2).
- `IAM_OPS_CONSOLE_STEP_UP` (C7) — **the ops console requires a Logto-issued session with step-up MFA before reaching saga state; operator access is role-scoped and Grafana trace access is logged (Boundary 6 → 7)**. The OIDC-gate + role wiring is present; the step-up-MFA-demanded leg stays Planned (interactive).

Two commitments carry no catalogue Test ID of their own:

- **Narrow, per-tool scopes only — no god-scope** (C5: `deposits:read` / `deposits:write` / `transfers:write`) — realised and governed at the [ADR-IC-006](./ADR-IC-006-edge-api-gateway.md) gateway + the MCP edge (`RESOURCE_SCOPES`), not as this ADR's own catalogue row; the three narrow scopes were registered live in slice 1.
- **No DCR for arbitrary agents** (C6) — a deliberate, visible gap: agents are hand-registered at staging (the open-Boundary-9 production gate). A tracker, not a testable commitment.

## Amendment — 2026-07-11: Logto owns and generates its own OIDC signing key (not OpenBao-injected)

Staging implementation revealed that an operator-**injected** OIDC signing key breaks the Logto admin
console: every console login fails with `id_token_signed_response_alg must be 'ES256'`. The cause is a
node-oidc-provider key-handling incompatibility — an imported EC P-256 key (in *either* SEC1 or PKCS#8
encoding) leaves the `admin-console` client defaulting to `RS256` against an ES256-only provider — whereas
a **Logto-generated** key (`db config rotate oidc.privateKeys --type ec`) works. Because nothing outside
Logto consumes the OIDC signing key — the engine does not read it (verified), and the CSI-synced copy was
dead plumbing — the fix is to let Logto own the key. This refines the §Staging-first-rollout provisioning
slot; it does **not** change the choice of Logto as the Authorization Server (bd `babelstone-zla1.10.16`).

### A1 · The OIDC signing key is Logto-generated and Logto-owned, not OpenBao-injected

§Staging-first-rollout item 1 said to *"Store the `SECRET_VAULT_KEK` and OIDC signing keys in OpenBao …
injected at deploy."* Revised: **only `SECRET_VAULT_KEK` is OpenBao-injected** (Logto still uses it to
encrypt connector secrets at rest). The **OIDC signing key (`oidc.privateKeys`) is generated by Logto's
`db seed` and persisted in Logto's own database** — never injected, never on the bus. It is unshared: no
other component reads it. Accordingly the `OIDC_PRIVATE_KEYS` secret key, its OpenBao KV entry
(`secret/data/babelstone/logto → oidc_private_keys`), and its CSI `SecretProviderClass` object are removed
in this change.

### A2 · The annual in-place rotation is unchanged; the ADR-PC-004 §A1 boundary still holds for what remains

The annual signing-key rotation (`rotate oidc.privateKeys`, grace-period enabled) as a cron Job stays
exactly as decided — Logto now rotates a key it also generated. The
[ADR-PC-004 §A1](../../product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md) OpenBao secret boundary
remains binding for every secret that *does* resolve through it (`SECRET_VAULT_KEK`, the engine's
AppRole/transit anchors); it simply no longer carries the Logto OIDC signing key, which never leaves
Logto's own DB.

### A3 · This amends the decision; it does not supersede this ADR

The core decision — **Logto as the OAuth 2.1 / OIDC Authorization Server** — and every §Staging-first-rollout
item other than the OIDC-key-provisioning clause of item 1 remain binding as written. This amendment
refines a single provisioning detail; it is appended to, not a revision of, the Decision.

## Cross-references

- [doc 10 — Security and Threat Model](../10-security-and-threat-model.md) — Boundaries 1, 6, 9, whose token issuer this ADR selects.
- [doc 11 — Chat Agent Channel Strategy](../11-chat-agent-channel-strategy.md) — the OAuth 2.1 / RFC 8707 / DCR commitments for the MCP channel.
- [ADR-IC-006](./ADR-IC-006-edge-api-gateway.md) — Kong CE, the edge that *validates* the tokens this AS issues (unchanged by this decision; discovery repointed at Logto).
- [ADR-IC-007](./ADR-IC-007-observability-stack.md) — Grafana LGTM, secured via Logto OIDC (`generic_oauth`) for the Boundary-7 RBAC surface, and the OTLP plane that observes Logto itself (no PII on spans).
- [ADR-IC-010](./ADR-IC-010-mcp-server-runtime-and-sdk.md) — the MCP server as OAuth resource server; its RFC 9728 metadata + `aud`-validation become load-bearing once Logto binds `aud` natively.
- [ADR-IC-016](./ADR-IC-016-service-identity-and-mtls.md) — service-to-service identity (the *separate* Plane B: mTLS + Redpanda SASL/SCRAM), which this customer/agent IdP does not touch.
- [ADR-IC-000](./ADR-IC-000-common-evaluation-criteria.md) — the F1/F2 + S1–S4 framework and verdict vocabulary this evaluation applies.
- [ADR-PC-004 §A1](../../product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md) — the OpenBao secret boundary the AS's `SECRET_VAULT_KEK` resolves through (never on the bus). Per the 2026-07-11 amendment, the OIDC **signing** key is Logto-generated and Logto-owned, not OpenBao-injected.

---

*Accepted 2026-06-27 by jhosm.*
