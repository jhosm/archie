-- 0015_command_dedup.sql
--
-- The engine's COMMAND-INGRESS idempotency ledger (ADR-PC-029 slot 4, the
-- ENGINE_COMMAND_IDEMPOTENT fitness function). ADR-PC-029 fixes the engine's command
-- ingress as synchronous idempotent REST: the saga's command dispatcher (and the edge /
-- MCP) POST commands point-to-point, delivery is at-least-once, and "a replay of an
-- already-applied command id returns the original commit_sequence with no second append."
-- This table is where the RECEIVER records what it has applied so a retry is safe.
--
-- The mirror of the consumer-side inbox (0012): the inbox dedupes physical EVENT deliveries
-- on message_id; this dedupes logical COMMAND intents on the caller's command_id — "cousins,
-- not twins" (Document 04, quoted in 0012). The inbox is the message_id half; this is the
-- command_id half. (ADR-PC-029 §Decision slot 4 / §Residual-risks.)
--
-- Forward-only: once applied this migration is never edited (ADR-PC-001 §P5); shape changes
-- land as new, higher-numbered migrations.
--
-- ---------------------------------------------------------------------------
-- command_dedup — { command_id (PK), stream_id, commit_sequence, created_at }.
--
-- The dedup mechanism is the PRIMARY KEY itself, exactly as the inbox's is: the receiver
-- INSERTs the command_id INSIDE the same transaction as the events + outbox append
-- (PostgresEventStore.AppendAsync), recording the head the append reached. A retry of an
-- already-applied command collides on the primary key; that violation IS the "already
-- applied -> return the original outcome" signal. The receipt INSERT precedes the events
-- INSERT in the transaction, so a concurrent duplicate that picked a different (server-
-- generated) stream id still loses on command_id before it can open a second stream — and
-- because the whole sequence is one transaction, the receipt and the events commit (or roll
-- back) together: no receipt without its events, no second append behind a winning receipt.
--
--   command_id      — the caller's deterministic command id (ADR-PC-029 slot 1; in practice
--                     the saga's saga_outbox row id). The idempotency key, scoped per
--                     aggregate by construction: one command mutates one aggregate, and the
--                     stored stream_id records which.
--   stream_id       — the aggregate the command was applied to. For a constitution the
--                     deposit id is an OUTPUT (it may be server-generated), so a replay reads
--                     it back from here rather than from the (possibly newly-generated)
--                     retried request — that is why the dedup key is command_id, not stream.
--   commit_sequence — the per-stream head version the append reached (ADR-IC-005 §P3
--                     read-your-writes token). The original result a replay returns.
--   created_at      — when the command was first applied (DB clock). Informational + the
--                     retention sweep's age key; never part of the dedup decision (the PK is).
--                     Carries NO PII (ADR-PC-004 §P2) — only structural ids and a sequence.
-- ---------------------------------------------------------------------------
CREATE TABLE command_dedup (
    command_id      UUID         NOT NULL,
    stream_id       UUID         NOT NULL,
    commit_sequence BIGINT       NOT NULL,
    created_at      TIMESTAMPTZ  NOT NULL DEFAULT clock_timestamp(),

    -- command_id is the dedup key: the PRIMARY KEY is the in-transaction dedup mechanism.
    -- A replayed command violates this constraint — that violation IS the "already applied"
    -- signal the receiver returns the original receipt on.
    CONSTRAINT command_dedup_pkey PRIMARY KEY (command_id)
);

-- Retention sweep seam (mirrors the inbox's, 0012): the ledger grows with every command, so
-- a nightly job deletes rows older than the at-least-once retry window (ADR-PC-029 slot 4:
-- "a bounded retention window is an implementation detail"). A btree on created_at keeps that
-- range-delete cheap and bounded to the tail. The sweep is operational (a cron/job), out of
-- this schema's scope — the index is the seam.
CREATE INDEX command_dedup_created_at_idx ON command_dedup (created_at);

-- The engine's runtime role (provisioned in 0002) reads the receipt (the pre-check SELECT)
-- and inserts it in the append transaction, and the retention sweep DELETEs aged rows — so
-- the ledger grants SELECT, INSERT, DELETE. Deliberately UNLIKE the append-only events log
-- (no UPDATE here: a receipt is written once and only ever deleted by retention, never
-- mutated), exactly as the inbox. The belt-and-braces REVOKE keeps the intent explicit and
-- survives a future GRANT mistake.
GRANT SELECT, INSERT, DELETE ON command_dedup TO babelstone_engine;
REVOKE UPDATE, TRUNCATE ON command_dedup FROM babelstone_engine;
