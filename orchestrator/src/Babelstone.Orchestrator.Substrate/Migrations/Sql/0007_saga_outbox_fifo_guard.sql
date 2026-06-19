-- 0007_saga_outbox_fifo_guard.sql
--
-- In plain English: today the saga command dispatcher delivers PENDING saga_outbox rows in
-- emission order (seq), but nothing STOPS a second dispatcher pod — or a single pod's
-- skip-on-5xx path — from delivering two commands for the SAME saga instance (process_id) out of
-- order or concurrently. That is harmless TODAY (the constitution saga is single-writer per
-- process_id, so at most one command per saga instance is in flight at a time, and the only
-- concurrent pair is order-independent). It becomes a latent CORRECTNESS bug the moment a future
-- saga (e.g. a renewal/termination flow) emits two ORDER-DEPENDENT commands for one aggregate at
-- once. This migration is the SCHEMA half of the per-aggregate FIFO guard (bd babelstone-t7o3.7):
-- a partial index that makes "the earliest still-PENDING command FOR EACH process_id" an
-- index-only scan, so the dispatcher's new FIFO drain query stays cheap as the table grows. The
-- BEHAVIOURAL half — claiming each row under a per-process_id transaction advisory lock so two
-- dispatchers serialise on the SAME aggregate while DIFFERENT aggregates still run in parallel —
-- lives in SagaCommandDispatchDrainer; it needs no schema (pg_try_advisory_xact_lock is
-- session/transaction state, not a table). Governing: ADR-PC-029 slot 3 ("per-aggregate ordering
-- is the caller's responsibility") — this hardens that responsibility from a single-writer
-- ASSUMPTION into an enforced GUARANTEE, without contradicting the decision.
--
-- Forward-only (ADR-PC-001 §P5, lifted): 0001–0006 stay untouched; this is a higher-numbered,
-- purely additive migration. It adds ONE index and no column, constraint, or grant change — the
-- runtime role's existing SELECT/UPDATE envelope (0002) already covers the FIFO drain + flip.
--
-- NO PII (ADR-PC-004 §P2). An index over (process_id, seq) carries only structural references — a
-- saga-instance UUID and a monotone emission ordinal — never a NIF/IBAN/name/amount.

-- ---------------------------------------------------------------------------
-- saga_outbox_pending_fifo_idx — the per-aggregate FIFO drain index.
--
-- The existing indexes do not serve the FIFO drain shape efficiently:
--   * saga_outbox_pending_idx (seq) WHERE status='PENDING' orders the GLOBAL pending tail but
--     cannot find the MIN(seq) PER process_id without a scan + sort.
--   * saga_outbox_process_idx (process_id, seq) is a FULL index (every status), so a PENDING-tail
--     scan pages over PUBLISHED/FAILED history as the table grows.
--
-- This partial composite — (process_id, seq) restricted to PENDING rows — is the twin of
-- saga_outbox_pending_idx for the per-aggregate drain: DISTINCT ON (process_id) … ORDER BY
-- process_id, seq returns the earliest un-dispatched command for each aggregate as an index-only
-- scan, and it stays bounded to the unpublished tail. The dispatcher delivers exactly those
-- per-process heads each cycle (one in-flight command per aggregate), so a later seq for an
-- aggregate is never attempted before its earlier seq settles — FIFO per aggregate, parallel
-- across aggregates.
-- ---------------------------------------------------------------------------
CREATE INDEX saga_outbox_pending_fifo_idx
    ON saga_outbox (process_id, seq)
    WHERE status = 'PENDING';

COMMENT ON INDEX saga_outbox_pending_fifo_idx IS
    'Per-aggregate FIFO drain index (bd babelstone-t7o3.7, ADR-PC-029 slot 3). Serves '
    'DISTINCT ON (process_id) ... ORDER BY process_id, seq — the earliest still-PENDING command '
    'for each saga instance — as a bounded index-only scan, so ordered-per-aggregate dispatch '
    'stays cheap as the table grows. Structural only, NOT PII (ADR-PC-004 §P2).';

-- Privilege envelope (ADR-PC-001 §P3, lifted): no GRANT change. The runtime role
-- (babelstone_orchestrator) already holds SELECT/UPDATE on saga_outbox (0002), which covers the
-- new FIFO drain query and the PENDING → PUBLISHED/FAILED flip. An index is transparent to the
-- privilege envelope.
