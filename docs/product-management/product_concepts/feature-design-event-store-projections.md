# Feature Design — Event Store and Projections

> Companion to the brief. Deepens the engine's source-of-truth model from [00 §3](./00-product-vision.md), [01 §2](./01-product-architecture.md), and [02 §2.3](./02-v1-scope-term-deposits.md): event store + bitemporal projections, the log is the truth, projections are derived state.
>
> Interlocks with [authoring](./feature-design-configuration-authoring.md): family schemas declare variants and handlers (that document) and also event types (this one). Everything outside the cross-cutting event set is family-schema material.
>
> Reading order: §1 source-of-truth · §2 four time-dimensional capabilities · §3 engine-vs-family separation · §4 event taxonomy · §5 handler discipline · §6 bitemporal projections · §7 replay reconciliation · §8 snapshots · §9 GL coupling · §10 risk mitigations.

---

## 1. Frame: Event Store + Projections as the Source of Truth

The engine's source of truth is the **event store**, co-located with the outbox. State is *derived* by deterministic, side-effect-free event handlers. Projections — positions, accrual schedules, maturity calendars, withholding ledgers — are bitemporal tables built from the event store. The CQRS read model ([integration_concepts §03](../integration_concepts/03-cqrs-and-read-models.md)), the GL system, the IFRS 9 system, and the regulatory reporting application are all *consumers* of these projections; none of them is the engine's primary state holder.

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

The unification thesis in [01 §1](./01-product-architecture.md) holds only if the engine code is genuinely generic — i.e. does not know what a "deposit" or "credit" or "mortgage" is. Without that separation, "one engine, many families" silently becomes "one engine plus a lot of family-conditional code in the engine," and the unification is a label, not a structure.

The separation, made explicit:

| Layer | Owns | Knows about |
|---|---|---|
| **Engine** | Event store, outbox, handler dispatch, projection runtime, validator runtime, snapshot machinery, cross-cutting generic event types ([§4.1](#41-cross-cutting-generic-events-engine-declared)), the family-schema loading mechanism | Nothing family-specific. The engine does not know what a deposit is. |
| **Family schema** | Family-specific event types, event handlers (pure functions), family-specific projections, lifecycle state machine, pack-binding declarations | The engine's interfaces only — not the engine's internals. |
| **Pack** | Jurisdiction-specific primitives and parameters (see [surface §3](./feature-design-configuration-surface.md)) | The engine's primitive interface only. |

The line between engine and family schema is the load-bearing one. The engine is a small, stable runtime. Family schemas are the variable part. Adding a new family is a new schema; the engine code does not change. This is what makes the engine commitment from [authoring §7.1](./feature-design-configuration-authoring.md) — *zero engine code per new variant; contained engine code per new family* — testable rather than aspirational.

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
| `PackVersionMigrated` | Operator-initiated retroactive pack migration per [surface §3.6](./feature-design-configuration-surface.md) | `instance_id`, `from_pack_version`, `to_pack_version`, `migration_id`, `operator_actor` |
| `SchemaVersionMigrated` | Operator-initiated family-schema migration per [authoring §6](./feature-design-configuration-authoring.md) | `instance_id`, `from_schema_version`, `to_schema_version`, `migration_id`, `operator_actor` |
| `LegacyInstanceObserved` | Daily batch arrives from legacy DDA (per [coexistence §5](./feature-design-strangler-fig-coexistence.md)) | `legacy_instance_id`, `observed_at`, `legacy_state_snapshot`, `batch_file_id` |
| `FundsHeld` | Court order, garnishment, or external hold instruction | `instance_id`, `hold_id`, `held_amount_cents`, `legal_reference`, `hold_expires_at` (optional) |
| `AccountFrozen` | Compliance hold (fraud, AML, sanctions screening) | `instance_id`, `freeze_id`, `freeze_reason`, `compliance_actor`, `freeze_expires_at` (optional) |

These five exist because they describe *operational realities* that span every product family: regulation changes (`PackVersionMigrated`), engine evolution (`SchemaVersionMigrated`), strangler-fig coexistence (`LegacyInstanceObserved`), legal interventions (`FundsHeld`), and compliance actions (`AccountFrozen`). A v1 catalogue that omits them assumes a happy path the production engine will never see.

### 4.2 Family-specific events (declared by family schemas)

Events that describe family-specific lifecycle transitions. Declared in family schemas; handlers also in family schemas. The engine dispatches by event type but knows nothing about the semantics.

The current v1 catalogue in [02 §2.4](./02-v1-scope-term-deposits.md) declares 8 deposit events: `DepositConstituted`, `DepositConstitutionFailed`, `InterestAccrued`, `WithholdingApplied`, `InterestPaid`, `DepositMatured`, `DepositRenewed`, `DepositTerminatedEarly`. These are happy-path events. Under event sourcing, the catalogue must also cover operationally inevitable events or the audit trail will have gaps. Three additions for v1:

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

This is the same shape as the outbox pattern from [ADR-IC-004](../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md). The outbox *is* the side-effects-as-scheduled-events mechanism for the event-bus publication side. The same shape extends to other side-effecting consumers (notifications, payments, regulatory submissions).

### 5.3 Handlers can be replayed

A handler is replayable if running it against the historical event sequence produces the same projections as running it the first time. Replayability is testable: store a fixture event sequence, apply handlers, compare projections. The team runs this test on every PR that touches a handler.

A handler that is not replayable (because it reads the clock, calls an API, depends on environment) is a bug, not a tradeoff. The engine's CI rejects handlers that break the determinism test.

### 5.4 Schema evolution is forward-only

Once an event with `event_schema_version: N` is written, that schema must remain readable forever. Two consequences:

- **Adding fields is always allowed** (the field is optional; old events parse with the field unset).
- **Removing fields is never silent**. A field deprecated in schema version N+1 is still present in the schema and still parseable from old events; the new handler may ignore it, but the data does not disappear.
- **Renaming and re-typing require an explicit migration step** — a new event type, not a new version of the old one. The engine carries both event types in parallel until all instances pinned to old schemas have matured.

This is the same disclosure as pack pinning ([surface §3.5](./feature-design-configuration-surface.md)) and schema pinning ([authoring §6](./feature-design-configuration-authoring.md)), specialised to event payloads.

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

### 6.2 GDPR erasure and the bitemporal model

[Open Question §7 in 04-open-questions](./04-open-questions.md) opens the collision: GDPR Article 17 requires a data subject to be able to compel erasure of their personal data, but an immutable event log cannot satisfy that request without invalidating the replay invariant that audit and as-of queries depend on. The PT GDPR transposition (Lei 58/2019) is in force at v1, so the choice cannot be deferred to a later phase. Three architectural shapes were considered:

| Shape | What it does | Replay impact | Supervisory acceptance |
|---|---|---|---|
| **Crypto-shredding** | PII fields encrypted per data subject under a per-subject key; erasure = key destruction; cipher-text remains in the log, plaintext is unrecoverable | Replay produces nulls in PII fields after erasure; structural fields (amounts, dates, lifecycle transitions) remain intact and replayable | High — aligns with the GDPR Article 4(5) pseudonymisation definition and the "additional information held separately" test |
| **Tombstoning** | A tombstone event overrides PII fields on replay; cipher-text not destroyed | Same as crypto-shredding for replay | Mixed — some EU supervisors have rejected this as "deletion" because cipher-text remains recoverable from raw storage |
| **PII off-store** | Event log carries only structural fields plus a foreign-key reference to a mutable PII store; PII store is a normal database where erasure is straightforward | Replay determinism requires the PII store to be itself versioned bitemporally, or replays return "PII as it is now" rather than "PII as it was then" — losing bitemporality on the PII side | High — simplest legal story but the engineering invariant is harder to maintain |

**v1 commits to crypto-shredding.** The PII surface in v1 is bounded — customer name, NIF, address, contact, and free-text fields on a small set of lifecycle events — and is encryptable per-subject using the bank's existing KMS / HSM infrastructure. The structural fields the engine actually reasons about (principal, rate, dates, withholding ledger, lifecycle state) are not PII and remain in the clear, so handlers and projections continue to operate over erased records exactly as they do over live ones, with PII fields returning null instead of plaintext. The audit trail after erasure shows "an event occurred at this transaction_time; payload PII is unrecoverable due to subject erasure" — the GDPR-compliant audit state, not a gap.

The position is conditional on a DPO confirmation (see §6.4) that crypto-shredding satisfies the operating bank's interpretation of Article 17 in conjunction with PT banking-record retention obligations (typically 10 years for accounting records, 7 years for AML records). The fallback is PII off-store; tombstoning is rejected.

Two engineering consequences fall out immediately and constrain §6.3:

- Every event-type payload schema declares its PII fields explicitly. The engine's CI rejects schemas that introduce a string field without a PII / non-PII annotation. Family schemas declare; engine enforces.
- The chosen bitemporal storage path in §6.1 must host per-subject encryption envelopes at the field level, not the row level. Row-level encryption forecloses structural-field queries on erased records. This becomes scoring criterion #2 in the §6.3 spike.

### 6.3 Q-X implementation spike: scope and scoring

§6.1 names three candidate paths for bitemporal projection storage and defers the choice "to a follow-up issue with a small spike per path." This sub-section specifies the spike so it produces a comparable result across paths rather than three differently-shaped reports.

**Spike scope (per path, timeboxed at 5 engineering days).**

1. Implement the deposit-position projection (per [02 §2.5](./02-v1-scope-term-deposits.md)) end-to-end against the candidate storage: schema declaration, valid_time and transaction_time handling, the four canonical queries (#1–#4 from §2), and a forced correction round-trip — initial event, retroactive correction, both states queryable.
2. Implement the per-subject PII encryption envelope per §6.2 against at least one PII field on `DepositConstituted` (customer name) and verify the field is queryable when the key exists and returns null when the key is destroyed.
3. Run the v1 cold-replay performance target from §8.2 (one instance, ~24-260 events, under 5 seconds) and report achieved time.
4. Document operational profile: backup mechanism, point-in-time-recovery story, observability hooks, on-call complexity for a team operating Postgres today.

**Scoring criteria (in priority order).**

| # | Criterion | Why this priority |
|---|---|---|
| 1 | Correctness on the forced correction round-trip | If the path silently loses the original-then-corrected pair, it fails the bitemporal commitment in §6 — no other property compensates |
| 2 | GDPR erasure compatibility from §6.2 | Foreclosure risk: per-subject field-level encryption is hard to retrofit once projection schemas are written |
| 3 | DR / RTO / RPO shape (per [Q-AY in 04-open-questions](./04-open-questions.md)) | Production gating; the recovery story constrains the storage decision in ways that operate-time discovery is too late |
| 4 | Cold-replay time vs the §8.2 target | If the path hits the v1 target without snapshots, snapshots become optional rather than mandatory |
| 5 | Operational profile match to the team's existing stack | The team's moderate event-sourcing experience cannot absorb a new database technology simultaneously — see §10.4 |
| 6 | Query ergonomics for application code | Bitemporal joins are written by every family schema; ergonomics compound |

**Spike deliverable.** A single comparison table — one row per path — scoring each criterion 1–5 with a one-line justification, plus the working PR for each path. The decision is made by the engine technical lead with input from the operations function (criterion 5) and the DPO (criterion 2).

The spike runs only after Q-Y (§6.4) returns. If Q-Y confirms bitemporal is required, scoring proceeds as above. If Q-Y returns "unitemporal is sufficient for v1," criteria 1 and 6 fall away and the choice collapses to a simpler operational fit between the three paths.

### 6.4 Q-Y compliance verification: what to bring

[Open question Q-Y in 04-open-questions](./04-open-questions.md) asks whether PT regulators expect retroactive corrections to be queryable in both time dimensions. The §6 design assumes yes; if no, projection schemas simplify materially. The conversation is short — one meeting — but its result resets the storage decision in §6.1 and §6.2, so it runs before the §6.3 spike committee meets. The same meeting also resolves the §7 DPO question, since both turn on the same compliance/legal reading.

**Who attends.** Operating bank's compliance lead, internal audit lead, DPO, and the engine technical lead. Optional: external counsel familiar with BdP supervisory practice on system-of-record requirements.

**What to bring.**

1. **A concrete retroactive-correction scenario.** A worked example: a deposit's principal is recorded as €10,000 on 2026-03-15 due to clerk-data-entry error; the true principal was €100,000; the correction is applied on 2026-05-19 via a `DepositCorrected` event. An auditor on 2026-09-01 asks "what was the principal as we knew it on 2026-04-01?" The answer with bitemporal storage is "€10,000 — the wrong value, which is what we knew then." The answer without bitemporal storage is "€100,000 — the corrected value, projected backward as if always known." The question to compliance: which answer does BdP expect when this happens in supervisory inspection? Is there a written supervisory expectation, or is it inferred from general system-of-record practice?
2. **A retention-vs-erasure scenario.** A customer requests erasure of their PII on 2032-04-01; the deposit matured on 2029-03-15 and is therefore outside the 7-year AML retention window but inside the 10-year accounting-record window. Three candidate paths: (a) crypto-shred the PII per §6.2 — cipher-text remains, plaintext unrecoverable, audit trail shows erasure event; (b) retain everything until 2039-03-15 and reject the erasure request as legally exempt; (c) move PII off-store and erase from the mutable store. The question to compliance and the DPO: which path is defensible under PT supervisory practice and Lei 58/2019, and is crypto-shredding adequate as "erasure" or does it count as "pseudonymisation" that still requires deletion at the cipher-text level after the 10-year window?
3. **The three §6.1 candidate paths** as a reference list, so the conversation can foreclose paths that fail the compliance shape rather than only confirming a preferred one.

**Decision outputs needed from the meeting.**

- Bitemporal required, optional, or forbidden. (Forbidden is unlikely but possible if compliance views queryable "what we used to think" as making errors hard to disavow.)
- Crypto-shredding accepted as Article 17 erasure, or PII off-store required as the v1 mechanism.
- Retention windows confirmed for v1 deposit data: structural events vs PII fields, with the cipher-text question answered for the post-window period.
- Whether the engine must support a "regulator query" mode that bypasses subject-erasure for supervisory inspection (some EU jurisdictions allow this; the PT position is the open question).

The meeting's output is folded into [§7 and Q-Y in 04-open-questions](./04-open-questions.md), unblocks §6.3, and is committed to a one-paragraph addendum in [01 §2](./01-product-architecture.md) so the architectural commitment carries the regulatory qualification.

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
- The CQRS read model ([integration_concepts §03](../integration_concepts/03-cqrs-and-read-models.md))
- The GL system (see §9)
- The IFRS 9 system
- The regulatory reporting application
- Any analytics / BI consumer of the event stream

Each consumer agrees with the engine on a reconciliation contract (which checksums it publishes, which event-count it reports, how full rebuilds are coordinated). The contracts are part of the event catalogue's governance ([integration_concepts §08](../integration_concepts/08-event-catalog-governance.md)).

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

The brief says ([00 §4](./00-product-vision.md)) the engine emits signals; the GL consumes them. This document commits to the specific shape of that coupling.

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
| **Kafka-as-event-store** (the bank's existing Redpanda per [ADR-IC-001](../integration_concepts/adrs/ADR-IC-001-event-backbone-message-broker.md)) | Mature (Redpanda is operationally proven); streaming-first semantics | Very high natively | Single technology with the existing event backbone; trades query ergonomics for streaming semantics; pattern is used by some modern fintechs |

**Decision: PostgreSQL-based event store ([ADR-PC-001](./adrs/ADR-PC-001-event-store-technology.md)).** The decisive force is outbox co-location ([ADR-IC-004 P6](../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md)) combined with the bitemporal-projections, field-level PII crypto-shredding, and snapshot commitments in this document — PostgreSQL is the only candidate that satisfies all four invariants ([two-modes §6.3](./feature-design-two-modes-asymmetry.md)) without forcing a re-decision of ADR-IC-004. Kurrent and Redpanda-as-event-store rejected; full evaluation in ADR-PC-001. The constraint is firm: building an event store in-house is rejected. The team's moderate experience cannot absorb both event-sourcing-pattern discipline AND event-store-infrastructure correctness simultaneously.

**Clarification (2026-05-23).** "No in-house build" targets event-store *infrastructure* — the storage engine, durability, crash recovery, replication — which PostgreSQL provides as purchased capability. It does **not** forbid a thin event-sourcing *module* (append, ordered load, projection apply, outbox write) on top of PostgreSQL: that module is event-sourcing-*pattern* discipline, the capability this section says the team can own. Whether that module is hand-rolled or a third-party library is the build-time choice [ADR-PC-001](./adrs/ADR-PC-001-event-store-technology.md) defers to [ADR-PC-010](./adrs/ADR-PC-010-dotnet-hand-rolled-engine.md); ADR-PC-010 takes the hand-rolled branch (Marten/Wolverine retained as reference implementations, not dependencies). The infrastructure-correctness burden stays with PostgreSQL either way.

### 10.5 Snapshots as optimisation, not architecture

Snapshots accelerate replay; they do not replace the event log. §8 captures this in detail. The reason it is a risk mitigation: in event-sourcing systems that fail, the failure mode is often "snapshots became the source of truth and the event log silently rotted." The discipline prevents that drift by treating snapshots as recomputable performance state.

### 10.6 Synthetic load testing with v4-scale traffic in v1

This connects to [two-modes](./feature-design-two-modes-asymmetry.md). Even though v1's family (term deposits) generates ~12M events/year, the engine's event store and replay infrastructure must be load-tested against synthetic v4-scale traffic (~100M-600M events/year, sustained 100s TPS, bursts to 1000s) during v1 development. The point is to surface event-store and projection-runtime bottlenecks while v1 is still malleable, not after v4 commitment hardens.

