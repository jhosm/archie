-- 0021_account_holds_legal_kind.sql
-- Extend the spine-owned active-hold read model (0020) with a second HOLD KIND: the legal hold
-- (ADR-PC-041 §Decision slots 1–2). In plain English: the same table that already earmarks funds
-- for approved-but-unsettled AUTHORIZATIONS now also folds LEGAL holds (a court order / garnishment
-- placed by `operations.FundsHeld`, lifted by `operations.FundsReleased`), so a legal hold lowers
-- `available balance = accounting balance − Σ(active holds)` for free — the sum spans both kinds.
--
-- ADR-PC-041 slot 1 — a legal hold is a SECOND kind of active hold, not a new ledger. It reuses the
--   0020 fold; only the discriminator (`kind`), its release state ('RELEASED'), the court reference
--   (`legal_reference`), and its advisory expiry horizon (`expires_at`) are new. `FundsHeld` carries
--   only an InstanceId, so the projector keys the row by account_ref = InstanceId (the degenerate
--   single-account 1:1 mapping every family's IAccount seam exposes today; a multi-account family is
--   a later refinement, ADR-PC-041 residual risk).
-- ADR-PC-041 slot 2 — a legal hold is NEVER captured (releasing it moves no money); it leaves the
--   active set as 'RELEASED', distinct from the authorization lifecycle's CAPTURED/EXPIRED. Expiry
--   stays a projection-derived read (ADR-PC-023) over `expires_at`, never a clock-manufactured fold.
-- ADR-PC-004 §P2 — NO PII: `legal_reference` is a case/court reference, `kind` a closed-enum name.
-- ADR-PC-001 §P5 — forward-only; there is no down-migration.
--
-- The new columns are PLACEMENT FACTS (written once on INSERT, immutable): they stay OUTSIDE the
-- 0020 column-scoped UPDATE grant, so the database still enforces "a recorded placement is
-- immutable" — only the lifecycle-transition columns (state, released_*) move a legal hold out of
-- the active set, exactly as for an authorization hold.

-- The hold kind: AUTHORIZATION (the 0020 default — an approved-but-unsettled earmark) or LEGAL (a
-- court order / garnishment). The DEFAULT keeps every existing row an authorization hold with no
-- backfill (ADR-PC-041: the pre-existing rows are all authorization holds).
ALTER TABLE account_holds
    ADD COLUMN kind TEXT NOT NULL DEFAULT 'AUTHORIZATION';

-- The court/case reference a LEGAL hold names (ADR-PC-041 slot 1) — the "why" a HOLD_REASON_OBSERVABLE
-- read surfaces. Null for an authorization hold. STRUCTURAL, never PII (ADR-PC-004).
ALTER TABLE account_holds
    ADD COLUMN legal_reference TEXT;

-- A legal hold's advisory expiry horizon (ADR-PC-041 slot 2 / ADR-PC-023): the projection-derived
-- expiry read flags a legal hold whose horizon has passed so an operator can append FundsReleased.
-- Null = open-ended (never a horizon candidate). Authorization holds use `value_date` as their
-- horizon instead, so this stays null for them.
ALTER TABLE account_holds
    ADD COLUMN expires_at DATE;

-- A legal hold has no economic effective date on `operations.FundsHeld`, so `value_date` is optional
-- for the LEGAL kind. It stays REQUIRED for an authorization hold (its expiry-horizon axis), enforced
-- by the coherence CHECK below rather than a blanket NOT NULL.
ALTER TABLE account_holds
    ALTER COLUMN value_date DROP NOT NULL;

-- The kind is a closed set; a typo fails LOUD.
ALTER TABLE account_holds
    ADD CONSTRAINT account_holds_kind_chk CHECK (kind IN ('AUTHORIZATION', 'LEGAL'));

-- Coherence: an authorization hold MUST carry its value_date (the horizon axis); a legal hold MUST
-- name its legal_reference (the observable "why"). Each kind's shape is enforced by the database.
ALTER TABLE account_holds
    ADD CONSTRAINT account_holds_authorization_shape_chk
        CHECK (kind <> 'AUTHORIZATION' OR value_date IS NOT NULL);
ALTER TABLE account_holds
    ADD CONSTRAINT account_holds_legal_shape_chk
        CHECK (kind <> 'LEGAL' OR legal_reference IS NOT NULL);

-- A legal hold leaves the active set as 'RELEASED' (ADR-PC-041 slot 2) — distinct from the
-- authorization lifecycle's CAPTURED/EXPIRED, because a legal release settles nothing.
ALTER TABLE account_holds
    DROP CONSTRAINT account_holds_state_chk;
ALTER TABLE account_holds
    ADD CONSTRAINT account_holds_state_chk
        CHECK (state IN ('ACTIVE', 'CAPTURED', 'EXPIRED', 'RELEASED'));

-- The legal-hold expiry-horizon access pattern (ADR-PC-041 slot 2 / ADR-PC-023): the operator
-- expiry read scans ACTIVE legal holds whose horizon has passed. A partial index over exactly that
-- set answers it while authorization rows and open-ended legal holds leave the index.
CREATE INDEX account_holds_legal_active_idx
    ON account_holds (expires_at)
    WHERE state = 'ACTIVE' AND kind = 'LEGAL' AND expires_at IS NOT NULL;

-- No new grants: table-level INSERT already covers the new placement columns, and the 0020
-- column-scoped UPDATE (state, captured_amount_cents, released_stream_id, released_sequence) already
-- covers the 'RELEASED' transition — so kind / legal_reference / expires_at are database-immutable
-- once placed, the same discipline the authorization placement facts get.
