-- 0022_account_freezes.sql
-- The spine-owned, instance-keyed FROZEN-PREDICATE read model (ADR-PC-041 §Decision slots 2/5).
-- In plain English: a compliance freeze (`operations.AccountFrozen`, lifted by
-- `operations.AccountUnfrozen`) is a TOTAL BLOCK, not an amount — it cannot honestly be subtracted
-- from a balance. So instead of folding into `account_holds`, a freeze folds here, and the
-- stages-3–5 authorization decider consults "is this instance frozen?" before its funds check,
-- refusing debits while an active freeze exists and naming the reason in the decline.
--
-- ADR-PC-041 slot 2 — a freeze is per-INSTANCE (it blocks the whole instance, every account it
--   owns), so the fold key is instance_id, NOT account_ref. One row per freeze (keyed by freeze_id,
--   the slot-4 idempotency/correlation key), state-transitioned ACTIVE -> LIFTED by AccountUnfrozen.
-- ADR-PC-041 slot 5 — the "why" is observable: an active row carries freeze_reason + compliance_actor,
--   which the decline surfaces (HOLD_REASON_OBSERVABLE). Credits/accrual are NOT gated — the freeze
--   blocks the authorization decision, never the recording or folding of facts.
-- ADR-PC-023 — freeze EXPIRY is a projection-derived read over this table's active set against a
--   freeze_expires_at horizon (the active_idx below), never a clock-manufactured engine event.
-- ADR-PC-004 §P2 — NO PII: freeze_id / instance_id are opaque structural references; freeze_reason
--   is a closed-ish machine code (AML_SCREENING, SANCTIONS_MATCH, …); *_actor are operator/service
--   identities, never a data subject.
-- ADR-PC-001 §P5 — forward-only; there is no down-migration.
--
-- A REBUILDABLE derived cache (the same posture as account_holds, 0020): the events are the truth
-- and a rebuild is TRUNCATE + re-fold. A freeze row is state-TRANSITIONED in place (ACTIVE -> LIFTED),
-- so the engine role gets a COLUMN-SCOPED UPDATE naming only the lift-transition columns — the
-- placement facts (freeze_id, instance_id, freeze_reason, compliance_actor, freeze_expires_at,
-- placed_*) are written once and stay outside the UPDATE grant, so the database enforces
-- "a recorded freeze placement is immutable".

CREATE TABLE account_freezes (
    -- The freeze's lifecycle idempotency/correlation key (ADR-PC-041 slot 4): AccountFrozen and its
    -- AccountUnfrozen carry the same freeze_id, so a re-delivered lifecycle event folds at most once.
    freeze_id          TEXT         NOT NULL,
    -- The instance the freeze blocks — the frozen-predicate fold key (never PII). A freeze is
    -- per-instance (it blocks every account the instance owns), so this is instance_id, not account_ref.
    instance_id        UUID         NOT NULL,
    -- Why the freeze was placed — a stable machine code (AML_SCREENING, SANCTIONS_MATCH, …), never
    -- free-text PII (ADR-PC-004). Surfaced into the authorization decline (ADR-PC-041 slot 5).
    freeze_reason      TEXT         NOT NULL,
    -- The compliance operator/service actor that placed the freeze — an operator identity, never PII.
    compliance_actor   TEXT         NOT NULL,
    -- When the freeze lapses, if time-bounded — the expiry-horizon axis (ADR-PC-023). Null = open-ended.
    freeze_expires_at  DATE,
    -- The lifecycle state: ACTIVE (blocking debits) -> LIFTED (a matching AccountUnfrozen arrived).
    state              TEXT         NOT NULL DEFAULT 'ACTIVE',
    -- The producing AccountFrozen event's identity — the placement provenance.
    placed_stream_id   UUID         NOT NULL,
    placed_sequence    BIGINT       NOT NULL,
    -- The lifting AccountUnfrozen's identity + reason/actor. Null while active.
    lifted_stream_id   UUID,
    lifted_sequence    BIGINT,
    unfreeze_actor     TEXT,
    unfreeze_reason    TEXT,

    CONSTRAINT account_freezes_pkey PRIMARY KEY (freeze_id),
    -- The lifecycle is a closed set of exactly two states; a typo'd state fails LOUD.
    CONSTRAINT account_freezes_state_chk CHECK (state IN ('ACTIVE', 'LIFTED'))
);

-- The frozen-predicate + expiry-horizon access pattern (ADR-PC-041 slot 2 / ADR-PC-023): every hot
-- read scopes to the ACTIVE set — "is this instance frozen?" per instance, and the freeze-expiry
-- horizon scan the operator read drives — so a partial index over ACTIVE rows answers both while
-- lifted rows leave the index.
CREATE INDEX account_freezes_active_idx
    ON account_freezes (instance_id, freeze_expires_at)
    WHERE state = 'ACTIVE';

-- Least-privilege grants (the 0002 role): INSERT places a freeze; the COLUMN-SCOPED UPDATE names
-- ONLY the lift-transition columns, so the placement facts are database-immutable; TRUNCATE is the
-- rebuild path (refold from the stream). No DELETE — a freeze leaves the active set by state, never
-- by erasure.
GRANT SELECT, INSERT, TRUNCATE ON account_freezes TO babelstone_engine;
GRANT UPDATE (state, lifted_stream_id, lifted_sequence, unfreeze_actor, unfreeze_reason)
    ON account_freezes TO babelstone_engine;
REVOKE DELETE ON account_freezes FROM babelstone_engine;
