-- 0023_account_holds_authorization_index.sql
-- A hot-path perf index on the spine-owned active-hold read model (0020/0021) for the current-account
-- velocity read. In plain English: on every authorize, the daily/monthly velocity check reads a rolling
-- sum of an account's AUTHORIZATION holds in a value_date window (ADR-PC-037 §D5) — the payment hot path.
-- That read (GetWindowedAuthorizationHoldCentsAsync) sums over EVERY hold state (ACTIVE/CAPTURED/EXPIRED),
-- so the ACTIVE-only partial index (account_holds_active_idx, 0020) does NOT serve it and the read falls
-- back to an account_ref scan that grows with hold history. This adds a dedicated PARTIAL index over
-- exactly the authorization holds so the windowed sum is an index range scan, not a growing seq-scan.
--
-- ADR-PC-037 §D5 — the velocity limits read is `Σ amount_cents WHERE account_ref = ? AND
--   kind = 'AUTHORIZATION' AND value_date BETWEEN ? AND ?`. This index's (account_ref, value_date) key
--   with the kind = 'AUTHORIZATION' partial predicate covers that predicate exactly: account_ref is the
--   leading equality, value_date the range, and the partial WHERE drops every LEGAL row from the index.
-- ADR-PC-011 (load proof) — a perf index is added because a real access pattern earns it, not
--   speculatively: this is the windowed velocity sum, a per-authorize read on the payment hot path, and
--   the existing 0020 partial index is ACTIVE-only so it cannot answer an all-states window. The index is
--   sized to the query (leading equality + range + partial predicate), so it earns its write cost.
-- ADR-PC-021 §P2 / ADR-PC-001 §P1 — family-agnostic: account_holds is a spine-owned, account_ref-keyed
--   read model, and an INDEX on an existing spine table adds NO table, schema, column, or FK and names no
--   family vocabulary. So this migration needs NO new append-only role grant (0002) and stays inside the
--   EVENT_STORE_SCHEMA_FAMILY_AGNOSTIC allowlist untouched.
-- ADR-PC-001 §P5 — forward-only; there is no down-migration.
--
-- Style note: this matches the plain CREATE INDEX form of the sibling partial indexes (0020's
-- account_holds_active_idx, 0021's account_holds_legal_active_idx) — no CONCURRENTLY (the migration Job
-- runs the forward-only set in a single transactional apply, where CONCURRENTLY is illegal) and no new
-- grant (an index inherits the table's existing privileges).

CREATE INDEX account_holds_authorization_window_idx
    ON account_holds (account_ref, value_date)
    WHERE kind = 'AUTHORIZATION';
