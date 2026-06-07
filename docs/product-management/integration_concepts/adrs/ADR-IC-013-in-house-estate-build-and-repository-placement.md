# ADR-IC-013: In-House Estate — Build Provenance and Repository Placement

| Field | Value |
|---|---|
| Status | Accepted |
| Date | 2026-05-24 |
| Deciders | jhosm |
| Common criteria | [ADR-IC-000](./ADR-IC-000-common-evaluation-criteria.md) |
| Depends on | [ADR-IC-003](./ADR-IC-003-saga-orchestrator.md), [ADR-IC-004](./ADR-IC-004-outbox-pattern-mechanism.md), [ADR-IC-010](./ADR-IC-010-mcp-server-runtime-and-sdk.md), [ADR-IC-011](./ADR-IC-011-async-saga-completion-notification.md), [ADR-IC-012](./ADR-IC-012-anti-corruption-layer-implementation.md) (the in-house components this classifies), [ADR-PC-019](../../product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md) (the product-engine monorepo these components join) |
| Resolves | bd `archie-ux4` (re-split ADR-PC-019/020 + add ADR-IC-013) |

---

## Context

ADR-IC-001 through ADR-IC-012 each selected a *tool* for one infrastructure concern. None of them decided **where the code we write lives** or stated, as one fact, **which of those concerns are code we build versus images we consume**. That is a genuine gap, not a detail: with the project moving from specification to implementation, and with [ADR-PC-019](../../product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md) having decided the product-engine repository strategy (a monorepo), the integration estate's build provenance and repository placement is the open question that blocks scaffolding the estate services.

The gap was hidden by a **conflated taxonomy**. The [C4 container model](../../product_concepts/feature-design-c4-architecture.md) partitions the world by *architectural role* — product engine (blue), integration estate (teal), external (grey) — and describes the teal estate as "inherited … the engine operates but did not design here." That phrasing silently equates *estate by role* with *consumed by provenance*. But the two axes are orthogonal:

- **Architectural role** — is this the product engine, surrounding estate, or an external system?
- **Build provenance** — do we write and own the code, consume a third-party image/SDK, or is it external?

Several estate components are *estate by role* but *in-house by provenance*: the IC ADRs already chose to **build** them. The saga orchestrator is an "event-driven application orchestrator" we write ([ADR-IC-003](./ADR-IC-003-saga-orchestrator.md)); the outbox is a "custom polling publisher" in each service ([ADR-IC-004](./ADR-IC-004-outbox-pattern-mechanism.md)); the MCP server is "the bank's MCP server", custom code on the Python SDK ([ADR-IC-010](./ADR-IC-010-mcp-server-runtime-and-sdk.md)); the notification service is a "dedicated notification service" ([ADR-IC-011](./ADR-IC-011-async-saga-completion-notification.md)); the ACL is a "dedicated ACL service per bounded context", hand-rolled ([ADR-IC-012](./ADR-IC-012-anti-corruption-layer-implementation.md)). The C4 taxonomy has no cell for "estate we build", so the placement of this code was never decided.

### What this ADR decides

| # | Decision | Options evaluated |
|---|---|---|
| D1 | **Build-provenance classification** of the twelve IC tool decisions | (a synthesis of choices already made — see the matrix) |
| D2 | **Repository placement** of the in-house estate components | Co-locate in the product monorepo (extraction-ready, split reserved); separate estate repo now; per-service estate repos |

D2 is the load-bearing decision; D1 is the classification that makes D2's scope precise. The two are coupled: only the *in-house* components are a placement question at all — consumed third-party tools are images/SDKs, not repositories we structure.

### Scope boundary

This ADR does not re-decide *which* tool each IC concern uses (ADR-IC-001…012 own those), nor the monorepo-vs-multirepo question for the product engine ([ADR-PC-019](../../product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md) owns that), nor the conformance regime that governs the resulting code ([ADR-PC-020](../../product_concepts/adrs/ADR-PC-020-llm-toolchain-and-conformance-governance.md) owns that). It decides build provenance and placement, and nothing else.

---

## Evaluation

### D1 — Build-provenance classification

A factual synthesis of the choices ADR-IC-001…012 already made, sorted onto the role × provenance grid:

| IC ADR | Component | Architectural role | Build provenance |
|---|---|---|---|
| [IC-001](./ADR-IC-001-event-backbone-message-broker.md) | Redpanda broker | estate | **consumed** (self-hosted image) |
| [IC-002](./ADR-IC-002-schema-format-and-registry.md) | Avro + Schema Registry | contract format / governance | **convention** (format) + **consumed** (SR is Redpanda built-in) |
| [IC-003](./ADR-IC-003-saga-orchestrator.md) | Saga orchestrator | estate | **in-house** |
| [IC-004](./ADR-IC-004-outbox-pattern-mechanism.md) | Outbox publisher | estate (per-service worker) | **in-house** |
| [IC-005](./ADR-IC-005-cqrs-read-model-storage.md) | PostgreSQL read store | estate | **consumed** (self-hosted image) |
| [IC-006](./ADR-IC-006-edge-api-gateway.md) | Kong gateway | estate | **consumed** (self-hosted image) |
| [IC-007](./ADR-IC-007-observability-stack.md) | Grafana LGTM + OTel Collector | estate | **consumed** (self-hosted images) |
| [IC-008](./retired/ADR-IC-008-event-catalog-governance-tooling.md) | EventCatalog | governance (offline) | **consumed tool** + **in-house source** (the AsyncAPI specs we author) |
| [IC-009](./ADR-IC-009-testing-infrastructure.md) | Testcontainers / Pact / WireMock / Toxiproxy | dev / test | **consumed** (libraries) |
| [IC-010](./ADR-IC-010-mcp-server-runtime-and-sdk.md) | MCP server | estate (runtime deliverable) | **in-house** (code on the Python SDK) |
| [IC-011](./ADR-IC-011-async-saga-completion-notification.md) | Notification service | estate | **in-house** |
| [IC-012](./ADR-IC-012-anti-corruption-layer-implementation.md) | ACL service | estate | **in-house** |

**The in-house estate is five components: the saga orchestrator (IC-003), the outbox publisher (IC-004, a per-service worker), the MCP server (IC-010), the notification service (IC-011), and the ACL (IC-012).** Everything else is consumed (images/SDKs) or convention. The in-house *contract artefacts* (the Avro/CUE schemas and the EventCatalog AsyncAPI source) are also code we write, but they are part of the product engine's `/contracts` plane ([ADR-PC-019 §P1](../../product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md)), not estate *services*, so they are out of D2's scope.

This classification is the cell the C4 taxonomy was missing; the C4 document is updated to carry it (Consequences).

### D2 — Repository placement of the in-house estate

#### Hard filters

Per [ADR-IC-000](./ADR-IC-000-common-evaluation-criteria.md), F1 (cost) and F2 (regulatory fit) are applied first — but, as for the analogous product-side decision ([ADR-PC-019](../../product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md)), **they do not discriminate**: every option uses Git on the existing host at zero incremental cost (F1 · **Pass** for all), and source-tree layout carries no PII and is not a DORA/PSD2 runtime artefact — approval-boundary auditability is enforceable by `CODEOWNERS` + per-service pipelines within one repo or by per-repo permissions across many (F2 · **Pass** for all). The decision rests on the soft criteria, chiefly S2.

#### Options

**Option A: Co-locate in the product monorepo, extraction-ready, split reserved.** The five in-house services live as sibling top-level paths in the [ADR-PC-019](../../product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md) monorepo (`/orchestrator`, `/acl`, `/mcp-server`, `/notification`; the outbox is a per-service worker inside `/engine`, `/acl`, `/notification`). Each is a cleanly-bounded subtree with its own `CODEOWNERS`, its own Dockerfile, and its own deploy pipeline, so deploy independence holds inside one repo and a future `git filter-repo` extraction stays mechanical.

**Option B: Separate estate repo now.** One repository for all five in-house estate services, versioned independently from the engine.

**Option C: Per-service estate repos.** One repository per in-house service — full multirepo for the estate.

**Chosen: Option A — co-locate, extraction-ready, split reserved.**

**S2 · Ecosystem coherence — decisive.** The in-house estate is **the most contract-coupled code in the system**. The ACL translates external calls into engine events; the orchestrator drives sagas across engine commands; the notification service subscribes to engine terminal events; the outbox publishes engine events. They bind to the same event envelope, Avro payloads, and CUE schemas the engine produces. The [ADR-PC-019](../../product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md) monorepo argument — atomic "envelope + every consumer + schema + contract test" change, single-context LLM authorship — therefore applies *more strongly* to the estate than to anything inside the engine. A split would maximise the version-skew surface on precisely the assets the brief calls "the bank's most valuable asset" ([01 §6](../../product_concepts/01-product-architecture.md)).

**S1 · Operational complexity (1–2 people).** Lowest under A: one clone, one path-scoped CI, no cross-repo "which orchestrator version goes with which engine schema" matrix. Deploy independence — the usual reason to reach for separate repos — is a *pipeline* property (per-service Dockerfiles + path-scoped CI), achieved inside one repo, not a repo-count property.

**S3 · Exit cost.** Low and asymmetric in A's favour: §P2 of [ADR-PC-019](../../product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md) keeps every service an extraction-ready subtree, so splitting later (`git filter-repo` on a clean path) is mechanical; merging separate repos back after paying the coordination tax is not.

**S4 · Longevity.** Neutral — Git and the host outlive any layout.

#### Rejected

**Option B — separate estate repo now.** Pays the cross-repo version-skew tax on the most contract-coupled code in the system *before any second consumer exists*. The two arguments that could justify it are real but not yet live: (1) the **estate-as-platform** future — if a *second* product engine consumes the estate, a shared-infra repo earns its keep; (2) the **disposability** of the orchestrator — [ADR-IC-003](./ADR-IC-003-saga-orchestrator.md) names it a POC stopgap with Temporal as the upgrade path. But IC-003 also says the *contracts* survive that swap ("the migration is an addition, not a rewrite"); the durable thing is the contract surface, which co-location protects, and the disposable thing (the orchestrator impl) is as deletable from a monorepo path as from a repo. Per the project's "reserve, don't pre-build" discipline ([ADR-PC-009 §P5](../../product_concepts/adrs/ADR-PC-009-per-instance-version-pinning.md)), B is **reserved as a future split**, taken only on a concrete trigger (a second consumer, or the Temporal migration wanting its own lifecycle) — not pre-built now.

**Option C — per-service estate repos.** Full multirepo's coordination cost (a contract change is a five-PR dance) with none of multirepo's benefit at 1–2-person, one-topology scale. No team boundary to mirror, no independent-cadence need that per-service pipelines do not already meet. Rejected outright.

---

## Decision

**D1 — Build provenance:** of the twelve IC tool decisions, **five are in-house builds** (saga orchestrator [IC-003], outbox publisher [IC-004], MCP server [IC-010], notification service [IC-011], ACL [IC-012]); the rest are consumed third-party images/SDKs or convention. Architectural role (estate) is explicitly distinguished from build provenance (in-house vs consumed).

**D2 — Repository placement:** the five in-house estate components **co-locate in the [ADR-PC-019](../../product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md) product monorepo** as cleanly-bounded, extraction-ready subtrees (own `CODEOWNERS`, own Dockerfile, own per-service pipeline). The **estate-repo split is reserved, not taken** — revisited only on a concrete trigger (a second product engine consuming the estate, or the [ADR-IC-003](./ADR-IC-003-saga-orchestrator.md) Temporal migration wanting its own lifecycle), per the "reserve, don't pre-build" discipline.

**Rejected: separate estate repo now** — pays the version-skew tax on the most contract-coupled code before a second consumer exists; reserved as a future split. **Rejected: per-service estate repos** — full multirepo cost, no benefit at this scale.

The conformance regime that governs all of this in-house code — engine *and* estate — is [ADR-PC-020](../../product_concepts/adrs/ADR-PC-020-llm-toolchain-and-conformance-governance.md); its coverage checker, conformance agent, and explicit-drift gate apply to the ADR-IC entries classified in-house here exactly as to ADR-PC.

---

## Consequences

**What this choice makes easier:**

1. **Atomic contract change across the whole build.** An envelope or Avro change plus the engine producer plus every in-house estate consumer plus the contract test land in one commit — no cross-repo skew on the most contract-coupled code.
2. **Single-context LLM authorship of the estate.** The primary author sees engine and estate in one working tree ([ADR-PC-019](../../product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md) LLM-codability), matching how it reasons across the contract boundary.
3. **A correct, explicit taxonomy.** The C4 model ([feature-design-c4-architecture](../../product_concepts/feature-design-c4-architecture.md)) is updated so "teal estate" no longer implies "consumed": the legend now distinguishes in-house-built estate (IC-003/004/010/011/012, source in the monorepo per this ADR) from consumed third-party estate, with provenance noted as orthogonal to the C4 role colour.

**What this choice makes harder or impossible:**

1. **The estate cannot evolve on a wholly independent release train without the reserved split.** Per-service pipelines give deploy independence, but the *source* shares one repo and one history until the split is taken. Mitigation: §P2 of [ADR-PC-019](../../product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md) keeps each service extraction-ready, so the split is a path extraction, not a restructuring.
2. **Estate-as-platform must be a deliberate trigger, not a drift.** If a second product engine starts consuming the estate informally, the shared-infra boundary should be recognised and the split taken — not left implicit. Mitigation: the trigger is named here and tracked as an Open Action.

**Residual risks:**

- **The reserved split may arrive under live operation.** If the Temporal migration or a second consumer forces the boundary later, the extraction happens against a running system. Mitigation: extraction-ready subtrees (§P2 of [ADR-PC-019](../../product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md)) make it mechanical.

---

## Open Actions

1. **Scaffold the in-house estate paths** in the monorepo skeleton (`/orchestrator`, `/acl`, `/mcp-server`, `/notification`; outbox workers inside their owning services), each with its own `CODEOWNERS`, Dockerfile, and path-scoped pipeline — alongside [ADR-PC-019 Open Action #1](../../product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md).
2. **Update the C4 model** to carry the role-vs-provenance distinction (Consequences #3).
3. **Revisit the estate-repo split** on a concrete trigger: a second product engine consuming the estate, or the [ADR-IC-003](./ADR-IC-003-saga-orchestrator.md) Temporal migration wanting its own lifecycle.

---

## Cross-references

- [ADR-PC-019](../../product_concepts/adrs/ADR-PC-019-repository-strategy-monorepo.md) — the product-engine monorepo these in-house components join; its §P1 layout shows the estate paths and its §P2 keeps them extraction-ready.
- [ADR-PC-020](../../product_concepts/adrs/ADR-PC-020-llm-toolchain-and-conformance-governance.md) — the conformance regime that governs the in-house estate code classified here (its both-namespaces scope is grounded on this ADR).
- [ADR-IC-003](./ADR-IC-003-saga-orchestrator.md) / [ADR-IC-004](./ADR-IC-004-outbox-pattern-mechanism.md) / [ADR-IC-010](./ADR-IC-010-mcp-server-runtime-and-sdk.md) / [ADR-IC-011](./ADR-IC-011-async-saga-completion-notification.md) / [ADR-IC-012](./ADR-IC-012-anti-corruption-layer-implementation.md) — the per-component in-house build decisions this ADR classifies and places.
- [ADR-PC-009 §P5](../../product_concepts/adrs/ADR-PC-009-per-instance-version-pinning.md) — the "reserve, don't pre-build" discipline applied to the reserved estate-repo split.
- [feature-design-c4-architecture](../../product_concepts/feature-design-c4-architecture.md) — the build/estate/external split (by role) this ADR refines into role-vs-provenance.
- [01 §6](../../product_concepts/01-product-architecture.md) — "the bank's most valuable asset … is the integration shape"; the contract-coherence force behind co-location.

---

*Decided 2026-05-24 by jhosm.*
