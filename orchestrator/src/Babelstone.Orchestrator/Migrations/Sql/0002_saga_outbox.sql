-- 0002_saga_outbox.sql
--
-- The saga command outbox (ADR-IC-003 §P1 "Outbox for commands: saga-emitted commands use
-- the same outbox mechanism as all other services in the architecture … not a separate
-- publish path"). H.2 (babelstone-n55u) replaces the substrate's in-memory RecordingCommandSink
-- (babelstone-mj2i) with a REAL outbox-row writer behind the same ISagaCommandSink seam, so a
-- command the saga decides commits ATOMICALLY with the state move, the transition-history row,
-- and the inbox dedup row — no command escapes for a transition that rolled back, and none is
-- lost for one that committed (effectively-once command emission, the same transactional-outbox
-- guarantee the engine's 0001 outbox gives event publication).
--
-- Forward-only (ADR-PC-001 §P5, lifted): once applied this migration is never edited; shape
-- changes land as higher-numbered migrations.
--
-- NO PII (ADR-PC-004 §P2 / no-PII-on-the-durable-bus). Every column below is structural — a
-- process reference, the identity trio (causation/correlation), a command-TYPE name, and an
-- opaque payload of references. A subject's NIF/IBAN/name/amount NEVER lands here; the saga
-- carries REFERENCES and resolves PII internally behind the engine's OpenBao boundary, exactly
-- as the durable bus does. The drain seam (a future relay, Epic E's mechanism) is the only
-- reader; it is not wired here (this migration delivers the WRITE side H.2 owes).

-- ---------------------------------------------------------------------------
-- saga_outbox — ONE row per command the saga emitted. Mirrors the engine outbox shape
-- (engine 0001_events_and_outbox.sql) adapted to the saga's command emission:
--
--   message_id     — the OPERATIONAL delivery id of THIS command emission (a fresh v4 GUID
--                    minted in the impure sink shell, NOT in the logical payload). The dedup
--                    identity a downstream consumer's inbox keys on. Minted per emission, so
--                    it is the ONE place wall-clock-adjacent uniqueness is allowed — it never
--                    rides the payload body.
--   process_id     — the saga instance the command belongs to (structural, not PII).
--   command_type   — the command NAME the state machine decided (e.g. 'ReserveAccountBalance').
--                    The contract the drain dispatches on; a type name, never PII.
--   causation_id   — the triggering event's message id (the §P7 causation source). A
--                    pre-existing reference carried through — never minted here.
--   correlation_id — the originating request's correlation reference, carried UNCHANGED through
--                    the saga (§P7). Structural GUID, nullable, not PII.
--   payload        — the LOGICAL command body bytes: process id + identity trio + structural
--                    references ONLY (the H.2 command DTOs). Byte-STABLE — re-emitting the same
--                    logical command yields identical bytes (no minted GUID/timestamp inside).
--                    NO PII (ADR-PC-004 §P2). BYTEA mirrors the engine outbox payload column.
--   status         — PENDING → PUBLISHED, the drain lifecycle (mirrors engine outbox).
--   seq            — the monotonic EMISSION-SEQUENCE ordinal, DB-GENERATED ALWAYS AS IDENTITY.
--                    Retrieval orders on THIS, not created_at: a BIGINT identity is strictly
--                    monotone per insert, so it reflects EMISSION ORDER independent of clock
--                    granularity (two rows inserted within one clock_timestamp() tick still get
--                    distinct, ordered seq values). The sink NEVER writes it — it is generated.
--   created_at /
--   published_at   — DB-clock OPERATIONAL audit stamps (clock_timestamp()). The wall clock lives
--                    HERE, in the operational column — NEVER in the payload body or any
--                    transition decision (ADR-PC-010 §P5). Retained as the operational audit
--                    stamp; emission ORDERING is seq's job, not created_at's.
-- ---------------------------------------------------------------------------
CREATE TABLE saga_outbox (
    seq            BIGINT       GENERATED ALWAYS AS IDENTITY,
    message_id     UUID         NOT NULL,
    process_id     UUID         NOT NULL,
    command_type   VARCHAR      NOT NULL,
    causation_id   UUID         NOT NULL,
    correlation_id UUID,
    payload        BYTEA        NOT NULL,
    status         VARCHAR      NOT NULL DEFAULT 'PENDING',
    created_at     TIMESTAMPTZ  NOT NULL DEFAULT clock_timestamp(),
    published_at   TIMESTAMPTZ,

    -- message_id is the emission's stable delivery identity and primary key — the dedup key a
    -- downstream consumer's inbox collides a redelivery on.
    CONSTRAINT saga_outbox_pkey PRIMARY KEY (message_id),
    -- The payload belongs to a saga instance; the FK keeps a command from referencing a
    -- phantom saga (mirrors saga_transition's FK).
    CONSTRAINT saga_outbox_process_fk FOREIGN KEY (process_id)
        REFERENCES saga_state (process_id),
    CONSTRAINT saga_outbox_status_chk CHECK (status IN ('PENDING', 'PUBLISHED'))
);

-- The drain reads PENDING rows in EMISSION ORDER (seq, monotonic — independent of clock
-- granularity, mirrors engine outbox_pending_idx). A partial index keeps that bounded to the
-- unpublished tail and cheap as the table grows.
CREATE INDEX saga_outbox_pending_idx ON saga_outbox (seq) WHERE status = 'PENDING';

-- Reconstructing one saga's emitted commands is "every row for this process_id". A btree on
-- (process_id, seq) makes that an emission-ordered scan.
CREATE INDEX saga_outbox_process_idx ON saga_outbox (process_id, seq);

-- ---------------------------------------------------------------------------
-- Privilege envelope (ADR-PC-001 §P3, lifted; extends 0001's babelstone_orchestrator role).
--   saga_outbox — SELECT/INSERT (the sink writes a PENDING row in the saga tx) + UPDATE (the
--                 drain flips PENDING → PUBLISHED). No DELETE at runtime (a published row is
--                 retained for audit; a retention sweep, if any, is a separate privileged job).
-- The belt-and-braces REVOKE keeps the intent explicit and survives a future GRANT mistake.
-- ---------------------------------------------------------------------------
GRANT SELECT, INSERT, UPDATE ON saga_outbox TO babelstone_orchestrator;
REVOKE DELETE, TRUNCATE ON saga_outbox FROM babelstone_orchestrator;
