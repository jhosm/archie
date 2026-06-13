# ADR-IC-017: Integration-Event Promotion — Catalog-Gated Relay + Explicit Domain-vs-Integration Criterion

| Field | Value |
|---|---|
| Status | Proposed |
| Date | 2026-06-13 |
| Deciders | jhosm |
| Common criteria | [ADR-IC-000](./ADR-IC-000-common-evaluation-criteria.md) |
| Depends on | [ADR-IC-004](./ADR-IC-004-outbox-pattern-mechanism.md), [ADR-IC-002](./ADR-IC-002-schema-format-and-registry.md), [ADR-IC-015](./ADR-IC-015-event-catalog-governance-tooling-backstage.md), [ADR-PC-028](../../product_concepts/adrs/ADR-PC-028-event-store-payload-format.md) |
| Resolves | bd `(proposed — see the ACTUAL→INTENDED graph; new G-series issue, pending review)` |

---

## In plain English

Inside the deposits engine, lots of things happen and get recorded as events. Only some of them are facts that *other* systems (Core Banking, CRM, Compliance, Notifications, Reporting) need to hear about — those are **integration events** and belong on the shared message bus. The rest are **internal** and should stay inside the engine's own store. Today the engine has no rule for telling the two apart and no gate that enforces one: it publishes *every* event it records, so an event ends up "internal" or "public" by accident of whether someone happened to write a schema for it. This ADR fixes that. It writes down the criterion for deciding (does a named outside consumer need this coarse business fact?) and makes the publisher **refuse to publish anything that hasn't been deliberately promoted** — turning "internal" into a guarantee instead of an accident.

## Context

[Document 01, Primitive 2](../01-the-six-primitives.md) draws the **domain event vs integration event** line: internal events are "fine-grained and abundant" and "do not cross Kafka"; integration events are "coarse-grained, rare … with unambiguous business meaning," and "there is a **boundary publisher** that listens to the domain and decides what deserves to be promoted." That is the *only* place the criterion lives, and it is purely conceptual — **no ADR owns it, and no gate enforces it.**

A review of the running estate (2026-06-13) found the conceptual line is not realised anywhere:

- **The relay publishes everything.** `AggregateRuntime.AppendAsync` writes an `OutboxRow` for **every** appended event unconditionally (no "is this an integration event?" branch); `OutboxDrainer` selects `WHERE status = 'PENDING'` and publishes all of them; the wired codec is `JsonEventSerializer`, which encodes **any** `DomainEvent` regardless of whether a schema exists. So the operative rule today is *"every event a family appends is published on the bus."*
- **The `.avsc` is a consequence, not a cause — and not even a runtime gate.** Of the 11 `term_deposit` `DomainEvent` records, only 4 have an `.avsc`/AsyncAPI entry (`DepositConstituted`, `DepositMatured`, `InterestAccrued`, `WithholdingApplied`); the other 7 (incl. `DepositConstitutionFailed`) are schemaless yet would ride to the bus if appended. `EmitContractFitnessTests` documents this explicitly: *"7 of the 11 family events are schemaless today … so without this they ride to the bus unguarded until their `.avsc` exists."*
- **The existing EventCatalog gate runs only one direction.** [ADR-IC-015](./ADR-IC-015-event-catalog-governance-tooling-backstage.md)'s `asyncapi-catalog-validate.sh` orphan check asserts *every governed `.avsc` has a catalog entry* (`.avsc` → catalog). It never asks the reverse — *should this event be on the bus at all?* / *was its publication a deliberate promotion?* — so it cannot catch an event that reaches the bus without ever being promoted.

The net is the exact failure mode [ADR-PC-020 §D3](../../product_concepts/adrs/ADR-PC-020-llm-toolchain-and-conformance-governance.md)'s explicit-drift gate exists to prevent — a load-bearing decision (Primitive 2) silently unenforced — except there is no ADR to drift *from*. The risk: an engineer adds a `DomainEvent`, wires it into a decider's append, and it is published on the durable bus **by accident of being appended**, leaking the engine's fine-grained, config-volatile internal vocabulary (and any `evidence_ref`-style audit lineage) into other teams' domains — the [Primitive 2](../01-the-six-primitives.md) "Option C" anti-pattern.

This decision is needed **now**, ahead of the event-plane wiring (Epics G/H/I/J), because once consumers (the saga, Core, CRM, Compliance, Notifications, Reporting) couple to whatever is on the bus, an un-governed surface becomes an un-removable one — [Primitive 1](../01-the-six-primitives.md): an event contract is "like historical data: must remain readable forever."

> This is an **integration-boundary governance decision, not a tool bake-off** (cf. [ADR-IC-016](./ADR-IC-016-service-identity-and-mtls.md)). The mechanisms it governs — the outbox relay ([ADR-IC-004](./ADR-IC-004-outbox-pattern-mechanism.md)) and the AsyncAPI/EventCatalog surface ([ADR-IC-015](./ADR-IC-015-event-catalog-governance-tooling-backstage.md)) — are already chosen; this ADR decides the **posture** layered on them. The [ADR-IC-000](./ADR-IC-000-common-evaluation-criteria.md) F1/F2 hard filters are immaterial (nothing is bought or installed); the decision rides on operational coherence (S1), ecosystem fit (S2), and reversibility (S3).

## Decision

Adopt a **catalog-gated relay** plus a **written promotion criterion**. The two together make a catalog/AsyncAPI entry the *single, enforced* record of an integration-event promotion: an event is on the bus **if and only if** it has a catalog entry, and authoring that entry is a deliberate act gated by the criterion below.

### Candidates considered

| Option | Approach | Verdict |
|---|---|---|
| **A — Status quo (implicit promotion)** | Every appended `DomainEvent` is published; no criterion, no gate (today's behaviour). | **Rejected.** Drift by construction: "internal vs integration" decided by accident of appending; the Primitive-2 anti-pattern is the default, not the exception. |
| **B — Catalog-gated relay + criterion** | The relay publishes only events whose `event_type` resolves to an EventCatalog/`.avsc` entry (fail-closed); authoring that entry is the promotion, governed by the criterion. | **CHOSEN.** The catalog ([ADR-IC-015](./ADR-IC-015-event-catalog-governance-tooling-backstage.md)) already is the governed boundary artefact; gating on it adds one source of truth, not a new one. |
| **C — Per-event CLR annotation** | An `[IntegrationEvent]` attribute on the event record, consulted by the relay. | **Rejected.** Puts the boundary decision in engine code rather than the integration-estate contract surface, and creates two promotion records (attribute + catalog) that can drift. |
| **D — Relay allowlist config** | An explicit list of publishable `event_type`s in relay config. | **Rejected.** A third registry to keep in sync with the catalog and the schemas; the catalog already *is* the allowlist once we gate on it. |

### The promotion criterion (when to author a catalog entry)

A `DomainEvent` is promoted to an **integration event** — and only then gets an `.avsc`/AsyncAPI entry — when **all three** hold ([Primitive 2](../01-the-six-primitives.md)):

1. **Coarse-grained, business-meaningful fact** — an "unambiguous business" outcome other contexts model, not a fine-grained internal step, calculation buffer, or state-transition trace.
2. **A named external consumer needs it** — at least one bounded context (Core, CRM, Compliance, Notifications, Reporting, or the saga) must *react* to it. New events/fields are added "only when an integration consumer states a need, not when the aggregate happens to grow one" (the [Primitive 2](../01-the-six-primitives.md) Option-B discipline).
3. **Stable contract, not volatile vocabulary** — the event's shape and meaning are a contract that "must remain readable forever," not an internal taxonomy that churns with refactors or config (e.g. an engine-owned, config-extensible key set).

An event that fails **any** test stays a **domain event**: store-only JSON in the event store ([ADR-PC-028](../../product_concepts/adrs/ADR-PC-028-event-store-payload-format.md)), folded into state and replayable, but never on the durable bus. If the *coarse* business fact behind an internal event is itself consumer-relevant, it is surfaced by the **owning coarse integration event** (e.g. a constitution refusal is surfaced to the ecosystem by the saga's terminal `DepositCancelled`, not by promoting the engine's internal `DepositConstitutionFailed`).

## Implementation principles

- **§P1 — Catalog-gated relay (fail-closed).** The outbox relay publishes a row **iff** its `event_type` resolves to a catalogued integration event (an AsyncAPI entry / governed `.avsc` per [ADR-IC-015](./ADR-IC-015-event-catalog-governance-tooling-backstage.md) / [ADR-IC-002](./ADR-IC-002-schema-format-and-registry.md)). An uncatalogued event is **store-only by construction** — it is still appended, folded, and replayable; it simply never produces a published message. This *replaces* the current "publish every PENDING row" behaviour. (Mechanically this can be a relay-side filter or an append-side decision not to write an outbox row for un-promoted events — the **append+outbox atomicity** of [ADR-IC-004 §P2](./ADR-IC-004-outbox-pattern-mechanism.md) / `ES_ATOMIC_APPEND_OUTBOX` is preserved either way.)
- **§P2 — The catalog entry is the promotion record.** Promotion happens by authoring the AsyncAPI/`.avsc` entry, which is a reviewed change (the `contract-reviewer` agent + the criterion above). There is no second promotion switch.
- **§P3 — Bidirectional catalog gate.** Extend [ADR-IC-015](./ADR-IC-015-event-catalog-governance-tooling-backstage.md)'s orphan check (every `.avsc` has a catalog entry) with the **reverse**: a CI check asserting every event the relay *can* publish has a catalog entry, i.e. the §P1 runtime rule mirrored at build time. Together they make **catalog entry ⇔ on the bus** a hermetic biconditional, not a convention.
- **§P4 — The v1 integration-event set is ratified per-event against the criterion, not inherited from today's `.avsc` files.** The current 4-schema set is *not* presumed correct. Applying the criterion (notably tests 1 and 3) is expected to reclassify some events — e.g. the granular `InterestAccrued` / `WithholdingApplied` accrual mechanics are candidate **internal** events, while the coarse "interest paid out" fact (`InterestPaid`, [v1.x](../../product_concepts/v1-build-backlog.md)) is the candidate **integration** event; `DepositConstitutionFailed` is **internal** (its coarse fact rides the saga's `DepositCancelled`). The implementing issue performs this classification pass and records the resulting consumer map in the catalogue.
- **§P5 — No PII on the promoted surface.** The promoted set is exactly the surface the no-PII-on-bus rule ([ADR-PC-004 §P2](../../product_concepts/adrs/ADR-PC-004-pii-crypto-shredding.md), `G.6`) must cover; shrinking the bus to deliberately-promoted events shrinks that surface. PII-by-reference still applies to every promoted event.

## Consequences

**Easier:**
- "Internal" becomes a **guarantee** rather than an accident; the durable bus carries only deliberately-promoted, stable business facts.
- The catalog is the single source of truth for the bus surface (no annotation/allowlist drift); the no-PII-on-bus audit surface is bounded and known.
- Adding an internal event (the common case) is free of bus-contract obligations — it just doesn't get a catalog entry.

**Harder / slower (by design):**
- Promoting an event now requires a catalog entry *and* passing the criterion review — intended friction at the boundary.
- A schemaless event a developer *expected* to publish is now silently store-only until catalogued; §P3's reverse gate is what surfaces "published-intent without a promotion" at CI rather than at runtime.

## Residual risks

- **The engine⇄saga command/event topology is NOT decided here, and it bounds which engine events are integration events.** Whether the saga *consumes* engine integration events, or the engine *consumes* saga commands via its inbox (or both), determines whether facts like `DepositConstituted` must be promoted for the saga's benefit or are learned another way. This ADR governs the *criterion and the gate*; the ingress topology is a separate decision touching [ADR-IC-003](./ADR-IC-003-saga-orchestrator.md) / [ADR-IC-004](./ADR-IC-004-outbox-pattern-mechanism.md) and is **flagged for explicit sign-off**, not silently chosen here. The criterion is stable under either topology; only the resulting consumer map shifts.
- **Until §P1 ships, the drift persists.** The decision is inert until the relay is gated; §P1 is the load-bearing implementing work and should land before any external context subscribes to a `term_deposit` topic.
- **The §P4 classification is a judgement pass, not a mechanical one.** Borderline events (`InterestAccrued`/`WithholdingApplied` vs `InterestPaid`) require a deliberate consumer-need call; this ADR sets the test, not the verdicts.
- **Cross-context vs intra-context topics.** This ADR governs *promotion to the durable integration bus*. If an intra-context choreography channel is later introduced (e.g. saga ⇄ engine on a private topic), its governance is a follow-on; the criterion's "named external consumer" test already distinguishes the two.

## Verifiable commitments

This decision's load-bearing commitment is **not yet catalogued** (the ADR is `Proposed`); on acceptance it migrates to a Test-ID row in the [commitment catalogue](../../product_concepts/adrs/commitment-catalogue.md) per [ADR-PC-020 §P5–§P7](../../product_concepts/adrs/ADR-PC-020-llm-toolchain-and-conformance-governance.md) and is referenced here. Proposed (inline until catalogued):

| # | Commitment (§-anchor) | Gate (pyramid level) | Test ID | Status |
|---|---|---|---|---|
| 1 | The relay publishes an event **iff** it is catalogued; an uncatalogued event is store-only (§P1) | integration (relay + Testcontainers) | `INTEGRATION_EVENT_CATALOG_GATED` | Planned |
| 2 | Every relay-publishable `event_type` has an AsyncAPI/`.avsc` entry — reverse orphan check (§P3) | CI (`contracts` job) | `NO_UNCATALOGUED_EVENT_ON_BUS` | Planned |

A `Planned` status is a deliberate, listed hole pending the implementing issue (§P1/§P3); visibility is the point.
