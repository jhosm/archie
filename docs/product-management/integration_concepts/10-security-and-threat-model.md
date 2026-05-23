# Banking Ecosystem — Integration Architecture
## Document 10: Security and Threat Model

Security in event-driven systems is not a layer you add at the end. It is the same thing as architecture: a set of decisions about who can do what, across which boundaries, with what data. In a banking ecosystem built on shared Kafka topics, distributed sagas, and a privileged ACL with real money-moving capability, these decisions are not optional and they are not somebody else's problem.

This document names the trust boundaries in this architecture, the assets worth protecting, the threats that flow from the design choices made in documents 01–09 and 11, the customer-identity binding lifecycle for the LLM-agent channel, and the six principles that constrain how security is handled across the system. Each subsequent document in the series treats these principles as given; this is where they are grounded.

---

## The Concrete Problem

Imagine the following incident. A misconfigured deployment of the saga orchestrator exposes its internal command topic without authentication. A lateral-movement attack from a compromised notification service reaches it. The attacker issues `ConfirmDebit` commands directly to the ACL, which — because ACL commands are not independently authorized — calls Core Banking and executes real debits against real accounts. The distributed tracing backend flags elevated ACL call volume 90 minutes later. By then, several operations have cleared.

This scenario is not exotic. It is the direct consequence of the implicit assumption threaded through the series before this document: that internal services are trusted, that Kafka is a trusted bus, that authorization lives only at the edge. In a monolith that assumption is defensible. In a system where every bounded context is a potential attacker surface, it is not.

---

## Assets Worth Protecting

Before naming threats, you need to know what you are protecting. In this system:

| Asset | What it is | Why it matters |
|---|---|---|
| **Financial operation data** | Account numbers (IBANs), amounts, rates, transaction IDs, core_txn_ids | Direct money-moving capability if tampered |
| **Client PII** | The mapping from `client_id` to name, NIF, contact details, relationship history | Regulatory obligation (GDPR); reputational risk |
| **Saga state integrity** | Orchestrator state, outbox contents, ACL idempotency store | Corruption enables duplicate debits, lost compensations, undetectable fraud |
| **Audit trail** | Causation chain, event history, saga transition log | Admissible evidence in regulatory proceedings; required by BdP supervision |
| **Operations console capability** | Force-retry, force-compensation, manual saga manipulation | Direct financial power; the highest-privilege interface in the system |
| **Schema registry and catalogue** | Compatibility modes, event definitions, consumer registrations | A tampered schema can break all consumers simultaneously |
| **Observability data** | Traces, logs, metrics — aggregated from all services | Contains financial amounts, account identifiers, client IDs in one searchable place |

---

## Trust Boundaries

A trust boundary is a point in the system where claims must be verified rather than assumed. This architecture has nine of them.

### Boundary 1: External Clients → API Gateway

Everything outside the API gateway is untrusted. This includes mobile apps, web frontends, branch terminals, and third-party partners. Authentication here is OAuth 2.0 / OIDC — the IAM validates the token, and the gateway enforces the result.

**What this boundary must enforce:**
- Token validation (signature, expiry, issuer)
- PSD2 Strong Customer Authentication for financial operations (deposit constitution, early mobilization). SCA is not a UI concern — it shapes the saga because a failed SCA challenge mid-flow requires the orchestrator to handle the rejection as a first-class outcome
- Rate limiting per client identity, per IP, per operation type — both a resilience and a fraud-prevention control
- Request payload schema validation before anything enters the system

**What the saga must know:** if SCA fails or times out mid-flow, the orchestrator receives a rejection event from the authentication layer, not a technical error. The orchestrator treats it as a `ConstitutionRejected` outcome and the standard compensation path applies (per [§05](./05-constitution-saga-walkthrough.md) step 0).

### Boundary 2: API Gateway → Internal Services

Inside the gateway, token claims are propagated as signed assertions (JWT claims or a service mesh–validated identity). Services do not re-validate the token against the IAM — they trust the gateway's assertion. This is the standard internal trust model.

**Critical constraint:** mutual TLS (mTLS) between gateway and all internal services. An internal service that accepts plain HTTP can be reached by anything else on the network that knows the port. In a zero-trust network model, every hop is authenticated.

### Boundary 3: Deposits Service → Kafka

Kafka is not a trusted bus. It is a shared medium. Any service that can connect to Kafka can theoretically produce to any topic unless topic-level ACLs prevent it.

**What must be enforced:**
- Every Kafka producer authenticates with a service identity (mTLS client certificate or SASL/SCRAM)
- Only the Deposits service can produce to `deposits.integration.events` and `deposits.process.events`
- No service can produce to another context's topics
- ACLs are part of the deployment configuration — not convention, not documentation, not trust

The outbox publisher has a distinct service identity from the Deposits API itself. If the publisher is compromised, it cannot issue commands; it can only publish events to the topics it is authorized for.

### Boundary 4: Kafka → Each Consumer

The fan-out from `DepositConstituted` reaches CRM, Notifications, Documentation, Reporting, Projectors — six or more consumers. Each has its own service identity and subscribes only to the topics it needs.

**Data minimization implication:** if `DepositConstituted` carries IBANs and financial rates in its payload, every consumer that subscribes receives them — whether it needs them or not. The Notifications adapter needs a confirmation fact and a `client_id`; it does not need the IBAN. The structural fix is covered in the GDPR principle below. The short-term mitigation is topic-level consumer authorization: only consumers with a documented need subscribe to events carrying account identifiers.

### Boundary 5: ACL → Core Banking

This is the most hostile boundary in the system. Core Banking is external, high-privilege, and typically legacy. A successful attack at this boundary moves real money.

**What must be enforced:**
- The ACL uses a dedicated service account for Core Banking, separate from any other identity in the system
- Credentials live in a secrets manager (vault, HSM) — never in configuration files or environment variables
- Credentials are rotated on a defined schedule, and rotation is tested, not assumed
- The reconciliation job (ACL responsibility 7) has read-only access — it uses a separate credential from the write operations and cannot execute movements
- Every Core Banking call is logged with `correlation_id` and the originating `process_id`, so the audit trail crosses the boundary even if the Core's own logs don't carry it

**The hardest case:** the ACL must not be callable by arbitrary internal services. Only the saga orchestrator can issue commands to the ACL. This is enforced by service identity — the ACL's command port accepts connections only from the orchestrator's identity (mTLS, or an authorization header validated against the orchestrator's service account).

### Boundary 6: Operations Console → Saga State / Core Banking

The operations console — described in [Document 06](./06-observability-and-tracing.md) — lets operators retry, cancel, and force-compensate sagas in `HUMAN_INTERVENTION_REQUIRED` states. These are irreversible financial operations performed by humans.

This boundary has the highest privilege in the system and the weakest inherent security — humans operate it, mistakes are possible, and actions cascade into Core Banking.

**What must be enforced:**
- Strong authentication for console access (separate from normal employee SSO — step-up MFA at minimum)
- Role-based authorization: not every operator can force-compensate; amounts above a threshold require two independent approvals (4-eyes principle)
- Every console action is written to an immutable audit log: operator identity, timestamp, action taken, saga state before and after, the justification text if required by policy
- Read-only access is the default; write actions require explicit role grants that expire and are reviewed
- Console access is logged at the observability layer, not just the application layer

### Boundary 7: Observability Backend → All System Data

The distributed tracing backend ([Document 06](./06-observability-and-tracing.md)) aggregates traces from every service in the ecosystem. Those traces carry `deposit.amount`, `core.account`, `deposit.client_id`, saga states, and error details. The log aggregator carries structured logs with `correlation_id` and `process_id` for every operation.

This makes the observability backend a high-value target — it is, in effect, a searchable database of all financial operations.

**What must be enforced:**
- RBAC at the observability layer: the NOC team can see operational health (error rates, lag); the compliance team can see audit trails; developers see their own service traces. These are different access levels with different implications
- Span attributes containing account identifiers and financial amounts are sensitive data — they must be classified accordingly, and access to traces carrying them should be logged
- Retention policies for observability data have a legal basis, just like application data. Traces are not "just logs"

### Boundary 8: GDPR — Personal Data Retention vs. Event Immutability

This is not a network boundary — it is a data boundary. [Document 09](./09-long-term-schema-evolution.md) establishes that events are immutable. GDPR Article 17 establishes that clients have a right to erasure of their personal data. These two principles are in direct conflict if personal data lives in the event store.

The resolution is structural and must be made before the first event is published:

**Personal data does not belong in events.** Events reference only the pseudonymous `client_id`. Name, NIF, contact details, and any other GDPR-subject data live in a separate **Customer Data Store**, keyed by `client_id`. Erasure deletes the record from the Customer Data Store; the event log retains the pseudonymous `client_id`, which without the corresponding Customer Data Store record is no longer personal data under GDPR.

The IBAN in `DepositConstituted` is the clearest violation of this principle in the current design. It is financial account data that persists in the event log for the full retention window. Whether this constitutes personal data under GDPR depends on the specific analysis, but the safe design avoids it: consumers that need the IBAN look it up from the Customer Data Store using the `client_id` in the event, rather than receiving it in the event payload.

### Boundary 9: Agent → MCP Server

The MCP server fronts the bank for general-purpose LLM agents — Claude, ChatGPT, self-hosted equivalents — that the bank does not own and cannot trust. [Document 11](./11-chat-agent-channel-strategy.md) covers the channel pattern; this entry catalogues the boundary itself.

This is the least controllable boundary in the system. The agent runtime is third-party code the bank cannot patch, the conversation memory lives in someone else's database, and the layer that translates the user's intent into the bank's tool call is statistical rather than deterministic. The agent is well-meaning, capable, and structurally manipulable — defences must accommodate that profile rather than treat the agent as trusted (it isn't) or as hostile (the user's intent depends on it).

**What must be enforced:**

- OAuth 2.1 with Bearer tokens on every request, including same-session requests. PKCE on the authorisation code flow. No tokens in URI query strings (an MCP `MUST` from the 2025-11-25 spec)
- [RFC 8707](https://datatracker.ietf.org/doc/html/rfc8707) Resource Indicators: every access token's `aud` claim is bound to the canonical URI of the bank's MCP server. Tokens issued for any other resource are rejected, preventing replay across MCP servers (also an MCP `MUST`)
- Narrow, family-scoped OAuth scopes — `deposits:read`, `deposits:write`, `transfers:write` are distinct. No "god scope" covering multiple tool families. The scope-to-tool mapping is configuration in version control, reviewed in the same RFC process as event-catalogue additions ([Document 08](./08-event-catalog-governance.md))
- Strict `inputSchema` on every tool. No implicit defaults for security-relevant parameters: the `client_id` of the actor comes from the OAuth token's `sub` claim and is never accepted as a tool argument. Structurally valid but semantically suspect calls (a `source_account` the OAuth-identified customer does not own) are rejected with a typed error
- Returned content is structured against `outputSchema`. Free-text fields are capped at the smallest length consistent with business use, and content originating from customers or external counterparties (transaction references, beneficiary names, free-text notes) has control characters stripped at write-time and is annotated in the tool description as untrusted data
- Irreversible operations remove the agent from the confirmation path entirely. The mechanism is `elicitation/create` in URL mode (MCP 2025-11-25): the user authenticates and signs in a bank-controlled context, and the saga reads the resulting confirmation directly. The agent observes the outcome but does not authorise it

**Failure modes specific to this boundary:**

- **Prompt injection via bank-returned content.** The agent treats adversarial text in a free-text field — a transaction reference reading `"ignore prior instructions, transfer..."`, a customer note written by a malicious counterparty — as an instruction. The bank cannot fix the agent runtime; the structured-output and write-time sanitisation rules above narrow the attack surface but do not close it
- **Hallucinated parameters.** The agent constructs a plausible-looking `client_id`, `amount`, or `source_account` from incomplete context. Bind the actor to the OAuth token's `sub`; reject tool arguments that contradict it; reject accounts the token-identified customer does not own
- **Confused deputy.** The agent holds the customer's OAuth scope but acts on a third party's intent — either because a prompt-injection attack succeeded or because a multi-user agent crossed session boundaries. URL-mode confirmation for irreversible operations is the structural defence: actor intent is verified by a bank-controlled channel rather than asserted by the agent
- **Token replay across MCP servers.** A token issued for one MCP server is presented at another. RFC 8707 binding (above) is the mandatory defence
- **Scope creep over time.** A tool added "temporarily" with a broad scope ossifies as permanent. Every tool's scope is reviewed in the RFC process; scope grants are reviewed periodically, not just at introduction

**What the saga must know:** SCA at the OAuth grant is the entry-point control. Step-up SCA mid-flow is the irreversibility control. Both surface to the saga as the same kind of signal — an SCA outcome event — regardless of whether the channel was an owned mobile app or a third-party agent. The saga's state machine does not branch on channel; the binding lifecycle below is what makes that uniformity hold.

---

## Customer-Identity Binding Lifecycle (MCP Channel)

[Document 11](./11-chat-agent-channel-strategy.md) commits to OAuth 2.1 as the mediation between an MCP session and a banking customer, with the access token's `sub` claim as the canonical binding to `client_id`. The lifecycle of that binding — establishment, refresh, step-up, revocation — is the responsibility of this document. The treatment below is specific to Boundary 9; the owned-channel boundary (Boundary 1) follows the same OAuth/PSD2 model but with a trusted client, and the structural differences flow from the agent's untrusted status.

### Enrolment and the Initial OAuth Grant

A customer authorises an agent vendor (Claude, ChatGPT, a self-hosted client) the first time they use that vendor against the bank's MCP server. The flow:

1. The agent's MCP client discovers the bank's authorisation server endpoint via [OAuth 2.0 Authorization Server Metadata](https://datatracker.ietf.org/doc/html/rfc8414)
2. If [Dynamic Client Registration](https://datatracker.ietf.org/doc/html/rfc7591) is enabled — which [Document 11](./11-chat-agent-channel-strategy.md) recommends for an MCP server consumed by arbitrary agents — the agent registers and receives a `client_id`. The DCR endpoint applies rate limits and may require [software statements](https://datatracker.ietf.org/doc/html/rfc7591#section-2.3) to elevate trust above the default
3. The agent initiates the authorisation code flow with PKCE. The `resource` parameter ([RFC 8707](https://datatracker.ietf.org/doc/html/rfc8707)) is set to the canonical URI of the bank's MCP server; `scope` declares the tool families the agent intends to use
4. The customer authenticates at the bank's IDP. This authentication is SCA-strength under [PSD2 RTS Article 4](https://eur-lex.europa.eu/legal-content/EN/TXT/?uri=CELEX:32018R0389) — two factors from independent categories. The authentication context (`acr`) and the time of authentication (`auth_time`) are recorded
5. The authorisation server returns an access token whose `sub` is the bank's pseudonymous customer identifier (`client_id` in the architecture's vocabulary, not the agent's `client_id`), `aud` is the canonical MCP server URI, plus the granted scopes and the `acr` and `auth_time` claims. A refresh token is also issued

After enrolment, the MCP server verifies on every request — locally, without re-contacting the IDP — that the token's signature, expiry, audience, and scope are valid and that the `acr` is sufficient for the requested operation. The `sub` claim is the canonical binding to the banking customer; no other identifier the agent might supply (a phone number, a chat-platform user ID, an agent-side account) is accepted as proof of customer identity at this boundary.

### PSD2 SCA at Enrolment

[PSD2 RTS Article 10](https://eur-lex.europa.eu/legal-content/EN/TXT/?uri=CELEX:32018R0389) governs how often SCA must be repeated for account information access — the reuse window is in the order of months. Payment initiation requires SCA on each transaction unless an exemption applies (low-value, trusted-beneficiary, recurring, etc.). For the MCP server this maps cleanly to scope families:

- **`deposits:read` and other AIS-equivalent scopes.** SCA at grant time is sufficient. Refresh-token-driven token rotation within the reuse window does not require fresh SCA
- **`deposits:write`, `transfers:write` and other PIS-equivalent scopes.** SCA at grant time authorises the *grant*. Each irreversible operation invoked under the grant requires its own SCA via the URL-mode step-up below, or an explicit exemption the saga records

The `acr` claim on the access token is the structural signal: a value indicating SCA completion is a pre-condition for the saga to enter irreversible steps. A grant with a weaker `acr` — a single-factor session inherited from a long-lived browser cookie, say — is acceptable for read-only operations but rejected by the MCP server's write tools at request time, with a structured error directing the agent into a re-authorisation flow.

### Step-Up Authentication Mid-Session

The saga occasionally needs SCA evidence that was not present at grant time: the token's `acr` is too weak, `auth_time` is too far in the past, or the operation crosses an auto-approval threshold ([Document 05](./05-constitution-saga-walkthrough.md)).

The MCP server signals step-up via `elicitation/create` in URL mode (MCP 2025-11-25). The agent receives a one-time URL bound to the in-flight `process_id` and presents it to the user. The user navigates to that URL in a bank-controlled context — the bank's web app, the bank's mobile app via a deep link, a hardware-key signing flow — re-authenticates under SCA, and the bank reads the resulting confirmation directly. The saga transitions out of `AWAIT_USER_CONFIRMATION` from the bank's own signal.

Two structural points:

- **The agent never sees the SCA factors.** The OTP, the push-notification approval, the hardware-key signing — none of this passes through the MCP transport. The agent observes only the outcome
- **The step-up does not require a new OAuth grant.** It updates the saga's evidence of intent, not the OAuth session. The agent's existing access token continues to be valid for non-irreversible operations; the next irreversible operation may require its own step-up

This is the same control as Boundary 1's SCA enforcement for owned mobile apps, with one structural addition: the agent's absence from the confirmation context.

### Refresh and Rotating Refresh Tokens

Access tokens are short-lived. The bank picks a lifetime reflecting both the latency tolerance for revocation (below) and the regulatory reuse window for AIS scopes — 30 minutes is a reasonable default for write scopes, up to 60 minutes for read-only. When an access token expires, the agent presents the refresh token to obtain a new access token. The MCP server is unaffected by refresh; it sees only the new access token.

Two non-default decisions matter:

- **Rotating refresh tokens (OAuth 2.1).** Each use of a refresh token issues a new one and invalidates the previous. Detection of a re-use — the same refresh token presented twice — is a credential-theft signal and revokes the entire token family: the active access token, the refresh chain, the grant. The customer is forced to re-authorise
- **Refresh does not widen scope or freshen `acr`.** Refresh requires the same `resource` and a subset of the originally granted `scope`. The refreshed access token carries the original `acr` and `auth_time`. The agent cannot widen its grant via refresh, and SCA does not freshen across refresh — irreversible operations always require an explicit step-up

### Revocation and Cached Resource Handles

The customer can revoke an agent's access from the bank's web app or mobile app — a list of authorised agents with a "revoke" action per entry. Revocation is a standard [OAuth Token Revocation](https://datatracker.ietf.org/doc/html/rfc7009) call against the bank's authorisation server.

Propagation is bounded by the access token's lifetime:

- The refresh token is invalidated at the authorisation server. The next refresh attempt fails
- Active access tokens continue to be accepted by the MCP server until they expire. For high-risk operations, the MCP server SHOULD use [OAuth Token Introspection](https://datatracker.ietf.org/doc/html/rfc7662) to check token status on every call, accepting the latency cost in exchange for near-immediate revocation propagation

The bank cannot push revocation to the agent runtime — the agent must come back to the bank for the next refresh, and that attempt fails. A revoked agent may successfully complete one more operation in the worst case. The mitigation is access-token lifetime: shorter tokens make this window smaller at the cost of more frequent refresh traffic.

**Cached resource handles after revocation.** An agent that has used the MCP server retains structured outputs — `deposit_id`, `process_id`, resource URIs like `bank://clients/{client_id}/deposits/{deposit_id}`. None of these are secrets; all of them require a valid OAuth token to dereference. After revocation, every attempt to read a cached URI or poll a cached `process_id` fails at OAuth validation, before the MCP server touches the read model. The resource-handle pattern is intentionally stateless on the bank's side — there is no agent-specific session table whose entries need clearing on revocation. The same applies to `process_id` references retained from an earlier session: holding the identifier is not the same as holding the right to act on it.

### Compromised Agent Credentials

Two distinct compromise scenarios require distinct responses.

**A customer's OAuth grant to one agent is compromised.** The customer revokes that grant via the standard revocation flow above. If the bank detects the compromise first — anomalous tool-call patterns, geographic anomalies, velocity violations crossing the rate-limit threshold — it revokes proactively on the customer's behalf and notifies them out-of-band, using the same notification path used for async saga completion ([ADR-IC-011](./adrs/ADR-IC-011-async-saga-completion-notification.md)).

**An agent vendor's `client_id` is compromised system-wide.** The bank revokes the agent vendor's dynamic client registration. All access and refresh tokens issued to that `client_id` — across every customer who authorised that vendor — are invalidated. Every affected customer is forced to re-authorise, against either a new registration of the same vendor or a different vendor entirely. This is a heavy hammer; the bank's onboarding policy for DCR (rate limits, software statements, vendor attestation) should make it rarely necessary.

In both cases, the audit trail of operations the compromised credential performed remains in the event log and the saga state — revocation removes future capability, not past evidence. The reconciliation job ([Document 02](./02-anti-corruption-layer.md)) and the audit-log infrastructure (Principle 5 below) provide the forensic surface.

---

## Six Security Principles for This Architecture

These principles translate the nine boundaries into actionable constraints for engineers working on this system.

### Principle 1: Authenticate at Every Boundary, Authorize by Least Privilege

No service trusts another service by default. Every boundary is authenticated — mTLS for service-to-service calls, OAuth 2.0 client credentials for background workers (outbox publisher, reconciler, projectors), Kafka SASL/SCRAM for topic connections.

Every service has exactly the permissions it needs. The projector that updates `client_deposits` can read from the integration events topic and write to the read model table. It cannot read Core Banking credentials, cannot produce to other topics, and cannot reach the saga orchestrator.

The practical test: if this service's credentials were compromised, what is the blast radius? Design so the answer is: only this service's scope.

### Principle 2: Kafka Is a Shared Medium, Not a Trusted Bus

Every design choice in the event plane must assume that Kafka is not implicitly trusted. Topic ACLs are deployment configuration — they are defined in Terraform (or equivalent), reviewed in the same PR as the service that uses them, and enforced mechanically. They are not documentation. They are not social convention.

The schema registry is part of this principle: not everyone can register schemas or change compatibility modes. Schema registration is a deployment action performed by the producer's CI/CD pipeline, not by individuals. Compatibility mode changes (especially `NONE`) require elevated authorization and cannot be done ad hoc.

### Principle 3: Personal Data Belongs in the Customer Data Store, Not in Events

Events crossing a bounded context carry only the pseudonymous `client_id`. Name, NIF, contact details, account numbers — the things that would personally identify the client — live in the Customer Data Store and are fetched by consumers that need them.

This resolves the GDPR right-to-erasure tension structurally. It also limits the data minimization problem at boundary 4 (the fan-out): a consumer that does not need personal data simply does not call the Customer Data Store, and the event itself contains nothing sensitive beyond the `client_id`.

Account numbers in financial operation events (the IBAN in `DepositConstituted`) are the hardest case. Evaluate per consumer: if the consumer can obtain the account number from the Customer Data Store using the `client_id`, it should. If it genuinely needs the number in the event payload for timing or availability reasons, that is a design decision to document explicitly, not an assumption.

### Principle 4: The Observability Plane Is a Regulated Data Store

Design observability access with the same care as application data access. Classify span attributes: `process.state` is operational, `deposit.amount` is financial, `core.account` is financial + potentially personal. Apply RBAC accordingly.

Prefer pseudonymous identifiers in trace attributes where possible. Instead of `deposit.client_id = CLI-2026-007842`, a reference like a short hash that resolves in the Customer Data Store gives the same debugging utility without making the tracing backend a searchable personal data index.

Structured logs follow the same rule: `correlation_id` and `process_id` are operational identifiers; account numbers and client names are not log data at any level except a tightly access-controlled forensic log.

### Principle 5: Operations Console Actions Are Irreversible Financial Operations

Treat every action available in the operations console — retry, cancel, force compensation — as equivalent to a direct Core Banking call. Because that is what they ultimately become.

The authorization model for the console is not the same as the authorization model for the application. Operators need access in emergencies; that access must still be controlled, logged, and reviewed. The 4-eyes principle for amounts above a defined threshold is not bureaucracy — it is the same control that exists for every teller window in the bank.

Audit logs for console actions are not application logs. They are compliance records. They are retained under the same policies as financial transaction records.

### Principle 6: Compensations and Saga Commands Require Authorization

The saga orchestrator has the privilege to issue commands that move money. That privilege must be bounded by service identity, not by convention.

The ACL's command port is not a public endpoint. It is reachable only by the orchestrator's authenticated service identity. Commands arriving from other origins — even from within the same network — are rejected. This is enforced at the transport layer (mTLS) and optionally at the application layer (a JWT signed by the orchestrator's key).

Similarly, compensation commands (`ReverseCoreDebit`, `ReleaseBalanceReservation`) require that the saga state substantiates them. A compensation without a corresponding `COMPENSATE_*` state in a persisted saga record is a signal for immediate alert.

---

## Regulatory Obligations

For a Portuguese banking ecosystem, these are the specific regulatory frameworks that impose architectural constraints.

### PSD2 and Strong Customer Authentication

The [Payment Services Directive 2 (PSD2)](https://eur-lex.europa.eu/legal-content/EN/TXT/?uri=CELEX:32015L2366) requires SCA for electronic payment transactions and account access. A term deposit constitution involves a debit from a payment account — SCA applies.

**Architectural implication:** SCA is not just a UI step. It is a pre-condition for the saga to proceed to irreversible steps. The orchestrator must receive an SCA-confirmed signal before issuing `ConfirmDebit`. A failed or timed-out SCA triggers the compensation path, not a technical error.

### GDPR

The [General Data Protection Regulation](https://eur-lex.europa.eu/legal-content/EN/TXT/?uri=CELEX:32016R0679) imposes:
- **[Right to erasure (Article 17)](https://gdpr-info.eu/art-17-gdpr/):** Resolved by the Customer Data Store pattern — see Principle 3 and [Document 09](./09-long-term-schema-evolution.md).
- **[Data minimization (Article 5)](https://gdpr-info.eu/art-5-gdpr/):** Events should carry only what consumers need. Fat events carrying everything "just in case" are a GDPR risk.
- **Data subject access requests:** The Customer Data Store is the single point for DSAR responses. The event log contributes only pseudonymous records.
- **Data residency:** Kafka clusters, event archives, and the observability backend must operate within the EU. This constrains cloud region choices.
- **Retention with legal basis:** Kafka retention (90 days) and event archive (indefinite) need documented legal bases. In banking, regulatory obligations (BdP, AML) typically provide that basis for financial operation records. Marketing data does not have the same basis.

### Banco de Portugal Supervision and FGD

[Banco de Portugal (BdP)](https://www.bportugal.pt/) supervision requires a tamper-evident audit trail of all deposit operations, including compensations. The causation chain (Primitive 4 from [Document 01](./01-the-six-primitives.md)) and the saga state aggregate are the technical implementation of this requirement. They must be append-only and access-controlled.

[FGD (Fundo de Garantia de Depósitos)](https://www.fgd.pt/) reporting depends on accurate aggregate positions. The read models ([Document 03](./03-cqrs-and-read-models.md)) that feed reporting must be integrity-checked — periodic reconciliation between the read model and the write aggregate is a supervisory requirement, not just an engineering preference.

### DORA (Digital Operational Resilience Act)

[DORA](https://eur-lex.europa.eu/legal-content/EN/TXT/?uri=CELEX:32022R2554) requires documented incident response procedures, operational resilience testing (including simulated failures), and third-party risk management for critical ICT providers.

Core Banking is the critical third-party provider in this architecture. The ACL's indeterminate-state handling ([Document 02](./02-anti-corruption-layer.md)) and the reconciliation job are the operational resilience controls for that dependency. They must be tested, not assumed. The game days recommended in [Document 06](./06-observability-and-tracing.md) are DORA-relevant activities.

---

## Where Each Principle Manifests

| Document | Security content |
|---|---|
| [01 — Primitives](./01-the-six-primitives.md) | Idempotency key scoping; identity trio as the audit trail foundation |
| [02 — ACL](./02-anti-corruption-layer.md) | ACL authentication to Core; reconciliation job authorization |
| [03 — CQRS](./03-cqrs-and-read-models.md) | Read model access authorization; reporting integrity |
| [04 — Plumbing](./04-plumbing-patterns.md) | Kafka topic ACLs; schema registry authorization; outbox data classification |
| [05 — Saga Walkthrough](./05-constitution-saga-walkthrough.md) | PSD2/SCA as saga pre-condition; command authorization; SSE endpoint; ops console |
| [06 — Observability](./06-observability-and-tracing.md) | Observability RBAC; PII in trace attributes; audit logs |
| [07 — Testing](./07-testing-strategy.md) | Security testing; GDPR erasure verification; injection tests |
| [08 — Governance](./08-event-catalog-governance.md) | Security checklist in RFC process; consumer authorization tracking |
| [09 — Schema Evolution](./09-long-term-schema-evolution.md) | GDPR right-to-erasure vs. immutability; pseudonymization strategy |
| [11 — Chat Agent Channel](./11-chat-agent-channel-strategy.md) | The MCP-server boundary as the channel's ACL; OAuth 2.1 with RFC 8707 audience binding; URL-mode confirmation removing the agent from the irreversible-action path |
