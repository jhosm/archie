-- 0001_saga_state.sql
--
-- The in-house saga orchestrator's source-of-truth tables (ADR-IC-003: the
-- "Event-driven application orchestrator" — saga state is a table in the application
-- database, not a vendor's workflow store). Forward-only: once applied this migration
-- is never edited (ADR-PC-001 §P5, lifted convention); shape changes land as new,
-- higher-numbered migrations.
--
-- This schema is the substrate H.2 (constitution) and H.3 (renewal) build their
-- concrete sagas onto (babelstone-mj2i). It carries the SHARED infrastructure of
-- ADR-IC-003 §P1 — optimistic-concurrency state persistence, the transition history,
-- and the consumer inbox dedup row — and is deliberately saga-type-agnostic: the state
-- enumeration and the valid-transition table are saga-SPECIFIC and live in code
-- (ADR-IC-003 §P1 "Saga-specific — never shared", §P2 "the state machine is the
-- specification"), never as data here.
--
-- NO PII (ADR-PC-004 §P2 / no-PII-on-the-durable-bus): every column below is
-- structural — a process id, a saga-type name, a business STATE label, a correlation
-- GUID, an operational note. A subject's NIF/IBAN/name/amount NEVER lands here; the
-- saga carries REFERENCES (process_id, correlation_id) and resolves PII internally
-- behind the engine's OpenBao boundary, exactly as the durable bus does.

-- ---------------------------------------------------------------------------
-- babelstone_orchestrator — the orchestrator's runtime role (mirror of 0002's
-- babelstone_engine, ADR-PC-001 §P3). UNLIKE the engine's append-only log, the saga
-- aggregate is MUTATED in place under optimistic concurrency (ADR-IC-003 §Residual
-- "Concurrent writer race"), so this role is granted UPDATE on saga_state — the one
-- deliberate difference from the append-only envelope. saga_transition and inbox stay
-- append-only (INSERT, never UPDATE). A NOLOGIN group role: deployments create a
-- concrete login user GRANTed membership; tests SET ROLE to assert the envelope.
-- Idempotent so re-running the migration set is a no-op.
DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'babelstone_orchestrator') THEN
        CREATE ROLE babelstone_orchestrator NOLOGIN;
    END IF;
END
$$;

-- ---------------------------------------------------------------------------
-- saga_state — ONE row per running saga instance (ADR-IC-003 §S2 "Saga state is a
-- table in the application database … long-running waits are rows in a state column
-- that survive crashes"). The aggregate ConstitutionProcess (Document 05) IS this row.
--
--   process_id     — the saga instance id (Document 05's PROC-… reference). Stable,
--                    structural, NOT PII; the dedup/idempotency identity for the saga.
--   saga_type      — which state machine governs this row (e.g. 'ConstitutionProcess').
--                    The code-side transition table (ADR-IC-003 §P2) is keyed on this.
--   state          — the CURRENT business state (ADR-IC-003 §P3 "states model business
--                    reality": STARTED / PARALLEL_VALIDATION / … / COMPLETED /
--                    HUMAN_INTERVENTION_REQUIRED). A human operator reads this column
--                    directly — that is what makes the ops console possible without a
--                    vendor workflow UI (ADR-IC-003 §S2).
--   version        — the optimistic-concurrency guard (ADR-IC-003 §Residual "Concurrent
--                    writer race", §P1). Every advance does
--                    UPDATE … SET version = version + 1 WHERE process_id = ? AND
--                    version = ?; the losing concurrent writer matches zero rows,
--                    re-reads, and retries — it never clobbers.
--   correlation_id — the originating request's correlation id (ADR-IC-003 §P7, Primitive
--                    4): carried UNCHANGED through the saga so its whole execution is one
--                    traceable chain. Structural GUID, not PII.
--   created_at /
--   updated_at     — DB-clock audit stamps (clock_timestamp()). NEVER part of a
--                    transition decision (the state + version are) — purely operational.
-- ---------------------------------------------------------------------------
CREATE TABLE saga_state (
    process_id     UUID         NOT NULL,
    saga_type      VARCHAR      NOT NULL,
    state          VARCHAR      NOT NULL,
    version        BIGINT       NOT NULL DEFAULT 0,
    correlation_id UUID,
    created_at     TIMESTAMPTZ  NOT NULL DEFAULT clock_timestamp(),
    updated_at     TIMESTAMPTZ  NOT NULL DEFAULT clock_timestamp(),

    -- process_id is the saga instance identity and primary key. A duplicate StartSaga
    -- for the same process_id collides here — that collision is the "saga already
    -- started" signal the start path treats idempotently.
    CONSTRAINT saga_state_pkey PRIMARY KEY (process_id)
);

-- The ops console (ADR-IC-003 §S2 / Document 05 §"What This Concrete Saga Shows" point 1)
-- lists sagas by state — "show me every HUMAN_INTERVENTION_REQUIRED". A btree on
-- (saga_type, state) keeps that bounded scan cheap as instance count grows.
CREATE INDEX saga_state_type_state_idx ON saga_state (saga_type, state);

-- ---------------------------------------------------------------------------
-- saga_transition — the APPEND-ONLY transition history (ADR-IC-003 §F2 "ConstitutionProcess
-- state transitions are persisted … full audit trail"; §P2 the transition table is the
-- spec, this is its execution log). One row per accepted advance: who/what drove it, the
-- from→to states, the triggering event, and the message that caused it (the identity trio,
-- §P7). This is the immutable audit trail an operator or auditor reads to reconstruct a
-- saga's history — DORA/PSD2 evidence (ADR-IC-003 §F2).
--
--   from_state / to_state — the transition the state machine accepted (ADR-IC-003 §P2).
--   event_type            — the inbox event that triggered the transition (e.g.
--                           'ConstitutionRequested', 'BalanceReserved'). Structural type
--                           name, never PII.
--   message_id            — the ce_id of the triggering event (the causation source, §P7).
--                           Structural GUID.
--   note                  — optional operational-tier label (e.g. the compensation reason
--                           CATEGORY). MUST stay operational — NEVER a NIF/IBAN/amount
--                           (ADR-PC-004 §P2), exactly like the inbox result_summary.
--   occurred_at           — DB-clock audit stamp.
-- ---------------------------------------------------------------------------
CREATE TABLE saga_transition (
    id          BIGINT       GENERATED ALWAYS AS IDENTITY,
    process_id  UUID         NOT NULL,
    from_state  VARCHAR      NOT NULL,
    to_state    VARCHAR      NOT NULL,
    event_type  VARCHAR      NOT NULL,
    message_id  UUID,
    note        VARCHAR,
    occurred_at TIMESTAMPTZ  NOT NULL DEFAULT clock_timestamp(),

    CONSTRAINT saga_transition_pkey PRIMARY KEY (id),
    -- The history belongs to a saga instance; cascade is irrelevant (saga_state rows
    -- are never deleted) but the FK keeps a transition from referencing a phantom saga.
    CONSTRAINT saga_transition_process_fk FOREIGN KEY (process_id)
        REFERENCES saga_state (process_id)
);

-- Reconstructing one saga's history is "every transition for this process_id, in order".
-- A btree on (process_id, id) makes that an index-only ordered scan.
CREATE INDEX saga_transition_process_idx ON saga_transition (process_id, id);

-- ---------------------------------------------------------------------------
-- inbox — the consumer-side dedup row (Document 04 "Inbox Pattern"; ADR-IC-003 §P1
-- "Inbox deduplication: the same deduplication table … applied to saga event
-- consumption"). The orchestrator is a Redpanda consumer like every other service
-- (ADR-IC-003 §S2), so it carries the SAME inbox the engine does (lifted shape from the
-- engine's 0012_inbox.sql). The message_id PK is the dedup mechanism: the saga-advance
-- handler INSERTs the message_id INSIDE the SAME transaction as the state UPDATE, so a
-- duplicate physical delivery collides on the PK and the advance never runs twice —
-- effectively-once saga progression (the idempotent inbox-driven advance this issue owes).
--
--   message_id     — the envelope's CloudEvents ce_id (ADR-IC-015). The dedup identity.
--   source_topic   — the topic the event arrived on (structural, not PII).
--   result_summary — optional operational note: the saga step taken (e.g. the to_state).
--                    NEVER PII (ADR-PC-004 §P2).
--   processed_at   — DB-clock stamp; the retention sweep's age key, not the dedup decision.
-- ---------------------------------------------------------------------------
CREATE TABLE inbox (
    message_id     UUID         NOT NULL,
    source_topic   VARCHAR      NOT NULL,
    processed_at   TIMESTAMPTZ  NOT NULL DEFAULT clock_timestamp(),
    result_summary VARCHAR,

    CONSTRAINT inbox_pkey PRIMARY KEY (message_id)
);

-- Retention sweep seam (Document 04 "Inbox retention"): a nightly job deletes rows older
-- than the re-delivery window. A btree on processed_at keeps that range-delete cheap.
CREATE INDEX inbox_processed_at_idx ON inbox (processed_at);

-- ---------------------------------------------------------------------------
-- Privilege envelope (ADR-PC-001 §P3, lifted). The orchestrator's runtime role:
--   saga_state      — SELECT/INSERT (start a saga) + UPDATE (advance under optimistic
--                     concurrency, the one mutation the saga aggregate needs). No DELETE.
--   saga_transition — SELECT/INSERT only: the audit history is append-only, never
--                     mutated or deleted at runtime.
--   inbox           — SELECT/INSERT (dedup in the handler tx) + DELETE (retention sweep),
--                     exactly the engine inbox envelope (0012). No UPDATE: a dedup row is
--                     written once and only ever deleted by retention.
-- The belt-and-braces REVOKEs keep the intent explicit and survive a future GRANT mistake.
GRANT SELECT, INSERT, UPDATE ON saga_state TO babelstone_orchestrator;
REVOKE DELETE, TRUNCATE ON saga_state FROM babelstone_orchestrator;

GRANT SELECT, INSERT ON saga_transition TO babelstone_orchestrator;
GRANT USAGE ON SEQUENCE saga_transition_id_seq TO babelstone_orchestrator;
REVOKE UPDATE, DELETE, TRUNCATE ON saga_transition FROM babelstone_orchestrator;

GRANT SELECT, INSERT, DELETE ON inbox TO babelstone_orchestrator;
REVOKE UPDATE, TRUNCATE ON inbox FROM babelstone_orchestrator;
