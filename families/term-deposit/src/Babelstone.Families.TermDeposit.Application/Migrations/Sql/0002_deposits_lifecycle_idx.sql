-- 0002_deposits_lifecycle_idx.sql
-- TERM-DEPOSIT FAMILY-OWNED migration (ADR-PC-021 family-owned ownership), forward-only (ADR-PC-001 §P5).
--
-- Adds a B-tree index on read_model.deposits(lifecycle) so the surface §3.6 pack-migration
-- instance_filter predicate { product_family, currently_active } resolves with an index scan instead of
-- a sequential scan. The resolver (DepositInstanceFilterResolver → IDepositReadModelStore
-- .ListActiveStreamIdsAsync) runs `WHERE lifecycle = 'Active'` to select the live population an operator
-- re-pins to a newer pack (ADR-PC-009 §P3). Correctness-neutral — the query is correct without it; this
-- is the access-path index for a production-shaped population, mirroring deposits_maturity_date_idx (0001)
-- for the maturity range scan.
--
-- read_model.deposits already exists (0001) and the babelstone_engine role already holds SELECT on it,
-- so this migration adds only the index — no new GRANTs. Forward-only; no down-migration.

CREATE INDEX deposits_lifecycle_idx ON read_model.deposits (lifecycle);
