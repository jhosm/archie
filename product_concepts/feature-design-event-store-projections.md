# Feature Design — Event Store and Projections

> A design-notes companion to the brief, not a numbered member of the series. Deepens the engine's source-of-truth model: [§00-product-vision §3](./00-product-vision.md), [§01-product-architecture §2](./01-product-architecture.md), and [§02-v1-scope §2.3](./02-v1-scope-term-deposits.md) commit to **event store + bitemporal projections** — the event log is the truth, projections are derived state — and this document specifies the engine-vs-family separation, the event taxonomy, handler discipline, replay reconciliation, snapshot strategy, and the GL coupling that operationalise the commitment. The strict separation between generic engine core and family-specific event logic is what makes the unification claim in [§01-product-architecture §1](./01-product-architecture.md) structurally true rather than aspirational.
>
> This document is interlocked with [feature-design-configuration-authoring](./feature-design-configuration-authoring.md): that document established that family schemas declare variants and handlers; this one establishes that they also declare *event types* and that everything in the engine outside the cross-cutting set is family-schema material. Read together, the two documents specify why "one engine, many families" is structurally possible.
>
> Reading order: §1 frames the source-of-truth model. §2 names the four time-dimensional capabilities the engine must support. §3 specifies the engine-vs-family separation. §4 specifies the event taxonomy (cross-cutting generic events + family-specific events). §5 covers handler discipline. §6 covers bitemporal projections and defers the implementation choice. §7 covers replay reconciliation. §8 covers snapshots. §9 covers GL and downstream coupling. §10 covers risk mitigations for a team with moderate event-sourcing experience.

---

## 1. Frame: Event Store + Projections as the Source of Truth

The engine's source of truth is the **event store**, co-located with the outbox. State is *derived* by deterministic, side-effect-free event handlers. Projections — positions, accrual schedules, maturity calendars, withholding ledgers — are bitemporal tables built from the event store. The CQRS read model ([integration_concepts/03](../integration_concepts/03-cqrs-and-read-models.md)), the GL system, the IFRS 9 system, and the regulatory reporting application are all *consumers* of these projections; none of them is the engine's primary state holder.

The four capabilities the engine must support — point-in-time reconstruction, audit trails, counterfactual replay, forward projection — are not properties of a state-holding ledger; they are properties of an event-sourced model with derived projections. The event-sourced shape is not a design choice but a consequence of those capabilities being mandatory, which is why [Open Question 3 (Time-Travel)](./04-open-questions.md) closed as resolved.

The commitment carries four consequences:

- The events ARE the truth. State that does not derive from events does not exist.
- Replay is a routine operation, not a recovery scenario. Counterfactual queries ("what would the accrual be if pack `pt.2027.1` had applied from 2026-01-01?") are answered by replay with modified inputs.
- The outbox is not a sidecar; it is part of the event store's commit boundary. The same write that appends to the event log emits to the bus.
- Projections can be rebuilt at any time. A projection that cannot be rebuilt is broken.

---

## 2. The Four Time-Dimensional Capabilities

The engine must support four queries against the same underlying truth. The brief implies them in scattered places; this section names them explicitly because each is what makes one regulatory obligation answerable.

| # | Capability | Query shape | Driven by |
|---|---|---|---|
| 1 | **As-of queries** | "What was the state of this instance on date X, as we knew it on date Y?" | Customer disputes; regulator inquiries; statement reconstruction |
| 2 | **Audit trail** | "What sequence of events led to the current state, and who caused each one?" | BdP supervisory audits; internal audit; succession enquiries |
| 3 | **Counterfactual replay** | "What would the state be if we replayed with corrected inputs or different rules?" | IFRS 9 backtesting; pack-correction scenarios; ALCO stress tests |
| 4 | **Forward projection** | "What will the state be on date Y if no further events occur?" | Operational planning; maturity calendars; risk reports |

All four are *time-dimensional* queries against the same source. The storage shape question (event-sourced vs snapshot+journal) is downstream of the capability requirement: only event sourcing makes #3 (counterfactual replay) tractable, and #2 (audit trail) is a free property of the event log rather than something to engineer separately. #1 and #4 are projection concerns — they are how downstream consumers see the truth.

Recasting them as architectural requirements rather than deferred decisions clarifies what the engine commits to. The brief's [Open Question 3](./04-open-questions.md) framing positioned these as "do we need them?" The right framing is "we need them; the architecture is the one that makes them properties, not features."

---

## 3. Engine vs Family Separation

The unification thesis in [§01-product-architecture §1](./01-product-architecture.md) holds only if the engine code is genuinely generic — i.e. does not know what a "deposit" or "credit" or "mortgage" is. Without that separation, "one engine, many families" silently becomes "one engine plus a lot of family-conditional code in the engine," and the unification is a label, not a structure.

The separation, made explicit:

| Layer | Owns | Knows about |
|---|---|---|
| **Engine** | Event store, outbox, handler dispatch, projection runtime, validator runtime, snapshot machinery, cross-cutting generic event types ([§4.1](#41-cross-cutting-generic-events-engine-declared)), the family-schema loading mechanism | Nothing family-specific. The engine does not know what a deposit is. |
| **Family schema** | Family-specific event types, event handlers (pure functions), family-specific projections, lifecycle state machine, pack-binding declarations | The engine's interfaces only — not the engine's internals. |
| **Pack** | Jurisdiction-specific primitives and parameters (see [feature-design-configuration-surface §3](./feature-design-configuration-surface.md)) | The engine's primitive interface only. |

The line between engine and family schema is the load-bearing one. The engine is a small, stable runtime. Family schemas are the variable part. Adding a new family is a new schema; the engine code does not change. This is what makes the engine commitment from [feature-design-configuration-authoring §7.1](./feature-design-configuration-authoring.md) — *zero engine code per new variant; contained engine code per new family* — testable rather than aspirational.

A concrete consequence: when a developer working on a new family writes a new event handler, that handler lives in the family schema's source tree, not in the engine's. The engine's handler dispatch runtime *loads* handlers from family schemas at startup; it does not contain handlers itself. A handler that references engine internals is an architectural violation visible at PR review.

---

## 4. Event Taxonomy

The hardest event-design question: should events be *family-specific* (`DepositConstituted`, `CreditConstituted`) or *generic* (`InstanceConstituted` with a `family` field)? Three credible answers were considered in brainstorm:

| Approach | Event shape | Audit readability | Engine genericity |
|---|---|---|---|
| **A. Generic events** | `InstanceConstituted { family: term_deposit, family_data: {...} }` | Poor — consumer interprets opaque blob | Excellent — engine knows nothing |
| **B. Family-specific events, generic engine runtime** | `DepositConstituted` declared by `TermDepositSchema`; engine dispatches by event type at runtime | Excellent — events are readable business facts | Excellent — engine operates on interface, not event names |
| **C. Layered (generic + family-specific projections)** | Engine emits generic events; family-specific events are projections | Both available | Excellent — but double engineering surface |

The recommendation is **Approach B**. Approach A loses what auditors and regulators actually need: an event log that reads as a sequence of business facts. Approach C is architecturally elegant but doubles the engineering surface for marginal benefit. Approach B keeps the engine generic *by operating on interfaces*, not by erasing event semantics.

Under Approach B, the engine has two distinct event categories:

### 4.1 Cross-cutting generic events (engine-declared)

Events that apply to any instance regardless of family. The engine declares these and owns their handler runtime. Five for v1:

| Event | Trigger | Carries |
|---|---|---|
| `PackVersionMigrated` | Operator-initiated retroactive pack migration per [feature-design-configuration-surface §3.6](./feature-design-configuration-surface.md) | `instance_id`, `from_pack_version`, `to_pack_version`, `migration_id`, `operator_actor` |
| `SchemaVersionMigrated` | Operator-initiated family-schema migration per [feature-design-configuration-authoring §6](./feature-design-configuration-authoring.md) | `instance_id`, `from_schema_version`, `to_schema_version`, `migration_id`, `operator_actor` |
| `LegacyInstanceObserved` | Daily batch arrives from legacy DDA (per [feature-design-strangler-fig-coexistence §5](./feature-design-strangler-fig-coexistence.md)) | `legacy_instance_id`, `observed_at`, `legacy_state_snapshot`, `batch_file_id` |
| `FundsHeld` | Court order, garnishment, or external hold instruction | `instance_id`, `hold_id`, `held_amount_cents`, `legal_reference`, `hold_expires_at` (optional) |
| `AccountFrozen` | Compliance hold (fraud, AML, sanctions screening) | `instance_id`, `freeze_id`, `freeze_reason`, `compliance_actor`, `freeze_expires_at` (optional) |

These five exist because they describe *operational realities* that span every product family: regulation changes (`PackVersionMigrated`), engine evolution (`SchemaVersionMigrated`), strangler-fig coexistence (`LegacyInstanceObserved`), legal interventions (`FundsHeld`), and compliance actions (`AccountFrozen`). A v1 catalogue that omits them assumes a happy path the production engine will never see.

### 4.2 Family-specific events (declared by family schemas)

Events that describe family-specific lifecycle transitions. Declared in family schemas; handlers also in family schemas. The engine dispatches by event type but knows nothing about the semantics.

The current v1 catalogue in [§02-v1-scope §2.4](./02-v1-scope-term-deposits.md) declares 8 deposit events: `DepositConstituted`, `DepositConstitutionFailed`, `InterestAccrued`, `WithholdingApplied`, `InterestPaid`, `DepositMatured`, `DepositRenewed`, `DepositTerminatedEarly`. These are happy-path events. Under event sourcing, the catalogue must also cover operationally inevitable events or the audit trail will have gaps. Three additions for v1:

| Event | Why it must exist | Carries |
|---|---|---|
| `DepositPartiallyWithdrawn` | Some PT deposit products allow partial early withdrawal (pack-conditional). Without this event, partial withdrawal becomes a `DepositTerminatedEarly` + `DepositConstituted` pair, losing the historical link. | `deposit_id`, `withdrawn_principal_cents`, `withholding_on_withdrawn_cents`, `remaining_principal_cents`, `withdrawal_date` |
| `DepositCorrected` | Clerk-data-entry correction (wrong principal, wrong rate, wrong term). Required for bitemporal correctness — distinguishes "what we thought" from "what we now know." | `deposit_id`, `correction_id`, `corrected_fields: { field: { old, new } }`, `correction_reason`, `corrected_by` |
| `DepositTransferredToHeirs` | Succession on death of holder. Lifecycle terminator that is neither maturity nor early termination. | `deposit_id`, `transfer_id`, `from_holder_id`, `to_heirs: [{ heir_id, share }]`, `succession_evidence_ref` |

A future family (personal credit, mortgage, current account) declares its own event set when it ships. The engine code does not change to accommodate them.

### 4.3 Event envelope (shared structure)

Every event — cross-cutting or family-specific — wraps a common envelope:

```
event_id: <uuid>
event_type: <fully qualified, e.g. term_deposit.DepositConstituted>
event_schema_version: <integer, monotonic per event_type>
instance_id: <uuid>
family: <term_deposit | personal_credit | ...>           # for routing, not interpretation
pack_version: <e.g. pt.2026.1>                            # pinned at the instance
schema_version: <e.g. term_deposit@2026.1>                # pinned at the instance
valid_time: <ISO-8601 timestamp; when the fact was true>
transaction_time: <ISO-8601 timestamp; when we recorded it>
causation_id: <event_id of the causing event, if any>
correlation_id: <saga correlation per integration_concepts/08>
actor: <who or what initiated this event>
payload: { ... event-type-specific fields ... }
```

The envelope is engine-declared; the payload schema is event-type-declared (and therefore family-schema-declared for family events, engine-declared for cross-cutting events). The engine reads the envelope to route, to pin, to order, to project; it never reads the payload.

---

## 5. Handler Discipline

Event handlers are the place where event sourcing fails when it fails. The discipline below is non-negotiable; relaxing any rule turns the engine into a state-holding ledger pretending to be event-sourced.

### 5.1 Handlers are pure functions

A handler has signature `(state, event) → new_state`. It is a pure function:

- No clock reads. All timestamps come from the event envelope.
- No external API calls. All data needed is in the event or the state.
- No randomness. Deterministic for the same inputs.
- No side effects. The handler does not send notifications, debit accounts, or write to the database directly. It returns a new state.

The engine's handler dispatch runtime applies the function and persists the new state alongside the appended event in a single transaction. The handler does not know about persistence.

### 5.2 Side effects are scheduled, not performed

A handler that "needs to send a notification" emits a `NotificationScheduled` event in its returned state's pending-effects list. A separate handler (the notification effect handler) consumes those scheduled events and dispatches them to the notification system. The original handler stays pure; the side effect is observable, retriable, and replayable.

This is the same shape as the outbox pattern from [ADR-004](../integration_concepts/adrs/ADR-004-outbox-pattern-mechanism.md). The outbox *is* the side-effects-as-scheduled-events mechanism for the event-bus publication side. The same shape extends to other side-effecting consumers (notifications, payments, regulatory submissions).

### 5.3 Handlers can be replayed

A handler is replayable if running it against the historical event sequence produces the same projections as running it the first time. Replayability is testable: store a fixture event sequence, apply handlers, compare projections. The team runs this test on every PR that touches a handler.

A handler that is not replayable (because it reads the clock, calls an API, depends on environment) is a bug, not a tradeoff. The engine's CI rejects handlers that break the determinism test.

### 5.4 Schema evolution is forward-only

Once an event with `event_schema_version: N` is written, that schema must remain readable forever. Two consequences:

- **Adding fields is always allowed** (the field is optional; old events parse with the field unset).
- **Removing fields is never silent**. A field deprecated in schema version N+1 is still present in the schema and still parseable from old events; the new handler may ignore it, but the data does not disappear.
- **Renaming and re-typing require an explicit migration step** — a new event type, not a new version of the old one. The engine carries both event types in parallel until all instances pinned to old schemas have matured.

This is the same disclosure as pack pinning ([feature-design-configuration-surface §3.5](./feature-design-configuration-surface.md)) and schema pinning ([feature-design-configuration-authoring §6](./feature-design-configuration-authoring.md)), specialised to event payloads.

---

## 6. Bitemporal Projections

Projections are derived state. They support the four time-dimensional capabilities from §2. Each projection row carries two time dimensions:

| Dimension | Meaning | Example |
|---|---|---|
| **valid_time** | When the fact was true in the world | "Principal was €10,000 from 2026-03-15 onward" |
| **transaction_time** | When we recorded the fact | "We learned this on 2026-03-15T14:23:00, originally; we corrected it on 2026-05-19T09:11:00" |

A bitemporal query has the shape *"as of `valid_time` T1, as known at `transaction_time` T2"*. This is what makes corrections auditable. A clerk-data-entry error on 2026-03-15, corrected on 2026-05-19, leaves *both* facts queryable: "what we thought on 2026-03-15 about 2026-03-15" (the original wrong principal) and "what we now know on 2026-05-19 about 2026-03-15" (the corrected principal). Unitemporal projections collapse these.

PT regulatory expectations on financial systems include this distinction by default — auditors expect to be able to query both "as we knew then" and "as we know now" for any past date. Confirming this expectation explicitly with the operating bank's compliance and audit functions is left as an open question ([Q-Y in 04-open-questions](./04-open-questions.md)).

### 6.1 Three implementation choices, deferred decision

The implementation of bitemporal projections is the largest remaining engineering choice in this design notes. Three credible paths:

| Path | What you write | What you operate | Tradeoff |
|---|---|---|---|
| **PostgreSQL temporal extensions** (or SQL:2011 temporal tables) | Standard SQL with `PERIOD FOR valid_time` declarations; engine maintains transaction_time via standard triggers/audit columns | Vanilla Postgres + extensions (e.g. `temporal_tables`, or PG17's native support) | Mainstream ecosystem; query syntax is `AS OF` clauses; team operates one DB |
| **XTDB / datomic-style temporal-native DB** | Datalog-style temporal queries; immutable by design | New operational dependency the team takes on | Best query ergonomics for bitemporal; new tech to operate; smaller ecosystem |
| **Application-level bitemporality on plain Postgres** | Every projection table carries `valid_from`, `valid_to`, `recorded_at`, `superseded_at` columns; engine code maintains them; queries are explicit joins | Vanilla Postgres | Most code to write; most subtle correctness bugs; familiar operational ecosystem |

The choice depends on three factors: the team's operational comfort with new infrastructure, the maturity of PG temporal support at the engine's target Postgres version, and how heavily the engine relies on temporal joins (which the application-level path makes painful). The decision is left to a follow-up issue with a small spike per path; this design notes commits to bitemporal projections as a property without specifying which mechanism.

The wrong way to defer this is to ship with unitemporal projections and "add bitemporality later." Adding bitemporality to a unitemporal projection set is a rewrite, not an enhancement, because every projection's identity (the row identifier) changes when valid_time is introduced. The decision must be made before projection schemas are first written.

---

## 7. Replay Reconciliation

"The event log is the source of truth" is aspirational unless consumers can prove they consumed it correctly. The discipline below is what makes the claim operationally true.

### 7.1 Three reconciliation patterns

| Pattern | Cadence | What it catches |
|---|---|---|
| **Daily checksum** | Every day at end-of-day | Per-instance state hash from the engine compared to the consumer's projection hash. Mismatch = consumer drift since the last reconciliation. |
| **Event-count reconciliation** | Continuous (per consumer) | Consumer should have processed exactly N events through time T. Engine publishes its monotonic event sequence number per instance; consumer reports last-processed sequence number; gap = events not yet consumed (acceptable) or events skipped (alert). |
| **Periodic full rebuild** | Monthly or quarterly | Consumer rebuilds projections from the full event log; result is compared against the running projection. Any divergence reveals subtle handler bugs that the daily checksum missed (e.g. accumulated rounding error, conditional logic that depends on consumer state). |

The three are layered: the daily checksum catches recent drift cheaply; the event-count reconciliation catches plumbing failures (lost events, lagging consumers); the periodic full rebuild catches the slow-drift bugs that pass the cheap checks.

### 7.2 Projection-rebuild drills

The team runs full rebuilds on a calendar schedule, not only when something looks wrong. A "projection-rebuild drill" is exactly what it sounds like: rebuild every projection from the full event log; compare against current state; investigate any divergence. The drill is run in a non-production environment with production-shaped data.

The cadence is monthly for v1, quarterly once the engine stabilises. The drill exists for two reasons: it catches divergence before regulators or auditors do, and it keeps the team's replay infrastructure exercised. Replay infrastructure that is never exercised rots; replay infrastructure that is exercised monthly stays operational.

### 7.3 What "consumer" means here

Every downstream system that derives state from engine events is a consumer subject to reconciliation:

- The engine's own projection runtime (positions, accrual schedules, withholding ledger, maturity calendar)
- The CQRS read model ([integration_concepts/03](../integration_concepts/03-cqrs-and-read-models.md))
- The GL system (see §9)
- The IFRS 9 system
- The regulatory reporting application
- Any analytics / BI consumer of the event stream

Each consumer agrees with the engine on a reconciliation contract (which checksums it publishes, which event-count it reports, how full rebuilds are coordinated). The contracts are part of the event catalogue's governance ([integration_concepts/08](../integration_concepts/08-event-catalog-governance.md)).

---

## 8. Snapshot Strategy

Snapshots are a performance optimisation, not a part of the architecture. The engine must always be able to rebuild any projection from the event log alone. Snapshots accelerate the rebuild; they do not replace the log.

### 8.1 When snapshots are taken

Three trigger conditions, applied independently per instance:

- **Per N events.** A configurable threshold (typically 100-1000 events) per family. Triggered when the un-snapshotted event count crosses the threshold.
- **At lifecycle boundaries.** Constitution, renewal, partial withdrawal, maturity, termination. These are natural boundaries where the instance's state is interpretable on its own.
- **At calendar boundaries.** Month-end and year-end alignment with reporting periods, regardless of event count. Required so as-of queries at period boundaries return without long replay.

The triggers compose: a snapshot is taken if any condition fires. Snapshots are never *required* — if they fail to write, the engine continues; the next rebuild will be slower but correct. Snapshot writes are eventually-consistent with the event log, not transactional with it.

### 8.2 Replay must work cold

The engine's replay path must function with no snapshots at all. "Cold replay" — rebuilding an instance's state from the first event — is the correctness fallback. The performance budget for cold replay is named explicitly:

- **v1 (with-a-plan families)**: cold replay of one instance's full lifecycle (~ 24-260 events for term deposits) completes in under 5 seconds.
- **v4 (irregular families)**: cold replay of one instance with 5 years of transactions (~ 250-1000 events) completes in under 30 seconds.

These targets are workflow falsifiable claims; failure to meet them is an engineering bug, not a budget overrun. The targets are tested as part of the regular projection-rebuild drills (§7.2).

### 8.3 Snapshot correctness

Snapshots can be wrong — a buggy snapshot is the worst failure mode in event sourcing because subsequent reads trust the snapshot blindly. Two defences:

- **Snapshot hash includes the last event_id covered.** A snapshot at event sequence N records the hash. Any rebuild from snapshot N to "now" includes a verification that the rebuilt state at sequence N matches the snapshot hash.
- **Snapshots are routinely discarded.** The monthly projection-rebuild drill (§7.2) discards all snapshots and rebuilds from cold. If the rebuilt state matches the snapshot-accelerated state, the snapshots are correct; if it doesn't, the snapshot infrastructure is investigated.

Snapshots that pass these checks for six months become trusted enough to use in production replays. Until then, they are advisory only.

---

## 9. GL Coupling and Downstream Integration

The brief says ([§00-product-vision §4](./00-product-vision.md)) the engine emits signals; the GL consumes them. This document commits to the specific shape of that coupling.

### 9.1 Engine emits raw business events; downstream systems map

The engine emits its raw business events on the bus per the outbox pattern. The GL system maintains its own event-to-GL-account mapping, derived from the GL chart of accounts, the bank's accounting policy, and the relevant accounting standards (Portuguese GAAP, IFRS where applicable).

This separation is the cleanest architectural commitment available: the engine knows nothing about GL account codes, GL postings, debit/credit polarity, or accounting periods. The GL system reads the same event stream as every other consumer, projects it into GL postings using its own logic, and produces ledger entries.

The same shape applies to the IFRS 9 system (which interprets `LoanRestructured`, `DaysPastDueCrossed`, etc. — events from v2+ — into staging decisions), the regulatory reporting application (which aggregates events into BdP returns), and any analytics/BI consumer.

### 9.2 Coordination dependency

The downside of the clean separation is a coordination dependency: the GL team must be willing and able to maintain the event-to-GL mapping. Most banks' GL systems do not have first-class event-stream consumption; the GL team typically wants flat-file extracts or pre-mapped postings. This is a real organisational risk to the architectural commitment, not just a technical detail.

The mitigation is to surface the dependency explicitly as a discovery item in the engine roadmap ([Q-AB in 04-open-questions](./04-open-questions.md)). If the GL system cannot consume events, the answer is *not* to make the engine emit GL-shaped events — that would couple the engine to a specific GL and break the clean separation. The answer is to introduce a small "GL adapter" — a thin transformation layer owned by the GL team — that consumes engine events and produces GL postings. The adapter is a GL-team artefact; it is not part of the engine.

---

## 10. Risk Mitigations for Moderate Event-Sourcing Experience

The team's event-sourcing experience is moderate: concepts understood, production-scale shipping is new. The recommendation in this document lands with risk, and the risk is concentrated in well-known event-sourcing failure modes. The mitigations below are non-negotiable v1 commitments, not best-effort goals.

### 10.1 Mandatory event-versioning discipline from day 1

No breaking changes to event schemas. Adding optional fields is allowed; everything else is a new event type. The engine's CI rejects PRs that change an existing event schema in a non-additive way. This rule applies from the first event written; relaxing it later breaks every instance pinned to the old schema.

### 10.2 Projection-rebuild drills as ops practice

The monthly drill (§7.2) is not "if needed." It is on the engine team's operations calendar, and missing one is a process incident. The drill exists to keep the team's replay infrastructure exercised and to surface drift bugs before regulators do.

### 10.3 Side-effect-free handler discipline enforced by CI

Determinism tests (§5.3) run on every PR. A handler that reads the clock, calls an external service, or depends on environment fails the test. The CI gate is automatic; PRs with non-deterministic handlers cannot merge.

### 10.4 Battle-tested event store; no in-house build

The event store is purchased capability, not built capability. Three candidates with their tradeoffs:

| Candidate | Maturity | Throughput ceiling | Operational profile |
|---|---|---|---|
| **Kurrent / EventStoreDB** | Mature; event-sourcing-native | Very high (millions of events/day routine) | New operational dependency for the team; smaller ecosystem |
| **Postgres-based event store** (e.g. on top of plain Postgres tables with strict append semantics) | Mainstream | ~1k TPS without sharding; higher with careful design | Familiar; team already operates Postgres; engineering work to sustain v4 scale |
| **Kafka-as-event-store** (the bank's existing Redpanda per [ADR-001](../integration_concepts/adrs/ADR-001-event-backbone-message-broker.md)) | Mature (Redpanda is operationally proven); streaming-first semantics | Very high natively | Single technology with the existing event backbone; trades query ergonomics for streaming semantics; pattern is used by some modern fintechs |

The choice is deferred to a follow-up issue (see [Q-AC in 04-open-questions](./04-open-questions.md)) but the constraint is firm: building an event store in-house is rejected. The team's moderate experience cannot absorb both event-sourcing-pattern discipline AND event-store-infrastructure correctness simultaneously.

### 10.5 Snapshots as optimisation, not architecture

Snapshots accelerate replay; they do not replace the event log. §8 captures this in detail. The reason it is a risk mitigation: in event-sourcing systems that fail, the failure mode is often "snapshots became the source of truth and the event log silently rotted." The discipline prevents that drift by treating snapshots as recomputable performance state.

### 10.6 Synthetic load testing with v4-scale traffic in v1

This connects to [feature-design-two-modes-asymmetry](./feature-design-two-modes-asymmetry.md). Even though v1's family (term deposits) generates ~12M events/year, the engine's event store and replay infrastructure must be load-tested against synthetic v4-scale traffic (~100M-600M events/year, sustained 100s TPS, bursts to 1000s) during v1 development. The point is to surface event-store and projection-runtime bottlenecks while v1 is still malleable, not after v4 commitment hardens.

