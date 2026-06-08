-- 0013_read_model.sql
-- D.4 CQRS read-model surface (ADR-IC-005): the denormalized, query-optimized read side
-- for term deposits, on the SAME PostgreSQL tier as the event store (ADR-IC-005 §S1 — zero
-- incremental infrastructure). This is DISTINCT from the bitemporal `projections` table
-- (D.1/D.2, migration 0005/0010): `projections` is the rebuildable bitemporal belief store
-- behind the typed AsOf/CurrentBelief/HistoryOf query (ADR-PC-002); THIS table is the flat
-- CQRS read model that backs the sub-50ms client-facing query surface (ADR-IC-005 §S1, the
-- 500ms deposits-screen SLA), feeding the I.2 Query API.
--
-- ADR-IC-005 §P1 — read-model tables live in a DEDICATED `read_model` schema, separate from
--   the write-side domain tables (`events`, `projections`, …) in `public`. The schema boundary
--   makes a cross-boundary join visible in code review; no projector writes the event log and
--   no command path writes here.
-- ADR-IC-005 §P2 — the canonical projection write is an UPSERT with a monotonicity guard. This
--   engine's event store has no Redpanda-offset column (events drain PER STREAM with no
--   cluster-wide order, see IEventStore.ReadStreamIdsAsync); the per-stream `sequence_number`
--   IS this engine's offset analog, so the §P2 `last_event_offset` guard is realised here as
--   `last_sequence`. The projector writes `ON CONFLICT (stream_id) DO UPDATE … WHERE
--   read_model.deposits.last_sequence < EXCLUDED.last_sequence`, so a re-delivered or
--   out-of-order event never overwrites a fresher row (the at-least-once drainer is safe).
-- ADR-IC-005 §P3 — every read-model row carries `last_updated` (TIMESTAMPTZ) and the offset
--   analog (`last_sequence`). UNLIKE the §P3 sketch, `last_updated` here is RUNTIME-SUPPLIED
--   from the producing event's transaction_time, never the SQL clock — a CQRS read model fed by
--   an event-sourced log must rebuild byte-identically (ADR-PC-010 §P5, the rebuild-determinism
--   gate), and clock_timestamp()-at-write cannot (two rebuilds would disagree). So no column
--   DEFAULT here; the projector always stamps it.
-- ADR-PC-018 §6.2 — the unified read surface exposes `sor ∈ {engine, legacy}` as a first-class
--   per-instance column: it is the single source of routing truth (the channel/gateway tier
--   READS it; the engine never embeds routing logic). An engine-materialised deposit is always
--   `sor = 'engine'`, set at constitution and never changed; the column is reserved here for the
--   coexistence read surface a legacy-ingest path (ADR-PC-017) later co-populates.
-- ADR-PC-004 §P2 — structural read fields are cleartext; this read model holds NO PII (no holder
--   name, no NIF) — only structural deposit facts and a serialized structural `detail` body. PII,
--   when it lands, rides a separate ciphertext envelope, never the durable read surface.
-- ADR-PC-001 §P5 — forward-only; no down-migration.
--
-- Like `projections`/`snapshots` (rebuildable caches), this table GRANTs UPDATE (the §P2 UPSERT
-- needs it) and is rebuildable by TRUNCATE + re-fold (ADR-IC-005 §P5), UNLIKE the append-only
-- `events` table which REVOKEs UPDATE/DELETE.

CREATE SCHEMA IF NOT EXISTS read_model;
GRANT USAGE ON SCHEMA read_model TO babelstone_engine;

CREATE TABLE read_model.deposits (
    -- The instance id (= the aggregate/stream id). One denormalized row per deposit: the
    -- `deposit_detail` point-lookup of ADR-IC-005 §"six projections".
    stream_id        UUID         PRIMARY KEY,

    -- ADR-PC-018 §6.2 routing-truth column. 'engine' for every engine-materialised deposit;
    -- the enum widens additively (a future third owning system) without a schema rewrite.
    sor              TEXT         NOT NULL DEFAULT 'engine',

    -- Denormalized query dimensions — the columns the client-facing and range-scan reads index
    -- on (ADR-IC-005: point lookup by id, range scan by maturity_date). All money is integer
    -- cents (ADR-PC-010 §P1 / BMNY002), never a float or a nested object.
    --
    -- TWO product keys are surfaced, each under its HONEST name:
    --   * `rate_sheet_version_id` — the PRICE/version key (e.g. `pt-deposits-2026.1`): which rate
    --     sheet the TAN was resolved from. One-to-many to products (one sheet prices many variants).
    --   * `product_code` — the catalogue STRUCTURAL product code (e.g. `dpz_pt_12m_juros_venc`):
    --     the queryable "which product is this" dimension a client filters on. NOW IMPLEMENTED
    --     (bd babelstone-v794): DepositConstituted carries it (additive Avro field, default ""),
    --     the position folds it, and this read model denormalizes it.
    --
    -- PROSPECTIVE-ONLY semantics (bd babelstone-v794): the catalogue code is stamped from
    -- `ConstituteDepositCommand.ProductId` AT constitution. Deposits constituted BEFORE v794 never
    -- carried it: their `DepositConstituted` decodes the Avro field as the "" default, and the code
    -- CANNOT be back-filled from the event log because it was discarded at constitution and
    -- `rate_sheet_version_id` → product is one-to-many (a version cannot be inverted to a single
    -- product). So historical read-model rows carry the empty code; only deposits constituted from
    -- v794 onward carry a populated `product_code`. (Earlier this column was deliberately ABSENT to
    -- avoid a `product_id` mislabelled as the version id — bd babelstone-yfr2 deferred note; v794
    -- carries the real code end-to-end and adds the column under its true name.) Structural, NOT
    -- PII (ADR-PC-004 §P2). NOT NULL DEFAULT '' so it is additive over migration 0013's prior rows.
    principal_cents       BIGINT       NOT NULL,
    tan_basis_points      INTEGER      NOT NULL,
    rate_sheet_version_id TEXT         NOT NULL,
    product_code          TEXT         NOT NULL DEFAULT '',
    term_days             INTEGER      NOT NULL,
    start_date            DATE         NOT NULL,
    maturity_date         DATE         NOT NULL,
    interest_variant      TEXT         NOT NULL,
    lifecycle             TEXT         NOT NULL,
    total_payout_cents    BIGINT       NOT NULL,

    -- The fully-denormalized read body (the structural projection state, serialized). Stored as
    -- a byte-oriented payload so the read-model store stays family-agnostic (ADR-PC-021 §P2):
    -- the spine persists opaque bytes + the typed query columns above; the family owns the
    -- detail shape. A future projection needing JSON-path query on the body can migrate this to
    -- jsonb additively (ADR-IC-005 Decision: jsonb is the documented escape hatch).
    detail           BYTEA        NOT NULL,

    -- ADR-IC-005 §P3 mandatory pair. last_sequence is the §P2 monotonicity guard (this engine's
    -- per-stream offset analog); last_updated is the producing event's transaction_time
    -- (deterministic, see header), surfaced for staleness display and read-after-write strategies.
    last_sequence    BIGINT       NOT NULL,
    last_updated     TIMESTAMPTZ  NOT NULL
);

-- The range-scan access pattern of ADR-IC-005's `upcoming_maturities` projection: a B-tree on
-- maturity_date answers "deposits maturing in [from, to)" with an index range scan.
CREATE INDEX deposits_maturity_date_idx ON read_model.deposits (maturity_date);

-- The engine role reads and writes this rebuildable read model (the §P2 UPSERT needs UPDATE).
-- A clean rebuild (ADR-IC-005 §P5) TRUNCATEs and re-folds, so the role also gets DELETE/TRUNCATE
-- here — UNLIKE the append-only `events` log. INSERT into no SERIAL column, so no sequence grant.
GRANT SELECT, INSERT, UPDATE, DELETE, TRUNCATE ON read_model.deposits TO babelstone_engine;
