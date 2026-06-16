-- 0004_saga_outbox_dispatch_failure.sql
--
-- The saga command DISPATCHER's terminal-failure surface (bd babelstone-t7o3.3, ADR-PC-029
-- slot 5). The saga DECIDES commands and writes them to saga_outbox; the dispatcher (the drain
-- side this change builds) DELIVERS each one to the engine over idempotent HTTP and flips the row
-- PENDING → PUBLISHED on a 2xx. But the engine can also REFUSE a command (a 4xx — an illegal
-- lifecycle transition or a validation reject); that outcome can never be retried into success, so
-- it must NOT be silently dropped and it must NOT loop forever as a transient retry. This migration
-- adds the FAILED terminal state (+ the operational columns that record WHY and WHEN), so a refused
-- command lands durably in a state the saga's compensation path can react to (ADR-PC-029 slot 5
-- "a terminal delivery outcome the dispatcher surfaces to the saga as a failure branch").
--
-- Forward-only (ADR-PC-001 §P5, lifted): 0002/0003 stay untouched; this is a higher-numbered
-- additive migration. The new columns are NULLABLE — a PENDING/PUBLISHED row carries NULL for both,
-- and only a FAILED row records the engine's status code + reason. No backfill: existing rows keep
-- their current status and NULL failure columns.
--
-- NO PII (ADR-PC-004 §P2 / no-PII-on-the-durable-bus). The added columns are operational only — an
-- integer HTTP status code and a bounded, structural reason string (never the request body, never a
-- NIF/IBAN/name/amount). A refused-command reason is a transition/validation label, not a subject's
-- data.

-- The status lifecycle widens from {PENDING, PUBLISHED} to {PENDING, PUBLISHED, FAILED}. A FAILED
-- row is a TERMINAL delivery outcome (a 4xx engine refusal): the dispatcher never re-POSTs it; the
-- saga's compensation path is what reacts to it. (5xx/timeout stay PENDING and retry — idempotency
-- on the engine's command_dedup makes the retry safe, so a transient failure never reaches FAILED.)
ALTER TABLE saga_outbox
    DROP CONSTRAINT saga_outbox_status_chk,
    ADD CONSTRAINT saga_outbox_status_chk CHECK (status IN ('PENDING', 'PUBLISHED', 'FAILED'));

-- The engine's HTTP status on a terminal refusal (e.g. 422). Operational, NULL until a row FAILs.
ALTER TABLE saga_outbox
    ADD COLUMN failure_status_code INT,
-- A bounded structural reason the dispatcher captured from the refusal (a ProblemDetails title /
-- transition label) — for the audit trail and the compensation decision. NEVER the request body or
-- any PII (ADR-PC-004 §P2). NULL until a row FAILs.
    ADD COLUMN failure_reason      VARCHAR,
-- When the terminal failure was recorded (DB clock, the operational stamp — the wall clock lives in
-- the operational column, never a decision, ADR-PC-010 §P5). NULL until a row FAILs.
    ADD COLUMN failed_at           TIMESTAMPTZ;

COMMENT ON COLUMN saga_outbox.failure_status_code IS
    'The engine HTTP status on a TERMINAL (4xx) refusal of this command (bd babelstone-t7o3.3, '
    'ADR-PC-029 slot 5). Operational, NOT PII. NULL for a PENDING/PUBLISHED row.';

-- A partial index over the FAILED tail so the saga's compensation reader (and operability dashboards)
-- can find refused commands cheaply as the table grows — the failure-side twin of saga_outbox_pending_idx.
CREATE INDEX saga_outbox_failed_idx ON saga_outbox (seq) WHERE status = 'FAILED';

-- Privilege envelope (ADR-PC-001 §P3, lifted): the runtime role already holds UPDATE on saga_outbox
-- (0002), which covers the PENDING → PUBLISHED and PENDING → FAILED flips the dispatcher performs.
-- No new GRANT is needed; the added columns are reachable under the existing UPDATE privilege.
