-- 0003_deposits_product_config_version.sql
--
-- Denormalize the product-config generation pin onto the deposits read model
-- (bd babelstone-f0ic.15.8). The pin (a content hash `sha256:<hex>`, ADR-PC-009 §A2) is
-- stamped onto DepositConstituted at constitution and folded onto DepositPosition; carrying
-- it here keeps the read-model row a complete stand-in for the live fold, so the single
-- canonical GET /v1/deposits/{id} can serve `product_config_version` from either path
-- without the response shape differing (ADR-IC-005 §P3 single-resource discipline).
--
-- ADDITIVE, prospective-only — the same semantics as product_code (0001): deposits
-- constituted before the pin existed (bd babelstone-fk7m.9 / v794) decode the additive Avro
-- field as the "" default and CANNOT be back-filled (the governing config generation was
-- never recorded for them), so historical rows rest at ''. The DEFAULT '' back-fills
-- existing rows to that same resting value; the projector overwrites it with the folded pin
-- on the next upsert / rebuild (TRUNCATE + re-fold reproduces it deterministically —
-- ADR-PC-010 §P5). A structural version string, NOT PII (ADR-PC-004 §P2).
--
-- ADR-PC-001 §P5 — forward-only; no down-migration.

ALTER TABLE read_model.deposits
    ADD COLUMN product_config_version TEXT NOT NULL DEFAULT '';

COMMENT ON COLUMN read_model.deposits.product_config_version IS
    'Product-config generation pin (sha256:<hex> content hash, ADR-PC-009 §A2) folded from '
    'DepositConstituted.ProductConfigVersion. Prospective-only: '''' for deposits constituted '
    'before the pin (not back-fillable). Structural, never PII.';
