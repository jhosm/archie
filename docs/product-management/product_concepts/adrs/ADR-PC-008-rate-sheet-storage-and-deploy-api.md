# ADR-PC-008: Rate-Sheet Storage and Deploy API — Versioned Rows in the Existing PostgreSQL, Separate Treasury-Gated Deploy Endpoint

| Field | Value |
|---|---|
| Status | Accepted |
| Date | 2026-05-23 |
| Deciders | jhosm |
| Shape | Tool-selection |
| Common criteria | [ADR-IC-000](../../integration_concepts/adrs/ADR-IC-000-common-evaluation-criteria.md) (reused per [ADR-PC-000](./ADR-PC-000-namespace-and-contract-shape-framework.md) D2) |
| Depends on | [ADR-PC-001](./ADR-PC-001-event-store-technology.md) (PostgreSQL event store + read-model tier), [ADR-IC-005](../../integration_concepts/adrs/ADR-IC-005-cqrs-read-model-storage.md) (PostgreSQL read-model store), [ADR-PC-007](./ADR-PC-007-signed-yaml-oci-pack.md) (pack carries rate-sheet *refs* and bounds only; this ADR stores the sheets), [ADR-PC-009](./ADR-PC-009-per-instance-version-pinning.md) (the per-event pinning discipline this ADR's `rate_sheet_version_id` follows) |
| Resolves | bd `archie-10r.9` (ADR-PC-008: Rate-sheet storage and deploy API) |

---

## Context

The configuration surface is **three artefact families, not one** ([01 §3](../01-product-architecture.md), [surface §1](../feature-design-configuration-surface.md)): product configs (structure; product team; days–weeks; product + compliance sign-off), **rate sheets** (numerical rates; treasury / ALM; daily–weekly; treasury sign-off), and packs (jurisdiction vocabulary; engine team + counsel; per regulatory change). The load-bearing rule: *the cheapest change must move through the cheapest approval*. Collapsing rate sheets onto the product-config deploy path collapses a weekly price tweak onto a product-redesign approval gate, and the agility wedge ([00 §2](../00-product-vision.md)) "dies on the first promotional campaign" ([surface §2.1](../feature-design-configuration-surface.md)).

This ADR resolves five sub-problems ([bd archie-10r.9](../04-open-questions.md)): (1) **storage shape** of a rate sheet; (2) the **deploy API** — a deploy path separate from product-config deploy, with idempotency; (3) **version resolution at constitution** — how the engine resolves "the rate sheet effective on this date for this product variant" and what it pins on the instance ([02 §2.4.1](../02-v1-scope-term-deposits.md): `rate_sheet_version_id` on `DepositConstituted`); (4) **approval / sign-off workflow** — treasury / ALM ownership distinct from product-config approvers; (5) **Q-J typo-rollback** — confirming the storage and event model structurally support forward-only correction plus out-of-band compensation ([surface §2.7 Q-J](../feature-design-configuration-surface.md)).

A rate sheet is **numerical data indexed by `(product, role, principal_band)` with an `effective_from` timestamp** ([surface §2.2](../feature-design-configuration-surface.md)); it carries **no PII** (prices, bands, roles — never a customer attribute). It is forward-only and versioned: once published, never edited; corrections ship as a new version with a new `effective_from` ([surface §2.6](../feature-design-configuration-surface.md)). The pack carries only *refs and bounds* ([ADR-PC-007 §P1](./ADR-PC-007-signed-yaml-oci-pack.md) `rate-sheet-refs/`, [surface §2.5](../feature-design-configuration-surface.md) pack-declared `0 ≤ tan_basis_points ≤ 5000`); the sheets themselves live wherever this ADR decides.

**Candidates evaluated** ([bd archie-10r.9](../04-open-questions.md)):

| # | Candidate | Notes |
|---|---|---|
| A | **Versioned, immutable rows in the existing PostgreSQL** | A `rate_sheets` table of version-stamped immutable rows; the sheet body as JSONB; resolution is an indexed point-in-time query `(product_family, effective_from)`. Reuses the [ADR-PC-001](./ADR-PC-001-event-store-technology.md) / [ADR-IC-005](../../integration_concepts/adrs/ADR-IC-005-cqrs-read-model-storage.md) PG tier. |
| B | **Dedicated time-series store** (TimescaleDB extension or InfluxDB) | "Rate effective at T" is the canonical time-series query; the store is purpose-built for as-of reads. Adds a PG extension or a second datastore. |
| C | **KV / object store with version stamping** (S3-class object lock, or Redis) | Each sheet version is a blob keyed by `rate_sheet_version_id`; an effective-date index is maintained alongside. Object-lock immutability is strong. |

The decision collapses to **A vs B vs C** on the same axis ADR-PC-001 turned on: not raw fit, but *operational coherence for a 1–2 person team*. B and C are evaluated and rejected on operational-surface and as-of-query grounds.

---

## Evaluation

### Hard filter results

#### F1 · Cost / licensing

| Candidate | Licence | Verdict |
|---|---|---|
| A · PG versioned rows | PostgreSQL Licence (permissive). Already in the stack ([ADR-PC-001](./ADR-PC-001-event-store-technology.md), [ADR-IC-005](../../integration_concepts/adrs/ADR-IC-005-cqrs-read-model-storage.md)). Zero incremental licence cost. | **Pass** |
| B · Time-series store | TimescaleDB community is **Timescale License (TSL)** — *not* OSI-approved, restricts offering the software as a managed service and gates some features behind a paid tier; InfluxDB OSS (MIT) vs InfluxDB Enterprise (commercial). | **Pass (conditional)** — TSL/Influx-Enterprise boundaries must be re-assessed before any production commitment per [ADR-IC-000 F1](../../integration_concepts/adrs/ADR-IC-000-common-evaluation-criteria.md) ("flag licences that restrict use in a financial-services context even if currently free"). |
| C · KV / object store | Object storage (S3-class) is commodity; Redis is now **RSALv2 / SSPL** (source-available, not OSI); Valkey (BSD) is the open fork. | **Pass (conditional)** — if Redis, the licence-change history is an [ADR-IC-000 S4](../../integration_concepts/adrs/ADR-IC-000-common-evaluation-criteria.md) red flag; object-store-only avoids it. |

#### F2 · Regulatory fit (GDPR / DORA / PSD2)

A rate sheet holds **no PII** — GDPR Article 17 erasure does not reach it (the crypto-shredding machinery of [event-store §6.2](../feature-design-event-store-projections.md) and [ADR-PC-004](../04-open-questions.md) is irrelevant here). The discriminating dimension is **immutability / tamper-evidence** (a published rate is regulatory and commercial evidence of "what we offered, when") and **DORA RTO/RPO** (the sheet store must restore to a consistent point alongside the event store).

| Candidate | GDPR | DORA (RTO/RPO) | PSD2 (audit) | Verdict |
|---|---|---|---|---|
| A · PG versioned rows | No PII. | Same WAL archiving + PITR as the event store; **one restore point** covers events and rate sheets together — no cross-store RPO skew. | Immutable rows by `INSERT`-only role privilege ([ADR-PC-001 §P3](./ADR-PC-001-event-store-technology.md) pattern); the published sheet is a durable, never-updated row. | **Pass** |
| B · Time-series store | No PII. | A second backup regime and a second PITR mechanism; restoring events and rate sheets to the *same* instant requires coordinating two RPO timelines. | TSDB retention/compaction policies can be configured immutable, but it is a *new* discipline to audit. | **Pass (conditional)** — independent backup + a documented cross-store recovery-point reconciliation. |
| C · KV / object store | No PII. | Object-lock gives strong immutability; PITR for "the index of which version is active at T" is a separate concern from the blobs. | Object-lock (WORM) is excellent tamper-evidence; the as-of *index* is the audit-weak part if held outside the same transactional store. | **Pass (conditional)** — the effective-date index needs its own consistent-recovery story. |

All pass at POC scale; the conditional passes name mitigations carried into Consequences.

---

### Soft criteria

#### A · Versioned rows in the existing PostgreSQL — **CHOSEN**

**S1 · Operational complexity for 1–2 people.** Decisive, and the same argument as [ADR-PC-001 §S1](./ADR-PC-001-event-store-technology.md). The rate sheet becomes another table in the database the team already backs up, monitors, PITRs, and replicates. No new datastore, no second backup regime, no second on-call runbook. Immutability is the *same* `INSERT`-only role-privilege discipline already used for the `events` table ([ADR-PC-001 §P3](./ADR-PC-001-event-store-technology.md)).

**S2 · Ecosystem coherence.** Maximum. Resolution at constitution (§P3) is a single indexed `SELECT … WHERE product_family = $1 AND effective_from <= $2 ORDER BY effective_from DESC LIMIT 1` against the same connection pool, instrumented by the same OpenTelemetry surface ([ADR-IC-007](../../integration_concepts/adrs/ADR-IC-007-observability-stack.md)). Crucially, **the rate resolution can run in the same local transaction as the constitution event append** — the resolved `tan_basis_points` and `rate_sheet_version_id` are read and stamped onto `DepositConstituted` with no cross-system round-trip, preserving the [ADR-IC-004 P6](../../integration_concepts/adrs/ADR-IC-004-outbox-pattern-mechanism.md) local-atomicity property. Testcontainers ([ADR-IC-009](../../integration_concepts/adrs/ADR-IC-009-testing-infrastructure.md)) covers it with the same PG fixture.

**S3 · Exit cost.** Low. The sheet body is JSONB that round-trips 1:1 with the deployed YAML ([surface §2.2](../feature-design-configuration-surface.md)); migrating to any other store is a row-by-row export of portable JSON. No engine-framework lock-in.

**S4 · Community and longevity.** Inherits PostgreSQL's multi-decade stability and no-licence-change history ([ADR-PC-001 §S4](./ADR-PC-001-event-store-technology.md)). Lowest longevity risk of the three.

**Where this approach requires explicit engineering effort:**

- **As-of correctness is an index discipline, not a store feature.** The "active at T" semantics that a TSDB gives for free are here a `(product_family, effective_from DESC)` index plus the deploy-time uniqueness constraint that forbids two sheets sharing an `effective_from` ([surface §2.3](../feature-design-configuration-surface.md)). This is a few lines of schema, not a subsystem — the data volume (daily–weekly publication, a few product families) never approaches the scale where a TSDB's columnar/chunked machinery earns its operational cost.

#### B · Dedicated time-series store

**S1.** A second data tier for a 1–2 person team — the same step-up ADR-PC-001 rejected for Kurrent. **S2.** Resolution would cross a store boundary, breaking the same-transaction stamping property A enjoys, *or* require caching the sheets into PG anyway. **S3/S4.** TSL/Influx-Enterprise licence drift and a smaller ecosystem than PG.

**Decisive reason for not choosing B:** the as-of query a TSDB optimises is, at this data volume and cadence, a trivial indexed relational read. The store buys a query convenience the workload does not need, at the cost of a second operational tier and a broken same-transaction resolution path. The throughput case that justifies a TSDB (high-frequency tick data) is not the rate-sheet case (weekly retail publication).

#### C · KV / object store

**S1.** Object storage is operationally light, but the **as-of index** — "which `rate_sheet_version_id` is effective at T for product family F" — has to live *somewhere queryable and transactionally consistent*, which lands back in PG or a second index store. **S2.** Splitting the blob (object store) from the index (PG) means resolution touches two systems and the index can drift from the blobs. **S3.** Low. **S4.** Object-store longevity high; Redis (if chosen for the index) carries the RSALv2/SSPL flag.

**Decisive reason for not choosing C:** WORM object-lock is genuinely the strongest immutability story, but it solves a problem A already solves (PG `INSERT`-only role privilege) while introducing a store split whose index half needs the very transactional consistency A has natively. The immutability gain does not justify the resolution-path split.

---

## Decision

**Chosen: rate sheets are stored as versioned, immutable rows in the existing PostgreSQL tier, with the sheet body as JSONB; resolution at constitution is an indexed point-in-time query; deployment is a separate, treasury-gated `POST /v1/rate-sheets` endpoint with idempotency keyed on `rate_sheet_version_id`.**

The decisive forces, in order: (1) **operational coherence for a 1–2 person team** — no new datastore, the same backup/PITR/role-privilege discipline as the event store ([ADR-PC-001](./ADR-PC-001-event-store-technology.md)); (2) **same-transaction resolution** — the resolved `tan_basis_points` and `rate_sheet_version_id` are stamped onto `DepositConstituted` within the constitution transaction, no cross-system round-trip; (3) **the workload does not need a time-series store** — daily–weekly publication over a handful of product families is an indexed relational read, not a tick stream.

**Rejected: dedicated time-series store** — buys an as-of query convenience the volume does not need, at the cost of a second operational tier and a broken same-transaction stamping path; TSL/Influx-Enterprise licence drift compounds it. **Rejected: KV / object store** — WORM immutability is real but redundant with PG role-privilege immutability, and the as-of *index* half re-introduces the transactional-consistency need A satisfies natively.

The **separation of artefact families** ([surface §1](../feature-design-configuration-surface.md)) is honoured structurally: rate sheets are a different table, a different deploy endpoint, and a different approver scope from product configs — the two can deploy in either order but the engine never accepts a state where they disagree (the symmetric validator invariant, [surface §2.5](../feature-design-configuration-surface.md)).

---

## Implementation Principles

### P1 — Storage: one immutable, version-stamped `rate_sheets` table; body as JSONB

```
rate_sheets (
  rate_sheet_version_id  VARCHAR     PRIMARY KEY,   -- e.g. dpz_pt_rates_2026_05_19
  product_family         VARCHAR     NOT NULL,      -- e.g. term_deposit
  pack_version           VARCHAR     NOT NULL,      -- the pack the sheet validated against
  effective_from         TIMESTAMPTZ NOT NULL,
  body                   JSONB       NOT NULL,      -- products → role → bands (1:1 with deployed YAML)
  approved_by            VARCHAR     NOT NULL,      -- treasury / ALM approver actor (see P4)
  approval_ref           VARCHAR     NOT NULL,      -- treasury sign-off record
  published_by           VARCHAR     NOT NULL,
  published_at           TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp(),
  UNIQUE (product_family, effective_from)           -- surface §2.3: no two sheets share effective_from
);
CREATE INDEX rate_sheets_resolve_idx ON rate_sheets (product_family, effective_from DESC);
```

The body stays JSONB rather than a normalised `(product, role, band) → bps` table: a sheet is read *whole* at resolution, never queried band-by-band across sheets, and JSONB preserves an auditor-readable 1:1 round-trip with the deployed YAML. A normalised entries table is an available later optimisation if cross-sheet band analytics ever needs it — explicitly **not** built for v1 (no speculative normalisation). The engine's application role has `INSERT`/`SELECT` only on `rate_sheets`; no `UPDATE`/`DELETE` — immutability by privilege, the [ADR-PC-001 §P3](./ADR-PC-001-event-store-technology.md) pattern. **Index sheets** ([surface §2.4](../feature-design-configuration-surface.md), the variable-rate v3 cousin) reuse this exact table shape under a separate `index_sheets` table; v1 ships only `rate_sheets`.

### P2 — Deploy API: `POST /v1/rate-sheets`, separate path, idempotent on the version id

`POST /v1/rate-sheets` is a **distinct endpoint** from the product-config deploy path — different URL, different authz scope (P4), different validator invariant set ([surface §2.5](../feature-design-configuration-surface.md): referenced product IDs exist; every active-config `(product, role, principal)` is covered with non-overlapping exhaustive bands; pack-declared bounds honoured). Validation is **synchronous at deploy** — a sheet referencing a role no active config asks for, or leaving a band gap, is rejected at deploy, never at first constitution. **Idempotency key = `rate_sheet_version_id`** (the natural key; no separate header needed, though an `Idempotency-Key` header is accepted and must equal the version id): re-POSTing an identical body under an existing version id returns `200` with the stored resource; a *different* body under an existing version id is `409 Conflict` — the forward-only immutability guarantee ([surface §2.6](../feature-design-configuration-surface.md)) enforced at the API boundary, not just by the table constraint.

### P3 — Resolution at constitution: read the active sheet, stamp both version and value

At `DepositConstituted` (and `CreditConstituted`), within the constitution transaction, the engine ([surface §2.3](../feature-design-configuration-surface.md)):

1. Resolves the sheet **active at `constituted_at`** — `… WHERE product_family = $1 AND effective_from <= constituted_at ORDER BY effective_from DESC LIMIT 1`. The `UNIQUE (product_family, effective_from)` constraint guarantees no runtime ambiguity.
2. Reads the `deposit_origin` fact, maps it through the product config's `role_selector` to a role ([surface §2.2](../feature-design-configuration-surface.md)), and resolves `(product_id, role, principal_band) → tan_basis_points`.
3. Stamps **both** `rate_sheet_version_id` *and* the resolved `tan_basis_points` onto the event ([02 §2.4.1](../02-v1-scope-term-deposits.md)). Storing both is deliberate: the version id anchors audit/replay; the resolved value answers "what rate is this deposit paying?" without re-resolution.

Every subsequent `InterestAccrued` references the same instance's pinned rate; the audit chain is **decidable from the event stream alone** ([surface §2.3](../feature-design-configuration-surface.md)). `rate_sheet_version_id` is thus a *third* per-event pin alongside `pack_version` and `schema_version` ([ADR-PC-009](./ADR-PC-009-per-instance-version-pinning.md)) — but with different semantics: rate sheets resolve to "active at constitution" and are **never migrated** (no `RateSheetMigrated` event), because re-pricing a live deposit is a commercial act, not an operational one. The v3 index-sheet cousin re-binds at *every revision*, not just constitution ([surface §2.4](../feature-design-configuration-surface.md)).

### P4 — Approval / sign-off: treasury / ALM scope, distinct from product + compliance

The deploy endpoint is gated by a **treasury / ALM approver scope** ([01 §3](../01-product-architecture.md), [surface §1](../feature-design-configuration-surface.md) table) — distinct from the product + compliance scope that gates product-config deploys. Authorization itself is the API gateway's job ([ADR-IC-010](../../integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md) / the IC gateway ADR); the engine's contribution is to **record the approver identity (`approved_by`) and the sign-off reference (`approval_ref`) on the immutable row**, so "who signed off this rate, when" is answerable from the stored sheet. This is the structural half of cadence separation: a weekly rate change reaches production through treasury sign-off without ever touching the product-redesign gate.

### P5 — Q-J typo-rollback: forward-only fix + decidable compensation set

A wrong-rate publication (treasury types `350` bps for `35`) is corrected **forward-only** ([surface §2.6](../feature-design-configuration-surface.md), [§2.7 Q-J](../feature-design-configuration-surface.md)): a new `rate_sheet_version_id` with a corrected body and a new `effective_from`. The bad sheet is **never edited or deleted** — it remains the truthful record of what was offered during its window. Structural support for the compensation flow is the point this ADR confirms: because every affected instance carries `rate_sheet_version_id` on `DepositConstituted` (P3), *"which instances constituted under the bad sheet"* is a single decidable query over the event stream / projection — not a forensic reconstruction. The compensation itself (re-pricing affected deposits, customer outreach) is **out-of-band and commercial**, not an engine rollback: it lands as a `DepositCorrected` event ([02 §2.4.1](../02-v1-scope-term-deposits.md)) per affected instance, preserving bitemporal "what we thought vs what we now know" ([event-store §6](../feature-design-event-store-projections.md)). The engine guarantees the affected set is computable and the correction is auditable; it does not guarantee silent rollback, because there is no correct silent rollback of a price a customer was told.

---

## Consequences

**What this choice makes easier:**

- One data tier. Rate sheets, events, outbox, read model, and ACL state share one PostgreSQL — one backup, one PITR, one observability surface, one restore point with no cross-store RPO skew.
- Constitution resolves the rate in-transaction; no cross-system call sits between reading the sheet and appending `DepositConstituted`.
- Cadence separation is structural: separate table, separate endpoint, separate approver scope. A promotional weekly re-price never pays product-redesign approval cost.
- The Q-J affected-instance set is a query, because the version id is pinned per instance.

**What this choice makes harder or impossible:**

- As-of semantics are an index + uniqueness-constraint discipline, not a store primitive — correct, but a discipline the team owns rather than inherits.
- Cross-sheet rate analytics ("how did the new-money 12m rate move over 2026?") run against JSONB bodies, not a normalised column. Acceptable at v1 volume; a normalised entries table is the named later optimisation if analytics demand it.
- High-frequency rate sources (FX-adjacent, sub-second) would eventually outgrow the relational shape; v1 retail cadence (weekly/biweekly) is nowhere near that boundary, and the move to a TSDB would be a storage swap behind the same deploy API and resolution contract.

**Residual risks:**

- **Immutability relies on role privilege, not table-level WORM.** Mitigation: the application role has no `UPDATE`/`DELETE` on `rate_sheets`; migrations run under a separate privileged role; CI lints reject `UPDATE rate_sheets` / `DELETE FROM rate_sheets` in application code — the [ADR-PC-001 §P3](./ADR-PC-001-event-store-technology.md) discipline.
- **Validator coverage gap.** A config deployed *after* a sheet could reference an uncovered `(product, role, band)`. Mitigation: the symmetric invariant ([surface §2.5](../feature-design-configuration-surface.md)) — product-config deploy is rejected if the active sheet doesn't cover its `rate_ref`; the two artefacts deploy in either order but never into mutual disagreement.
- **Business-unit scoping (Q-K residual).** Whether sheets should be scopable per internal business unit (retail vs corporate) is left open ([surface §2.7 Q-K](../feature-design-configuration-surface.md)); the `(product_family, effective_from)` key would extend to `(business_unit, product_family, effective_from)` without a structural rewrite. Not built for v1.
- **Index-sheet sourcing (Q-L).** Who feeds Euribor fixings ([surface §2.7 Q-L](../feature-design-configuration-surface.md)) is a v3 question; the `index_sheets` table reuses this shape, so the storage decision does not pre-empt it.

---

## Amendment — 2026-05-30: HTTP host technology for the §P2 deploy endpoint

Implementing C.6 ([bd archie-9vpj](../04-open-questions.md)) stood up the §P2 `POST /v1/rate-sheets` endpoint as the **first HTTP host in the engine**. The Decision mandated the endpoint *exists* but named no host technology; that choice is load-bearing and was landing in code unrecorded — the silent drift the explicit-drift gate ([ADR-PC-020 §D3](./ADR-PC-020-llm-toolchain-and-conformance-governance.md)) exists to catch. This amendment records the host decision. It is additive: §P1–§P5 hold as written.

### A1 · The deploy endpoint is an ASP.NET Core minimal-API (Kestrel) process

The §P2 endpoint is hosted as an ASP.NET Core minimal-API process on Kestrel — the engine's native .NET web host ([ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md): the engine is C#/.NET, hand-rolled). The host is hand-wired (minimal API, no MVC controllers or scaffolding), consistent with PC-010's hand-rolled discipline, and adds no runtime, language, or framework beyond what PC-010 already fixes. It ships as `Babelstone.RateSheets.Api`.

### A2 · ADR-IC-010's .NET rejection does not bind engine-side HTTP surfaces

[ADR-IC-010](../../integration_concepts/adrs/ADR-IC-010-mcp-server-runtime-and-sdk.md) rejected .NET **only for the MCP / LLM-agent server**, on the reasoning that no other component used .NET. That scope does not reach an engine-side HTTP surface: the engine *is* .NET ([ADR-PC-010](./ADR-PC-010-dotnet-hand-rolled-engine.md)), so its native web host is ASP.NET Core. IC-010 remains binding for the MCP surface; it does not govern this or future engine-side endpoints.

### A3 · Authentication / authorization remain the edge gateway's job

Consistent with §P4 and the edge gateway ([ADR-IC-006](../../integration_concepts/adrs/ADR-IC-006-edge-api-gateway.md)), this host does **not** implement authn/authz. The gateway authenticates the caller and enforces the treasury / ALM approver scope; the host records the gateway-supplied deploying principal as `published_by` (from the `X-Deploy-Actor` header, never a payload field) and the treasury sign-off as `approved_by` / `approval_ref` on the immutable row.

### A4 · This amends the decision; it does not supersede this ADR

§P1 (storage), §P2 (deploy API + idempotency), §P3 (resolution), §P4 (approval), and §P5 (typo-rollback) all remain binding as written. This amendment is appended to — not a revision of — the Decision and Implementation Principles; it fills only the previously-unstated host-technology slot.

---

## Cross-references

- [ADR-PC-001](./ADR-PC-001-event-store-technology.md) — PostgreSQL tier and the `INSERT`-only role-privilege immutability pattern reused here; constitution resolution runs in the same local transaction as the event append.
- [ADR-PC-007 §P1](./ADR-PC-007-signed-yaml-oci-pack.md) — the pack carries rate-sheet *refs* and pack-declared bounds only; this ADR stores the sheets the refs point at.
- [ADR-PC-009](./ADR-PC-009-per-instance-version-pinning.md) — `rate_sheet_version_id` follows the same per-event pinning discipline as `pack_version` / `schema_version`, but rate sheets are never migrated (re-pricing is commercial, not operational).
- [ADR-IC-005](../../integration_concepts/adrs/ADR-IC-005-cqrs-read-model-storage.md) — the read-model PG tier this table co-locates with.
- [surface §2](../feature-design-configuration-surface.md) — rate-sheet premise (§2.1), worked example (§2.2), constitution-time binding (§2.3), index sheets (§2.4), validator invariants (§2.5), lifecycle (§2.6), open questions Q-I–Q-L (§2.7).
- [01 §3](../01-product-architecture.md) — the three-artefact-family table and the cadence-separation rationale.
- [02 §2.4.1](../02-v1-scope-term-deposits.md) — `rate_sheet_version_id` and `tan_basis_points` on `DepositConstituted`.

---

*Decided 2026-05-23 by jhosm.*
