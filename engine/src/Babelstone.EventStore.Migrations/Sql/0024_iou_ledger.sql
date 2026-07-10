-- 0024_iou_ledger.sql
-- The spine-owned, intent-keyed UNDELIVERABLE-CREDIT (IOU / escheat) read model (ADR-PC-043 slot 5).
-- In plain English: when a matured payout has nowhere to land — the beneficiary account is closed,
-- dormant-past-revival, or does not exist — the money is NOT disgorged into a void nor swept into an
-- anonymous escheat pot. It is held AT SOURCE and recorded as a NAMED IOU to a specific beneficiary
-- (`operations.CreditUnapplied`). When a live destination later exists, `operations.CreditReapplied`
-- records the resolution. This table is the rebuildable fold of that pair, so an operator can list
-- "which credits are still owed, to whom, and how old" as a query — the same posture as the
-- account_holds active-hold ledger (0020) and the account_freezes frozen-predicate ledger (0022).
--
-- ADR-PC-043 slot 5 — an undeliverable credit is an ATTRIBUTED IOU, keyed by intent_id (the slot-4
--   economic-intent id from SettlementReferences.DeriveIntentId), state-transitioned OUTSTANDING ->
--   RESOLVED by a CreditReapplied whose original_intent_id matches. The resolution key
--   g(intent_id) = SettlementReferences.DeriveResolutionIntentId(intent_id) is the double-pay guard:
--   a late original apply and the resolution collapse to one landing because both structurally key
--   off the SAME intent.
-- ADR-PC-043 slot 3 — the fold is COMMUTATIVE in the two lifecycle events, so a rebuild (TRUNCATE +
--   refold) re-derives the OUTSTANDING set IDENTICALLY regardless of the order the drainer folds
--   streams in. This matters because CreditUnapplied and CreditReapplied are keyed by an economic
--   INTENT id (f(source_id, occurrence)), NOT by an InstanceId, and the resolution may legitimately
--   re-target a DIFFERENT beneficiary account — so the open and the resolve do NOT ride one guaranteed
--   stream (unlike the ADR-PC-033 hold lifecycle, which carries an InstanceId and is single-stream by
--   construction). The drainer folds streams in UNORDERED sequence, so a resolve can be folded BEFORE
--   its open. Commutativity is achieved by a resolution TOMBSTONE: a CreditReapplied whose IOU has not
--   been opened yet inserts a RESOLVED row carrying NO open facts (the nullable columns below), and a
--   later CreditUnapplied for that intent no-ops on the intent_id conflict rather than re-opening it —
--   so either arrival order converges to the same terminal state and the same OUTSTANDING set.
-- ADR-PC-010 §P1 — money is integer cents (BIGINT), never a float; EUR-only, so no currency column.
-- ADR-PC-004 §P2 — NO PII: intent_id / beneficiary_ref are opaque structural references (never an
--   IBAN); reason is a closed-ish machine code (BENEFICIARY_ACCOUNT_CLOSED, …); state is a closed-enum
--   member NAME; the rest are structural ids and command-supplied dates.
-- ADR-PC-023 — every date (unapplied_at / reapplied_at) is a COMMAND-supplied input, never a clock
--   read in a fold; IOU AGE is a projection-derived read against an operator-supplied `as_of` horizon,
--   never a clock-manufactured number.
-- ADR-PC-001 §P5 — forward-only; there is no down-migration.
--
-- A REBUILDABLE derived cache (the same posture as account_holds, 0020): the events are the truth and
-- a rebuild is TRUNCATE + re-fold. An IOU row is state-TRANSITIONED in place (OUTSTANDING -> RESOLVED),
-- so the engine role gets a COLUMN-SCOPED UPDATE naming ONLY the resolution-transition columns — the
-- unapplied facts (intent_id, beneficiary_ref, amount_cents, reason, unapplied_at, unapplied_*) are
-- written once and stay outside the UPDATE grant, so the database enforces "a recorded IOU is immutable".

CREATE TABLE undeliverable_credits (
    -- The undeliverable credit's economic-intent id (ADR-PC-043 slot 4): the CreditUnapplied that
    -- opened the IOU and its resolving CreditReapplied (via original_intent_id) both key off the SAME
    -- intent, so a re-delivered CreditUnapplied folds at most once.
    intent_id            TEXT         NOT NULL,
    -- The opaque beneficiary the credit was meant to land on — a reference the engine resolves
    -- internally, never PII / an IBAN (ADR-PC-004). Answers "to whom is the credit owed".
    -- NULLABLE because the fold is COMMUTATIVE (see the resolve-first tombstone note below): a
    -- RESOLVED-before-OUTSTANDING arrival records a resolution tombstone that carries NO open facts.
    beneficiary_ref      TEXT,
    -- The undeliverable amount, integer cents (ADR-PC-010) — held at source, never disgorged. Nullable
    -- for the resolve-first tombstone (see beneficiary_ref).
    amount_cents         BIGINT,
    -- Why the credit was undeliverable — a stable machine code (BENEFICIARY_ACCOUNT_CLOSED,
    -- BENEFICIARY_ACCOUNT_NOT_FOUND, …), never free-text PII (ADR-PC-004). Nullable (tombstone).
    reason               TEXT,
    -- The economic date the credit was recorded unapplied — a command-supplied input, never a clock
    -- read (ADR-PC-023). The AGE axis: an operator lists open IOUs and their age against an `as_of`.
    -- Nullable (tombstone).
    unapplied_at         DATE,
    -- The lifecycle state: OUTSTANDING (owed, still open) -> RESOLVED (a matching CreditReapplied
    -- reapplied the credit to a now-live destination).
    state                TEXT         NOT NULL DEFAULT 'OUTSTANDING',
    -- The producing CreditUnapplied event's identity — the IOU-opening provenance. Nullable (tombstone).
    unapplied_stream_id  UUID,
    unapplied_sequence   BIGINT,
    -- Set on resolution: the resolution key g(intent_id) the CreditReapplied carried (the double-pay
    -- guard, ADR-PC-043), the account the reapplied credit landed on, the reapplied amount, and the
    -- reapply date. Null while OUTSTANDING.
    resolution_intent_id TEXT,
    reapplied_ref        TEXT,
    reapplied_amount_cents BIGINT,
    reapplied_at         DATE,
    -- The resolving CreditReapplied event's identity. Null while OUTSTANDING.
    resolved_stream_id   UUID,
    resolved_sequence    BIGINT,

    CONSTRAINT undeliverable_credits_pkey PRIMARY KEY (intent_id),
    -- The lifecycle is a closed set of exactly two states; a typo'd state fails LOUD.
    CONSTRAINT undeliverable_credits_state_chk CHECK (state IN ('OUTSTANDING', 'RESOLVED')),
    -- Data-integrity floor for the COMMUTATIVE fold: an OUTSTANDING row is a real, listable IOU, so it
    -- MUST carry its open facts (the operator list + age read depend on them) — only a RESOLVED
    -- resolution TOMBSTONE (resolve-folded-before-open) may carry null open facts, and it is filtered
    -- out of every OUTSTANDING read. So a null unapplied fact on an OUTSTANDING row fails LOUD.
    CONSTRAINT undeliverable_credits_outstanding_facts_chk CHECK (
        state <> 'OUTSTANDING' OR (
            beneficiary_ref IS NOT NULL AND amount_cents IS NOT NULL AND reason IS NOT NULL
            AND unapplied_at IS NOT NULL AND unapplied_stream_id IS NOT NULL
            AND unapplied_sequence IS NOT NULL))
);

-- The operator-query access pattern (ADR-PC-043 slot 5 / ADR-PC-023): the "list open IOUs" read
-- scopes to the OUTSTANDING set ordered by age (oldest first), so a partial index over OUTSTANDING
-- rows keyed by unapplied_at answers it while resolved rows leave the index.
CREATE INDEX undeliverable_credits_outstanding_idx
    ON undeliverable_credits (unapplied_at, intent_id)
    WHERE state = 'OUTSTANDING';

-- Least-privilege grants (the 0002 role): INSERT records an IOU; the COLUMN-SCOPED UPDATE names ONLY
-- the resolution-transition columns (the same discipline as account_holds / account_freezes), so the
-- unapplied facts are database-immutable; TRUNCATE is the rebuild path (refold from the stream). No
-- DELETE — an IOU leaves the outstanding set by state, never by erasure (ADR-PC-001 §P3).
GRANT SELECT, INSERT, TRUNCATE ON undeliverable_credits TO babelstone_engine;
GRANT UPDATE (state, resolution_intent_id, reapplied_ref, reapplied_amount_cents, reapplied_at,
              resolved_stream_id, resolved_sequence)
    ON undeliverable_credits TO babelstone_engine;
REVOKE DELETE ON undeliverable_credits FROM babelstone_engine;
