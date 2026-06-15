-- 0007_saga_business_ref_constitute_facts.sql
--
-- In plain English: the saga's "place the deposit" command (ActivateDeposit) now talks to the engine's
-- real constitute endpoint, so the saga must carry the handful of STRUCTURAL product facts that endpoint
-- needs — the term length, the interest style, the renewal policy, the coupon cadence, the pricing role,
-- and the start date. These are pinned once at the edge (alongside the amount/account the saga already
-- pinned in 0006) and read later when the dispatcher builds the engine request. None of them is PII.
--
-- The engine→saga event path (bd babelstone-t7o3.11) + the in-transaction rate-resolve+constitute (bd
-- babelstone-3k10, ADR-PC-008 §S2): the saga POSTs a valid ConstituteDepositRequest to the engine with
-- deposit_id = process_id (so the engine's relayed DepositConstituted carries ce_subject = process_id and
-- the saga correlates the integration fact back to itself). The engine resolves the RATE in-transaction;
-- these structural facts are pinned at the edge for replay-stability (ADR-PC-010 §P5 — the saga's command
-- bytes carry no clock, so start_date is pinned, not "today at the engine"). A per-deposit product-config
-- registry resolving these engine-side is the documented later work in TermDepositConstitutionService
-- (ADR-PC-009).
--
-- Forward-only (ADR-PC-001 §P5, lifted): 0001–0006 stay untouched; this is a higher-numbered additive
-- migration. ADD COLUMN with a NOT NULL + DEFAULT is a metadata-only, lock-light change on PostgreSQL 11+
-- (no table rewrite); babelstone is pre-production (no rows to backfill), so the defaults are a
-- belt-and-braces safety net that also documents the walking-skeleton 12-month AT_MATURITY shape.
--
-- NO PII (ADR-PC-004 §P2 / no-PII-on-the-durable-bus). Every column is a STRUCTURAL product fact — a
-- term-day count, closed variant/policy codes, a coupon cadence, a pricing role, a date — never a
-- subject's NIF/IBAN/name.

ALTER TABLE saga_business_ref
    -- The deposit term in days (e.g. 365). The engine's term_days. Defaults to the 12-month
    -- walking-skeleton value (dpz_pt_12m_juros_venc).
    ADD COLUMN term_days INTEGER NOT NULL DEFAULT 365,
    -- The interest-variant code — a CLOSED vocabulary (AT_MATURITY / PERIODIC / ADVANCE). The engine's
    -- interest_variant. Defaults to the walking-skeleton AT_MATURITY.
    ADD COLUMN interest_variant VARCHAR NOT NULL DEFAULT 'AT_MATURITY',
    -- The auto-renewal policy code — a CLOSED vocabulary (NONE / SAME_TERM_CURRENT_RATE / SAME_TERM_SAME_RATE).
    -- The engine's auto_renewal_policy. Defaults to the walking-skeleton NONE.
    ADD COLUMN auto_renewal_policy VARCHAR NOT NULL DEFAULT 'NONE',
    -- The PERIODIC coupon cadence in months (0 for AT_MATURITY / ADVANCE). The engine's payment_period_months.
    ADD COLUMN payment_period_months INTEGER NOT NULL DEFAULT 0,
    -- The pricing role for the rate-sheet resolve (e.g. 'standard'). The engine's role. Defaults to the
    -- walking-skeleton 'standard'.
    ADD COLUMN role VARCHAR NOT NULL DEFAULT 'standard',
    -- The deposit start date PINNED at the edge at admission. The engine's start_date. Pinned (not "today
    -- at the engine") so the saga's command bytes carry no clock and the constitution replays stably
    -- (ADR-PC-010 §P5). The DEFAULT is a placeholder for the pre-production no-rows case; the edge always
    -- writes the real admission date.
    ADD COLUMN start_date DATE NOT NULL DEFAULT DATE '2026-01-01';

-- A closed interest-variant / renewal-policy vocabulary, enforced in the schema so a typo cannot reach
-- the engine's constitute surface as a malformed body. The CHECKs mirror the engine's DepositConstituted
-- codes (02 §2.1 / §2.4.4).
ALTER TABLE saga_business_ref
    ADD CONSTRAINT saga_business_ref_interest_variant_chk
        CHECK (interest_variant IN ('AT_MATURITY', 'PERIODIC', 'ADVANCE')),
    ADD CONSTRAINT saga_business_ref_renewal_policy_chk
        CHECK (auto_renewal_policy IN ('NONE', 'SAME_TERM_CURRENT_RATE', 'SAME_TERM_SAME_RATE')),
    -- A term is a positive day count; the coupon cadence is non-negative (0 = no intra-term coupons).
    ADD CONSTRAINT saga_business_ref_term_days_chk CHECK (term_days > 0),
    ADD CONSTRAINT saga_business_ref_payment_period_chk CHECK (payment_period_months >= 0);
