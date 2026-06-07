-- 0012_inbox.sql
--
-- The consumer-side INBOX (Document 04 "Inbox Pattern — Solving Duplication at the
-- Consumer"; ADR-IC-004 §Residual-risks: "The inbox idempotency pattern from Document
-- 04 is mandatory — not optional — for every consumer in this architecture"). The mirror
-- of the producer-side outbox (0001): the outbox makes publication at-least-once; the
-- inbox makes consumption effectively-once by deduplicating PHYSICAL deliveries.
--
-- Forward-only: once applied this migration is never edited (ADR-PC-001 §P5); shape
-- changes land as new, higher-numbered migrations.
--
-- ---------------------------------------------------------------------------
-- inbox — Document 04: { message_id (PK), processed_at, result_summary (optional) }.
--
-- The dedup mechanism is the PK itself (Document 04): the handler INSERTs the message_id
-- INSIDE its own transaction; a second physical delivery of the same message collides on
-- the primary key, the INSERT fails, and the consumer treats the constraint violation as
-- "already processed → skip and commit the offset". Two threads racing the same delivery
-- resolve the same way — one INSERT wins, the other loses by constraint and ignores.
--
--   message_id   — the envelope's CloudEvents ce_id (the producer's event_id, ADR-IC-015),
--                  the stable physical-delivery identity. NOT idempotency_key: message_id
--                  deduplicates physical deliveries of one event; idempotency_key (in a
--                  command payload) deduplicates logical intents — "cousins, not twins"
--                  (Document 04). This table is the message_id half.
--   source_topic — the topic the message arrived on (e.g. 'term_deposit'). Structural, not
--                  PII — it is the aggregate_type / topic name, never a subject value.
--   processed_at — when this consumer first processed the delivery (DB clock). Informational
--                  + the retention sweep's age key; never used in the dedup decision itself
--                  (the PK is the decision).
--   result_summary — optional, nullable (Document 04 "result_summary (optional)"): a short
--                  consumer-local note (e.g. the saga step taken). MUST stay operational-tier
--                  — NEVER a NIF/IBAN/name/amount or any PII (ADR-PC-004 §P2); the durable bus
--                  carries references, and so does this consumer-local audit column.
-- ---------------------------------------------------------------------------
CREATE TABLE inbox (
    message_id     UUID         NOT NULL,
    source_topic   VARCHAR      NOT NULL,
    processed_at   TIMESTAMPTZ  NOT NULL DEFAULT clock_timestamp(),
    result_summary VARCHAR,

    -- message_id is the dedup key: the PRIMARY KEY is the in-transaction dedup mechanism
    -- (Document 04). A duplicate physical delivery violates this constraint — that violation
    -- IS the "already processed" signal the consumer skips on.
    CONSTRAINT inbox_pkey PRIMARY KEY (message_id)
);

-- Retention sweep seam (Document 04 "Inbox retention"): the table grows indefinitely, so a
-- nightly job deletes rows older than the re-delivery window (Kafka retention × N; typically
-- 7–30 days). A btree on processed_at keeps that range-delete cheap and bounded to the tail.
-- The sweep is operational (a cron/job), out of this schema's scope — the index is the seam.
CREATE INDEX inbox_processed_at_idx ON inbox (processed_at);

-- The consumer's runtime role (provisioned in 0002) reads + inserts the dedup row in the
-- handler transaction, and the retention sweep DELETEs aged rows — so the inbox grants
-- SELECT, INSERT, DELETE. Deliberately UNLIKE the append-only events log (no UPDATE here:
-- a dedup row is written once and only ever deleted by retention, never mutated). The
-- belt-and-braces REVOKE keeps the intent explicit and survives a future GRANT mistake.
GRANT SELECT, INSERT, DELETE ON inbox TO babelstone_engine;
REVOKE UPDATE, TRUNCATE ON inbox FROM babelstone_engine;
