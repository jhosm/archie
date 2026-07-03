-- 0001_lifecycle_dispatch_ledger.sql
--
-- In plain English: the lifecycle driver's "already fired this occurrence" memory used to be an
-- in-memory set — it forgot everything on a restart and no replica could see another's dispatches,
-- so a reboot re-POSTed every still-due maturity/installment (safe only because the engine dedupes)
-- and N replicas each POSTed every due occurrence N times. This table replaces it: one durable row
-- per due occurrence, keyed on the canonical, SERVER-DERIVED, number-pinned dispatch id, whose
-- atomic claim (FOR UPDATE SKIP LOCKED + a per-instance transaction advisory lock, in
-- PostgresLifecycleDispatchLedger) IS the multi-replica single-firing guard — no elected leader.
-- Governing decision: ADR-PC-038 (§Decision 1 substrate, §Decision 2 claim, §Decision 3 ordering);
-- commitments LIFECYCLE_DRIVER_SINGLE_FIRING + LIFECYCLE_DISPATCH_LEDGER_DURABLE.
--
-- Row lifecycle (the behavioural half lives in PostgresLifecycleDispatchLedger):
--   • PENDING     — "seen due, not yet successfully POSTed". Inserted idempotently the first time any
--                   replica's pass sees the occurrence due. Claimable; a claim that fails/crashes
--                   mid-POST rolls back to exactly this state (re-claimable, nothing strands — the
--                   forward calendar re-derives the occurrence every tick anyway).
--   • DISPATCHED  — "the engine acknowledged the POST". Flipped in the SAME commit that releases the
--                   claim, stamping dispatched_at (DB clock). Terminal: the durable record that makes
--                   a re-tick or a host RESTART a no-op, and the queryable audit trail of what the
--                   driver dispatched and when.
-- The engine's command_dedup (ADR-PC-029 slot 4) remains the AUTHORITATIVE idempotency floor: a gap
-- here degrades to a redundant POST the engine dedupes — never a double money leg.
--
-- Forward-only (ADR-PC-001 §P5, lifted): this is the driver host's OWN series, version 0001; it is
-- never edited in place, only superseded by higher-numbered migrations.
--
-- NO PII (ADR-PC-004 §P2): every column is a structural reference — a derived UUID key, a stream id,
-- a command-kind code, an occurrence NUMBER, a due DATE, and DB-clock timestamps. Never a NIF, IBAN,
-- account number, name, or amount.

-- ---------------------------------------------------------------------------
-- The runtime role (ADR-PC-001 §P3, lifted): the driver's poll loop connects as
-- babelstone_lifecycle, which holds ONLY the claim/record envelope below — no DDL,
-- no DELETE/TRUNCATE (the ledger is an append-then-flip audit trail). NOLOGIN
-- group role; deployment GRANTs a concrete login user membership.
-- ---------------------------------------------------------------------------
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'babelstone_lifecycle') THEN
        CREATE ROLE babelstone_lifecycle NOLOGIN;
    END IF;
END
$$;

-- ---------------------------------------------------------------------------
-- lifecycle_dispatch_ledger — one row per due lifecycle-command occurrence.
--
--   dispatch_id    the PRIMARY KEY and claim key: LifecycleCommandKey.Derive(instance_id,
--                  command_kind, occurrence_key) — the SAME number-pinned value the sink presents as
--                  the engine Idempotency-Key (LCD-1), so the ledger's "have I fired this?" and the
--                  engine's "have I applied this?" agree on occurrence identity by construction.
--   instance_id /
--   command_kind /
--   occurrence_key the id's three derivation parts, denormalised so the audit trail is queryable
--                  without re-deriving ("what did the driver dispatch for loan X, and when?").
--   due_at         the occurrence's business due date (diagnostics: dispatch lag = dispatched_at −
--                  due_at).
--   status         PENDING → DISPATCHED, forward-only (guarded by the CHECK below; the claim path
--                  is the only writer).
--   first_seen_at  DB-clock stamp of the first pass that saw the occurrence due.
--   dispatched_at  DB-clock stamp of the successful POST — the LIFECYCLE_DISPATCH_LEDGER_DURABLE
--                  audit column; present exactly on DISPATCHED rows (CHECK-paired with status).
-- ---------------------------------------------------------------------------
CREATE TABLE lifecycle_dispatch_ledger (
    dispatch_id    UUID        NOT NULL PRIMARY KEY,
    instance_id    UUID        NOT NULL,
    command_kind   TEXT        NOT NULL,
    occurrence_key BIGINT      NOT NULL,
    due_at         DATE        NOT NULL,
    status         TEXT        NOT NULL DEFAULT 'PENDING'
        CONSTRAINT lifecycle_dispatch_ledger_status_chk
            CHECK (status IN ('PENDING', 'DISPATCHED')),
    first_seen_at  TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp(),
    dispatched_at  TIMESTAMPTZ,
    -- dispatched_at and status travel together: a DISPATCHED row always carries its audit stamp, a
    -- PENDING row never does — the audit trail cannot silently go half-recorded.
    CONSTRAINT lifecycle_dispatch_ledger_dispatched_at_chk
        CHECK ((status = 'DISPATCHED') = (dispatched_at IS NOT NULL))
);

COMMENT ON TABLE lifecycle_dispatch_ledger IS
    'The lifecycle driver''s durable dispatch ledger (ADR-PC-038): one row per due occurrence, keyed '
    'on the number-pinned server-derived dispatch id. The atomic PENDING-row claim (FOR UPDATE SKIP '
    'LOCKED + per-instance advisory lock) is the multi-replica single-firing guard; the DISPATCHED '
    'flip + dispatched_at stamp are the crash-surviving record and audit trail. Structural references '
    'only, NO PII (ADR-PC-004 §P2). The engine command_dedup stays the correctness floor.';

-- The instance-history audit read ("what has the driver fired for this loan/deposit?") and the
-- per-instance recurring sequence walk, served without a full scan as the ledger grows.
CREATE INDEX lifecycle_dispatch_ledger_instance_idx
    ON lifecycle_dispatch_ledger (instance_id, occurrence_key);

-- Privilege envelope (ADR-PC-001 §P3, lifted): the runtime role claims (SELECT ... FOR UPDATE),
-- ensures (INSERT ... ON CONFLICT DO NOTHING), and flips (UPDATE) — and nothing else. The ledger is
-- an audit trail: DELETE/TRUNCATE stay denied (belt-and-braces REVOKE keeps the intent explicit).
GRANT SELECT, INSERT, UPDATE ON lifecycle_dispatch_ledger TO babelstone_lifecycle;
REVOKE DELETE, TRUNCATE ON lifecycle_dispatch_ledger FROM babelstone_lifecycle;
