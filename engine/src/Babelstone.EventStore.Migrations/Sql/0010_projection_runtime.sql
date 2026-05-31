-- 0010_projection_runtime.sql
-- D.2 projection runtime: make the D.1 `projections` table multi-projection-ready,
-- idempotent under at-least-once replay, and rebuild-deterministic
-- (ADR-PC-002 §P1/§P2/§P4, two-modes §5.4, ADR-PC-010 §P5).
--
-- D.1 (0005) shipped a single-projection store keyed only by stream_id, with recorded_at
-- defaulting to clock_timestamp(). D.2 turns it into the runtime substrate:
--
-- (1) projection_kind discriminator — one stream carries MORE than one projection (F.6:
--     deposit position, accrual schedule, maturity calendar, withholding ledger), so
--     supersede/read-current-belief scope to a (stream_id, projection_kind) PAIR. The value
--     is family-prefixed (e.g. 'term_deposit.deposit_position'); the runtime always stamps it.
--
-- (2) source_sequence — the per-stream sequence_number of the event that produced this
--     belief. The async drainer is at-least-once (it re-reads from a high-water checkpoint
--     after a crash); the projection-apply step is made IDEMPOTENT by skipping any event
--     whose sequence_number is <= the current belief's source_sequence. Without this, the
--     accumulating folds (state.X + event.Y) would double-count a re-delivered event.
--
-- (3) recorded_at is RUNTIME-SUPPLIED (the source event's transaction_time), never the SQL
--     clock, so a cold rebuild reproduces a BIT-IDENTICAL projection (ADR-PC-010 §P5).
--     clock_timestamp()-at-insert cannot — two rebuilds would disagree on belief-time.
--
-- (4) exactly-one-current-belief per (stream_id, projection_kind) becomes a DB invariant via
--     a PARTIAL UNIQUE index (WHERE superseded_at IS NULL), not a runtime convention: a
--     missed supersede fails LOUD instead of leaving two current rows the LIMIT-1 read masks.
--
-- ADR-PC-001 §P5 — forward-only; no down-migration. All changes are additive over the
--   rebuildable projection cache; grants are inherited from 0005 (SELECT/INSERT/UPDATE; NO
--   DELETE/TRUNCATE — supersede, never destroy).

-- (1) projection_kind. The transient DEFAULT back-fills any pre-D.2 rows so NOT NULL holds
--     during the ALTER; dropped immediately so steady-state writes MUST supply the kind.
ALTER TABLE projections ADD COLUMN projection_kind TEXT NOT NULL DEFAULT 'unknown';
ALTER TABLE projections ALTER COLUMN projection_kind DROP DEFAULT;

-- (2) source_sequence. Transient DEFAULT -1 (= "no source event") back-fills pre-D.2 rows;
--     dropped so the runtime always stamps the producing event's sequence_number.
ALTER TABLE projections ADD COLUMN source_sequence BIGINT NOT NULL DEFAULT -1;
ALTER TABLE projections ALTER COLUMN source_sequence DROP DEFAULT;

-- (3) recorded_at is the source event's transaction_time, supplied by the runtime.
ALTER TABLE projections ALTER COLUMN recorded_at DROP DEFAULT;

-- (4) Replace the D.1 non-unique (stream_id) lookup index with a PARTIAL UNIQUE index on the
--     (stream_id, projection_kind) pair — it serves BOTH the current-belief lookup and the
--     one-current-belief invariant, so the old index is redundant and dropped.
DROP INDEX projections_current_belief_idx;
CREATE UNIQUE INDEX projections_current_belief_uq
    ON projections (stream_id, projection_kind)
    WHERE superseded_at IS NULL;
