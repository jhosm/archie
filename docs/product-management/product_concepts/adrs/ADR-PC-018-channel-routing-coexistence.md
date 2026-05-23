# ADR-PC-018: Channel Routing for State-Changing Operations During Coexistence

| Field | Value |
|---|---|
| Status | Accepted |
| Date | 2026-05-23 |
| Shape | Contract-shape |
| Counterparty | The operating bank's channel tier and edge API gateway ([ADR-IC-006](../../integration_concepts/adrs/ADR-IC-006-edge-api-gateway.md)) |

---

## Context

During coexistence, a state-changing operation — open a term deposit, settle interest, terminate early, withdraw partially, change the auto-renewal policy — lands either on the new engine (term deposits, from v1) or on the legacy core (current accounts, v1–v3). Each request needs a routing decision: which system holds the system-of-record for this instance, and therefore which backend gets the command. Routing to the wrong backend is the split-brain failure mode the [coexistence §3](../feature-design-strangler-fig-coexistence.md) SoR map exists to prevent.

[Coexistence §6.4](../feature-design-strangler-fig-coexistence.md) names three credible locations for the routing logic, none obviously correct:

| Location | Mechanism | Tradeoff |
|---|---|---|
| **Channel** | Each channel reads `sor` and dispatches | Routing rule enforced in N places; channels diverge over time |
| **Unified API gateway** | One API in front of channels resolves `sor` and dispatches | Centralises the rule; adds a hop and an operational dependency |
| **Read model** | The projection exposes a command endpoint beside the row | Mixes CQRS sides; pragmatic but architecturally noisy |

The decision is [Q-AI](../04-open-questions.md), and its defining constraint is that **the engine should fit the operating bank's existing channel architecture, not invent a new pattern** — the bank's channel tier "probably already has an opinion about this" ([coexistence §6.4](../feature-design-strangler-fig-coexistence.md)). This is a contract-shape ADR ([ADR-PC-000 D3](./ADR-PC-000-namespace-and-contract-shape-framework.md)): the deliverable is the contract between the engine and the channel/gateway tier — what routing data the engine exposes and what it refuses to own — not a tool. The six slots are adapted for a synchronous routing/API contract rather than an event payload.

---

## Decision

The engine's commitment splits cleanly into what it **owns** and what it **defers**.

**The engine owns the routing *data*, not the routing *logic*.** It exposes `sor` per instance as a first-class column on the unified read surface ([coexistence §6.2](../feature-design-strangler-fig-coexistence.md)), and it makes a single negative commitment about its own surfaces: it will **not** staple a command endpoint onto its read projection. That rejects [§6.4](../feature-design-strangler-fig-coexistence.md) option (c) *for the engine*, preserving the CQRS read/write separation that [ADR-IC-005](../../integration_concepts/adrs/ADR-IC-005-cqrs-read-model-storage.md) commits to — the read model is a query surface, not a command router.

**Recommended placement: the unified API gateway** ([§6.4](../feature-design-strangler-fig-coexistence.md) option b), reusing the edge API gateway already decided in [ADR-IC-006](../../integration_concepts/adrs/ADR-IC-006-edge-api-gateway.md) (Kong Gateway CE). The §6.4 cost of option (b) — "a new system to build and operate" — is largely already paid: the bank runs the ADR-IC-006 gateway as its security boundary, rate-limiter, and PSD2 SCA enforcement point. Adding SoR-based routing is a configuration concern on an existing tier, not a new component. Option (a), routing in each channel, is rejected as the recommendation because it duplicates the rule across N channels that diverge over time; the gateway tier enforces it once.

**Binding authority: the channel team's existing architecture.** Because [Q-AI](../04-open-questions.md) is explicit that the engine must conform rather than impose, the gateway recommendation is a *position*, not a hard requirement on the bank. If the bank's channels already route per-product-family at their own tier, the engine fits into that. The engine's *hard* commitments are only the two above (expose `sor`; do not embed routing in the engine's command or read surfaces).

### 1 · Payload shape (routing inputs)

The unified read surface exposes, per instance ([coexistence §6.2](../feature-design-strangler-fig-coexistence.md) projection sketch): `instance_id`, `product_family`, `sor ∈ {engine, legacy}`, `as_of`, `source`. A state-changing request carries `instance_id` and the operation; the router resolves `instance_id → sor → backend` (engine API vs legacy API). For a **new constitution** (no instance exists yet), routing is by `product_family` plus cutover state: per the [coexistence §3.1](../feature-design-strangler-fig-coexistence.md) date-based rule, every term deposit constituted on or after cutover is engine-SoR, so all new term-deposit constitutions route to the engine without an instance lookup.

### 2 · Semantics

`sor` is the single source of routing truth — the materialisation of the [coexistence §3](../feature-design-strangler-fig-coexistence.md) SoR map's invariant that every instance is owned by exactly one system. It is set to `engine` at constitution and **never changes** ([coexistence §3.3](../feature-design-strangler-fig-coexistence.md)); a legacy instance that migrates by renewal does not flip its `sor` — a *new* engine instance is created and the legacy instance matures ([coexistence §9](../feature-design-strangler-fig-coexistence.md)). The router **reads** `sor`; it never **decides** `sor`. This separation is what keeps routing a pure lookup rather than a second place where ownership is computed.

### 3 · Ordering and delivery guarantees

Routing is a **synchronous, per-request decision** in the request path — not asynchronous, no queue. The one ordering hazard is read-your-writes: immediately after a constitution on the engine, the new `sor` row must be visible before a follow-up state-changing operation on the same instance routes. Engine-sourced rows are fresh to within seconds ([coexistence §6.2](../feature-design-strangler-fig-coexistence.md)), so back-to-back channel calls are safe in practice; for the residual brand-new-instance window, the gateway falls back to the `product_family` rule (slot 1) — every new term deposit is engine-SoR regardless of projection lag, so the fallback cannot misroute a v1 deposit.

### 4 · Idempotency

Routing itself is a pure, side-effect-free function of `(instance_id → sor)` or `(product_family, constituted_at → sor)` and is trivially idempotent — re-evaluating it yields the same backend. The router adds **no** dedupe of its own; the idempotency of the routed *operation* is the backend's concern — the engine's saga and event store on the engine side, the [ADR-PC-016](./ADR-PC-016-legacy-current-account-adapter.md) ACL idempotency on the legacy side. Keeping the router stateless is deliberate: a router that maintained its own idempotency state would be a third place state could diverge.

### 5 · Error model

**Fail closed.** If `sor` is unresolvable — the instance is unknown to the read model (a legacy instance not yet observed via the [ADR-PC-017](./ADR-PC-017-legacy-batch-ingest-contract.md) batch file, or projection lag) — the router **refuses** the operation and surfaces "instance state unavailable" rather than guessing a backend. Guessing risks routing a command to the system that does not hold the SoR, which is the split-brain outcome the whole design prevents. For legacy-SoR instances, the [coexistence §6.3](../feature-design-strangler-fig-coexistence.md) staleness caveat applies: a branch teller authorising certain operations against a 24-hour-stale legacy-sourced row must trigger an explicit refresh-from-legacy first. Gated, not post-flagged.

### 6 · Ownership and versioning

The **engine owns the `sor` data contract** — the read-surface column, its enum, and its semantics. The **channel/integration team owns where routing logic physically lives** (channel, gateway, or read model) and which existing architecture it conforms to. The engine constrains that choice in exactly one way: it will not host the routing logic inside its own command path or read projection ([§6.4](../feature-design-strangler-fig-coexistence.md) option c rejected for the engine). **Versioning:** the only foreseeable change is adding a new `sor` value — a third owning system in a future family migration — which is an additive change to the enum and the routing table, backward-compatible for existing `engine`/`legacy` routing. The read-surface contract evolves under the same [ADR-IC-005](../../integration_concepts/adrs/ADR-IC-005-cqrs-read-model-storage.md) projection governance as the rest of the unified read model.

---

## Consequences

**What this makes easier:**

- Exposing `sor` as a first-class property ([coexistence §6.4](../feature-design-strangler-fig-coexistence.md)) lets **any** of the three routing locations work — the engine does not force the bank's hand, which is precisely what [Q-AI](../04-open-questions.md) asks for.
- Recommending the [ADR-IC-006](../../integration_concepts/adrs/ADR-IC-006-edge-api-gateway.md) gateway tier avoids standing up a new system; SoR routing becomes Kong configuration alongside auth, rate-limiting, and SCA.
- Refusing a read-model command endpoint keeps CQRS clean and the read model honest as a pure query surface ([ADR-IC-005](../../integration_concepts/adrs/ADR-IC-005-cqrs-read-model-storage.md)).
- New-constitution routing is the trivial [coexistence §3.1](../feature-design-strangler-fig-coexistence.md) date rule — no per-instance lookup, no lag exposure.

**What this makes harder or locks in:**

- The engine cannot unilaterally guarantee correct routing — it depends on the channel/gateway tier reading `sor` and on the read model being current. The engine's contribution is bounded to *exposing* the data correctly and *failing closed* when it cannot.
- Fail-closed means a projection-lag or unobserved-legacy-instance window produces a *refusal* a customer can see, rather than a silent best-effort route. This is the right trade against split-brain, but it surfaces as occasional "try again shortly" rather than a guess.

---

## Residual risks

- **The channel-team review is the production gate** ([Q-AI](../04-open-questions.md)). This ADR is **Accepted** on the engine's side of the contract (expose `sor`; do not embed routing logic) and on the *recommendation* of gateway-tier placement. The binding placement decision belongs to the operating bank's channel architecture; if it already routes per-product-family elsewhere, the engine conforms. Same posture as the other coexistence ADRs whose final shape depends on an operator input: committed in architecture, confirmed before production.
- **The engine cannot prevent the bank from choosing [§6.4](../feature-design-strangler-fig-coexistence.md) option (c)** channel-side (a command endpoint beside the read model). The engine rejects it for *its own* surfaces; if the bank builds an equivalent in its own tier, that is the bank's CQRS-coupling risk to own, and this ADR names it rather than forbidding it.
- **Per-channel staleness tolerance** ([Q-AF](../04-open-questions.md), [coexistence §6.3](../feature-design-strangler-fig-coexistence.md)) interacts with routing: which channels may act on a 24-hour-stale legacy-sourced row, and where per-channel refresh paths back to legacy are needed, is a channel-by-channel review this ADR depends on but does not resolve.
- **What this contract does not commit to:** the channels' own request/response shapes, the legacy core's state-changing API surface (absorbed by the [ADR-PC-016](./ADR-PC-016-legacy-current-account-adapter.md) ACL), or authentication and SCA enforcement (owned by [ADR-IC-006](../../integration_concepts/adrs/ADR-IC-006-edge-api-gateway.md)).
