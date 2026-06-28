-- 0002_read_model.sql
-- PERSONAL_LOAN FAMILY-OWNED migration (ADR-PC-021 family-owned ownership), forward-only (ADR-PC-001 §P5).
--
-- Adds the denormalized read BODY column to read_model.installment_calendar so the now-shipping PRODUCER
-- (bd babelstone-6cpq.12 — IInstallmentCalendarReadModelStore + a ReadModelRunner<LoanPosition, …>, the
-- read-side mirror of term-deposit's read_model.deposits feed) can carry the serialized structural loan
-- position the engine's generic ReadModelRunner re-hydrates to CONTINUE its accumulating fold across events
-- (it re-folds from the stored body rather than from seed — ADR-IC-005 §P5 / ADR-PC-010 §P5). Migration
-- 0001 created the table as an empty, queryable-but-unfed range-scan surface (its DEFERRED-PRODUCER note);
-- this `detail` column is the one piece that surface lacked for the §P2 read-model write the spine performs
-- through IReadModelRow (stream_id + the last_sequence guard + this opaque body).
--
-- ADR-PC-021 §P2 — the generic read-model spine knows a row ONLY through IReadModelRow: stream_id + the §P2
--   last_sequence guard + this opaque `detail` body. The family owns the body's shape and its codec; the
--   spine persists it as bytes and never names a loan column, so adding a non-loan family is zero
--   generic-engine diff. Stored as BYTEA — the same byte-oriented payload as read_model.deposits.detail.
-- ADR-PC-004 §P2 — the body is the structural loan position, serialized: NO PII (no borrower name, NIF, or
--   IBAN) — only structural schedule facts, the same stance as every other column on this table.
-- ADR-PC-001 §P5 — forward-only; no down-migration.
--
-- The table is the family's own (0001) and is still EMPTY in every environment — nothing wrote it until this
-- producer ships — so ADD COLUMN … NOT NULL is safe: there are no existing rows to back-fill. A transient
-- DEFAULT satisfies the NOT NULL for any conceivable in-flight row, then is dropped so the producer is the
-- SOLE supplier of the body (the same "the projector always stamps it" discipline 0001 applies to
-- last_updated — a deterministic, event-derived rebuild, never a column default at write). The table-level
-- GRANTs from 0001 already cover this new column (a table-level privilege extends to columns added later),
-- so this migration adds no new GRANT.

ALTER TABLE read_model.installment_calendar
    ADD COLUMN detail BYTEA NOT NULL DEFAULT '\x'::bytea;

ALTER TABLE read_model.installment_calendar
    ALTER COLUMN detail DROP DEFAULT;
