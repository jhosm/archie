# ADR-011: Async Saga Completion Notification — Out-of-Band Callback Wire Format

| Field | Value |
|---|---|
| Status | Accepted |
| Date | 2026-05-17 |
| Deciders | jhosm |
| Common criteria | [ADR-000](./ADR-000-common-evaluation-criteria.md) |
| Depends on | [ADR-001](./ADR-001-event-backbone-message-broker.md), [ADR-003](./ADR-003-saga-orchestrator.md), [ADR-006](./ADR-006-edge-api-gateway.md) |

---

## Context

[Document 05](../05-constitution-saga-walkthrough.md) and [ADR-006](./ADR-006-edge-api-gateway.md) establish the synchronous edge pattern: a `202 Accepted` with a `stream_url` pointing to an SSE endpoint where the client subscribes for saga progress. That model presumes a connected, attentive client — a browser or mobile app with an open HTTP connection that stays open for the duration of the saga.

[Document 11](../11-chat-agent-channel-strategy.md) breaks that presupposition in two places:

1. **MCP sessions do not stay open.** An LLM agent initiating a `constitute_deposit` tool call may have no session at all by the time the saga completes — the MCP session may end between the `202 Accepted` and the saga's terminal state. For `AWAIT_WORKFLOW_APPROVAL` sagas that wait hours or days on a human approver, this is not an edge case; it is the normal case.

2. **SSE is not the only async channel.** Owned mobile and web clients can hold SSE connections for seconds to minutes. For sagas that take days, and for clients the bank does not own (agents, partner integrations), a push notification to an out-of-band channel is the only viable model.

Document 11 describes the *shape* of Pattern 3 (out-of-band callback): the initiating request includes a reference to a notification preference registered with the bank; when the saga reaches a terminal state, the bank's notification service delivers to that channel. Document 11 explicitly defers the wire format to this ADR.

### What this ADR decides

Six decisions, taken in logical dependency order:

| # | Decision | Options evaluated |
|---|---|---|
| D1 | **How the callback endpoint is registered** | Free-form URL in request body; pre-registered subscription endpoint |
| D2 | **Delivery guarantees** | At-most-once; at-least-once with stable idempotency key |
| D3 | **Authenticity and integrity scheme** | No signature; HMAC-SHA256 with timestamp |
| D4 | **Retry and dead-letter policy** | Fixed interval; exponential backoff with jitter; no retry |
| D5 | **Owning component for delivery** | Orchestrator directly; dedicated notification service |
| D6 | **Relationship to the existing SSE path** | SSE deprecated in favour of callbacks; the two paths coexist |

These decisions are not independent: D1 (pre-registration) enables D3 (HMAC shared secret established at registration); D5 (dedicated notification service) determines where D4 (retry logic) is implemented; D6 (coexistence) determines what the 202 response looks like. The decisions are evaluated in order because each one constrains the next.

### Scope boundary

This ADR covers the **webhook callback path** — the bank calling a pre-registered HTTP endpoint when a saga reaches a terminal state. It does not cover:

- **SMS / push notifications / email** — those channels are downstream of the notification service and may be added independently; they do not affect the callback wire format decided here.
- **MCP tasks (Pattern 1) and polling (Pattern 2)** from document 11 — both are covered by the MCP server design and do not require changes to the event backbone or the Deposits API.
- **The `get_process_status` polling tool** — that is an MCP server implementation concern.

---

## Evaluation

### D1 — Callback endpoint registration

**Option A: Free-form URL in the request body.** The initiating `POST /deposits/constitute` request includes a `callback_url` field containing the full URL to deliver to. Simpler — no prior registration step — but carries a material risk: it turns the notification service into an **SSRF (Server-Side Request Forgery) oracle**. The notification service would make authenticated HTTP calls to URLs controlled by the caller, which could target internal services, cloud metadata endpoints, or other external systems. This risk is not theoretical; SSRF via webhook URLs is a documented class of production incidents. No mitigation short of a comprehensive allowlist closes the gap — and a comprehensive allowlist is equivalent to pre-registration.

**Option B: Pre-registered subscription.** The client registers callback endpoint(s) in advance via a dedicated subscription endpoint. The initiating request references a `notification_subscription_id`. The notification service delivers only to pre-registered, validated URLs. Document 11 already commits to this shape: "a notification preference registered with the bank, not a free-form URL the agent supplies." Option B is the constraint doc 11 established.

**Chosen: Option B — pre-registered subscription endpoint.**

The SSRF risk from Option A is sufficient to disqualify it. The pre-registration model also enables the HMAC shared secret (D3) to be established at registration time and held server-side, not transmitted in every request.

**Registration endpoint:** `POST /api/v1/notification-subscriptions` (authenticated, requires `notifications:write` OAuth scope). Returns `{subscription_id, secret}`. The `secret` is shown once; if lost, the subscription must be deleted and recreated. Fields:

| Field | Description |
|---|---|
| `endpoint_url` | HTTPS only; must resolve to a public IP (RFC 1918, loopback, and link-local addresses are rejected at registration time; redirects are not followed at delivery time) |
| `saga_types` | Array of saga types to notify on (`["ConstitutionProcess", "MobilizationProcess"]`) or `["*"]` for all |
| `events` | Array of terminal events to notify on (`["COMPLETED", "CANCELLED"]`) or `["*"]` |

---

### D2 — Delivery guarantees

**At-most-once:** simpler — deliver once, no retry, no tracking. Appropriate for low-stakes notifications (marketing, informational). Wrong for saga completion in a banking context: a missed delivery means the client's user never learns their deposit was cancelled, and may never think to poll.

**At-least-once with stable idempotency key:** deliver until confirmed, track delivery state, accept that the receiver may see duplicate deliveries and must handle them idempotently. The idempotency key is derived deterministically from `process_id` + `terminal_event_type` — stable across retries, unique per saga terminal event. The receiver uses this key to deduplicate.

**Chosen: At-least-once with stable idempotency key.**

Banking outcomes are load-bearing: a "deposit constitution cancelled" notification that never arrives is a support call, a regulatory trace gap, and a trust event. At-least-once is the only acceptable guarantee. The receiver's idempotency obligation is the same as for any other consumer of this event backbone — Primitive 5 from [document 01](../01-the-six-primitives.md) applies to the callback receiver exactly as it applies to Redpanda consumers.

Every callback payload carries:

| Field | Value |
|---|---|
| `idempotency_key` | `sha256("{process_id}:{terminal_event_type}")`, hex-encoded, stable across retries |
| `delivery_attempt` | Integer starting at 1; increments on each retry |
| `event_id` | UUID unique to this delivery attempt (for receiver logging) |

---

### D3 — Authenticity and integrity scheme

**No signature:** rely on HTTPS for transport security. The receiver has no way to verify the payload came from the bank — any party that knows the receiver's URL can forge a delivery. In a banking context, a forged "your deposit was approved" notification is a fraud vector.

**HMAC-SHA256 with timestamp:** the notification service signs the raw request body with the subscription's HMAC secret, using a short delivery timestamp to prevent replay attacks. Industry standard: GitHub, Stripe, Shopify, Twilio all use this pattern. The receiver independently computes the signature using the shared secret and rejects the delivery if it does not match.

**Chosen: HMAC-SHA256 with timestamp in signature scope.**

The delivery envelope carries:

| Header | Value |
|---|---|
| `X-Webhook-Signature` | `sha256=<hex-encoded HMAC-SHA256 of timestamp + "." + raw body>` |
| `X-Webhook-Timestamp` | Unix epoch seconds (integer string) |
| `X-Webhook-Subscription-Id` | The `subscription_id` the delivery is for |
| `Content-Type` | `application/json` |

The HMAC input is: `"{timestamp}.{raw_body}"` (UTF-8 bytes). The receiver:

1. Rejects if `X-Webhook-Timestamp` is more than 5 minutes old (replay prevention).
2. Computes `HMAC-SHA256(secret, "{timestamp}.{raw_body}")`.
3. Compares with `X-Webhook-Signature` using a constant-time comparison function.
4. Returns `200` only if the signature matches; returns `401` if it does not.

The 5-minute replay window is the same as Stripe's recommended tolerance. The constant-time comparison prevents timing-oracle attacks on the secret.

---

### D4 — Retry and dead-letter policy

**No retry:** at-most-once; conflicts with D2.

**Fixed interval:** simple, but hammers a slow or overwhelmed receiver at a constant rate. Does not respect `Retry-After` headers. Does not give the receiver time to recover from an outage before the next attempt.

**Exponential backoff with jitter:** each failed delivery doubles the wait before the next attempt, with jitter to prevent thundering herd when multiple subscriptions share a recovering receiver. Standard practice for outbound HTTP with reliability guarantees.

**Chosen: Exponential backoff with jitter; dead-letter on exhaustion.**

Delivery schedule (from first failed delivery):

| Attempt | Delay (before jitter) | Jitter |
|---|---|---|
| 2 | 30 seconds | ±25% |
| 3 | 2 minutes | ±25% |
| 4 | 8 minutes | ±25% |
| 5 | 30 minutes | ±25% |
| 6–10 | 2 hours | ±25% |

After 10 failed attempts (approximately 12 hours of retry), the delivery is abandoned: a `NotificationDeliveryExhausted` record is written to the notification service's database and a corresponding event is published to the Redpanda backbone. The saga itself is unaffected — it is already terminal. The client can always call `get_process_status` (Pattern 2, document 11) to retrieve the structured outcome.

HTTP status handling:

| Receiver response | Action |
|---|---|
| `2xx` | Delivery confirmed; no retry |
| `429` with `Retry-After` | Wait the `Retry-After` duration, then resume the backoff schedule |
| `4xx` (except 429) | Delivery abandoned immediately; subscription flagged as misconfigured; human-review event published |
| `5xx` or timeout | Retry per backoff schedule |

A `4xx` (client error) from the receiver means the URL is misconfigured — retrying would not fix it. The subscription is suspended; the subscriber must re-register or update the endpoint. A `5xx` means the receiver is temporarily unavailable — retry is appropriate.

---

### D5 — Owning component for delivery

**Option A: Orchestrator delivers directly.** When the saga state machine (ADR-003) transitions to a terminal state, it posts the callback in-process — before or after committing the final state transition. This is simpler (no additional service) but couples the orchestrator to the notification contract, the retry scheduler, the dead-letter store, and the HMAC signing logic. If the delivery attempt blocks or fails, it affects the orchestrator's processing throughput. If the callback logic has a bug, it can stall sagas.

**Option B: Dedicated notification service.** The orchestrator emits a terminal domain event to the Redpanda backbone (e.g., `SagaTerminated` — which it already emits as part of its event stream). A dedicated notification service subscribes to those events and handles delivery, retry, dead-letter, and subscription management. The orchestrator is unaware of the notification service.

**Chosen: Option B — dedicated notification service.**

The orchestrator's job is saga state management. Retry scheduling, HMAC signing, dead-lettering, subscription management, and outbound HTTP with backoff are a distinct operational concern. Coupling them to the orchestrator introduces failure modes in one that affect the other: a stuck delivery queue delays saga processing; a bug in the HMAC signing code affects all sagas, not just those with callbacks.

The choreography pattern — the orchestrator emits, subscribers react — is already the architecture's default for side effects. Notification delivery is a side effect of saga completion; the notification service is the appropriate subscriber. No new event types are required: the notification service subscribes to the existing `SagaTerminated` event (or equivalent terminal events from each saga type) and correlates via `process_id` to the subscription store.

This also means a future addition (SMS, push notification, email) requires adding a new subscriber to the same events — not modifying the orchestrator or the callback delivery path.

**Chosen: dedicated notification service, subscribed to saga terminal events via Redpanda.**

---

### D6 — Relationship to the existing SSE path

**Option A: Deprecate SSE in favour of callbacks.** One completion mechanism simplifies the surface but breaks browser and mobile clients that depend on real-time saga progress updates — clients that CAN maintain a connection and BENEFIT from the sub-second latency of SSE. Replacing SSE with polling + callback for these clients degrades the user experience without any technical benefit to the client.

**Option B: Coexistence — bifurcated by notification preference.** SSE remains the completion mechanism for connected clients. Callbacks are an opt-in layer for clients that register a notification subscription and reference it in the initiating request. The `202 Accepted` response evolves to signal both paths.

**Chosen: Option B — coexistence.**

SSE and callbacks solve different problems for different clients. SSE is correct for a browser that stays open for 700ms to 30 seconds. Callbacks are correct for an MCP agent session that may end before the saga completes. There is no scenario in which one mechanism is universally superior — the right model depends on the client's connectivity model, which the bank should not dictate.

The bifurcation is client-controlled: if the initiating request includes a `notification_subscription_id`, the Deposits API stores it with the process record and the response includes a `notification` field. If absent, the response is unchanged from the current `{deposit_id, process_id, status, stream_url}` shape.

A client that wants both SSE (for in-flight progress) and a callback (as a safety net for long-running stages) can have both: it opens the SSE stream after receiving the `stream_url` and relies on the callback if the stream drops before the saga completes.

---

## Decision

Summary of all six choices:

| Decision | Chosen |
|---|---|
| D1 — Callback registration | Pre-registered subscription endpoint (`POST /api/v1/notification-subscriptions`); free-form URL in request body rejected for SSRF risk |
| D2 — Delivery guarantees | At-least-once; stable `idempotency_key = sha256("{process_id}:{terminal_event_type}")` on every payload |
| D3 — Authenticity scheme | HMAC-SHA256 over `"{timestamp}.{raw_body}"`; timestamp in `X-Webhook-Timestamp`; 5-minute replay window; constant-time signature comparison |
| D4 — Retry policy | Exponential backoff with ±25% jitter; 10 attempts over ~12 hours; dead-letter on exhaustion; `4xx` suspends subscription immediately |
| D5 — Owning component | Dedicated notification service; subscribes to saga terminal events via Redpanda; orchestrator is unaware |
| D6 — SSE coexistence | SSE retained as-is; callbacks are opt-in via `notification_subscription_id` in the initiating request; the two paths are independently useful |

---

## Consequences

**What this choice makes easier:**

- The notification service is a pure choreography consumer — it subscribes to events the orchestrator already emits. Adding a new saga type that needs callbacks requires no orchestrator change; the notification service subscribes to its terminal events.
- Callback registration's SSRF mitigations (HTTPS-only, public IP validation, no redirect following) are enforced at subscription creation time, not at delivery time. The registration endpoint is the single security gate; the delivery path is straightforward.
- The HMAC signature scheme is stateless at verification time — the receiver only needs the shared secret and the raw body. No server-to-server handshake is needed at delivery time. This makes the receiver implementation simple: compute, compare, accept or reject.
- SSE and callbacks coexisting means the bank's product surface does not regress for owned clients while gaining the agent-channel capability.

**What this choice makes harder or impossible:**

- **No free-form webhook URL.** Partners or agents that want a bespoke callback URL must register it through the subscription endpoint first. For quickly prototyped integrations this adds a step; it is a deliberate friction that prevents SSRF.
- **Receiver must be idempotent.** At-least-once means the receiver will occasionally see duplicate deliveries — guaranteed on retry after a timeout where the receiver processed the first delivery but the acknowledgement was lost. Receivers that are not idempotent will double-process terminal events. The `idempotency_key` is the contract; receiver correctness is the receiver's responsibility.
- **Notification service is a new operational component.** It requires a database (subscription store + delivery log + dead-letter records), a Redpanda consumer loop, an outbound HTTP client with retry scheduler, and its own observability configuration (ADR-007 P2 span naming: `notification.delivery.attempt`, `notification.delivery.confirmed`, `notification.delivery.exhausted`). This is appropriate operational overhead for the capability — it is not zero-overhead.
- **Dead-lettered deliveries require human review.** When a subscription is suspended (after `4xx`) or a delivery is exhausted (after 10 retries), an operator or the subscriber must intervene. This is the correct behaviour — silent swallowing of failed deliveries would be worse — but it adds an operational surface to monitor.

**Residual risks:**

- **HMAC secret exposure.** The `secret` is returned once at subscription creation and never again. A subscriber that loses it must delete and recreate the subscription. If the secret is accidentally committed to version control or logged, it must be treated as compromised and the subscription recreated immediately. The notification service must never log the secret at any level.
- **Clock skew at the receiver.** The 5-minute replay window assumes the receiver's clock is reasonably synchronised (NTP). A receiver with a severely drifted clock will reject valid deliveries. Document this in the integration guide: receivers must have NTP-synchronised clocks; a ±30-second drift is acceptable, a ±10-minute drift is not.
- **Subscription sprawl.** A single `client_id` can register multiple subscriptions (different saga types, different environments, historical subscriptions that were never deleted). The subscription endpoint must enforce a reasonable per-client limit (e.g., 20 active subscriptions per `client_id`) to prevent operational abuse. Inactive subscriptions (no successful delivery in 90 days) are candidates for automated suspension with a prior warning event.

---

## Implementation Principles

### P1 — Subscription lifecycle and SSRF mitigations

The `POST /api/v1/notification-subscriptions` endpoint (authenticated, `notifications:write` scope) validates `endpoint_url` before creating the subscription:

1. URL scheme must be `https`. HTTP URLs are rejected with `400 Bad Request`.
2. The hostname must resolve to a public IPv4 or IPv6 address. RFC 1918, loopback (`127.0.0.0/8`, `::1`), and link-local (`169.254.0.0/16`, `fe80::/10`) addresses are rejected with `422 Unprocessable Entity`.
3. A DNS lookup is performed at registration time and cached. The notification service does **not** re-resolve DNS at delivery time — it delivers to the IP recorded at registration. This prevents a DNS rebinding attack where a hostname resolves to a public IP at registration and to an internal IP at delivery.
4. Redirects are not followed at delivery time. The `endpoint_url` must be the final HTTPS endpoint.

On successful validation, the response includes `{subscription_id, secret}`. The `secret` is a 32-byte random value (256-bit CSPRNG), hex-encoded, shown once, stored hashed (bcrypt or Argon2id) in the notification service's database.

---

### P2 — Delivery envelope

Every callback is an HTTP POST to the registered `endpoint_url` with the following envelope:

```
POST {endpoint_url}
Content-Type: application/json
X-Webhook-Signature: sha256={HMAC-SHA256(secret, "{timestamp}.{raw_body}")}
X-Webhook-Timestamp: {unix_epoch_seconds}
X-Webhook-Subscription-Id: {subscription_id}
X-Webhook-Delivery-Id: {delivery_attempt_uuid}

{
  "idempotency_key": "{sha256(process_id + ':' + terminal_event_type)}",
  "delivery_attempt": 1,
  "event_id": "{delivery_attempt_uuid}",
  "occurred_at": "{ISO 8601 timestamp of saga terminal transition}",
  "saga_type": "ConstitutionProcess",
  "terminal_event": "COMPLETED",
  "process_id": "PROC-2026-00098765",
  "client_id": "CLI-2026-007842",
  "outcome": {
    "deposit_id": "DEP-2026-00012345",
    "status": "ACTIVE",
    "constituted_at": "2026-05-17T14:32:18.450Z"
  }
}
```

The `outcome` field is populated from the CQRS read model for the process (ADR-005 / ADR-003) — the same structured data that `get_process_status` returns. The receiver can reconstruct full saga detail by calling `get_process_status` if the callback payload is insufficient.

The `terminal_event` field carries the saga's terminal state name (`COMPLETED`, `CANCELLED`, `HUMAN_INTERVENTION_REQUIRED`) — not a derived summary. The receiver makes its own interpretation.

---

### P3 — Notification service as a choreography consumer

The notification service subscribes to the Redpanda topic carrying saga terminal events. The topic naming follows the convention from [document 04](../04-plumbing-patterns.md): `deposits.integration.events` (or the equivalent per bounded context). The notification service filters on event type — consuming only events whose `event_type` field matches a terminal transition for a saga type with at least one registered subscription.

For each consumed terminal event:

1. Look up active subscriptions for `(client_id, saga_type, terminal_event)`.
2. For each matching subscription: create a delivery record (status = PENDING) in the notification service's local store.
3. Commit the Redpanda offset only after all delivery records are written — the outbox pattern applies here, preventing a crash between "consume event" and "create delivery record" from losing a notification obligation.
4. A delivery worker picks up PENDING records and attempts HTTP delivery.
5. On confirmed delivery (`2xx`): update record to DELIVERED.
6. On transient failure: update record to RETRY, schedule next attempt per D4.
7. On permanent failure (`4xx` non-429, or 10 retries exhausted): update record to DEAD_LETTERED; publish `NotificationDeliveryExhausted` to the backbone.

The notification service uses its own PostgreSQL table for the delivery store — consistent with ADR-005's choice of PostgreSQL as the single relational store.

---

### P4 — The 202 response amendment

The `202 Accepted` response from `POST /api/v1/deposits/constitute` gains an optional `notification` field when the request included a `notification_subscription_id`:

```json
{
  "deposit_id": "DEP-2026-00012345",
  "process_id": "PROC-2026-00098765",
  "status": "PROCESSING",
  "stream_url": "/api/v1/processes/PROC-2026-00098765/stream",
  "notification": {
    "subscription_id": "sub-8f3a2b1c",
    "will_notify_on": ["COMPLETED", "CANCELLED", "HUMAN_INTERVENTION_REQUIRED"]
  }
}
```

The `stream_url` is always present — the client may choose to open it or not. If the request included a `notification_subscription_id` that does not belong to the authenticated `client_id`, the Deposits API returns `403 Forbidden` — the subscription must be owned by the requesting client.

If the `notification_subscription_id` references a suspended subscription (flagged by a prior `4xx`), the Deposits API returns `422 Unprocessable Entity` with a machine-readable error body indicating that the subscription is suspended and must be re-registered. This is preferable to silently accepting the request and failing to deliver — the caller learns immediately that their notification channel is broken.

For MCP tool calls, the `structuredContent` in the `constitute_deposit` result carries the `notification` field alongside `process_id` and the `follow_up` poll hint — both notification paths are visible to the agent so it can relay whichever is appropriate to the user.
