-- 0020_account_holds.sql
-- The spine-owned, account_ref-keyed ACTIVE-HOLD read model (ADR-PC-033 §Decision slots 1–3).
--
-- ADR-PC-033 slot 1 — a transactional account's available balance is
--   `accounting balance − Σ(active holds)`, and NEITHER balance is ever a stored mutable number:
--   the accounting balance is the movement_ledger signed sum (0019), and THIS table is the
--   rebuildable fold of the hold lifecycle the available-balance read subtracts. One row per hold
--   (keyed by hold_id, the ADR-PC-033 slot-4 idempotency/correlation key), state-transitioned by
--   the three pure lifecycle events HoldPlaced -> HoldCaptured | HoldExpired.
-- ADR-PC-033 slot 3 — per-account append order folds a hold's lifecycle deterministically; a
--   rebuild (TRUNCATE + refold) re-derives the active-hold set identically from the stream
--   (ACCOUNT_BALANCE_IS_A_FOLD).
-- ADR-PC-023 — hold EXPIRY is a projection-derived read over this table's active set against a
--   value-date horizon (the active_idx below), never a clock-manufactured engine event.
-- ADR-PC-010 §P1 — money is integer cents (BIGINT), never a float; EUR-only, so no currency column.
-- ADR-PC-004 §P2 — NO PII: hold_id / account_ref are opaque structural references (never an IBAN);
--   state is a closed-enum member NAME; the rest are structural ids, amounts, and dates.
-- ADR-PC-001 §P5 — forward-only; there is no down-migration.
--
-- This table is a REBUILDABLE derived cache (the same posture as `movement_ledger`, 0019): the
-- events are the truth, and a rebuild is TRUNCATE + re-fold. Unlike the INSERT-only movement
-- ledger, a hold row is state-TRANSITIONED in place (ACTIVE -> CAPTURED | EXPIRED), so the engine
-- role gets a COLUMN-SCOPED UPDATE naming only the transition columns — the placement facts
-- (hold_id, account_ref, amount_cents, value_date, placed_*) are written once and stay outside
-- the UPDATE grant, so the database enforces "a recorded placement is immutable".

CREATE TABLE account_holds (
    -- The hold's lifecycle idempotency/correlation key (ADR-PC-033 slot 4): HoldPlaced,
    -- HoldCaptured, and HoldExpired for one authorization all carry the same hold_id, so a
    -- re-delivered lifecycle event folds at most once.
    hold_id            TEXT         NOT NULL,
    -- The opaque account the earmark applies to — the available-balance fold key (never PII).
    account_ref        TEXT         NOT NULL,
    -- The earmarked amount, integer cents (ADR-PC-010). While state = 'ACTIVE' this amount
    -- reduces the account's available balance.
    amount_cents       BIGINT       NOT NULL,
    -- The economic date the hold took effect — the expiry-horizon axis (ADR-PC-023).
    value_date         DATE         NOT NULL,
    -- The lifecycle state: ACTIVE (placed, reducing available balance) -> CAPTURED (settlement
    -- arrived; the posting Movement carries the money) | EXPIRED (timed out; nothing posted).
    state              TEXT         NOT NULL DEFAULT 'ACTIVE',
    -- The producing HoldPlaced event's identity — the placement provenance.
    placed_stream_id   UUID         NOT NULL,
    placed_sequence    BIGINT       NOT NULL,
    -- Set on capture: the captured amount MAY be less than amount_cents (a partial capture
    -- releases the remainder, ADR-PC-033 slot 2). Null while active / on expiry.
    captured_amount_cents BIGINT,
    -- The releasing event's identity (HoldCaptured or HoldExpired). Null while active.
    released_stream_id UUID,
    released_sequence  BIGINT,

    CONSTRAINT account_holds_pkey PRIMARY KEY (hold_id),
    -- The lifecycle is a closed set of exactly three states; a typo'd state fails LOUD.
    CONSTRAINT account_holds_state_chk CHECK (state IN ('ACTIVE', 'CAPTURED', 'EXPIRED'))
);

-- The available-balance + expiry-horizon access pattern (ADR-PC-033 slot 1 / ADR-PC-023): every
-- hot read scopes to the ACTIVE set — Σ(active holds) per account, and the value-date horizon
-- scan the operator expiry read drives — so a partial index over ACTIVE rows answers both while
-- captured/expired rows leave the index.
CREATE INDEX account_holds_active_idx
    ON account_holds (account_ref, value_date)
    WHERE state = 'ACTIVE';

-- Least-privilege grants (the 0002 role): INSERT places a hold; the COLUMN-SCOPED UPDATE names
-- ONLY the lifecycle-transition columns (the same discipline as outbox / bulk_operation_targets),
-- so the placement facts are database-immutable; TRUNCATE is the rebuild path (refold from the
-- stream, ADR-PC-033 slot 3). No DELETE — a hold leaves the active set by state, never by erasure.
GRANT SELECT, INSERT, TRUNCATE ON account_holds TO babelstone_engine;
GRANT UPDATE (state, captured_amount_cents, released_stream_id, released_sequence)
    ON account_holds TO babelstone_engine;
REVOKE DELETE ON account_holds FROM babelstone_engine;
