-- 0001_events_and_outbox.sql
--
-- The engine's source-of-truth tables. Forward-only: once this migration is
-- applied it is never edited (ADR-PC-001 §P5); shape changes land as new,
-- higher-numbered migrations.
--
-- Anchors:
--   events  — ADR-PC-001 §P1 (the column contract is the integration boundary),
--             §P4 (the two day-one indices).
--   outbox  — ADR-IC-004 §P1 (the outbox column contract).
-- The append + outbox write commit in ONE local transaction (ADR-PC-001 §P2 /
-- ADR-IC-004 §P6); that is the writer's job (A.2), not the schema's.

-- ---------------------------------------------------------------------------
-- events — the append-only log. PII lives inside `payload` as ciphertext under
-- per-subject keys (ADR-PC-004); the structural columns stay queryable.
-- ---------------------------------------------------------------------------
CREATE TABLE events (
    event_id             UUID         NOT NULL,
    stream_id            UUID         NOT NULL,
    sequence_number      BIGINT       NOT NULL,
    event_type           VARCHAR      NOT NULL,
    event_schema_version INTEGER      NOT NULL,
    family               VARCHAR      NOT NULL,
    partition_key        UUID         NOT NULL,
    pack_version         VARCHAR      NOT NULL,
    schema_version       VARCHAR      NOT NULL,
    valid_time           TIMESTAMPTZ  NOT NULL,
    transaction_time     TIMESTAMPTZ  NOT NULL DEFAULT clock_timestamp(),
    causation_id         UUID,
    correlation_id       UUID,
    actor                VARCHAR      NOT NULL,
    payload              BYTEA        NOT NULL,
    payload_schema_id    INTEGER      NOT NULL,

    -- event_id is the stable identifier and primary key (§P1).
    CONSTRAINT events_pkey PRIMARY KEY (event_id),
    -- Per-stream monotonicity: (stream_id, sequence_number) is unique (§P1, §P4).
    -- This is the seam optimistic concurrency rejects a stale append against (A.2).
    CONSTRAINT events_stream_seq_uq UNIQUE (stream_id, sequence_number)
);

-- §P4 — UNIQUE (stream_id, sequence_number) backs per-stream cold replay in order.
-- The UNIQUE constraint above already creates this index; named here for the
-- contract's sake. (No separate CREATE INDEX: the constraint's index is it.)
COMMENT ON CONSTRAINT events_stream_seq_uq ON events IS 'events_stream_seq_idx (ADR-PC-001 §P4)';

-- §P4 — (partition_key, sequence_number) is the v4-sharding seam. Non-unique,
-- low cost at v1 traffic; makes hash(partition_key) partitioning non-breaking later.
CREATE INDEX events_partition_key_seq_idx ON events (partition_key, sequence_number);

-- ---------------------------------------------------------------------------
-- outbox — ADR-IC-004 §P1. Written in the same transaction as the event it
-- mirrors; drained by the polling publisher (Epic E), which is the ONLY reader.
-- ---------------------------------------------------------------------------
CREATE TABLE outbox (
    event_id        UUID         NOT NULL,
    aggregate_type  VARCHAR      NOT NULL,
    aggregate_id    UUID         NOT NULL,
    sequence_number BIGINT       NOT NULL,
    event_type      VARCHAR      NOT NULL,
    payload         BYTEA        NOT NULL,
    schema_id       INTEGER      NOT NULL,
    status          VARCHAR      NOT NULL DEFAULT 'PENDING',
    created_at      TIMESTAMPTZ  NOT NULL DEFAULT clock_timestamp(),
    published_at    TIMESTAMPTZ,

    CONSTRAINT outbox_pkey PRIMARY KEY (event_id),
    CONSTRAINT outbox_status_chk CHECK (status IN ('PENDING', 'PUBLISHED'))
);

-- §P2 (IC-004, amended 2026-05-29) — the publisher drains PENDING rows in per-aggregate
-- order. created_at TIES within a single multi-event append: one transaction_time stamps
-- every row, so the tiebreaker must be sequence_number — the authoritative per-stream
-- monotonic key (mirrors events.sequence_number) — NOT the random v4 event_id, which
-- cannot order intra-append rows. A partial index keeps the drain cheap and bounded to
-- the unpublished tail.
CREATE INDEX outbox_pending_idx ON outbox (created_at, sequence_number) WHERE status = 'PENDING';
