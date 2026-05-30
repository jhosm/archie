-- 0004_rate_sheets.sql
--
-- Rate-sheet storage (ADR-PC-008 §P1): versioned, immutable rows in the existing
-- PostgreSQL tier, sheet body as JSONB. The sheet is numerical pricing data indexed
-- by (product, role, principal_band) with an effective_from; it carries NO PII
-- (surface §2.2), so the crypto-shredding machinery of the event payload does not
-- reach it. Resolution at constitution is the indexed point-in-time query in §P3;
-- deployment is the treasury-gated POST /v1/rate-sheets endpoint, idempotent on
-- rate_sheet_version_id (§P2).
--
-- Immutability is the SAME pattern as the events log (ADR-PC-001 §P3 / 0002): the
-- runtime role gets SELECT + INSERT only — no UPDATE/DELETE/TRUNCATE — so a published
-- sheet is a durable, never-edited record and a buggy engine PR that issues
-- `UPDATE rate_sheets` is rejected at the database boundary, not merely in review.
-- Corrections ship forward-only as a new version with a new effective_from
-- (§P5, surface §2.6); there is no RateSheetMigrated path because re-pricing a live
-- deposit is a commercial act, not an operational one (§P3).

CREATE TABLE rate_sheets (
    rate_sheet_version_id  VARCHAR     NOT NULL,       -- natural key, e.g. 'pt-deposits-2026.1'
    product_family         VARCHAR     NOT NULL,       -- e.g. 'term_deposit'
    pack_version           VARCHAR     NOT NULL,       -- the pack the sheet validated against
    effective_from         TIMESTAMPTZ NOT NULL,       -- constitution-resolution anchor (§P3)
    body                   JSONB       NOT NULL,       -- products -> role -> bands; 1:1 with deployed YAML
    approved_by            VARCHAR     NOT NULL,       -- treasury / ALM approver actor (§P4)
    approval_ref           VARCHAR     NOT NULL,       -- treasury sign-off record (§P4)
    published_by           VARCHAR     NOT NULL,
    published_at           TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT rate_sheets_pkey PRIMARY KEY (rate_sheet_version_id),
    -- surface §2.3: no two sheets share an effective_from within a family, so the
    -- point-in-time resolve (§P3) is never ambiguous at runtime.
    CONSTRAINT rate_sheets_family_effective_uq UNIQUE (product_family, effective_from)
);

-- §P3 resolution index: "the sheet active at T for this family" is
-- WHERE product_family = $1 AND effective_from <= $2 ORDER BY effective_from DESC LIMIT 1.
CREATE INDEX rate_sheets_resolve_idx ON rate_sheets (product_family, effective_from DESC);

-- Immutability by privilege (ADR-PC-001 §P3), the 0002 pattern: the runtime role
-- appends and reads — nothing else. The migration role (which owns the table and
-- holds UPDATE/DELETE) is what runs this DDL; the runtime role is denied the
-- mutating verbs explicitly so the intent survives a future GRANT mistake.
GRANT SELECT, INSERT ON rate_sheets TO babelstone_engine;
REVOKE UPDATE, DELETE, TRUNCATE ON rate_sheets FROM babelstone_engine;
