# ADR-005: CQRS Read Model Storage

| Field | Value |
|---|---|
| Status | Accepted |
| Date | 2026-05-17 |
| Deciders | jhosm |
| Common criteria | [ADR-000](./ADR-000-common-evaluation-criteria.md) |
| Depends on | [ADR-001](./ADR-001-event-backbone-message-broker.md), [ADR-002](./ADR-002-schema-format-and-registry.md) |

---

## Context

[Document 03](../03-cqrs-and-read-models.md) defines CQRS as the mechanism for meeting the 500ms requirement on the client-facing deposits screen. Read models — denormalized, pre-computed projections fed by the Redpanda event backbone — decouple read performance from write-side complexity. The Anti-Corruption Layer ([document 02](../02-anti-corruption-layer.md)) feeds Core Banking data into these projections, producing a cross-system combination of deposit conditions, client labels from CRM, and KYC signals from Compliance that no single source system holds. What remains to be decided is the concrete storage technology for those projections.

The decision is structurally different from the tool-selection ADRs that precede it. ADR-001 through ADR-004 each evaluate a single storage technology for a single purpose. Read model storage is inherently plural: document 03 identifies six distinct projections, each designed for a specific query, and those queries have different access patterns, latency requirements, and volume characteristics.

The right question is therefore not "which storage technology wins?" but "which storage paradigm serves each projection, and where does the team constraint change the answer?"

### The six projections and their access patterns

| Projection | Primary access pattern | Notes |
|---|---|---|
| `deposits_by_client` | Point lookup by `client_id` | Hot path; client-facing; sub-50ms SLA; updated on every deposit event |
| `deposit_detail` | Point lookup by `deposit_id` | Hot path; client-facing; fully denormalized; sub-50ms SLA |
| `upcoming_maturities` | Range scan by `maturity_date` | Background; notification trigger; days-level freshness acceptable |
| `interest_history_by_deposit` | Point lookup + ordered list by `deposit_id` | Statement screen; ordered by date; sub-200ms acceptable |
| `product_catalog_for_simulation` | Full-scan, small static dataset | Rate simulation; cache-friendly; rarely updated |
| `aggregated_positions` | Full aggregation by product, term, date range | BdP regulatory reporting; hours-level freshness acceptable; complex aggregations |

These patterns map naturally to different storage paradigms:

- **Point lookups with sub-50ms SLA** → relational (with index) or key-value
- **Range scans and ordered lists** → relational (B-tree or sorted index)
- **Small, frequently read static datasets** → any; in-process cache suffices
- **Complex multi-dimensional aggregations** → columnar/OLAP or materialized views

The team constraint (1–2 people, zero operational budget) is the forcing function that determines how many paradigms are worth introducing.

**Candidates evaluated:**

The candidates are evaluated as paradigm choices, not merely as individual tools. Each paradigm brings a different cost model — operational, query-model, and exit — and the decision applies that model to the specific projections above.

| # | Paradigm | Representative tool | Notes |
|---|---|---|---|
| A | **Relational (PostgreSQL)** | PostgreSQL | Write-side database already present; row-oriented; SQL |
| B | **Key-value cache** | Valkey | Open-source Redis fork under Linux Foundation governance; BSD licence |
| C | **Search / inverted index** | OpenSearch | Apache 2.0 fork of Elasticsearch; maintained by AWS |
| D | **Embedded columnar (OLAP)** | DuckDB | In-process analytical engine; MIT licence; zero operational overhead |

MongoDB (document store) was considered and excluded before evaluation: MongoDB changed its licence to SSPL 1.0 in 2018, which fails the F1 hard filter — SSPL is not OSI-approved and contains use restrictions that constrain the host application's deployment options. Open-source substitutes exist (CouchDB under Apache 2.0, ArangoDB Community Edition under Apache 2.0), but none materially out-performs PostgreSQL for the denormalized-projection access patterns identified above at POC scale; the relational paradigm serves the same need without introducing a second storage technology to operate. The decision can be revisited if a future projection genuinely requires flexible nested-document indexing that PostgreSQL `jsonb` cannot serve.

---

## Evaluation

### Hard filter results

#### F1 · Cost / licensing

| Candidate | Licence | Assessment | Proceeds? |
|---|---|---|---|
| PostgreSQL | PostgreSQL Licence (permissive, OSI-approved) | Fully open source; self-hosted; already committed by ADR-004 | **Pass** |
| Valkey | BSD 3-Clause | Linux Foundation project; fork of Redis 7.2.4 (before Redis re-licensing); permissive licence | **Pass** |
| OpenSearch | Apache 2.0 | AWS-maintained fork of Elasticsearch 7.10; open source | **Pass** |
| DuckDB | MIT | In-process OLAP engine; embedded in the application; MIT licence | **Pass** |

*Original Redis changed to RSALv2 + SSPLv1 dual-licence in April 2024 (Redis 7.4+). Valkey (maintained under the Linux Foundation) is the recommended open-source continuation of the Redis 7.2.x codebase. Any evaluation of "Redis" in this ADR refers to Valkey unless otherwise noted.*

*Date of licence assessment: 2026-05-17.*

#### F2 · Regulatory fit

Read model projections contain a cross-system combination of data drawn through the ACL: deposit conditions from the write-side aggregate, client-facing labels from CRM, KYC state signals from Compliance. Document 03 notes that this combination can produce a richer cross-system profile than any individual source system holds — which makes the regulatory surface of the read model at least as sensitive as any individual source.

| Candidate | GDPR | DORA | PSD2 | Proceeds? |
|---|---|---|---|---|
| PostgreSQL | Row-level deletion and schema-level encryption are standard PostgreSQL capabilities. Projections can be rebuilt from the event stream after a GDPR erasure tombstone on the write side; the projection row can be deleted or masked independently. No new GDPR surface beyond the write-side database, with which it shares operational controls. | PostgreSQL HA, backup, and recovery semantics are well-understood and documentable. Replication lag is an observable metric. DORA resilience testing (failover drills) is standard PostgreSQL practice. | Read model query responses are not PSD2-regulated API surfaces themselves, but the data they expose falls within PSD2 account data access rules when account-level fields are projected. Enforcement is at the query API layer (authorization), not the storage layer. | **Pass** |
| Valkey | Persistence in Valkey is optional (RDB snapshots, AOF log, or neither). A purely in-memory Valkey deployment introduces a GDPR risk: erasure tombstones on the write side do not automatically propagate to the Valkey cache without an explicit invalidation mechanism. The projector must actively delete or overwrite affected cache entries on every erasure event. If Valkey persistence is enabled, the persistence files must be in the GDPR data inventory. | A Valkey cache in front of PostgreSQL read models degrades gracefully: on Valkey failure, the application falls back to PostgreSQL. However, Valkey must still be treated as a DORA resilience target if its availability enters the SLA definition. | No specific PSD2 concern beyond the shared GDPR and PSD2 data access obligations. | **Pass (conditional)** — explicit cache erasure on every GDPR tombstone is mandatory; persistence files (if enabled) must be in the data inventory |
| OpenSearch | OpenSearch documents contain the full projection payload — the same PII surface as PostgreSQL, but in an inverted-index structure that does not support row-level deletion with the same granularity. Deleting a document removes the forward-stored document, but index segments containing the data are only fully removed on segment merge, which is asynchronous. GDPR erasure is achievable but less deterministic than a SQL DELETE; OpenSearch must be included in the erasure protocol with explicit attention to segment merge timing. | OpenSearch is a JVM process, which reintroduces the JVM operational concern from ADR-001 for a component already served by PostgreSQL at POC volumes. | No specific PSD2 concern beyond shared access-control obligations. | **Pass (conditional)** — erasure protocol must account for asynchronous segment merge timing; deterministic deletion windows must be defined and monitored |
| DuckDB | DuckDB runs embedded in the application process and writes to a single file (`.duckdb`) if persistence is enabled. The file is DuckDB's GDPR surface: it must be in the data inventory, and PII deletion in the application database does not automatically propagate to the DuckDB file. For the `aggregated_positions` projection (the primary DuckDB use case), data is aggregated at the product/term level — individual PII is typically not present. GDPR risk depends on whether the projection retains identifiable fields. | DuckDB's durability model (file-backed or in-memory) is under application control. No independent operational process to manage or monitor. | No specific PSD2 concern beyond shared data-access obligations on the aggregated fields. | **Pass (conditional)** — projection schema must be reviewed to confirm no PII in aggregated fields; if identifiable fields are present, the DuckDB file must enter the erasure protocol |

All four candidates pass both hard filters.

---

### Soft criteria

#### PostgreSQL (relational)

**S1 · Operational complexity:** Zero incremental infrastructure. The application database is PostgreSQL already — ADR-004 committed this for the outbox pattern, and ADR-003 for the saga state tables. Read model projections are tables in a separate schema (`read_model.*`), populated by projector services using the same Kafka client and PostgreSQL driver already present in the stack. No new connection pool, no new backup policy, no new monitoring target beyond what the existing database already requires.

**S2 · Ecosystem coherence:** Maximum. Projectors are PostgreSQL writers; the API layer is a PostgreSQL reader. The same ORM, the same connection pool, the same EXPLAIN plan tooling, the same PostgreSQL slow-query log. UPSERT semantics (`INSERT … ON CONFLICT DO UPDATE`) are the natural mechanism for idempotent projectors (Primitive 5 from [document 01](../01-the-six-primitives.md)). The `processed_events` deduplication table described in document 03 is a PostgreSQL table, in the same transaction as the projection update — the idempotency check is atomic with the write by construction. OpenTelemetry instrumentation is uniform with the rest of the application.

**S3 · Exit cost:** Lowest possible. A read model in PostgreSQL is a table with a known schema and standard SQL semantics. Migrating a specific projection to a different paradigm requires: creating the new store, updating the projector to write to both, verifying convergence, and cutting the API layer over. This is addition, not replacement. The PostgreSQL projection remains until the team has validated the new store.

**S4 · Community and longevity:** PostgreSQL is the world's most advanced open-source relational database. Multi-decade track record, foundation governance, enormous ecosystem. The PostgreSQL licence has not changed in 30 years. No longevity concern.

**Where PostgreSQL requires more explicit effort than specialized paradigms:**

- **Sub-10ms cache-tier latency** is not achievable: on a properly indexed table, a point lookup by `client_id` returns in 1–5ms at low concurrency and under 20ms at moderate load. This meets a 50ms SLA comfortably but does not achieve the sub-10ms tier a memory-resident key-value store can provide. At Portuguese banking volumes (thousands of active deposits, not millions), this gap is academically measurable but not operationally significant.
- **OLAP-scale aggregations** (full-table scans across millions of rows for BdP reporting) will eventually exceed PostgreSQL's row-oriented performance. At POC volumes, `aggregated_positions` can be served by a PostgreSQL materialized view refreshed on a schedule. The threshold at which this breaks SLA depends on row count and query complexity — it is not a day-one concern.

---

#### Valkey (key-value cache)

**S1 · Operational complexity:** Introduces a second process, a second persistence configuration decision (none / RDB / AOF), and a second set of availability guarantees to document and meet. Valkey is operationally simpler than PostgreSQL and OpenSearch, but it is not free: each additional service adds monitoring surface, connection management, and a new failure mode (cache miss cascade, stale cache, cold-start eviction). The cache invalidation logic — projector writes to both PostgreSQL and Valkey, and must handle partial failure atomically — is a coordination obligation that does not exist with PostgreSQL alone.

**S2 · Ecosystem coherence:** Valkey speaks the Redis protocol; every language has a mature client. However, the data model is a mismatch for read model projections: the natural storage unit for `deposits_by_client` in Valkey is a hash (field-per-column) keyed by `client_id`, but projections often contain nested structures (multiple deposits per client) that require either serializing the full projection as a JSON string (losing individual-field update semantics) or modelling as a sorted set of deposit IDs (requiring multiple round-trips). PostgreSQL's relational model aligns more naturally with the UPSERT-based projector pattern.

**S3 · Exit cost:** Low. Valkey holds no proprietary data format beyond its protocol. Client code is the Redis protocol, which is stable. Migrating away replaces the Valkey client calls with PostgreSQL queries; projector logic that writes to both stores loses the Valkey write leg. The migration does not touch the write side or the Redpanda backbone.

**S4 · Community and longevity:** Valkey was launched in April 2024 as the Linux Foundation-governed fork of Redis 7.2.4, responding to Redis's licence change. The founding contributors include engineers from AWS, Google Cloud, Oracle, Ericsson, and Snap. Linux Foundation governance is a positive longevity signal. The risk is that Valkey is young — two years old at the time of this ADR — and its long-term trajectory is not yet proven to the same depth as PostgreSQL.

---

#### OpenSearch (search / inverted index)

**S1 · Operational complexity:** OpenSearch is a JVM cluster. It requires heap sizing, JVM version management, shard configuration, index lifecycle management, and its own monitoring stack. ADR-001 chose Redpanda over Apache Kafka explicitly to eliminate JVM operational overhead from the event backbone. Introducing OpenSearch for read models reintroduces a JVM process — and a more demanding one than a typical service. For a 1–2 person team, this is the highest operational cost of all candidates per unit of read-model benefit.

**S2 · Ecosystem coherence:** OpenSearch excels at full-text search, complex boolean queries, and aggregations over large document sets — use cases not present in the current projection inventory. The `aggregated_positions` reporting model is a good fit for OpenSearch's aggregation framework, but it is an equally strong fit for a PostgreSQL materialized view at POC scale. None of the six projections from document 03 requires full-text search. The inverted-index paradigm is powerful but misdirected for projections that are primarily point-lookup or range-scan in character. Adding OpenSearch to serve `aggregated_positions` alone is disproportionate to the benefit at this scale.

**S3 · Exit cost:** Medium-high. OpenSearch index mappings are specific to the OpenSearch query DSL. Aggregation queries (`aggs`, `nested`, `pipeline`) are not portable to SQL. Application code that depends on OpenSearch aggregation semantics requires non-trivial rewriting on migration. The data (documents) can be re-projected to any target, but the query-layer logic is OpenSearch-specific.

**S4 · Community and longevity:** OpenSearch is AWS-maintained under the Apache 2.0 licence, with a growing community. Longevity is good. However, AWS's strategic investment in OpenSearch is commercially motivated — the community's independence from a single vendor is not as strong as the PostgreSQL Foundation or the Linux Foundation (Valkey).

---

#### DuckDB (embedded columnar)

**S1 · Operational complexity:** DuckDB runs in-process — embedded in the projector or API service as a library, with no separate process, no port, no cluster configuration. The operational surface is a `.duckdb` file (if persistence is enabled) and a dependency in the build. For the `aggregated_positions` use case, the projector can maintain an append-only DuckDB file that receives events and answers aggregate queries without any additional infrastructure. This is the lowest operational cost of any specialized paradigm candidate.

**S2 · Ecosystem coherence:** DuckDB's SQL interface is standard; its query model is compatible with the relational mental model the team already uses for PostgreSQL. The embedded paradigm means projector code writes to DuckDB with a local connection rather than a network connection. DuckDB's columnar storage makes the aggregation queries for `aggregated_positions` significantly faster than row-oriented PostgreSQL at large row counts. However, DuckDB's concurrency model (single writer at a time in most modes) limits the projector to serial writes — consistent with the document 03 recommendation that each projection has a dedicated projector.

**S3 · Exit cost:** Low. DuckDB uses standard SQL and stores data in a portable Parquet-compatible format. Migrating an `aggregated_positions` projection to PostgreSQL or another OLAP tool requires re-projecting from the event stream and rewriting the aggregation queries in the target system's SQL dialect. Application code is largely portable.

**S4 · Community and longevity:** DuckDB is developed by DuckDB Labs under the MIT licence. The community is fast-growing, with significant academic and industry adoption. The risk is that DuckDB Labs is a small company, and long-term maintenance trajectory depends more on a small team than PostgreSQL's global distributed community. For an embedded analytical engine at POC scale, this risk is acceptable.

---

## Decision

**Chosen: PostgreSQL (relational) — as the sole read model storage technology at POC inception**

The decisive reason is not that PostgreSQL is the best storage technology for every read model access pattern — it is not. The decisive reason is that the team constraint makes the cost of introducing a second storage paradigm non-trivially higher than the benefit, at any volume the Portuguese banking POC will generate.

The analysis bears this out projection by projection. The 500ms SLA on the deposits screen is served by a `SELECT * FROM read_model.deposits_by_client WHERE client_id = ?` on a properly indexed PostgreSQL table. At Portuguese banking volumes (thousands of active deposits, not millions), this query executes in under 5ms. The sub-10ms latency that Valkey could offer is unmeasured improvement in a budget that already has 495ms of headroom. The `aggregated_positions` reporting model requires aggregations that PostgreSQL serves with a materialized view at POC-scale row counts; the threshold at which DuckDB's columnar storage would be measurably faster is not reached by a proof-of-concept system. OpenSearch's inverted-index paradigm addresses full-text search and complex aggregations — use cases not present in the current projection inventory.

Introducing any of the three specialized paradigms (Valkey, OpenSearch, DuckDB) at inception means: provisioning an additional service (or embedded file), adding connection management, adding GDPR surface to the erasure protocol, adding operational monitoring, and adding projector complexity (dual-write for cache invalidation, or separate projectors per store). Each of these costs is real and compounds. The benefit at POC scale is not measured — it is anticipated.

The upgrade path from PostgreSQL to a specialized paradigm is clean, explicit, and informed by measurement:

- When `deposits_by_client` query latency exceeds SLA under production load → introduce Valkey with explicit cache invalidation and a 7-day operational observation period before making it the primary path
- When `aggregated_positions` materialized view refresh time exceeds the BdP reporting SLA → introduce DuckDB (embedded, zero operational overhead) for that projection only
- When a new projection requires full-text search or multi-dimensional aggregation that PostgreSQL cannot serve → introduce OpenSearch for that projection only, with explicit GDPR audit of the index
- When three or more projections simultaneously breach SLA on PostgreSQL → re-evaluate the single-storage assumption as a whole, rather than introducing a fourth paradigm piecemeal. At that point the operational cost of running PostgreSQL for some projections and a second paradigm for others is comparable to a planned migration, and the team has measurement to support a paradigm-level decision.

The upgrade path is never "migrate everything to the new paradigm" — it is "add the paradigm for the specific projection that exceeds SLA, measured." The wholesale re-evaluation above is the explicit exception: it kicks in only when piecemeal additions have stopped paying off.

---

**Rejected: Valkey (key-value)**

The latency advantage (sub-10ms vs. sub-20ms for PostgreSQL) is a real paradigm difference, but not a relevant one at Portuguese banking volumes where the total SLA budget is 500ms and the current read path consumes under 20ms. The cache invalidation coordination obligation — projector writes to both PostgreSQL and Valkey, and must handle partial failure atomically — adds projector complexity without any measured benefit at POC scale. The GDPR erasure path requires explicit cache invalidation logic that does not exist for the PostgreSQL-only case. Valkey is the correct upgrade when latency measurement under production load shows PostgreSQL is the bottleneck on the deposits hot path.

**Rejected: OpenSearch (search / inverted index)**

Reintroduces a JVM cluster — the operational concern ADR-001 deliberately avoided in the event backbone — for projections that do not require full-text search or complex boolean queries. None of the six projections from document 03 has an access pattern that PostgreSQL cannot serve at POC volumes. The aggregation use case (`aggregated_positions`) is equally served by a PostgreSQL materialized view at this scale. OpenSearch's GDPR erasure timing (segment merge asynchrony) adds protocol complexity not present for PostgreSQL.

**Rejected: DuckDB (embedded columnar)**

Not a permanent rejection — DuckDB is the preferred upgrade path for the `aggregated_positions` projection when materialized view refresh time becomes operationally significant. The rejection here is temporal: at POC inception, `aggregated_positions` is served adequately by a PostgreSQL materialized view with a scheduled refresh. Introducing DuckDB before the measurement is available is premature optimization. When the threshold is crossed, DuckDB's embedded, zero-operational-overhead model makes it the lowest-friction upgrade path for that specific projection.

---

## Consequences

**What this choice makes easier:**

- Projector code is uniform: every projector in the system is a Kafka consumer that writes to PostgreSQL using UPSERT semantics (`INSERT … ON CONFLICT DO UPDATE SET …, last_event_offset = ?`). No projector writes to a different storage technology; no dual-write coordination logic exists at inception. The idempotency check (`WHERE last_event_offset < current_offset`) is atomic with the projection write in a single PostgreSQL transaction — no two-phase check, no race condition.
- GDPR erasure is handled at one layer: a tombstone event in the Redpanda backbone (per ADR-001 and ADR-002) triggers re-projection; the PostgreSQL row is deleted or overwritten in the same operation. No cache invalidation, no index purge protocol, no external store to synchronize.
- The query API layer speaks SQL to one store. No result merging across paradigms, no fallback logic for cache miss.
- Rebuild and replay (identified in document 03 as non-negotiable) is straightforward: truncate the projection table, reset the consumer group offset to zero, replay the full event history. The rebuilding projector uses the same write path as live operations.

**What this choice makes harder or impossible:**

- **Cache-tier latency** for the deposits hot path is not available at inception. If the 500ms SLA is tightened (e.g., mobile client requirements), PostgreSQL's sub-20ms performance must be measured against the new requirement before Valkey is introduced.
- **Full-text search** across projection fields is available via PostgreSQL's `tsvector` / `to_tsquery` mechanism, which covers the basic cases (deposit product name search). Complex relevance ranking and aggregation-over-full-corpus queries are not available without OpenSearch.
- **OLAP-scale analytics** (sub-second aggregations across millions of projection rows) are not available without DuckDB or a dedicated read replica with a columnar extension (e.g., `pg_analytics`).

**Residual risks:**

- **Read-after-write staleness for the deposits screen:** this is an explicit architectural consequence of CQRS, not a storage decision risk, but it surfaces at the read model layer. See document 03 for the three strategies (write-model fallback, optimistic client projection, wait-for-projection). Every projection row must surface a `last_event_offset` field so the API layer can implement read-after-write strategies without querying both sides blindly.
- **Shared-database write contention:** placing read model projections in the same PostgreSQL instance as the write side creates contention risk if projection writes are high-volume. At Portuguese banking volumes, this risk is theoretical. If projector write volume measurably degrades write-side performance (observable via lock wait metrics and replication lag), the correct response is a read model replica — a PostgreSQL streaming replica used only by projectors and read APIs — not a different paradigm.
- **Materialized view refresh blocking:** the `aggregated_positions` materialized view uses `REFRESH MATERIALIZED VIEW CONCURRENTLY` to allow reads during refresh. If a refresh takes longer than the refresh interval, two refreshes contend for the materialized view's `ACCESS EXCLUSIVE` lock at the swap step and one will stall behind the other; the projection then falls progressively behind reality and reporting reads see increasingly stale data. Monitor `pg_stat_user_tables` refresh timing and alert if refresh duration exceeds 50% of the refresh interval — that threshold leaves headroom to act before successive refreshes overlap.

---

## Implementation Principles

### P1 — Projections live in a dedicated schema

All read model tables must reside in a dedicated PostgreSQL schema (`read_model`) separate from the write-side domain schema (`domain`) and the saga/outbox schema (`infra`). This makes the storage-boundary explicit in the schema layout: a query that joins `read_model.*` and `domain.*` tables is visible as a cross-boundary query and can be flagged in code review. No table may exist in `read_model` that is not a projection; no projector may write to `domain.*`.

---

### P2 — UPSERT with event offset guard is the canonical projection write

Every projector must write projections using an UPSERT with an offset guard:

```sql
INSERT INTO read_model.deposits_by_client (
  client_id, deposit_id, …, last_event_offset
)
VALUES (?, ?, …, ?)
ON CONFLICT (deposit_id) DO UPDATE SET
  …field assignments…,
  last_event_offset = EXCLUDED.last_event_offset
WHERE read_model.deposits_by_client.last_event_offset < EXCLUDED.last_event_offset;
```

The `WHERE` predicate ensures idempotency: a duplicate event with the same or lower offset does not overwrite a more recent projection state. The `last_event_offset` field must be present on every projection table and must carry the Redpanda topic partition offset of the event that produced the current state.

---

### P3 — Every projection table carries `last_updated` and `last_event_offset`

These two columns are mandatory on every read model table:

| Column | Type | Purpose |
|---|---|---|
| `last_updated` | TIMESTAMPTZ | Wall-clock time of the most recent projection write; used for staleness detection and API responses |
| `last_event_offset` | BIGINT | Redpanda offset of the event that last modified this row; used for idempotency (P2) and read-after-write strategies |

The `last_event_offset` is not a version number: it is the source-of-truth offset in the event backbone, making projection state traceable to the specific event that produced it.

---

### P4 — Projection lag is a first-class SLI

Each projector must emit a `projection_lag_seconds` gauge — the age of the most recently processed event relative to the current wall clock — on every event consumed. This is the read model equivalent of `outbox_publish_lag_seconds` from ADR-004. Alert thresholds:

- **Warning:** projection lag exceeds 5 seconds. The read model is falling behind the event stream.
- **Critical:** projection lag exceeds 60 seconds. The projector is likely stalled or the consumer group is lagging.

This metric enables the API layer to surface staleness to clients (e.g., showing a `last_updated` timestamp) and enables operations to detect a broken projector before it manifests as user-facing inconsistency.

---

### P5 — Rebuild is a first-class operation, not a break-glass procedure

Each projector must support a clean rebuild path:

1. Pause the projector consumer.
2. Truncate the affected projection table(s) (`TRUNCATE read_model.deposits_by_client`).
3. Reset the consumer group offset to the beginning of the topic partition.
4. Resume the projector; it replays the full event history and repopulates the projection.

The rebuild path must be tested in the initial development cycle — not deferred to a production incident. Document 03 identifies three triggers that make rebuild non-optional: projector bugs (fix and re-project), projection schema evolution (add a column that requires historical data), and corruption recovery.

---

### P6 — `aggregated_positions` uses a scheduled materialized view

The `aggregated_positions` projection is not maintained by a projector in the standard event-driven path. Its aggregation semantics (sum of positions by product, term, and date) are not well-served by row-by-row UPSERT: a single `DepositConstituted` event requires recalculating every aggregate that includes the new deposit's product, term, and date bucket.

Instead, `aggregated_positions` uses a PostgreSQL materialized view over the `deposits_by_client` and `interest_history_by_deposit` projection tables, refreshed on a schedule aligned with the BdP reporting SLA (e.g., every hour for operational dashboards, every night for regulatory submissions). The refresh uses `REFRESH MATERIALIZED VIEW CONCURRENTLY` to permit reads during refresh.

When this refresh latency becomes unacceptable (measurable via reporting query execution time), DuckDB is the preferred upgrade path: the existing projection tables can be exported to Parquet (DuckDB's native format) and the aggregation queries re-expressed in DuckDB's SQL dialect with minimal application changes.

---

### P7 — GDPR erasure propagates through the projection path

A GDPR erasure request on a client triggers a tombstone event in the Redpanda backbone (per ADR-001 and ADR-002). Each projector that holds data for the affected client must consume this tombstone and execute the appropriate erasure action on its projection table(s):

- For row-level PII (e.g., `client_id`, name fields in `deposits_by_client`): `DELETE` the affected rows, or overwrite with nulls per the applicable GDPR-minimum standard.
- For aggregate projections (`aggregated_positions`): verify that the projection schema does not retain individual `client_id` values. If it does, the same delete/overwrite discipline applies.

The erasure action must be processed by the projector in the same consumer loop as domain events — not as a separate out-of-band process. This ensures that the erasure acknowledgement timestamp (required by GDPR Article 17) is observable from the projector's `projection_lag_seconds` SLI and the `last_updated` column on the affected rows.
