-- 0021_product_config_versions.sql
--
-- Product-config deploy registry (ADR-PC-009 §A2, ADR-PC-008 §S2): versioned,
-- immutable rows in the existing PostgreSQL tier, the config body as JSONB. This is
-- the v2 registry named as later work in ADR-PC-009 §A2 — the audited deploy timeline
-- product-configs previously lacked. Until it landed, the product-config generation a
-- deposit was constituted under was pinned by hashing the YAML bytes (the interim
-- content-hash stand-in, bd babelstone-fk7m.9). This table gives product-configs the
-- SAME versioned deploy shape rate sheets already have (0004_rate_sheets.sql): a
-- registry-issued version id, an effective_from timeline, and a treasury-gated,
-- idempotent POST /v1/product-configs endpoint.
--
-- The config body is structural, auditor-readable configuration (term / interest
-- variant / renewal policy / partial-withdrawal gates) — it carries NO PII (ADR-PC-004),
-- so the crypto-shredding machinery of the event payload does not reach it. content_hash
-- is the SHA-256 of the canonical body: it bridges to the interim content-hash pin
-- (bd babelstone-fk7m.9) so a registry version id still resolves to the exact bytes an
-- auditor sees in git.
--
-- Immutability is the SAME pattern as rate_sheets (0004) and the events log (0002): the
-- runtime role gets SELECT + INSERT only — no UPDATE/DELETE/TRUNCATE — so a published
-- config version is a durable, never-edited record and a buggy engine PR that issues
-- `UPDATE product_config_versions` is rejected at the database boundary, not merely in
-- review. Corrections ship forward-only as a new version with a new effective_from
-- (ADR-PC-008 §P5 forward-only immutability, applied to product-configs).

CREATE TABLE product_config_versions (
    product_config_version_id  VARCHAR     NOT NULL,       -- registry-issued natural key, e.g. 'dpz_pt_12m_juros_venc@2026.1'
    product_id                 VARCHAR     NOT NULL,       -- the product code this config version defines
    pack_version               VARCHAR     NOT NULL,       -- the pack the config was authored against
    effective_from             TIMESTAMPTZ NOT NULL,       -- constitution-resolution anchor (mirrors rate_sheets §P3)
    body                       JSONB       NOT NULL,       -- the structural product-config; 1:1 with the deployed YAML
    content_hash               VARCHAR     NOT NULL,       -- sha256:<hex> of the canonical body (bridges the interim pin, bd fk7m.9)
    approved_by                VARCHAR     NOT NULL,       -- product/config approver actor (ADR-PC-008 §P4)
    approval_ref               VARCHAR     NOT NULL,       -- sign-off record (ADR-PC-008 §P4)
    published_by               VARCHAR     NOT NULL,
    published_at               TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT product_config_versions_pkey PRIMARY KEY (product_config_version_id),
    -- No two config versions share an effective_from within a product, so the
    -- point-in-time resolve is never ambiguous at runtime (mirrors rate_sheets_family_effective_uq).
    CONSTRAINT product_config_versions_product_effective_uq UNIQUE (product_id, effective_from)
);

-- Resolution index: "the config version active at T for this product" is
-- WHERE product_id = $1 AND effective_from <= $2 ORDER BY effective_from DESC LIMIT 1.
CREATE INDEX product_config_versions_resolve_idx ON product_config_versions (product_id, effective_from DESC);

-- Immutability by privilege (ADR-PC-001 §P3), the 0002/0004 pattern: the runtime role
-- appends and reads — nothing else. The migration role (which owns the table and holds
-- UPDATE/DELETE) is what runs this DDL; the runtime role is denied the mutating verbs
-- explicitly so the intent survives a future GRANT mistake.
GRANT SELECT, INSERT ON product_config_versions TO babelstone_engine;
REVOKE UPDATE, DELETE, TRUNCATE ON product_config_versions FROM babelstone_engine;
