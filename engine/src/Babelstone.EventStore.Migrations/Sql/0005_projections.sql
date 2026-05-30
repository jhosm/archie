-- 0005_projections.sql
--
-- Bitemporal projection storage (ADR-PC-002 §P1/§P2). Forward-only (ADR-PC-001 §P5).
-- PII columns are BYTEA ciphertext envelopes (ADR-PC-004 §P2) — the engine resolves
-- them via OpenBao; the storage layer sees opaque bytes only.
--
-- Two time axes (ADR-PC-002 §P1):
--   World-time  — (valid_from, valid_to): the interval during which the position
--                  was true in the real world. valid_to NULL = open-ended (current).
--   Transaction-time — (recorded_at, superseded_at): when the engine believed this
--                  row to be correct. superseded_at NULL = currently-believed.
--
-- Correction model (ADR-PC-002 §P2 / §6.3 criterion #1):
--   A correction is NOT an UPDATE of an existing row. Instead:
--     1. UPDATE deposit_position_projection SET superseded_at = <now>
--        WHERE stream_id = $1 AND superseded_at IS NULL  (close old belief)
--     2. INSERT a new row with the corrected values and superseded_at NULL  (new belief)
--   Both old and corrected rows are retained — the full bitemporal history is
--   queryable at any point in world-time × transaction-time.
--
-- Structural payload is BYTEA (serialized cleartext projection state, no PII).
-- PII payload is a separate BYTEA ciphertext envelope (ADR-PC-004 §P2).
-- This mirrors the SnapshotRecord byte-oriented boundary: the typed, domain-aware
-- columns (principal_cents, term_days, etc.) live in the D.3 typed query layer
-- above this storage boundary.
--
-- This table holds the term-deposit running example (deposit position). Later
-- product families add their own projection tables following the same pattern.

CREATE TABLE deposit_position_projection (
    -- Surrogate key: multiple historical rows share a stream_id.
    row_id              BIGSERIAL   NOT NULL,
    stream_id           UUID        NOT NULL,

    -- World-time axis: the interval during which the position was real.
    valid_from          TIMESTAMPTZ NOT NULL,
    valid_to            TIMESTAMPTZ NULL,           -- NULL = still open in world-time

    -- Transaction-time axis: when this belief was recorded / superseded.
    recorded_at         TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp(),
    superseded_at       TIMESTAMPTZ NULL,           -- NULL = currently-believed

    -- Structural cleartext payload (no PII — ADR-PC-004 §P2).
    -- Serialized projection state; typed columns are unpacked by the D.3 query layer.
    structural_payload  BYTEA       NOT NULL,

    -- PII ciphertext envelope (ADR-PC-004 §P2). Opaque to the storage layer; the
    -- engine decrypts via OpenBao. NULL until actual PII is added in later work.
    pii_ciphertext      BYTEA       NULL,

    CONSTRAINT deposit_position_projection_pkey PRIMARY KEY (row_id)
);

-- Hot read: "the current belief for this stream" — WHERE superseded_at IS NULL.
-- A partial index keeps it small: only currently-believed rows are indexed.
CREATE INDEX deposit_position_projection_current_belief_idx
    ON deposit_position_projection (stream_id)
    WHERE superseded_at IS NULL;

-- UPDATE is granted (unlike the append-only events/outbox/rate_sheets pattern)
-- because supersession requires closing current-belief rows:
--   UPDATE … SET superseded_at = <now> WHERE stream_id = $1 AND superseded_at IS NULL
-- This mirrors the snapshots pattern (ADR-PC-002 §P4): projections are a rebuildable
-- derived view, never the source of truth. The events log remains append-only.
GRANT SELECT, INSERT, UPDATE ON deposit_position_projection TO babelstone_engine;
