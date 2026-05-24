# ADR-PC-017: Legacy Batch Ingest Contract

| Field | Value |
|---|---|
| Status | Accepted |
| Date | 2026-05-23 |
| Shape | Contract-shape |
| Counterparty | The operating bank's legacy core (DDA) as batch-extract producer, parsed by the Deposits ACL ([ADR-IC-012](../../integration_concepts/adrs/ADR-IC-012-anti-corruption-layer-implementation.md)) |

---

## Context

The legacy current-account adapter ([ADR-PC-016](./ADR-PC-016-legacy-current-account-adapter.md)) handles the engine → legacy *command* direction. This ADR handles the opposite direction: how legacy's own state reaches the engine. [Coexistence §5](../feature-design-strangler-fig-coexistence.md) commits the mechanism — **a daily batch file**, not Redpanda events and not a CDC stream. Legacy is a multi-decade core never designed for event emission; it almost certainly already produces an end-of-day extract for some downstream consumer (GL, regulatory reporting, the data warehouse). Adding the engine as another consumer is days of work and zero risk to legacy's write path, whereas native event emission or CDC is months of work touching code nobody on the current team wrote, with real risk of destabilising legacy itself ([coexistence §5.1](../feature-design-strangler-fig-coexistence.md)). The price is latency: legacy-sourced data carries up to 24 hours of staleness, and the engine's coexistence guarantees are **eventual, not real-time, on the legacy side**.

Each ingested record becomes one `LegacyInstanceObserved` event — the cross-cutting, engine-declared event from [02 §2.4.2](../02-v1-scope-term-deposits.md) and [event-store §4.1](../feature-design-event-store-projections.md). This ADR fixes the contract shape of that file. The precise format, cutoffs, and dedupe keys depend on what the operating bank's legacy core can produce — [Q-AH](../04-open-questions.md), unblocked by a legacy-extract audit (the peer of [ADR-PC-016](./ADR-PC-016-legacy-current-account-adapter.md)'s [§12](../feature-design-strangler-fig-coexistence.md) meeting). This is a contract-shape ADR ([ADR-PC-000 D3](./ADR-PC-000-namespace-and-contract-shape-framework.md)): no tool is bought, and the rigor is in completing the six slots well enough that the legacy team and the engine team can build against the seam independently.

---

## Decision

### 1 · Payload shape

A once-daily flat-format extract covering the day's facts for the product families the engine cares about (term deposits, for v1). The contract binds the **canonical record fields** and the **`fact_kind` taxonomy** — **not** the wire encoding. Following the same logic [ADR-IC-012 D3](../../integration_concepts/adrs/ADR-IC-012-anti-corruption-layer-implementation.md) applies to inbound mechanisms ("an external constraint, not a choice the architecture makes"), the encoding is whatever legacy natively emits — CSV, fixed-width, JSONL, or Avro — and the ACL carries a per-format parser that normalises it to the canonical record. A delimited text format with a versioned header is the recommended default when legacy has a free choice, but the binding contract is the field set, not the bytes.

Per-record canonical fields ([coexistence §5.2](../feature-design-strangler-fig-coexistence.md)):

- `legacy_instance_id` — legacy's identifier for the instance.
- `fact_kind` — one value from a **closed taxonomy**: `constituted`, `interest_accrued`, `interest_paid`, `matured`, `terminated_early`, `partially_withdrawn`, `corrected`, `transferred_to_heirs`. Each maps onto a family event the engine already declares in [02 §2.4](../02-v1-scope-term-deposits.md) and [event-store §4.2](../feature-design-event-store-projections.md); adding a `fact_kind` is a versioned contract change (slot 6).
- `fact_date` — when the fact occurred in legacy's books.
- `legacy_state_snapshot` — the instance's state after the fact (principal, accrued interest, withholding to date, lifecycle state).
- Family-specific payload per `fact_kind` (principal cents, rate, term, maturity date, …).

The file header carries a **schema version** (slot 5/6). The ACL parses each record and emits exactly one engine event:

```
LegacyInstanceObserved {
  legacy_instance_id, observed_at, legacy_state_snapshot,
  batch_file_id, fact_kind, <family-specific payload>
}
```

### 2 · Semantics

`LegacyInstanceObserved` means **"we observed that legacy reports this fact"** — the engine's truthful record of *what legacy told us*. It is emphatically **not** "this fact is true in our domain" ([coexistence §5.2](../feature-design-strangler-fig-coexistence.md)). Legacy retains system-of-record for the instance ([coexistence §3](../feature-design-strangler-fig-coexistence.md)); the engine's view of it is a derived projection with the staleness profile above. The batch file is **not** the engine's event store — records are never committed as native domain events. When a legacy instance later migrates to the engine via renewal ([coexistence §9](../feature-design-strangler-fig-coexistence.md)), the engine emits its own native `DepositConstituted` (linked by `causation_id` to the triggering `LegacyInstanceObserved` and carrying `originating_legacy_id`), and the chain of `LegacyInstanceObserved` events remains as the audit trail of the legacy lifetime. This semantic separation is what keeps the engine from forking into a legacy-aware hybrid.

### 3 · Ordering and delivery guarantees

Once-daily, **all-or-nothing per day** ([coexistence §5.3](../feature-design-strangler-fig-coexistence.md) completeness contract): either every fact for the day is in the file or no file is shipped. Partial files are rejected (slot 5). Up to 24-hour staleness; the engine orders facts by `fact_date`, then by ingestion order within the file. There is no intra-day real-time guarantee — that is the deliberate trade for zero risk to legacy's write path. **Lateness contract:** the file must be available to the engine by a published cutoff (e.g. 04:00 local time on day D+1); a missed cutoff pages operations rather than passing silently. The `as_of` timestamp the unified read surface stamps on legacy-sourced rows is the engine's *ingestion* time, which lags `fact_date` by up to 24 hours — and that gap is observable, not hidden ([coexistence §6.2](../feature-design-strangler-fig-coexistence.md): bitemporal pair *(valid_time = fact_date, transaction_time = ingestion_time)*).

### 4 · Idempotency

A batch file can be **re-ingested without producing duplicate `LegacyInstanceObserved` events** — file replay is safe, which is what makes recovery-after-parse-failure a re-run rather than a surgical repair. The dedupe key is `(legacy_instance_id, fact_kind, fact_date)` plus a fact-specific natural key where the triple is not unique (e.g. the payment date for a periodic-interest payment) ([coexistence §5.3](../feature-design-strangler-fig-coexistence.md)). Dedupe runs **engine-side**, in the ACL's ingestion path, against its own dedup store (the `inbound_event_dedup` table per [ADR-IC-012 P1](../../integration_concepts/adrs/ADR-IC-012-anti-corruption-layer-implementation.md)). `batch_file_id` is recorded on every event for provenance and to tie a row in the read model back to the file that produced it (`source = legacy_batch_file_<id>` per [coexistence §6.2](../feature-design-strangler-fig-coexistence.md)).

### 5 · Error model

**Fail-loud at the file level, gated not post-flagged.** Three rejection conditions, each paging an operator rather than degrading silently ([coexistence §5.3](../feature-design-strangler-fig-coexistence.md)):

- **Unknown schema version** — the ACL rejects the file and pages, rather than guessing field positions and silently dropping or misparsing records.
- **Partial file** — a file that violates the all-or-nothing completeness contract is rejected whole.
- **Malformed record** — because a partially-ingested day breaks the completeness invariant that reconciliation flow 2 depends on, a single unparseable record rejects the **entire file**; the day's ingestion blocks until a clean file arrives. There is no partial-ingest mode.

This is the deliberate inverse of a permissive parser. A batch ingest that "skips the bad rows and carries on" produces an engine view that is silently incomplete — the worst failure mode for a system whose whole job during coexistence is to not lose track of legacy state.

### 6 · Ownership and versioning

The batch file is a **public contract between legacy and the engine even though both live inside the same bank** ([coexistence §5.3](../feature-design-strangler-fig-coexistence.md)). Legacy owns *production* of the file; the engine owns *parsing* it (inside its ACL). **Schema-drift protocol:** when legacy changes its extract — a new, removed, or renamed column, or a rate-scaling change — the change is coordinated through a written contract update, and the engine's ACL parser is updated to the new shape **before** the new extract ships. The header schema-version is the coordination handle: a version bump is the signal that the ACL must already understand the new shape, and the slot-5 unknown-version rejection is the safety net if coordination fails. Adding a `fact_kind` to the closed taxonomy is a versioned, coordinated change for the same reason. The canonical-record parser is gated by consumer-driven contract tests ([ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md)).

### Reconciliation invariant

This contract is one half of **reconciliation flow 2** ([coexistence §7.3](../feature-design-strangler-fig-coexistence.md)): the engine's projected view of every legacy instance (built from the cumulative `LegacyInstanceObserved` stream) must be consistent with today's batch file after applying today's facts. The invariant: **every legacy instance the engine reports must map to a corresponding fact in legacy's view, and vice versa.** The reconciler classifies divergence as engine-side gap (ingestion failure or a dropped record — an idempotency bug), legacy-side gap (legacy archived an instance, or definitional drift), or state-mismatch (the most concerning — a lost fact or an out-of-band legacy change the file did not surface). Mismatch counts feed the [Q-AG](../04-open-questions.md) alert thresholds, calibrated under real-data load.

---

## Consequences

**What this makes easier:**

- **Near-zero legacy-side effort and risk.** Reusing the existing end-of-day extract keeps the engine off legacy's write path entirely ([coexistence §5.1](../feature-design-strangler-fig-coexistence.md)).
- **The engine stays ignorant of legacy's data shape.** The ACL absorbs the native format; the engine sees only the canonical `LegacyInstanceObserved`. This keeps the engine's data model clean and is what lets regulatory reporting aggregate downstream rather than forcing the engine to read legacy data ([coexistence §8.1](../feature-design-strangler-fig-coexistence.md)).
- **The contract outlives term-deposit coexistence.** `LegacyInstanceObserved` stays a declared engine event even after the last term deposit migrates, because future product families reuse the pattern ([coexistence §11.3](../feature-design-strangler-fig-coexistence.md)).

**What this makes harder or locks in:**

- **Eventual, not real-time, legacy data.** The 24-hour staleness is inherited by the unified read surface and by reconciliation, and surfaces as a per-channel tolerance question ([Q-AF](../04-open-questions.md), [coexistence §6.3](../feature-design-strangler-fig-coexistence.md)). Channels that need intraday legacy state (e.g. real-time liquidity risk) cannot rely on the batch-sourced rows.
- **All-or-nothing rejection couples the engine's daily clock to legacy's file delivery.** A late or malformed file blocks the day's legacy view until resolved — chosen deliberately over silent partial ingest, but it makes file-delivery reliability an operational dependency.

---

## Residual risks

- **Exact format, cutoffs, and dedupe keys are a production input, not settled here** ([Q-AH](../04-open-questions.md)). This ADR is **Accepted** on the contract *shape*; the concrete values come from the legacy-extract audit, peer to the [ADR-PC-016](./ADR-PC-016-legacy-current-account-adapter.md) inventory meeting. Until that audit, the format-agnostic parser design is what keeps the commitment honest.
- **Out-of-band legacy changes** that never appear in the extract surface only as flow-2 state-mismatches after the fact ([coexistence §7.3](../feature-design-strangler-fig-coexistence.md)); the contract cannot catch what legacy does not emit.
- **Q-AG thresholds uncalibrated** — flow-2 mismatch counts have no noise-vs-incident boundary until a real-data calibration period sets one.
- **CDC and native event emission are rejected for v1** ([coexistence §5.1](../feature-design-strangler-fig-coexistence.md)), accepting the 24-hour latency as the cost of zero write-path risk. The format-agnostic design accommodates a future reversal: if a later legacy core can emit events cheaply, a streaming adapter is simply another parser behind the same `LegacyInstanceObserved` contract — the engine side does not change.
- **What this contract does not commit to:** the downstream reporting application's own ingestion of legacy facts ([Q-AE](../04-open-questions.md), [coexistence §8](../feature-design-strangler-fig-coexistence.md)), and the settlement (engine → legacy) direction, which is [ADR-PC-016](./ADR-PC-016-legacy-current-account-adapter.md).

## Verifiable commitments

| # | Commitment | Gate (pyramid level) | Test ID | Status |
|---|---|---|---|---|
| C1 | A batch file can be **re-ingested without producing duplicate `LegacyInstanceObserved` events** (§Decision · Idempotency) — file replay is safe, so recovery after a parse failure is a re-run, not a surgical repair. | integration / Testcontainers | `BATCH_INGEST_IDEMPOTENT` | Planned |

Seeded in the [commitment catalogue](../../../../conformance/README.md) ([`commitments.yaml`](../../../../conformance/commitments.yaml)) per [ADR-PC-020 §P5/§P7](./ADR-PC-020-llm-toolchain-and-conformance-governance.md) (Open Action #4).
