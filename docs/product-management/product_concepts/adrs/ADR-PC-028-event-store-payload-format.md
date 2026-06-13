# ADR-PC-028: Event-Store Payload Format — Self-Describing JSON, Decoupled from the Bus's Avro

| Field | Value |
|---|---|
| Status | Proposed |
| Date | 2026-06-10 |
| Deciders | jhosm |
| Shape | Tool-selection |
| Common criteria | [ADR-IC-000](../../integration_concepts/adrs/ADR-IC-000-common-evaluation-criteria.md) (reused per [ADR-PC-000](./ADR-PC-000-namespace-and-contract-shape-framework.md) D2) |
| Depends on | [ADR-PC-001](./ADR-PC-001-event-store-technology.md) (event store), [ADR-IC-002](../../integration_concepts/adrs/ADR-IC-002-schema-format-and-registry.md) (bus format), [ADR-IC-004](../../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md) (outbox) |
| Resolves | bd `babelstone-36mk` |

## Context

The engine is event-sourced ([ADR-PC-001](./ADR-PC-001-event-store-technology.md)): the `events` table is the **system of record**, and every projection is a deterministic fold rebuilt from it ([event-store §1](../feature-design-event-store-projections.md)). Each row's business payload is stored in the `payload` column.

**The format of that column was never decided on its own merits — it was inherited.** [ADR-IC-002](../../integration_concepts/adrs/ADR-IC-002-schema-format-and-registry.md) chose **Avro + Confluent Schema Registry**, and it scopes itself explicitly to **the bus**: *"the wire format for all integration events published to Redpanda topics"*, and *"the schema registry manages only the business payload schema."* It says nothing about internal storage. The event store came to hold Avro only because [ADR-PC-001 §P2](./ADR-PC-001-event-store-technology.md) writes the `events` row and the `outbox` row in **one transaction**, and the outbox carries Avro for the bus ([ADR-IC-004 §P3](../../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md)) — so the `events.payload` was made to **mirror** the outbox payload by structural co-location. The engine ships a `JsonEventSerializer` (`HostServices.cs`) as the *deferred-Avro stand-in*, and the skeleton (`event-store-skeleton.md §8`) defers only **which Avro library** the bus codec lands — neither treats *whether the store should be Avro* as a decision.

That inheritance carries a cost the bus decision never weighed for a system of record. [ADR-IC-002 §Consequences](../../integration_concepts/adrs/ADR-IC-002-schema-format-and-registry.md) states the hazard plainly — *"the schema ID in the Avro message header is meaningless without the registry"* — and its Residual Risks add: losing the registry's `_schemas` topic *"makes all persisted Avro events unreadable."* For a **bus**, that is tolerable (consumers cache; messages are transient). For the **book of record** — a banking event log that must outlive any single piece of infrastructure and stay auditable for years — making it undecodable without a second live service is the wrong coupling. A binary writer≠reader format also forces replay to resolve each event's *writer* schema before it can fold (the machinery the now-obsoleted `babelstone-ohdk` line of work began) — work a self-describing format does not need.

This ADR makes the storage-format choice an explicit decision, separate from the bus.

**Candidates** (for the `events.payload` column only — the bus stays Avro regardless):

| Candidate | What it is |
|---|---|
| **Self-describing JSON** (System.Text.Json) | Field names travel with the data; readable and decodable with no external schema or registry. |
| **Avro-in-store** (status quo inheritance) | Binary; `events.payload` mirrors the outbox's Avro bytes; decode needs the writer schema resolved from the Schema Registry by `payload_schema_id`. |

## Evaluation

### Hard filter results

#### F1 · Cost / licensing

| Candidate | Licence / cost | Verdict |
|---|---|---|
| Self-describing JSON | System.Text.Json ships in the .NET 10 BCL ([ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md)); no dependency, no cost | Pass |
| Avro-in-store | Apache 2.0; no cost | Pass |

#### F2 · Regulatory fit (GDPR / DORA / PSD2)

| Candidate | GDPR (data minimisation / erasure) | DORA (operational resilience) | Verdict |
|---|---|---|---|
| Self-describing JSON | PII is field-encrypted **before** serialisation ([ADR-PC-004 §P2](./ADR-PC-004-pii-crypto-shredding.md)); ciphertext rides as base64 — minimisation of PII preserved. Structural (non-PII) fields are readable at rest, which aids audit and is not a minimisation concern (structural facts are not personal data). | Removes the Schema Registry as a **recovery dependency** of the system of record: the log replays with the registry down or its `_schemas` lost. A net resilience improvement. | Pass |
| Avro-in-store | PII ciphertext inside binary; structural fields also opaque at rest (stronger minimisation of *structural* data, which is not the GDPR target). | Registry availability and `_schemas` backup become **recovery-critical** for reading history — the documented "unreadable without the registry" risk lands on the book of record. | Pass (conditional) — mitigation: registry `_schemas` included in the backup/restore drill ([ADR-IC-002](../../integration_concepts/adrs/ADR-IC-002-schema-format-and-registry.md) Residual Risks), restated below. |

Both pass the hard filters; the decision rides on S1–S4 and the residual-risk asymmetry.

### Soft criteria

#### Self-describing JSON — CHOSEN

- **S1 · Operational complexity (1–2 people).** Lowest. Replay and disaster recovery of the system of record need **no Schema Registry, no writer-schema resolution, no `payload_schema_id` decode path**. One fewer live dependency on the critical recovery path. Decisive for a small team that must be able to read its own history with a text editor and `jq` when something is on fire.
- **S2 · Ecosystem coherence.** This is JSON's only real cost: the store no longer shares bytes with the Avro bus, so the write path must **encode twice** (JSON for `events.payload`, Avro+`schema_id` for the `outbox` row) and two serialisations of the same event exist. The cost is **bounded** — both encodes happen in-process inside the existing single append transaction, and the bus contract ([ADR-IC-004 §P3](../../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md)) is unchanged. A fitness test pins store↔bus equivalence so the two cannot silently skew.
- **S3 · Exit cost.** Lowest of any format — self-describing JSON is portable to any tool, any language, with no schema artefact required. The book of record is never hostage to a codec.
- **S4 · Community and longevity.** JSON outlives any particular serialization library; a self-describing log is the most future-proof substrate for a record that must be readable in decades.

**Decisive reason:** the system of record must be **readable without the Schema Registry**. Everything else is secondary to that property for a banking event log.

#### Avro-in-store — rejected

Coheres with the bus (one codec, no dual-encode) and is more compact, but it makes the book of record **undecodable without a second live service**, and forces per-event writer-schema resolution on every replay. The compactness and single-codec savings do not justify coupling the durability of the system of record to Schema Registry availability and `_schemas` backup integrity. **Decisive reason against:** "lose the registry → lose the ability to read history" is unacceptable for the system of record (acceptable only for the transient bus).

## Decision

The `events.payload` column is **self-describing JSON** (System.Text.Json), decodable with no Schema Registry and no writer-schema resolution. This is a **permanent, deliberate** choice, not the skeleton's temporary stand-in.

**Avro + Confluent SR ([ADR-IC-002](../../integration_concepts/adrs/ADR-IC-002-schema-format-and-registry.md)) remain the bus wire format, unchanged.** When real Avro-on-the-bus lands, the write path **dual-encodes inside the existing single append transaction** ([ADR-PC-001 §P2](./ADR-PC-001-event-store-technology.md)):

- `events.payload` ← JSON (the system of record);
- `outbox.payload` ← Avro bytes + `schema_id` embedded at write ([ADR-IC-004 §P3](../../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md) intact — the publisher still frames the Confluent wire format with **no** registry lookup at publish time).

The event-store payload is **never migrated to Avro**; the forthcoming Schema-Registry/Avro integration applies to the outbox/bus encoding only.

This **amends [ADR-PC-001 §P1](./ADR-PC-001-event-store-technology.md)** (the `payload` column is JSON, not Avro-serialized — see that ADR's dated amendment) and **obsoletes** the replay-side Avro writer-schema work (`babelstone-ohdk`) and its follow-ups (`babelstone-4na5`, `babelstone-4tjj`): with a self-describing store there is no writer≠reader resolution to thread on replay.

## Consequences

**Easier:**
- The system of record replays and recovers **with the Schema Registry down or its `_schemas` lost** — the documented "unreadable without the registry" risk no longer touches the book of record.
- Replay is simpler: self-describing decode, no per-event writer-schema resolution, no `payload_schema_id` on the decode path.
- The raw event log is **auditor-readable** without tooling; structural facts are inspectable directly.

**Harder / impossible:**
- The write path **encodes the event twice** once Avro-on-bus lands (JSON for the store, Avro for the outbox) — two serialisations of the same event coexist.
- `events.payload` is **larger** than the equivalent Avro (text vs binary; [ADR-IC-002](../../integration_concepts/adrs/ADR-IC-002-schema-format-and-registry.md) cites ~5–10×) — a storage-cost trade accepted in exchange for durability and readability of the record.

**Residual risks:**
- **Store↔bus skew.** Two encodings of one event could diverge. Mitigation: `STORE_BUS_ENCODING_EQUIVALENCE` (below) asserts semantic equality.
- **JSON determinism.** Replay determinism ([ADR-PC-010 §P5](./ADR-PC-010-dotnet-hand-rolled-engine.md)) requires the store codec to be deterministic. System.Text.Json serialises properties in declaration order (stable per record type); the codec must not depend on dictionary/`Dictionary<>` ordering. Pinned by the codec-hardening follow-up.
- **Codec promotion.** `JsonEventSerializer` was a stand-in; it must be hardened (deterministic order, PII-ciphertext handling, explicit versioning) as the *decided* store codec (`babelstone-36mk`).
- **`payload_schema_id` reinterpreted.** The column stays as the cross-reference to the outbound Avro encoding ([ADR-IC-004 §P3](../../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md)); it does **not** govern decode of the JSON `events.payload`.
- **Per-instance pinning unaffected.** `pack_version` / `schema_version` remain versioned-string envelope columns ([ADR-PC-009](./ADR-PC-009-per-instance-version-pinning.md)); `REPLAY_PIN_PER_EVENT` is untouched by the payload-format choice.

## Verifiable commitments

Not yet catalogued centrally — these are new with this ADR (a `Gap`/`Planned` here is a deliberate, listed hole, tracked by bd `babelstone-36mk`):

| # | Commitment (with §-anchor) | Gate (pyramid level) | Test ID | Status |
|---|---|---|---|---|
| 1 | §Decision — the `events.payload` decodes with **no Schema Registry**: the replay/decode spine (`Babelstone.Engine`, `Babelstone.EventStore`) references no registry client / Avro-SR codec (structural fitness), and the decided store codec `JsonEventSerializer` round-trips from the bytes alone (behavioural) | Fitness + unit (default lane) | `EVENT_STORE_PAYLOAD_SELF_DESCRIBING` | **Live** |
| 2 | §Decision — for any event, the JSON `events.payload` and the Avro `outbox.payload` are **semantically equal** (no store↔bus skew) | Integration (Testcontainers Redpanda SR + PG) | `STORE_BUS_ENCODING_EQUIVALENCE` | **Live** (*Flipped 2026-06-13:* the dual-encode split shipped — the runtime now holds a STORE codec (JSON → `events.payload`) and a separate BUS codec (Avro + registered `schema_id` → `outbox.payload`), both committing in the one append transaction. `StoreBusEncodingEquivalenceTests` appends a catalogued event, decodes the JSON store payload AND the Avro outbox payload, and asserts they reconstruct the same event — same field values, money as integer cents; bd `babelstone-36mk`) |
