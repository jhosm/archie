-- 0003_snapshots.sql
--
-- Snapshot machinery (feature-design event-store §8). Forward-only (ADR-PC-001 §P5).
--
-- Snapshots are a performance optimisation, NOT the source of truth (§8 / §10.5):
-- the engine can always rebuild from the events log alone. So, unlike `events`
-- (strictly append-only, §P3), the `snapshots` table is a rebuildable cache — the
-- runtime role gets full CRUD on it, because discarding and re-taking snapshots is
-- normal operation (the monthly discard-rebuild drill, §8.3). A wrong snapshot is
-- the worst event-sourcing failure mode, so two defences are built into the shape:
--   • state_hash covers the last_event_id (§8.3) — rebuild verifies against it;
--   • trusted defaults FALSE (advisory-until-trusted, §8.3) — a snapshot is only
--     promoted to production-replay use after six months of passing drills.
--
-- Snapshot writes are eventually-consistent with the log, never transactional with
-- the append (§8.1) — this table is written outside AppendAsync's transaction.

CREATE TABLE snapshots (
    stream_id     UUID        NOT NULL,
    at_sequence   BIGINT      NOT NULL,
    last_event_id UUID        NOT NULL,   -- the event at at_sequence; folded into state_hash (§8.3)
    state_hash    TEXT        NOT NULL,   -- SHA-256 over (state || last_event_id), hex
    state         BYTEA       NOT NULL,   -- serialized projection state
    trusted       BOOLEAN     NOT NULL DEFAULT FALSE,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp(),

    CONSTRAINT snapshots_pkey PRIMARY KEY (stream_id, at_sequence)
);

-- Latest-snapshot-for-a-stream is the hot query (snapshot-then-tail rehydrate);
-- a descending index answers it with a single backward scan.
CREATE INDEX snapshots_stream_latest_idx ON snapshots (stream_id, at_sequence DESC);

-- The runtime role manages its own snapshot cache: write, read, promote-to-trusted,
-- and discard. None of this touches the append-only events guarantee.
GRANT SELECT, INSERT, UPDATE, DELETE ON snapshots TO babelstone_engine;
