-- 0001_installment_calendar.sql
-- PERSONAL_LOAN FAMILY-OWNED migration (ADR-PC-021 family-owned ownership): the FIRST migration in the
-- personal_loan family's own read-model set. `read_model.installment_calendar` is a family-NAMED table
-- with loan-typed query columns (`next_due_date`, `next_installment_number`, …); a family-named schema
-- belongs in a family-owned migration set, not the engine's — so the engine event-store migrations carry
-- ZERO personal-loan-named tables. This set runs under its own ledger (`schema_migrations_personal_loan`)
-- on the SAME Postgres tier as the engine event store (ADR-IC-005 §S1), AFTER the engine migrations — see
-- the hard engine-before-family ordering guard below.
--
-- The FORWARD read surface for the installment-calendar projection (the closed-end-asset analogue of the
-- deposit's maturity-reminder surface): the single NEXT still-unpaid installment a loan owes — the
-- denormalized row a reminder/notification path range-scans for "loans with an installment due in
-- [from, to)" without folding every stream. It denormalizes the `personal_loan.installment_calendar`
-- bitemporal projection (PersonalLoanProjectionModule), which itself materialises into the GENERIC,
-- engine-owned `projections` table; this table is the flat, query-optimized family-owned read side
-- (ADR-IC-005 §S1), DISTINCT from that bitemporal belief store.
--
-- ADR-IC-005 §P1 — read-model tables live in a DEDICATED `read_model` schema, separate from the
--   write-side domain tables (`events`, `projections`, …) in `public`. The schema boundary makes a
--   cross-boundary join visible in code review; no projector writes the event log and no command path
--   writes here. (The schema is created idempotently here — the term-deposit set may have created it
--   already, or this set may be the first; either way `CREATE SCHEMA IF NOT EXISTS` is safe.)
-- ADR-IC-005 §P2 — the canonical projection write is an UPSERT with a monotonicity guard. This engine's
--   per-stream `sequence_number` is its offset analog (events drain PER STREAM, no cluster-wide order),
--   so the §P2 `last_event_offset` guard is realised here as `last_sequence`.
-- ADR-IC-005 §P3 — every read-model row carries `last_updated` (TIMESTAMPTZ) and the offset analog
--   (`last_sequence`). `last_updated` is RUNTIME-SUPPLIED from the producing event's transaction_time,
--   never the SQL clock — a CQRS read model fed by an event-sourced log must rebuild byte-identically
--   (ADR-PC-010 §P5), and clock_timestamp()-at-write cannot. So no column DEFAULT here; the projector
--   always stamps it.
-- ADR-PC-018 §6.2 — `sor ∈ {engine, legacy}` is a first-class per-instance routing-truth column. An
--   engine-materialised loan is always `sor = 'engine'`, set at disbursement and never changed.
-- ADR-PC-004 §P2 — structural read fields are cleartext; this read model holds NO PII (no borrower name,
--   NIF, or IBAN) — only structural schedule facts and opaque references.
-- ADR-PC-010 §P1 — all money is integer cents, never a float.
-- ADR-PC-001 §P5 — forward-only; no down-migration.
--
-- Like `projections`/`snapshots` (rebuildable caches), this table GRANTs UPDATE (the §P2 UPSERT needs
-- it) and is rebuildable by TRUNCATE + re-fold (ADR-IC-005 §P5), UNLIKE the append-only `events` table
-- which REVOKEs UPDATE/DELETE.

-- Fail-loud engine-before-family ORDERING guard. The GRANTs below name the `babelstone_engine` runtime
-- role, which is created by ENGINE migration 0002_append_only_role.sql — a hard ordering dependency now
-- that this read model is a SEPARATE, family-owned migration set on the same tier (ADR-IC-005 §S1). If
-- the engine schema is not yet present (the family runner ran before the engine's), the GRANTs would
-- fail with an opaque "role does not exist" deep in the statement; this RAISEs a clear, actionable
-- EXCEPTION up front instead, naming the ordering contract.
DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'babelstone_engine') THEN
        RAISE EXCEPTION
            'babelstone_engine role is absent: run the ENGINE event-store migrations '
            '(0002_append_only_role.sql) BEFORE the personal-loan family read-model migrations. '
            'The family read model lives on the same Postgres tier as the engine event store '
            '(ADR-IC-005 §S1) and GRANTs on the engine runtime role, so engine-before-family is a '
            'hard ordering dependency (ADR-PC-021 family-owned ownership).';
    END IF;
END
$$;

CREATE SCHEMA IF NOT EXISTS read_model;
GRANT USAGE ON SCHEMA read_model TO babelstone_engine;

CREATE TABLE read_model.installment_calendar (
    -- The instance id (= the aggregate/stream id). One denormalized row per loan: the forward
    -- next-installment point-lookup, and the range-scan source for upcoming installments.
    stream_id                UUID         PRIMARY KEY,

    -- ADR-PC-018 §6.2 routing-truth column. 'engine' for every engine-materialised loan; the enum
    -- widens additively (a future owning system) without a schema rewrite.
    sor                      TEXT         NOT NULL DEFAULT 'engine',

    -- The schedule the disbursement FIXED: the first installment's due date (the anchor that the
    -- forward occurrence's due date rolls forward from), the term, and the level installment amount.
    first_installment_date   DATE         NOT NULL,
    term_months              INTEGER      NOT NULL,
    installment_amount_cents BIGINT       NOT NULL,

    -- How many scheduled installments have been paid (the highest installment number folded). The next
    -- unpaid occurrence is `installments_paid + 1`.
    installments_paid        INTEGER      NOT NULL,

    -- The forward next-unpaid occurrence, denormalized for a point read. NULLABLE: a fully-paid loan
    -- has no further occurrence (`installments_paid = term_months`), so both go NULL — the calendar is
    -- exhausted. `next_due_date` is the anchor rolled forward by `installments_paid` months; it is the
    -- range-scan dimension a reminder path filters "due in [from, to)" on (see the index below).
    next_installment_number  INTEGER,
    next_due_date            DATE,

    -- ADR-IC-005 §P3 mandatory pair. last_sequence is the §P2 monotonicity guard (this engine's
    -- per-stream offset analog); last_updated is the producing event's transaction_time (deterministic,
    -- see header), surfaced for staleness display and read-after-write strategies.
    last_sequence            BIGINT       NOT NULL,
    last_updated             TIMESTAMPTZ  NOT NULL
);

-- The range-scan access pattern (the forward analogue of the deposit read model's maturity index): a
-- B-tree on next_due_date answers "loans with an installment due in [from, to)" with an index range scan.
CREATE INDEX installment_calendar_next_due_date_idx ON read_model.installment_calendar (next_due_date);

-- The engine role reads and writes this rebuildable read model (the §P2 UPSERT needs UPDATE). A clean
-- rebuild (ADR-IC-005 §P5) TRUNCATEs and re-folds, so the role also gets DELETE/TRUNCATE here — UNLIKE
-- the append-only `events` log. INSERT into no SERIAL column, so no sequence grant.
GRANT SELECT, INSERT, UPDATE, DELETE, TRUNCATE ON read_model.installment_calendar TO babelstone_engine;
