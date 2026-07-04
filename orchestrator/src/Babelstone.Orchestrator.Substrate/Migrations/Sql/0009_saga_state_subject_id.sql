-- 0009_saga_state_subject_id.sql
--
-- Per-occurrence settlement identity (ADR-PC-032 §A9/§A10, Revised 2026-07-04; bd
-- babelstone-3o6m / open question Q-BH). In plain English: the settlement machinery used to
-- track ONE saga per account/instrument (process_id = ce_subject), and once that saga
-- completed it never restarted — so a product that moves money repeatedly (monthly loan
-- installments) had no settlement process for its second and later movements. Every
-- settlement instance's process_id is now a DETERMINISTIC derivation of
-- (ce_subject, event id, movement index) — one saga per occurrence — and the
-- account/instrument linkage moves to this new, indexed subject_id column.
--
--   subject_id — the account/instrument the saga instance belongs to (the Movement-bearing
--                event's ce_subject = the aggregate/stream id). For an edge-started saga
--                (the constitution process) and every pre-occurrence-identity row it EQUALS
--                process_id; for a per-occurrence settlement instance it is the shared
--                subject its derived process_id fans out from. Structural GUID, never PII
--                (ADR-PC-004 §P2).
--
-- The lifecycle driver's LCD-2 settlement-health probe (ADR-PC-036 §Decision 4, Revised
-- 2026-07-04) re-keys its parked-EXISTS onto THIS column: "is ANY settlement occurrence for
-- this instance parked in HUMAN_INTERVENTION_REQUIRED?" — which is why it is indexed.
--
-- Forward-only (ADR-PC-001 §P5, lifted): the backfill sets subject_id := process_id for
-- every existing row — exactly correct, because before this migration the saga instance id
-- WAS the ce_subject (the pre-per-occurrence scheme). NOT NULL after the backfill so every
-- future start MUST carry its subject linkage (both SagaStateStore start paths do).
ALTER TABLE saga_state ADD COLUMN subject_id UUID;

UPDATE saga_state SET subject_id = process_id WHERE subject_id IS NULL;

ALTER TABLE saga_state ALTER COLUMN subject_id SET NOT NULL;

-- The LCD-2 probe's read path: an indexed EXISTS over (subject_id) + the existing
-- (saga_type, state) btree keeps "any parked occurrence for this instance?" cheap as
-- occurrence count grows (one row per installment, not one per loan).
CREATE INDEX saga_state_subject_idx ON saga_state (subject_id);

-- No privilege change: the babelstone_orchestrator envelope (0001) is table-level, so the
-- new column inherits SELECT/INSERT/UPDATE and the DELETE/TRUNCATE denial unchanged.
