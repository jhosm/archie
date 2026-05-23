# ADR-PC-009: Per-Instance Pack and Schema Version Pinning — On the Event Envelope, Resolved Through Version Registries

| Field | Value |
|---|---|
| Status | Accepted |
| Date | 2026-05-23 |
| Deciders | jhosm |
| Shape | Tool-selection |
| Common criteria | [ADR-IC-000](../../integration_concepts/adrs/ADR-IC-000-common-evaluation-criteria.md) (reused per [ADR-PC-000](./ADR-PC-000-namespace-and-contract-shape-framework.md) D2; this is the [ADR-PC-000 §D3](./ADR-PC-000-namespace-and-contract-shape-framework.md) "operational discipline" residual category, declared tool-selection per the default) |
| Depends on | [ADR-PC-001 §P1](./ADR-PC-001-event-store-technology.md) (the `events`-table envelope already carries `pack_version` and `schema_version`), [ADR-PC-007 §P3](./ADR-PC-007-signed-yaml-oci-pack.md) (the `pack_versions` registry; per-instance pinning via `pack_version`), [ADR-PC-006](./ADR-PC-006-cue-schema-language.md) (CUE family schemas — the artefact `schema_version` resolves to) |
| Resolves | bd `archie-10r.10` (ADR-PC-009: Per-instance pack and schema version pinning) |

---

## Context

Two stability invariants run in parallel ([01 §5](../01-product-architecture.md)): every constituted instance pins to **the pack version** *and* **the family-schema version** active at constitution, and carries **both for its entire life**. A deposit constituted on 2026-03-15 under `pack: pt.2026.1` and `schema: term_deposit@2026.1` keeps computing under both even after `pt.2027.1` or `term_deposit@2027.1` ships ([surface §3.5](../feature-design-configuration-surface.md), [authoring §6](../feature-design-configuration-authoring.md)). Regulators expect it; auditors expect it; banks rely on it. Retroactive change is rare and explicit — a pack migration emits `PackVersionMigrated` per instance, a schema migration emits `SchemaVersionMigrated` ([02 §2.4.2](../02-v1-scope-term-deposits.md)).

This ADR resolves five sub-problems ([bd archie-10r.10](../04-open-questions.md)): (1) the **storage shape of the pinning fact** — columns on the instance projection, fields on every event, or a separate registry; (2) **replay-time lookup** — how a handler running at wall-clock time `T0` resolves the pack `vN.M` and schema `vN.M` active for instance `I`; (3) **`PackVersionMigrated` / `SchemaVersionMigrated` semantics** — payload, emission, downstream reaction; (4) **Q-N breaking-change opt-in** — explicit `POST /v1/pack-adoptions`, no silent upgrades ([surface §3.11 Q-N](../feature-design-configuration-surface.md)); (5) a **pack-effective-date placeholder** for the deferred per-primitive pin-or-float policy.

This entry is the [ADR-PC-000 §D3](./ADR-PC-000-namespace-and-contract-shape-framework.md) **residual category** ("operational discipline … per-instance version pinning … fits neither template cleanly … default to tool-selection"). The honest consequence, surfaced up front: **F1 and F2 do not discriminate.** The pinning columns *already exist* on the envelope ([ADR-PC-001 §P1](./ADR-PC-001-event-store-technology.md)); the candidates below are all PostgreSQL, all licence-free, all PII-free (a version string is neither a cost nor a regulatory surface). The load-bearing question is therefore not "which tool" but **where the pin lives so that replay stays correct** — and that is settled on S2 (coherence with the event-sourced model) plus a replay-correctness analysis, not on the hard filters. Per [ADR-PC-000 §D4](./ADR-PC-000-namespace-and-contract-shape-framework.md), the candidates are the three the issue names — they are real options, not straw-men, and the uniform hard-filter result is itself the finding.

**Candidates evaluated** ([bd archie-10r.10](../04-open-questions.md)):

| # | Candidate | Notes |
|---|---|---|
| A | **Pin on the event envelope (every event), resolved through version registries** | `pack_version` + `schema_version` on every event ([ADR-PC-001 §P1](./ADR-PC-001-event-store-technology.md)); resolution to the concrete artefact via a `pack_versions` ([ADR-PC-007 §P3](./ADR-PC-007-signed-yaml-oci-pack.md)) and an analogous `schema_versions` registry. |
| B | **Pin as columns on the instance projection only** | The projection row carries the two versions; events do not. |
| C | **Separate `instance_pins` registry keyed by `instance_id`** | A table mapping `instance_id → (pack_version, schema_version)`, written at constitution and updated on migration. |

---

## Evaluation

### Hard filter results

#### F1 · Cost / licensing

| Candidate | Licence | Verdict |
|---|---|---|
| A · envelope + registries | PostgreSQL; the columns already exist ([ADR-PC-001 §P1](./ADR-PC-001-event-store-technology.md)); `pack_versions` already specified ([ADR-PC-007 §P3](./ADR-PC-007-signed-yaml-oci-pack.md)). Zero incremental cost. | **Pass** |
| B · projection columns | PostgreSQL. Zero incremental cost. | **Pass** |
| C · separate registry | PostgreSQL. Zero incremental cost. | **Pass** |

Uniform pass — F1 does not discriminate (no candidate buys anything).

#### F2 · Regulatory fit (GDPR / DORA / PSD2)

A pin is two short version strings — **no PII**, so GDPR Article 17 / crypto-shredding ([event-store §6.2](../feature-design-event-store-projections.md)) does not reach it. DORA/PSD2 ask one thing of a pin: that "what pack and schema governed this instance, for its whole life" be **auditable and decidable**. That is a *correctness* property of where the pin lives, not a filter a candidate passes or fails on cost/regulatory grounds.

| Candidate | GDPR | DORA / PSD2 (auditability) | Verdict |
|---|---|---|---|
| A · envelope + registries | No PII. | The governing versions are on **every event**, so the audit answer is in the event stream itself — including the exact sequence boundary where a migration changed the pin. | **Pass** |
| B · projection columns | No PII. | The projection is *derived* and holds only the current pin — the pre-migration history is not recoverable from it. | **Pass** (clears the filter; fails on correctness — see S2) |
| C · separate registry | No PII. | The registry holds the current pin; the migration boundary is a row update, not a stream fact. | **Pass** (clears the filter; fails on correctness — see S2) |

All three clear the hard filters. The decision is entirely in S2 and the replay-correctness analysis below — the expected shape for the [ADR-PC-000 §D3](./ADR-PC-000-namespace-and-contract-shape-framework.md) residual category.

---

### Soft criteria

#### A · Pin on the event envelope, resolved through version registries — **CHOSEN**

**S1 · Operational complexity.** Nil incremental. The columns are already in the [ADR-PC-001 §P1](./ADR-PC-001-event-store-technology.md) `events` table; the `pack_versions` registry is already specified ([ADR-PC-007 §P3](./ADR-PC-007-signed-yaml-oci-pack.md)); this ADR adds one analogous `schema_versions` table. No new tier, no new write path.

**S2 · Ecosystem coherence — decisive.** The engine is event-sourced: **projections are rebuilt from events** ([event-store §1](../feature-design-event-store-projections.md), [ADR-PC-001](./ADR-PC-001-event-store-technology.md)). For a pin to be *correct under replay*, it must be a fact carried **by the events being replayed**, not by anything derived from them. Putting the pin on the envelope means a handler replaying event at sequence `N` reads, from that very event, the pack and schema that governed it — no wall-clock lookup, no external table consulted, no "what was active on 2026-03-15" reconstruction. This is the exact property [surface §2.3](../feature-design-configuration-surface.md) states for `rate_sheet_version_id` ("the audit chain is decidable from the event stream alone"), applied to the two version pins; `pack_version`, `schema_version`, and `rate_sheet_version_id` ([ADR-PC-008](./ADR-PC-008-rate-sheet-storage-and-deploy-api.md)) form one coherent per-event pinning family.

**S3 · Exit cost.** Low — the pin is two portable string columns; any future store reads them off the events.

**S4 · Longevity.** Inherits PostgreSQL's ([ADR-PC-001 §S4](./ADR-PC-001-event-store-technology.md)).

#### B · Projection columns only — **rejected on replay correctness**

A projection is *output* of replay; making it the *source* of the pin is circular. To replay instance `I`, the engine must know which pack/schema governs each event **before** it can rebuild the projection — but the pin would only exist *on* the projection it is trying to build. Worse, a single projection column holds one value, so it **cannot represent the pre/post-migration split**: after a `PackVersionMigrated` at sequence `M`, events `< M` ran under the old pack and events `≥ M` under the new — a property B structurally cannot encode. B fails the [01 §5](../01-product-architecture.md) "carries both for its entire life" invariant the moment a migration occurs.

#### C · Separate `instance_pins` registry — **rejected on correctness + atomicity**

C holds *the current* pin per instance, so it shares B's defect: it cannot represent the migration boundary within a stream (a row update overwrites the pre-migration value). It adds two further costs: (1) a registry write at constitution is a **second write outside the event-append transaction**, re-introducing exactly the dual-write the outbox pattern exists to eliminate ([ADR-IC-004 P6](../../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md), [ADR-PC-001 §P2](./ADR-PC-001-event-store-technology.md)); (2) a replay-time lookup the envelope already obviates. C buys nothing A lacks and loses atomicity.

**Decisive reason for A over B and C:** the pin must be a per-event fact for replay to be correct and for the migration boundary to be intrinsic to the stream. Both B and C store a single current value and cannot represent "old pack before sequence `M`, new pack after" — which is precisely what a migration creates and what the [01 §5](../01-product-architecture.md) invariant requires the engine to preserve for the instance's whole life.

---

## Decision

**Chosen: the pack and schema versions are pinned on the event envelope — `pack_version` and `schema_version` on every event — and resolved to concrete artefacts through two version registries (`pack_versions`, `schema_versions`). The pin is per-event, not per-instance; a migration changes the pin from its sequence forward, leaving prior history pinned to the old version.**

The decisive reason is **replay correctness**: in an event-sourced engine, projections are rebuilt from events, so the only place a pin can live without circularity — and the only place that can represent a mid-stream migration boundary — is on the events themselves. F1/F2 are uniform passes (this is the [ADR-PC-000 §D3](./ADR-PC-000-namespace-and-contract-shape-framework.md) operational-discipline category); the choice rests on S2 coherence with the event-sourced model.

**Rejected: projection columns** — a projection is replay output; sourcing the pin from it is circular and cannot encode the pre/post-migration split. **Rejected: separate `instance_pins` registry** — same inability to represent the migration boundary, plus a second non-atomic write at constitution that re-introduces the dual-write problem.

---

## Implementation Principles

### P1 — The pin is per-event; constitution stamps it, every later event copies it

Every event carries `pack_version` and `schema_version` ([ADR-PC-001 §P1](./ADR-PC-001-event-store-technology.md)). At `DepositConstituted` the engine resolves the **currently-active** pack version (the version the operating bank has adopted for new constitutions — see P4) and the **active family-schema version** for the product family, and stamps both onto the event. Every subsequent lifecycle event for that instance copies the instance's *current* pin (the value carried by its latest event) — **until** a migration event changes it (P3). The pin is never re-derived from wall-clock time after constitution; it is data flowing forward on the stream.

### P2 — Resolution through two registries; replay reads the pin off the event, not the clock

Two registry tables resolve a pinned version string to the concrete artefact:

- **`pack_versions`** — `(pack_id, pack_version) → OCI digest + signature digest` ([ADR-PC-007 §P3](./ADR-PC-007-signed-yaml-oci-pack.md), already specified).
- **`schema_versions`** — `(family, schema_version) → CUE family-schema digest + location` ([ADR-PC-006](./ADR-PC-006-cue-schema-language.md)). New in this ADR; the schema-language analogue of `pack_versions`.

A handler replaying instance `I`'s event at sequence `N` reads `pack_version` / `schema_version` **from that event's envelope**, then resolves each through its registry to the in-memory-cached artefact ([ADR-PC-007 §P4](./ADR-PC-007-signed-yaml-oci-pack.md): validate-then-cache, fail-loud). "The pack/schema active for instance `I` at the handler running at `T0`" is answered by the event, not by a time-range query — so replay is deterministic regardless of *when* it runs. The two registries are kept **separate, not merged**, because packs and schemas have different owners and cadences ([authoring §6](../feature-design-configuration-authoring.md): "sharing the version space would couple two cadences that should remain independent").

### P3 — `PackVersionMigrated` / `SchemaVersionMigrated`: operator-initiated, one per instance, pin changes from that sequence forward

Both are cross-cutting engine-declared events ([02 §2.4.2](../02-v1-scope-term-deposits.md)):

```
PackVersionMigrated   { instance_id, from_pack_version,  to_pack_version,   migration_id, operator_actor }
SchemaVersionMigrated { instance_id, from_schema_version, to_schema_version, migration_id, operator_actor }
```

- **Emission.** Operator-initiated only: `POST /v1/pack-migrations` ([surface §3.6](../feature-design-configuration-surface.md)) or `POST /v1/schema-migrations` ([authoring §6](../feature-design-configuration-authoring.md)), each with an explicit `instance_filter`; the engine emits **one event per affected instance**. There is no time-triggered or background migration.
- **Ordering / delivery.** The migration event is appended to the instance stream like any other event, in `(stream_id, sequence_number)` order, through the same atomic append + outbox write ([ADR-PC-001 §P2](./ADR-PC-001-event-store-technology.md)). Events appended *after* it carry the `to_*` version; events *before* it remain pinned to `from_*`.
- **Downstream reaction.** Projection rebuild treats the migration event as the point where the instance's effective pin switches; replaying the instance under the old pack (history before `M`) remains possible — the "reversible-in-principle" / counterfactual property ([surface §3.6](../feature-design-configuration-surface.md), [event-store §2](../feature-design-event-store-projections.md)). Idempotency: `migration_id` is the dedupe key; re-issuing a migration for an already-migrated instance is a no-op.

This is what makes retroactive regulatory change ("from 2027-01-01 the new rate applies to all existing instances") expressible **without violating pinning**: the pin still never silently moves; it moves only at an explicit, audited, per-instance event ([surface §3.6](../feature-design-configuration-surface.md)).

### P4 — Q-N breaking-change opt-in: adoption ≠ migration; no silent upgrades

A pack version with a non-empty `breaking_changes` block ([surface §3.4](../feature-design-configuration-surface.md), [ADR-PC-007 §P1](./ADR-PC-007-signed-yaml-oci-pack.md)) is **not adopted by anything** until an explicit operator action; the engine logs an `OperatorAck` ([surface §3.11 Q-N](../feature-design-configuration-surface.md)). Two distinct verbs, deliberately separated:

- **Adoption** — `POST /v1/pack-adoptions` with explicit acknowledgement of each breaking item: sets which pack version **new** constitutions pin going forward (changes the "currently-active" version P1 resolves).
- **Migration** — `POST /v1/pack-migrations` (P3): re-pins **existing** instances.

Neither happens silently or in the background. A newly published pack sits inert in the registry until adopted; existing instances never move until migrated. This is the [01 §5](../01-product-architecture.md) "first-class swap point, read at runtime, not baked in" made operationally safe.

### P5 — Pack-effective-date is a reserved no-op placeholder in v1

The pack manifest carries `pack_effective_from` ([surface §3.4](../feature-design-configuration-surface.md)). v1 **pins everything at constitution and floats nothing** — there is no per-primitive pin-or-float policy. The field is reserved so the manifest shape does not have to change later, but v1 reads it as informational metadata only. The per-primitive pin-or-float policy (some primitives float to the latest pack while others stay pinned) is **explicitly deferred** — it is on the epic's out-of-scope list ("pack-effective-date semantics policy … v2+ peers, tracked in [04-open-questions](../04-open-questions.md)") and is a [surface §3](../feature-design-configuration-surface.md)-class v2 question. No partial implementation ships in v1.

---

## Consequences

**What this choice makes easier:**

- "What pack and schema governed this instance, for its whole life?" is answerable from the event stream alone — including the exact sequence where any migration changed it. The DORA/PSD2 audit answer needs no external join.
- Replay is deterministic independent of when it runs: the pin is data on each event, never a wall-clock lookup.
- The migration boundary is intrinsic to the stream — counterfactual replay under the old pack (history before the migration) is structurally available ([event-store §2](../feature-design-event-store-projections.md)).
- Constitution writes the pin in the same atomic append as the event ([ADR-PC-001 §P2](./ADR-PC-001-event-store-technology.md)); no second write, no dual-write risk.
- `pack_version`, `schema_version`, and `rate_sheet_version_id` ([ADR-PC-008](./ADR-PC-008-rate-sheet-storage-and-deploy-api.md)) are one consistent per-event pinning family — one mental model for three pins.

**What this choice makes harder or impossible:**

- The pin is duplicated on every event of a stream (the same two strings repeat across an instance's ~24–1000 events). This is intentional event-sourcing redundancy — storage is cheap and the alternative (a single mutable current value) is the rejected B/C that cannot represent migration. Not a normalisation defect.
- Changing an instance's pin is *only* possible via an explicit migration event — there is deliberately no in-place edit path. Correct, but it means even an operator "oops, adopted too early" is itself a forward migration, never an undo.

**Residual risks:**

- **`schema_versions` registry drift from the CUE artefacts.** A pinned `schema_version` must always resolve to a retrievable CUE schema. Mitigation: the same validate-then-cache, fail-loud load discipline as packs ([ADR-PC-007 §P4](./ADR-PC-007-signed-yaml-oci-pack.md)); a pin resolving to a missing schema is a fail-loud startup error, never a silent skip.
- **Indefinite schema retention.** Because an in-flight 12-year instance can stay pinned to `term_deposit@2026.1` for 12 years, that schema version must remain resolvable for that long — the same indefinite-retention obligation packs carry ([ADR-PC-007](./ADR-PC-007-signed-yaml-oci-pack.md) Consequences). Mitigation: keep-forever retention on the schema registry, mirroring the pack registry.
- **Migration-set correctness.** A `pack-migrations` call with a wrong `instance_filter` could re-pin instances that should not move. Mitigation: the filter result is previewable before emission; every emitted `PackVersionMigrated` records `operator_actor` and `migration_id`, so the affected set is fully auditable and the migration is reversible-in-principle by replay.
- **Deferred pin-or-float (P5)** could, when it lands in v2, want per-event granularity finer than one `pack_version` string. Mitigation: deferring with a reserved manifest field keeps the v2 design space open without committing v1 to a shape it cannot honour.

---

## Cross-references

- [ADR-PC-001 §P1–§P2](./ADR-PC-001-event-store-technology.md) — the envelope already carries `pack_version` / `schema_version`; the atomic append this pin rides on.
- [ADR-PC-007 §P3–§P4](./ADR-PC-007-signed-yaml-oci-pack.md) — the `pack_versions` registry, per-instance pinning via `pack_version`, and the validate-then-cache/fail-loud load discipline `schema_versions` mirrors.
- [ADR-PC-006](./ADR-PC-006-cue-schema-language.md) — CUE family schemas are the artefact `schema_version` resolves to via the new `schema_versions` registry.
- [ADR-PC-008](./ADR-PC-008-rate-sheet-storage-and-deploy-api.md) — `rate_sheet_version_id` is the third member of the per-event pinning family; rate sheets pin at constitution but are never migrated.
- [01 §5](../01-product-architecture.md) — the two parallel stability invariants and the explicit-retroactive-change rule.
- [surface §3.5–§3.6, §3.11 Q-N](../feature-design-configuration-surface.md) — pack pinning, retroactive migration mechanics, breaking-change opt-in.
- [authoring §6](../feature-design-configuration-authoring.md) — schema-version pinning and the separate-version-space rationale.
- [02 §2.4.2](../02-v1-scope-term-deposits.md) — `PackVersionMigrated` / `SchemaVersionMigrated` payloads.

---

*Decided 2026-05-23 by jhosm.*
