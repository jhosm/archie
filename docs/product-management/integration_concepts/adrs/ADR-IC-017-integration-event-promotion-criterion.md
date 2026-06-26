# ADR-IC-017: Integration-Event Promotion — Catalog-Gated Relay + Explicit Domain-vs-Integration Criterion

| Field | Value |
|---|---|
| Status | Accepted |
| Date | 2026-06-13 |
| Deciders | jhosm |
| Common criteria | [ADR-IC-000](./ADR-IC-000-common-evaluation-criteria.md) |
| Depends on | [ADR-IC-004](./ADR-IC-004-outbox-pattern-mechanism.md), [ADR-IC-002](./ADR-IC-002-schema-format-and-registry.md), [ADR-IC-015](./ADR-IC-015-event-catalog-governance-tooling-backstage.md), [ADR-PC-028](../../product_concepts/adrs/ADR-PC-028-event-store-payload-format.md) |
| Resolves | bd `(proposed — see the ACTUAL→INTENDED graph; new G-series issue, pending review)` |

---

## In plain English

Inside an event-sourced engine, lots of things happen and get recorded as events. Only some of them are facts that *other* systems need to hear about — those are **integration events** and belong on the shared message bus. The rest are **internal** and should stay inside the engine's own store. By default such an engine has no rule for telling the two apart and no gate that enforces one: it publishes *every* event it records, so an event ends up "internal" or "public" by accident of whether someone happened to write a schema for it. This ADR fixes that. It writes down the criterion for deciding (does a named outside consumer need this coarse business fact?) and makes the publisher **refuse to publish anything that hasn't been deliberately promoted** — turning "internal" into a guarantee instead of an accident.

This is a **general integration-architecture decision**: the criterion and the gate apply to any event-sourced producer fronting a shared bus. The concrete code paths, event names, and consumer lists below are drawn from this repository's running example — a Portuguese term-deposit engine — purely to make the abstract rule legible; they are illustrations, not part of the decision.

## Context

[Document 01, Primitive 2](../01-the-six-primitives.md) draws the **domain event vs integration event** line: internal events are "fine-grained and abundant" and "do not cross Kafka"; integration events are "coarse-grained, rare … with unambiguous business meaning," and "there is a **boundary publisher** that listens to the domain and decides what deserves to be promoted." That is the *only* place the criterion lives, and it is purely conceptual — **no ADR owns it, and no gate enforces it.**

A review of the running example's estate — the term-deposit engine, 2026-06-13 — found the conceptual line is not realised anywhere. The findings are specific to that example, but the *failure mode* they illustrate is generic to any unguarded relay:

- **The relay publishes everything.** `AggregateRuntime.AppendAsync` writes an `OutboxRow` for **every** appended event unconditionally (no "is this an integration event?" branch); `OutboxDrainer` selects `WHERE status = 'PENDING'` and publishes all of them; the wired codec is `JsonEventSerializer`, which encodes **any** `DomainEvent` regardless of whether a schema exists. So the operative rule today is *"every event a family appends is published on the bus."*
- **The `.avsc` is a consequence, not a cause — and not even a runtime gate.** Of the 11 `term_deposit` `DomainEvent` records, only 4 have an `.avsc`/AsyncAPI entry (`DepositConstituted`, `DepositMatured`, `InterestAccrued`, `WithholdingApplied`); the other 7 (incl. `DepositConstitutionFailed`) are schemaless yet would ride to the bus if appended. `EmitContractFitnessTests` documents this explicitly: *"7 of the 11 family events are schemaless today … so without this they ride to the bus unguarded until their `.avsc` exists."*
- **The existing EventCatalog gate runs only one direction.** [ADR-IC-015](./ADR-IC-015-event-catalog-governance-tooling-backstage.md)'s `asyncapi-catalog-validate.sh` orphan check asserts *every governed `.avsc` has a catalog entry* (`.avsc` → catalog). It never asks the reverse — *should this event be on the bus at all?* / *was its publication a deliberate promotion?* — so it cannot catch an event that reaches the bus without ever being promoted.

The net is the exact failure mode [ADR-PC-020 §D3](../../product_concepts/adrs/ADR-PC-020-llm-toolchain-and-conformance-governance.md)'s explicit-drift gate exists to prevent — a load-bearing decision (Primitive 2) silently unenforced — except there is no ADR to drift *from*. The risk is general: an engineer adds a `DomainEvent`, wires it into a decider's append, and it is published on the durable bus **by accident of being appended**, leaking the engine's fine-grained, config-volatile internal vocabulary (and any audit-lineage references) into other teams' domains — the [Primitive 2](../01-the-six-primitives.md) "Option C" anti-pattern.

This decision is needed **before the event plane is wired**, because once any downstream context couples to whatever is on the bus, an un-governed surface becomes an un-removable one — [Primitive 1](../01-the-six-primitives.md): an event contract is "like historical data: must remain readable forever." (In the running example those downstream contexts are the saga, core banking, CRM, compliance, notifications, and reporting, and the wiring is Epics G/H/I/J.)

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
2. **A named external consumer needs it** — at least one downstream bounded context must *react* to it (in the running example: the saga, core banking, CRM, compliance, notifications, or reporting). New events/fields are added "only when an integration consumer states a need, not when the aggregate happens to grow one" (the [Primitive 2](../01-the-six-primitives.md) Option-B discipline).
3. **Stable contract, not volatile vocabulary** — the event's shape and meaning are a contract that "must remain readable forever," not an internal taxonomy that churns with refactors or config (e.g. an engine-owned, config-extensible key set).

An event that fails **any** test stays a **domain event**: store-only JSON in the event store ([ADR-PC-028](../../product_concepts/adrs/ADR-PC-028-event-store-payload-format.md)), folded into state and replayable, but never on the durable bus. If the *coarse* business fact behind an internal event is itself consumer-relevant, it is surfaced by the **owning coarse integration event** — not by promoting the fine-grained internal one. (In the running example: a constitution refusal reaches the ecosystem via the saga's terminal `DepositCancelled`, not by promoting the engine's internal `DepositConstitutionFailed`.)

## Implementation principles

- **§P1 — Catalog-gated relay (fail-closed).** The outbox relay publishes a row **iff** its `event_type` resolves to a catalogued integration event (an AsyncAPI entry / governed `.avsc` per [ADR-IC-015](./ADR-IC-015-event-catalog-governance-tooling-backstage.md) / [ADR-IC-002](./ADR-IC-002-schema-format-and-registry.md)). An uncatalogued event is **store-only by construction** — it is still appended, folded, and replayable; it simply never produces a published message. This *replaces* the current "publish every PENDING row" behaviour. (Mechanically this can be a relay-side filter or an append-side decision not to write an outbox row for un-promoted events — the **append+outbox atomicity** of [ADR-IC-004 §P2](./ADR-IC-004-outbox-pattern-mechanism.md) / `ES_ATOMIC_APPEND_OUTBOX` is preserved either way.)
- **§P2 — The catalog entry is the promotion record.** Promotion happens by authoring the AsyncAPI/`.avsc` entry, which is a reviewed change (the `contract-reviewer` agent + the criterion above). There is no second promotion switch.
- **§P3 — Bidirectional catalog gate.** Extend [ADR-IC-015](./ADR-IC-015-event-catalog-governance-tooling-backstage.md)'s orphan check (every `.avsc` has a catalog entry) with the **reverse**: a CI check asserting every event the relay *can* publish has a catalog entry, i.e. the §P1 runtime rule mirrored at build time. Together they make **catalog entry ⇔ on the bus** a hermetic biconditional, not a convention.
- **§P4 — The integration-event set is ratified per-event against the criterion, not inherited from the existing schemas.** An estate's current schema set is *not* presumed correct; applying the criterion (notably tests 1 and 3) is expected to reclassify some events. The implementing issue performs this classification pass and records the resulting consumer map in the catalogue. (In the running example: the granular `InterestAccrued` / `WithholdingApplied` accrual mechanics are candidate **internal** events, while the coarse "interest paid out" fact — `InterestPaid`, [v1.x](../../product_concepts/v1-build-backlog.md) — is the candidate **integration** event; `DepositConstitutionFailed` is **internal**, its coarse fact riding the saga's `DepositCancelled`. The current 4-schema term-deposit set is not presumed correct.)
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

- **The producer⇄orchestrator command/event topology is NOT decided here, and it bounds which producer events are integration events.** Whether an orchestrator *consumes* the producer's integration events, or the producer *consumes* the orchestrator's commands via an inbox (or both), determines whether a given fact must be promoted for the orchestrator's benefit or is learned another way. This ADR governs the *criterion and the gate*; the ingress topology is a separate decision touching [ADR-IC-003](./ADR-IC-003-saga-orchestrator.md) / [ADR-IC-004](./ADR-IC-004-outbox-pattern-mechanism.md) and is **flagged for explicit sign-off**, not silently chosen here. The criterion is stable under either topology; only the resulting consumer map shifts. (In the running example the producer is the engine, the orchestrator is the saga, and the bounded fact is `DepositConstituted`.)
- **Until §P1 ships, the drift persists.** The decision is inert until the relay is gated; §P1 is the load-bearing implementing work and should land before any external context subscribes to a producer's topic.
- **The §P4 classification is a judgement pass, not a mechanical one.** Borderline events require a deliberate consumer-need call; this ADR sets the test, not the verdicts. (In the running example, `InterestAccrued`/`WithholdingApplied` vs `InterestPaid` is exactly such a call.)
- **Cross-context vs intra-context topics.** This ADR governs *promotion to the durable integration bus*. If an intra-context choreography channel is later introduced (e.g. saga ⇄ engine on a private topic), its governance is a follow-on; the criterion's "named external consumer" test already distinguishes the two.

## Verifiable commitments

This decision's load-bearing commitments are fitness functions in the [commitment catalogue](../../product_concepts/adrs/commitment-catalogue.md) — the single source of truth for their exact claim, gate, and `Live`/`Planned`/`Gap` status ([ADR-PC-020 §P5–§P7](../../product_concepts/adrs/ADR-PC-020-llm-toolchain-and-conformance-governance.md)):

- `INTEGRATION_EVENT_CATALOG_GATED` — the relay publishes an event **iff** it is catalogued; an uncatalogued event is store-only by construction (§P1).
- `NO_UNCATALOGUED_EVENT_ON_BUS` — every relay-publishable `event_type` has an AsyncAPI/`.avsc` entry, the reverse orphan check that mirrors §P1 at build time (§P3).

Both are `Planned` — the gates are named and the Test IDs reserved; the tests are written with the implementing issue (§P1/§P3). The `Planned` status is a deliberate, listed hole; visibility is the point.

## §P4 classification pass — recorded verdict (2026-06-14)

*Additive implementation note (not a change to the Decision above — §P4 explicitly delegates this classification pass to the implementing issue; this records its result).* The per-event §P4 ratification of the term-deposit running example was performed (bd `babelstone-a7d4.4`). The net **catalogued (promoted) integration-event set is `{ DepositConstituted, InterestPaid, DepositMatured }`** — down from the four schemas the estate started with. The recorded **consumer map** is the `x-authorized-consumers` field on each AsyncAPI file under [`contracts/catalog/events/`](../../../../contracts/catalog/events/); the [catalogue README](../../../../contracts/catalog/README.md#the-promoted-set--the-adr-ic-017-p4-classification) carries the full verdict table.

- **`InterestPaid` — PROMOTED to integration (v1).** The coarse coupon/advance payout fact GL/accounting, notifications, and reporting react to; carries the withholding *amount* on `withholding_tax_cents`. The §P4 running example above (and the Residual-risks note) anticipated this as a v1.x *candidate*; the classification pass realised it in **v1**, so the running-example "v1.x" framing for `InterestPaid` is now historical.
- **`InterestAccrued` / `WithholdingApplied` — DE-PROMOTED to internal / store-only.** Fine-grained periodic accrual and tax-withholding *mechanics*; no downstream context reacts to each tick (fail tests 1 + 2), and the integration-relevant withholding amount already rides the coarse `InterestPaid` (a separate event is redundant). Their `.avsc`/AsyncAPI entries and registry subjects were removed; at v1 there are no live consumers, so the removal is non-breaking.
- **`DepositConstituted` / `DepositMatured` — stay integration.** Clear coarse facts (`DepositMatured` carries the AT_MATURITY maturity payout). `DepositConstitutionFailed` and the other F.2 lifecycle events stay internal/store-only per the Decision.

The events de-promoted here still **exist** as `DomainEvent` records and are appended, folded, and replayable from the JSON event store ([ADR-PC-028](../../product_concepts/adrs/ADR-PC-028-event-store-payload-format.md)); only their bus schema was removed. Replay/fold is unaffected — the `.avsc` is bus-encode only.

---

## Amendment — 2026-06-26: catalogue downstream-producer (non-engine-emitted) event schemas; the §P3 reverse-orphan gate is producer-scoped

**In plain English:** the catalogue and its reverse-orphan gate so far assume every catalogued event is one the **engine** emits — it asserts *catalogued ⇔ a relay-capable engine `DomainEvent`*. But some events on the estate are produced by a **different service**, not the engine, and they have nowhere sanctioned to be catalogued: the gate rejects their entry because there is no engine `DomainEvent` behind it. The first concrete case is the maturity-notice signal `NotificationDue(SCHEDULED)`, which the **notification scheduler** raises off a projection, never the engine. This amendment says where such schemas live and how the gate is scoped so a downstream-producer schema can be catalogued — keeping every other governance obligation (no-PII, BACKWARD compatibility, discoverability) — **without** being mistaken for an un-relayed engine event.

### What the gap is, precisely

The Decision above and §P3 make *catalogued ⇔ on the bus* a biconditional anchored on the **engine's** relay-capable event set: the reverse-orphan check (`asyncapi-catalog-validate.sh` §P3 + the .NET `CatalogGatedRelayReverseOrphanTests`, commitment-catalogue row 22 `NO_UNCATALOGUED_EVENT_ON_BUS`) asserts every catalogued `.avsc` record name is a real family-or-spine `DomainEvent`. That is correct for the **engine-produced** plane and must stay. The gap is a **second producer**:

- **`NotificationDue` is engine-OWNED, not homeless.** [ADR-PC-025 §1](../../product_concepts/adrs/ADR-PC-025-customer-notification-emit-contract.md) makes `NotificationDue` a new engine-cross-cutting event and gives the engine ownership of its schema and the `trigger_kind` taxonomy. The engine emits it for `EVENT_DRIVEN` + `PRE_CONTRACTUAL`. The enum **retains `SCHEDULED`** as a valid schema value a **downstream** producer carries — the engine itself emits no `SCHEDULED` signal and runs no scheduler ([ADR-PC-023](../../product_concepts/adrs/ADR-PC-023-temporal-signals-projection-derived.md): temporal signals are projection-derived, produced downstream).
- **The maturity scheduler is that downstream producer.** The family-agnostic notification platform ([ADR-IC-019](./ADR-IC-019-family-agnostic-notification-platform.md)) gives the notification estate its own service + scheduler + outbox; the scheduler reads the maturity-calendar / accrual projections cross-context and produces `NotificationDue(SCHEDULED)` itself ([ADR-PC-025 §Context](../../product_concepts/adrs/ADR-PC-025-customer-notification-emit-contract.md)). This is a **cross-context** signal (own service, own outbox → reads cross-context), so it is governance-bearing — it needs the catalogue's no-PII / BACKWARD-compat / discoverability discipline, not a Pact-only side-channel.
- **The precise gap is a build-sequencing artefact.** The engine's own `EVENT_DRIVEN` `NotificationDue` emission is deferred (no `DomainEvent`/`.avsc` on disk today — `NotificationDue` currently appears only in `EmitContractFitnessTests`). So when the **scheduler's** `SCHEDULED` producer needs to catalogue the engine-owned `NotificationDue` schema, §P3's reverse-orphan gate rejects it: there is no relay-capable engine `DomainEvent` behind the entry yet.

### The decision — where downstream-producer schemas live, and the producer-scoped gate

1. **Home — the existing catalogue, marked by producer.** A downstream-producer event schema lives in the **same** `contracts/avro/` tree and the **same** `contracts/catalog/events/` catalogue as every other governed event — *not* a separate `contracts/` subtree or a parallel catalogue. It is distinguished by a **required `x-producer` marker** on the catalogue (AsyncAPI) entry, naming the producing service (`engine` for an engine-emitted event; the registered downstream service name — e.g. `notification` — for a non-engine producer). The default and overwhelming-majority value is `x-producer: engine`. (`NotificationDue` is engine-owned but, for the `SCHEDULED` producer, the catalogue entry that the scheduler authors carries `x-producer: notification`; if and when the engine's `EVENT_DRIVEN` emission lands, that is the engine producing the same engine-owned schema — see Decision §5 below.)

2. **Schema-Registry subject + BACKWARD rules are unchanged.** A downstream-producer schema is a normal Avro schema: its registry subject is `{namespace}.{name}-value` ([ADR-IC-002 §P1](./ADR-IC-002-schema-format-and-registry.md)), and it is governed by the **BACKWARD** compatibility rule like every other integration schema ([ADR-IC-002](./ADR-IC-002-schema-format-and-registry.md), `avro-compat-check.sh`). Producer identity does **not** change the wire-compatibility contract — a consumer that reads the topic must tolerate schema evolution the same way regardless of which service produced the record.

3. **The §P3 reverse-orphan gate is scoped to `x-producer: engine`.** The reverse-orphan biconditional — *every catalogued schema resolves to a relay-capable engine `DomainEvent`* — applies **only to entries whose `x-producer` is `engine`** (or absent, which defaults to `engine`). A `NotificationDue` *engine `DomainEvent`* is still the right anchor for engine-produced notifications; the reverse-orphan gate keeps enforcing it for them. An entry with a non-engine `x-producer` is **exempt from the relay-capable-engine-event leg only** — there is no engine `DomainEvent` to anchor it, and that is correct: the engine does not produce it. It keeps **every other** catalogue obligation (validity, governance fields, no-PII, the BACKWARD diff, deprecation lifecycle, subject well-formedness).

4. **A non-engine `x-producer` must resolve to a registered service (guards mis-marking).** To stop `x-producer` becoming an escape hatch that smuggles an un-relayed engine event past §P3 by mislabelling it, the gate adds a fitness assertion: a non-`engine` `x-producer` value must resolve to a **registered downstream producer service** (the in-house estate service set — [ADR-IC-013](./ADR-IC-013-in-house-estate-build-and-repository-placement.md) — e.g. `notification`). An unknown producer name fails the gate. This makes the producer-scoping deliberate and auditable, not a silent relaxation.

5. **`NO_UNCATALOGUED_EVENT_ON_BUS` is narrowed to engine-produced events.** The commitment that the reverse-orphan gate realises (commitment-catalogue row 22, `NO_UNCATALOGUED_EVENT_ON_BUS`) is narrowed in scope to *engine-produced* events: *every relay-capable engine `event_type` has a catalogue entry*. The forward orphan check (every governed `.avsc` has a catalogue entry, §P2) and `INTEGRATION_EVENT_CATALOG_GATED` (§P1, the **engine relay** is fail-closed on the catalogue) are **unchanged** — they govern the engine's relay, which is the only relay this ADR's biconditional ever spoke about. A downstream producer's own outbox/relay is governed by its own service's discipline ([ADR-IC-019](./ADR-IC-019-family-agnostic-notification-platform.md) / [ADR-IC-004](./ADR-IC-004-outbox-pattern-mechanism.md)), not by the engine's catalog-gated relay.

### Why amend, not a new ADR — and why this is additive (ADR-PC-020 §D3/§D5)

This lands on IC-017's own pre-armed hook: the **Residual risks** above already flag that "the producer⇄orchestrator command/event topology is NOT decided here… flagged for explicit sign-off," and the promotion criterion's **"named external consumer"** test already presumes producers and consumers may be distinct services. Recognising a *second producer* and producer-scoping the gate is the natural extension of §P3, not a reversal of any clause — the engine-relay biconditional, the promotion criterion, the fail-closed relay, and the forward orphan check all stand **unchanged** for the engine plane. It is therefore an **additive amendment**, the correct [ADR-PC-020 §D3](../../product_concepts/adrs/ADR-PC-020-llm-toolchain-and-conformance-governance.md) instrument, and the §D5 immutability of the Decision above is preserved (no in-place edit). **A new `ADR-IC-021` was reserved for this concern but is left unused** — the decision belongs as a §P3 extension of this ADR, not as a free-standing record; `ADR-IC-021` is not consumed and remains available for a future, genuinely free-standing integration decision.

### What this amendment does NOT do

- It does **not** author the `NotificationDue` `.avsc`/AsyncAPI entry, add the `x-producer` field to the gate scripts/tests, or narrow the commitment-catalogue row 22 text — those are the implementing work (bd `babelstone-60n8.3`, which this amendment unblocks; and the gate/test/catalogue change it carries). This amendment is the **ADR-level decision** that sanctions them.
- It does **not** widen the engine's catalog-gated relay to publish non-engine events. The engine relay (`INTEGRATION_EVENT_CATALOG_GATED`) is unchanged; a downstream producer relays through its own outbox.
- It does **not** weaken any no-PII, BACKWARD-compat, or discoverability obligation for downstream-producer schemas — only the relay-capable-**engine**-event leg of §P3 is producer-scoped.
