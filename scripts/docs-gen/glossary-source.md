<!-- Hand-authored TERM SOURCE for the glossary. Rendered (sorted + ADR-linkified
     + bannered) to docs/product-docs/reference/glossary.md by docs-gen.
     This file is the single source of the vocabulary; edit terms HERE, then run
     `make docs-gen`. One row per term: | Term | Definition |. ADR ids in a
     definition are linkified automatically. -->

| Term | Definition |
|---|---|
| ACL (anti-corruption layer) | The boundary service that translates between the engine's domain model and a legacy/external system, isolating the engine from foreign shapes. Implemented per ADR-IC-012. |
| Accrual | The periodic recognition of earned-but-unpaid interest; emitted as the `InterestAccrued` event. The maths is defined in the financial-concepts reference. |
| Bitemporality | Every projection row carries both *valid* time (`valid_from`/`valid_to`) and *system* time (`recorded_at`/`superseded_at`), so the engine can answer "what did we believe, as of when". Application-level on PostgreSQL per ADR-PC-002. |
| Commitment catalogue | The registry of load-bearing invariants ("fitness functions") the engine must not silently drift from, each bound to a Test ID. The single source of truth governed by ADR-PC-020. |
| Constitution | Opening a term deposit — the v1 flagship write path, run as an orchestrated saga (the constitution saga). |
| Crypto-shredding | GDPR erasure by destroying a per-subject encryption key, rendering the ciphertext unrecoverable while structural fields stay cleartext. The engine's one managed-secret exception, per ADR-PC-004. |
| Day count | The convention mapping a calendar period to an interest fraction (e.g. Act/360 for PT retail deposits); a pack-bound primitive, not hard-coded. |
| Decider | The per-family, command-side function that turns a command into events — running the financial-math kernel, resolving rate sheet + pack primitives, and appending. Family-owned, never in the generic engine, per ADR-PC-021. |
| Event envelope | The metadata wrapper carrying each event's identity, ordering, and the pinned `pack_version` / `schema_version` (ADR-PC-009) — so an instance's governing schema+pack is answerable from its events alone. The business payload rides separately (the Avro record). |
| Explicit-drift gate | The rule that no change may contradict an Accepted ADR without an amendment or supersession in the *same* change — divergence is allowed, silent divergence is not. Defined in ADR-PC-020 §D3. |
| Family | A product family (e.g. `term_deposit`) — the unit of domain logic the engine hosts as a plugin. The engine is family-agnostic; adding a family is zero generic-engine diff (ADR-PC-021). |
| Fold | A pure, deterministic function that applies one event to aggregate state. Folds carry no clock, I/O, or randomness — the property the determinism gate enforces (ADR-PC-010). |
| Idempotency key | The stable key (e.g. `event_id`, `correlation_id`) a receiver dedupes on so at-least-once delivery never double-applies an effect. One of the six integration primitives. |
| MCP (Model Context Protocol) | The protocol by which the bank is exposed as a server of `tools`/`resources` to LLM agents (the agent channel). Runtime + SDK chosen in ADR-IC-010; strategy in integration-concepts 11. |
| Money / cents | Money is an integer count of EUR cents, never a float. The exact rounding rule and the single boundary it is applied at are fixed by ADR-PC-010 and enforced by the Money analysers. |
| Outbox | The transactional-outbox table that lets an event and its publish-intent commit together as one unit. The atomicity contract and mechanism are ADR-IC-004 (and ADR-PC-001 for the engine's own outbox). |
| Pack (regulatory pack) | A `pt.YYYY.N` bundle of auditor-readable YAML data (primitives, parameters, rate-sheet refs, sealed test corpus) plus bundled CUE schemas, cosign-signed and distributed as an OCI artefact pulled by digest. Format in ADR-PC-007. |
| Projection | A read model derived by folding the event log, rebuildable at any time; the query side of CQRS. Bitemporal on PostgreSQL per ADR-PC-002. |
| Rate sheet | Versioned, immutable pricing data resolved at constitution by an indexed point-in-time query; the resolved TAN is stamped onto the deposit and never moves. Storage + deploy API in ADR-PC-008. |
| Saga | A multi-step flow with compensation (not distributed transactions); the hybrid orchestration + choreography model. Orchestrator in ADR-IC-003; walkthrough in integration-concepts 05. |
| Snapshot | A recomputable cache of aggregate state that speeds rebuilds but never replaces the log; advisory until proven by discard-and-rebuild drills. Per ADR-PC-003. |
| SoR (system of record) | During coexistence, the authoritative owner of a given deposit (legacy core vs the engine); set at constitution and never flipped. Routing exposes the `sor`, not the routing logic (ADR-PC-018). |
| Strangler fig | The incremental migration posture by which the engine takes over slices of a legacy estate without a big-bang cutover; the coexistence design. |
| TAN (taxa anual nominal) | The nominal annual interest rate, carried in basis points and resolved from the rate sheet at constitution. Distinct from the effective rate (TAE/TAEG). |
| TAE / TAEG | The effective annual rate (TAEG = the all-in comparison rate), derived from the cash flows; the financial-concepts reference defines the computation. |
| Variant | A specific product configuration (YAML) an author writes, validated against a closed family schema with no DSL escape hatch (CUE; ADR-PC-006). |
| Withholding | Tax withheld at source on interest (the `WithholdingApplied` event), applied flow-by-flow; the rule is in the financial-concepts reference. |
