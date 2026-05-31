-- 0011_projection_checkpoints.sql
-- D.2 async-projector progress markers (ADR-PC-002 §P4 rebuildability, two-modes §5.4).
--
-- The events table carries NO cluster-wide total-order column (only the per-stream
-- (stream_id, sequence_number) order, 0001), so the async projection drainer works PER
-- STREAM: for each stream it folds events with sequence_number greater than this checkpoint.
-- The checkpoint is therefore keyed by (projection_kind, stream_id) and stores the last
-- per-stream sequence_number folded.
--
-- This is a HIGH-WATER MARK, not durable belief state: the `projections` rows are the truth,
-- and the source_sequence guard (0010) makes re-applying an already-folded event a no-op, so
-- losing a checkpoint is harmless — it just re-reads (and skips) from sequence 0. DELETE is
-- granted here for exactly that reason (rebuild resets the markers), deliberately UNLIKE the
-- append-only events/outbox and the supersede-only projections table.
--
-- v1 scale drains each kind single-threaded by enumerating the family's streams; a v4
-- partition-parallel drain would add a cluster-wide cursor (a global_sequence column on
-- events) — a forward change, out of D.2 scope.
--
-- ADR-PC-001 §P5 — forward-only; no down-migration.

CREATE TABLE projection_checkpoints (
    projection_kind      TEXT         NOT NULL,  -- family-prefixed, e.g. 'term_deposit.deposit_position'
    stream_id            UUID         NOT NULL,
    last_sequence_number BIGINT       NOT NULL,  -- last per-stream sequence_number folded
    last_processed_at    TIMESTAMPTZ  NOT NULL,  -- informational only; never used in rebuild logic
    PRIMARY KEY (projection_kind, stream_id)
);

-- Checkpoints are ephemeral high-water marks, so DELETE is granted (rebuild resets them) —
-- deliberately UNLIKE the append-only events/outbox tables.
GRANT SELECT, INSERT, UPDATE, DELETE ON projection_checkpoints TO babelstone_engine;
