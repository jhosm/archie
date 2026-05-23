# ADR-PC-002: Bitemporal Projection Implementation — Application-Level Bitemporality on PostgreSQL

| Field | Value |
|---|---|
| Status | Proposed (gated by Q-Y — see §Gate) |
| Date | 2026-05-23 |
| Deciders | jhosm |
| Shape | Tool-selection |
| Common criteria | [ADR-IC-000](../../integration_concepts/adrs/ADR-IC-000-common-evaluation-criteria.md) (reused per [ADR-PC-000](./ADR-PC-000-namespace-and-contract-shape-framework.md) D2) |
| Depends on | [ADR-PC-001](./ADR-PC-001-event-store-technology.md) (PostgreSQL event store), [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) (hand-rolled .NET engine; owns the projection-apply code), [ADR-PC-004](./ADR-PC-004-pii-crypto-shredding.md) (the field-level PII encryption envelope this projection must host), [ADR-IC-005](../../integration_concepts/adrs/ADR-IC-005-cqrs-read-model-storage.md) (PostgreSQL read model), [ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md) (Testcontainers) |
| Resolves | bd `archie-10r.3` (ADR-PC-002: Bitemporal projection implementation) |

---

## Context

Projections are derived state built from the event log; each row carries two time dimensions — `valid_time` (when the fact was true in the world) and `transaction_time` (when we recorded it) — so the engine can answer all four time-dimensional capabilities ([event-store §2](../feature-design-event-store-projections.md)) and, decisively, make retroactive corrections auditable ([event-store §6](../feature-design-event-store-projections.md)). The bitemporal-vs-unitemporal commitment is **firm and resolved** ([04 §3](../04-open-questions.md)); only the *mechanism* is open, tracked as Q-X. This ADR picks the mechanism.

[event-store §6.1](../feature-design-event-store-projections.md) names three candidate paths and [§6.3](../feature-design-event-store-projections.md) specifies a 5-day-per-path spike to choose between them. Two things narrow that bake-off before it runs:

1. **[ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) (which post-dates the §6.3 spec) chose a hand-rolled event-sourcing core on PostgreSQL** — no new database technology, no framework that owns its own tables. That decision forecloses any path that introduces a second datastore.
2. **[ADR-PC-004](./ADR-PC-004-pii-crypto-shredding.md) requires the projection to host the per-subject PII encryption envelope at the *field* level** (§6.2: "row-level encryption forecloses structural-field queries on erased records" — this is the spike's scoring criterion #2). Structural fields (principal, rate, dates) stay cleartext and queryable; only PII columns are ciphertext. A path that versions whole rows or whole documents fights this.

**Candidates evaluated** ([event-store §6.1](../feature-design-event-store-projections.md)):

| # | Candidate | Notes |
|---|---|---|
| A | **Application-level bitemporality on plain PostgreSQL** | Every projection table carries `valid_from`, `valid_to`, `recorded_at`, `superseded_at`; the hand-rolled engine maintains them; queries are explicit temporal joins. Most code; familiar ops; field-granular by construction. |
| B | **PostgreSQL temporal extensions / SQL:2011 temporal tables** | Application-time integrity via PG18 native temporal constraints (`WITHOUT OVERLAPS` / `PERIOD` foreign keys, shipped PG 18 — reverted from PG 17), or system-time via the `temporal_tables` extension (triggers); `AS OF`-style querying built on top. Neither gives native bitemporal. |
| C | **XTDB / Datomic-style temporal-native database** | Datalog-style temporal queries; immutable, bitemporal by design; a new datastore the team operates. |

---

## Evaluation

### Hard filter results

#### F1 · Cost / licensing

| Candidate | Licence | Verdict |
|---|---|---|
| A · Application-level on PG | PostgreSQL licence (already in the stack per [ADR-PC-001](./ADR-PC-001-event-store-technology.md)). Zero incremental cost. | **Pass** |
| B · PG temporal extensions | PG core (PostgreSQL licence); `temporal_tables` extension is community-maintained (BSD-style). | **Pass (conditional)** — `temporal_tables` extension maintenance cadence (S4) must be audited; PG18 native temporal constraints avoid the extension but require pinning PG 18+ and cover only application-time. |
| C · XTDB / Datomic | XTDB: MPL-2.0 (self-hostable). Datomic: free tier exists but proprietary licence. | **Pass (conditional)** — XTDB self-hostable and OSS; Datomic's licence restricts use and is flagged per [ADR-IC-000 F1](../../integration_concepts/adrs/ADR-IC-000-common-evaluation-criteria.md). |

#### F2 · Regulatory fit (GDPR / DORA / PSD2)

| Candidate | GDPR (field-level crypto-shred host) | DORA | PSD2 (audit) | Verdict |
|---|---|---|---|---|
| A · Application-level on PG | **Field-granular by construction** — structural columns cleartext, PII columns hold the [ADR-PC-004](./ADR-PC-004-pii-crypto-shredding.md) ciphertext; erased records keep structural queries working with PII returning null. | Inherits PG PITR/replication ([ADR-PC-005](./ADR-PC-005-dr-rto-rpo.md)). | Bitemporal rows make corrections auditable ("what we knew then" vs "now"). | **Pass** |
| B · PG temporal extensions | Period/row versioning is the native unit; field-level PII still has to be applied in application columns anyway, so the extension does not solve the hard part. | Inherits PG. | Same. | **Pass** |
| C · XTDB / Datomic | Document/entity is the native unit; field-level crypto-shred over a document store is awkward and risks foreclosing structural queries on erased entities. | New datastore's own DR story; **breaks outbox co-location** ([ADR-IC-004](../../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md)) — events in PG, projections in XTDB is a cross-store boundary. | Same. | **Pass (conditional)** — field-level erasure mechanism must be demonstrated on the document model; cross-store rebuild path documented. |

---

### Soft criteria

#### A · Application-level bitemporality on PostgreSQL — **CHOSEN**

**S1 · Operational complexity.** Lowest. No new infrastructure — the same PostgreSQL the event store, outbox, and read model already run on ([ADR-PC-001](./ADR-PC-001-event-store-technology.md)). One backup regime, one PITR mechanism, one on-call surface. The cost is *code*, not *operations*: every projection table carries the four temporal columns and the engine maintains them.

**S2 · Ecosystem coherence.** Maximum, and synergistic with [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md): the hand-rolled engine *already owns the projection-apply code*. Maintaining `valid_from`/`valid_to`/`recorded_at`/`superseded_at` is the same discipline the engine already exercises appending events and applying handlers — so the "most code to write" cost of this path is largely absorbed into work the engine is doing regardless. Plain SQL composes with Testcontainers ([ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md)) and the per-projection sync/async declaration ([two-modes §5.4](../feature-design-two-modes-asymmetry.md)).

**S3 · Exit cost.** Lowest. Projections are plain relational tables with explicit temporal columns; any migration reads standard SQL. No temporal-extension dialect, no datastore wire format.

**S4 · Community and longevity.** PostgreSQL's — the strongest available. No extension cadence risk, no new-product longevity risk.

**The named cost (event-store §6.1's tradeoff for this path):** *most code, most subtle correctness bugs* — bitemporal joins are hand-written and easy to get subtly wrong. Mitigation: the §6.3 spike's **criterion #1 (forced-correction round-trip)** becomes a v1 acceptance test, not a spike-only check; the monthly projection-rebuild drills ([event-store §7.2](../feature-design-event-store-projections.md)) exercise correctness continuously; and a typed bitemporal-query helper in the engine (see §P3) keeps family-schema code from hand-writing the joins.

#### B · PostgreSQL temporal extensions / SQL:2011

Better query ergonomics (`AS OF` syntax) than A, but three problems. First, it delivers at most *half* of bitemporality: PG18's native temporal support (the feature was reverted from PG 17 and shipped in **PG 18**, September 2025) is **application-time only** — `WITHOUT OVERLAPS` / `PERIOD` foreign keys give valid-time *integrity constraints*, not system-versioned storage — so `transaction_time` is hand-rolled either way; the `temporal_tables` extension covers system-time via triggers but is community-maintained (S4 risk). Second, it does **not** solve the field-level PII requirement (PII encryption is applied in application columns regardless), so it adds a dependency without removing the hard part. Third, period/row-versioning is a coarser unit than the field-granular control crypto-shredding wants. **Decisive reason for not choosing:** since PG-native temporal gets you at most one of the two time dimensions (valid-time integrity), the PG 18+ coupling buys little — marginal valid-time-query ergonomics over A, while leaving both `transaction_time` and criterion #2 (field-level erasure) exactly where A already has them solved.

#### C · XTDB / Datomic

Best bitemporal query ergonomics in the field. But it is a **new datastore the 1–2 person team would operate**, which [event-store §10.4](../feature-design-event-store-projections.md) rules out ("cannot absorb a new database technology simultaneously") and which contradicts the hand-rolled, single-substrate posture of [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md). It also **breaks outbox co-location** — events in PostgreSQL, projections in XTDB is the cross-store boundary [ADR-PC-001](./ADR-PC-001-event-store-technology.md) chose PostgreSQL specifically to avoid — and its document/entity storage unit makes field-level crypto-shredding awkward. **Decisive reason for not choosing:** new operational dependency forbidden by §10.4 and PC-010, plus the cross-store and field-level-erasure costs.

---

## Decision

**Chosen: application-level bitemporality on plain PostgreSQL (Path A).**

Three load-bearing reasons: (1) it is **field-granular by construction**, which is exactly what [ADR-PC-004](./ADR-PC-004-pii-crypto-shredding.md)'s per-field crypto-shredding needs (spike criterion #2) — structural columns stay cleartext and queryable on erased records, PII columns hold ciphertext; (2) it adds **no new infrastructure**, consistent with the hand-rolled PostgreSQL core ([ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md)) and the §10.4 constraint; (3) the engine **already owns the projection-apply code** ([ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md)), so the path's "most code" cost is absorbed into work already in scope. The query-ergonomics weakness (criterion #6) is the real price, paid down by a typed query helper (§P3) and the rebuild-drill correctness regime.

**Rejected: PG temporal extensions** — marginal `AS OF` ergonomics over Path A, bought with an extension/PG-version coupling, while leaving the field-level PII requirement exactly where Path A already solves it. **Rejected: XTDB / Datomic** — a new datastore the team cannot absorb (§10.4), breaks outbox co-location, and its document storage unit fights field-level crypto-shredding.

### Gate

This ADR is **Proposed pending Q-Y** ([event-store §6.4](../feature-design-event-store-projections.md), [04 §7](../04-open-questions.md)) — the compliance/audit confirmation that PT regulators expect retroactive corrections queryable in both time dimensions. The architecture commits to bitemporal regardless ([04 §3](../04-open-questions.md) RESOLVED); Q-Y resets *how much* bitemporal machinery v1 populates, and Path A is robust to all three outcomes:

- **Bitemporal required** → full §6.3 correctness scoring applies; Path A as specified.
- **Unitemporal sufficient for v1** → the projection schema keeps the columns but the engine need not populate the `valid_time` history; criteria 1 and 6 lose weight. Path A degrades gracefully (a unitemporal projection is application-level bitemporality with one dimension unused).
- **Bitemporal forbidden** → schemas simplify materially; Path A drops the `valid_*`/`superseded_at` columns. The least-rework path of the three candidates, because the columns are plain SQL the engine controls.

Q-Y runs before the §6.3 spike committee meets; given PC-010, that "spike" is now a correctness/performance validation of Path A rather than a three-way bake-off.

---

## Implementation Principles

### P1 — Four temporal columns on every projection table

Every projection table carries: `valid_from`, `valid_to` (the world-time interval the row asserts), `recorded_at` (transaction-time the row was written), `superseded_at` (transaction-time the row was corrected/closed; null = currently-believed). The current-belief view is `WHERE superseded_at IS NULL`; an as-known-at query filters on `recorded_at`/`superseded_at`; an as-of-valid-time query filters on `valid_from`/`valid_to`. Structural columns are cleartext; PII columns hold [ADR-PC-004](./ADR-PC-004-pii-crypto-shredding.md) ciphertext.

### P2 — Corrections supersede, never overwrite

A `DepositCorrected` event ([event-store §4.2](../feature-design-event-store-projections.md)) closes the affected rows (`superseded_at = transaction_time`) and inserts new rows with the corrected values and a new `recorded_at`. Both the original ("what we knew then") and the corrected ("what we now know") remain queryable — the forced-correction round-trip that is spike criterion #1 and the v1 acceptance test for this ADR.

### P3 — A typed bitemporal-query helper insulates family-schema code

Because hand-written temporal joins are the path's main risk, the engine exposes a small typed query layer (`AsOf(validTime, knownAt)`, `CurrentBelief()`, `HistoryOf(streamId)`) so family schemas never hand-write the four-column join. This is engine code ([ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md)), tested against the rebuild drills.

### P4 — Projections are rebuildable; per-projection sync/async

Every projection is rebuildable from the event log alone ([event-store §1](../feature-design-event-store-projections.md)); a projection that cannot be rebuilt is broken. Each projection declares sync (transactional with the event append) or async (eventually-consistent within a stated lag) per [two-modes §5.4](../feature-design-two-modes-asymmetry.md). Rebuild is exercised monthly ([event-store §7.2](../feature-design-event-store-projections.md)).

---

## Residual Risks

1. **Subtle bitemporal-join correctness.** The path's named weakness. Mitigation: the forced-correction round-trip is a v1 acceptance test; the typed query helper (§P3) centralises the joins; monthly rebuild drills catch drift.
2. **Q-Y outcome may simplify or reshape schemas.** Carried as the Gate; Path A is the least-rework choice under every Q-Y outcome.
3. **Query ergonomics (criterion #6) are worst here.** Mitigated by §P3; accepted as the price of no-new-infrastructure and field-level crypto-shred compatibility.
4. **Cold-replay/rebuild performance at v4 scale** is validated by the Q-AK load test ([two-modes §8](../feature-design-two-modes-asymmetry.md)) and accelerated by snapshots ([ADR-PC-003](./ADR-PC-003-postgresql-snapshots.md)).

---

## Cross-references

- [ADR-PC-001](./ADR-PC-001-event-store-technology.md) — PostgreSQL event store; projections live in the same database.
- [ADR-PC-003](./ADR-PC-003-postgresql-snapshots.md) — snapshots accelerate projection rebuild/cold-replay.
- [ADR-PC-004](./ADR-PC-004-pii-crypto-shredding.md) — the field-level PII encryption envelope this projection hosts (criterion #2).
- [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) — the hand-rolled engine owns the projection-apply and temporal-maintenance code.
- [event-store §6](../feature-design-event-store-projections.md) — bitemporal projections; §6.1 paths; §6.2 crypto-shred host constraint; §6.3 spike; §6.4 Q-Y.
- [two-modes §5.4](../feature-design-two-modes-asymmetry.md) — per-projection sync/async declaration.
- [04 §3, §7, Q-X, Q-Y](../04-open-questions.md) — bitemporal commitment resolved; mechanism (Q-X) decided here; Q-Y gate.

---

*Decided 2026-05-23 by jhosm. Proposed pending Q-Y. Mechanism choice (Q-X) made ahead of the §6.3 spike because [ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md) narrows the candidate set to the application-level path.*
