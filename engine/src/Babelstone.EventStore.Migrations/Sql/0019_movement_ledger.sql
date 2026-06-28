-- 0019_movement_ledger.sql
-- The spine-owned, account_ref-keyed movement ledger (ADR-PC-032 §A1 / §95 read side).
--
-- ADR-PC-032 §A1 — the read half the §Decision named but deferred: ONE spine-owned,
--   account_ref-keyed projection (NOT a per-family copy), folded off every Movement-bearing event
--   the engine appends. It folds only the family-agnostic `Movement` atom via the `IMovementBearing`
--   seam, so it stays family-agnostic (ENGINE_FAMILY_AGNOSTIC, ADR-PC-021 §P2) — this table carries
--   NO family-named column, exactly like `events`/`projections`/`snapshots`.
-- ADR-PC-032 §A5 — the balance fold is order-insensitive (a signed sum), so out-of-order arrival
--   within an account self-heals on rebuild; idempotency is the producing event's identity
--   (stream_id, sequence_number, movement_index), an event MAY bear several movements (§A3).
-- ADR-PC-010 §P1 — money is integer cents (BIGINT), never a float; EUR-only, so no currency column.
-- ADR-PC-004 §P2 — NO PII: account_ref is the opaque engine-resolved reference (never an IBAN);
--   direction/operation/origin are closed-enum member NAMES; the rest are structural ids, amounts,
--   and dates. No key material lives here.
-- ADR-PC-001 §P5 — forward-only; there is no down-migration.
--
-- This table is a REBUILDABLE derived cache (the same posture as `projections`/`snapshots`,
-- 0003/0005): one INSERT-only row per applied movement, rebuilt by TRUNCATE + re-fold. It therefore
-- GRANTs INSERT + TRUNCATE (rebuild) but never UPDATE — a recorded movement line is immutable, only
-- ever inserted (idempotently) or wiped wholesale on a rebuild.

CREATE TABLE movement_ledger (
    row_id          BIGSERIAL    PRIMARY KEY,
    -- The opaque account the value moved against — the ledger key (ADR-PC-004 §P2, never PII).
    account_ref     TEXT         NOT NULL,
    -- The producing event's identity — the idempotency key. An event MAY bear several movements
    -- (ADR-PC-032 §A3), so movement_index disambiguates the legs of one (stream_id, sequence_number).
    stream_id       UUID         NOT NULL,
    sequence_number BIGINT       NOT NULL,
    movement_index  INTEGER      NOT NULL,
    -- The closed-enum member NAMES the engine writes (SettlementDirection / MovementOperation /
    -- MovementOrigin). 'Debit'/'Credit' relative to account_ref: the balance fold signs by direction.
    direction       TEXT         NOT NULL,
    amount_cents    BIGINT       NOT NULL,
    value_date      DATE         NOT NULL,
    operation       TEXT         NOT NULL,
    origin          TEXT         NOT NULL,
    -- The ADR-PC-029 append-idempotency command id the originating command carried (correlation).
    command_id      UUID         NOT NULL,

    -- Idempotency under at-least-once delivery (ADR-PC-032 §A5): a re-delivered event re-inserts the
    -- same lines, and ON CONFLICT DO NOTHING makes that a no-op. One line per movement occurrence.
    CONSTRAINT movement_ledger_event_movement_uq
        UNIQUE (stream_id, sequence_number, movement_index)
);

-- The account statement + balance access pattern: every read scopes by account_ref
-- (GetBalanceCents / GetStatement), so a B-tree on it answers both with an index scan.
CREATE INDEX movement_ledger_account_ref_idx ON movement_ledger (account_ref);

-- The engine role reads and appends this rebuildable ledger, and TRUNCATEs it on a clean rebuild
-- (ADR-PC-032 §A5). It is INSERT-only — a recorded line is immutable — so no UPDATE is granted,
-- UNLIKE the rebuildable `projections` cache (which needs UPDATE for supersession). A re-delivered
-- event re-inserts idempotently (ON CONFLICT DO NOTHING), so the engine never updates a line in place.
GRANT SELECT, INSERT, TRUNCATE ON movement_ledger TO babelstone_engine;
-- INSERT into the BIGSERIAL surrogate draws from its backing sequence; grant USAGE so the engine
-- role can advance it.
GRANT USAGE ON SEQUENCE movement_ledger_row_id_seq TO babelstone_engine;
