-- 0005_projections.sql
-- Path-A bitemporal projection storage.
--
-- ADR-PC-002 §P1 — projection rows are bitemporal: world (valid_from/valid_to) is
--   what the position is BELIEVED true for, and belief (recorded_at/superseded_at)
--   is when we believed it. A forced correction supersedes the prior belief without
--   deleting it, so the full belief history stays queryable.
-- ADR-PC-002 §P2 — supersession is an in-place UPDATE that stamps superseded_at on
--   the previously-believed row(s); the corrected row is INSERTed alongside.
-- ADR-PC-004 §P2 — structural state is stored cleartext (structural_payload BYTEA);
--   PII rides in a separate ciphertext envelope column (pii_ciphertext BYTEA), NULL
--   until PII is added by a later task. No key material lives here.
-- ADR-PC-001 §P5 — migrations are forward-only; there is no down-migration.
--
-- This table is a REBUILDABLE projection cache (ADR-PC-002 §P4), the same posture as
-- snapshots (0003). It therefore GRANTs UPDATE (needed for supersession), UNLIKE the
-- append-only events/rate_sheets tables (0001/0004) which REVOKE UPDATE and DELETE.

CREATE TABLE deposit_position_projection (
    row_id             BIGSERIAL    PRIMARY KEY,
    stream_id          UUID         NOT NULL,
    -- World time: the slice of believed-reality this row describes.
    valid_from         TIMESTAMPTZ  NOT NULL,
    valid_to           TIMESTAMPTZ  NULL,         -- NULL = open-ended world time
    -- Belief time: when we recorded this belief, and when it was superseded.
    recorded_at        TIMESTAMPTZ  NOT NULL DEFAULT clock_timestamp(),
    superseded_at      TIMESTAMPTZ  NULL,         -- NULL = currently-believed
    -- Serialized cleartext structural state — byte-oriented boundary, mirroring
    -- snapshots.state BYTEA (0003). Serialization shape is the caller's concern.
    structural_payload BYTEA        NOT NULL,
    -- ADR-PC-004 §P2 ciphertext envelope; NULL until PII is added by a later task.
    pii_ciphertext     BYTEA        NULL
);

-- Fast lookup of the currently-believed row(s) for a stream (ReadCurrentBelief).
CREATE INDEX deposit_position_projection_current_belief_idx
    ON deposit_position_projection (stream_id)
    WHERE superseded_at IS NULL;

-- The engine role reads and writes this rebuildable cache. UPDATE is required so
-- supersession can stamp superseded_at on the prior belief (ADR-PC-002 §P2) — this
-- mirrors snapshots (0003, rebuildable cache, ADR-PC-002 §P4) and is deliberately
-- UNLIKE the append-only events/rate_sheets tables which REVOKE UPDATE.
GRANT SELECT, INSERT, UPDATE ON deposit_position_projection TO babelstone_engine;
-- INSERT into a BIGSERIAL column draws from its backing sequence; grant USAGE so the
-- engine role can advance it.
GRANT USAGE ON SEQUENCE deposit_position_projection_row_id_seq TO babelstone_engine;
