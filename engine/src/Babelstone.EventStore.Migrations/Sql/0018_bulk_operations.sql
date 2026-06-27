-- 0018_bulk_operations.sql
--
-- The bulk-operations runner's work-table substrate (ADR-PC-035 §P1–§P5). Some operator
-- actions must be applied to a HUGE population of product instances at once — a regulator
-- forcing a pack change across every deposit, the engine evolving its own schemas, a court
-- freezing funds on every account tied to an order. ADR-PC-035 decides the engine runs such a
-- job as a "register -> drain -> complete" worker over a Postgres WORK-TABLE (the second
-- instance of the ADR-IC-004 outbox pattern), NOT over a work-topic and NOT as a synchronous
-- capped request. This migration lays the two tables that worker walks:
--
--   bulk_operation_jobs    — one row per registered job: the frozen-universe header (the action
--                            id, the operation kind, the matched-set snapshot, the batch size,
--                            the operator actor) and the job's lifecycle status.
--   bulk_operation_targets — one row per instance in the frozen universe: the per-item work
--                            queue carrying status + optional per-item params + an optional
--                            per-item precondition input, drained in bounded SKIP-LOCKED batches.
--
-- FAMILY-AGNOSTIC by construction (ADR-PC-021 §P2, ADR-PC-001 §P1, the
-- EVENT_STORE_SCHEMA_FAMILY_AGNOSTIC fitness function): the runner is a spine component that
-- names NO family. Neither table carries a family-typed column — the operation kind is a free
-- VARCHAR (an open set, never a CHECK enumerating the four operations, so a new operation needs
-- no migration), the per-item params and precondition input are opaque JSONB the adapter fills,
-- and the only reference to an instance is the opaque instance_id. No PII rides either table
-- (ADR-PC-004 §P2) — only structural ids, an actor token, and operational-tier columns. When
-- these two tables are added to the engine set they MUST also be added to the AllowedEngineTables
-- allowlist in EventStoreSchemaFamilyAgnosticTests (a deliberate generic-engine edit) — done in
-- this same change.
--
-- Forward-only: once applied this migration is never edited (ADR-PC-001 §P5); shape changes land
-- as new, higher-numbered migrations.

-- ---------------------------------------------------------------------------
-- bulk_operation_jobs — ADR-PC-035 §P1. Registering a job freezes its target universe: in ONE
-- transaction the runner writes this header AND one bulk_operation_targets row per matched
-- instance. Once registered the target set is IMMUTABLE — the job owns a single frozen universe,
-- not a re-evaluated predicate that could drift between batches (§P7, the ADR-PC-009 §A2/§A3
-- single-auditable-matched-set guarantee). Progress is exposed BY QUERY over the targets table
-- (§P6), not by emitting milestone events onto the bus (those are store-only by construction,
-- ADR-IC-017 §P1).
--
--   job_id            — the job's identity AND the action_id: the per-instance command id is
--                       derived deterministically from (job_id, instance_id) (§P3), so a
--                       retried/restarted per-instance step reuses the engine's receiver-dedupe
--                       (ADR-PC-029 slot 4, ENGINE_COMMAND_IDEMPOTENT) and never double-appends.
--   operation_kind    — which cross-cutting operation this job runs (the adapter key, e.g.
--                       'PackVersionMigrated' / 'SchemaVersionMigrated' / 'FundsHeld' /
--                       'AccountFrozen'). A FREE VARCHAR, deliberately NOT a CHECK enum: the
--                       runner is generic over the operation (each rides as a thin adapter,
--                       §P4), so adding an operation must never need a schema migration. The
--                       VALUE is an operation name, never a family name — the column is generic.
--   matched_set       — the frozen matched-set predicate/snapshot the job targeted, as opaque
--                       JSONB. The audit record of "what exactly did this plan target?" — one
--                       decidable set over an immutable plan (the DORA/PSD2 audit-by-query story
--                       ADR-PC-035 F2 prefers). Carries no PII — a structural predicate only.
--   requested_batch_size — the drainer's bounded claim size (§P2). This is the cap PR #324 made
--                       the population CEILING, re-homed as the BATCH SIZE of one job over one
--                       frozen set (ADR-PC-009 §A3). CHECK > 0.
--   total_count       — the size of the frozen universe, set at registration (the matched_count
--                       preview salvaged from PR #324). The {total} of the
--                       {total, applied, skipped, failed, pending} progress tuple; the live
--                       breakdown is queried over bulk_operation_targets. CHECK >= 0.
--   actor             — the operator who registered the job (mirrors events.actor). A structural
--                       actor token, never PII.
--   status            — the job lifecycle: REGISTERED -> DRAINING -> COMPLETED | FAILED |
--                       CANCELLED (§P1/§P2/§P5). A VARCHAR + CHECK status enum, the same shape as
--                       outbox.status (0001).
--   created_at        — when the job was registered (DB clock).
--   started_at        — when draining began (null until the drainer first claims a batch).
--   completed_at      — when the job reached a terminal status (null until COMPLETED/FAILED/
--                       CANCELLED).
-- ---------------------------------------------------------------------------
CREATE TABLE bulk_operation_jobs (
    job_id               UUID         NOT NULL,
    operation_kind       VARCHAR      NOT NULL,
    matched_set          JSONB        NOT NULL,
    requested_batch_size INTEGER      NOT NULL,
    total_count          BIGINT       NOT NULL,
    actor                VARCHAR      NOT NULL,
    status               VARCHAR      NOT NULL DEFAULT 'REGISTERED',
    created_at           TIMESTAMPTZ  NOT NULL DEFAULT clock_timestamp(),
    started_at           TIMESTAMPTZ,
    completed_at         TIMESTAMPTZ,

    -- job_id is the action id and the stable identity (§P1/§P3).
    CONSTRAINT bulk_operation_jobs_pkey PRIMARY KEY (job_id),
    -- The job lifecycle is a closed set; a typo'd status fails LOUD at the boundary.
    CONSTRAINT bulk_operation_jobs_status_chk
        CHECK (status IN ('REGISTERED', 'DRAINING', 'COMPLETED', 'FAILED', 'CANCELLED')),
    -- The batch size is a positive bound (§P2); the frozen universe is non-negative.
    CONSTRAINT bulk_operation_jobs_batch_size_chk CHECK (requested_batch_size > 0),
    CONSTRAINT bulk_operation_jobs_total_count_chk CHECK (total_count >= 0)
);

-- §P2 — the drainer finds jobs with outstanding work (REGISTERED or DRAINING). A partial index
-- keeps that lookup bounded to the active tail, the same shape as outbox_pending_idx (0001):
-- terminal jobs (COMPLETED/FAILED/CANCELLED) never enter the index.
CREATE INDEX bulk_operation_jobs_active_idx
    ON bulk_operation_jobs (created_at)
    WHERE status IN ('REGISTERED', 'DRAINING');

-- ---------------------------------------------------------------------------
-- bulk_operation_targets — ADR-PC-035 §P2/§P4/§P5. One row per instance in the frozen universe,
-- written at registration with status PENDING. A BulkOperationDrainer BackgroundService — the
-- same shape as the ADR-IC-004 §P2 OutboxDrainer — claims a bounded batch of PENDING rows with
-- FOR UPDATE SKIP LOCKED, runs the per-instance step (optional precondition -> adapter event
-- factory -> native append, §P4), and flips each row's status (§P5) inside a transaction.
-- SKIP LOCKED lets a few drainers run concurrently without contending on the same rows; the
-- work-table IS the to-do list, so a host restart mid-run RESUMES from PENDING with no lost or
-- double-applied work (the idempotent (job_id, instance_id) command id makes a re-claimed row a
-- no-op, §P3). Per-item failure isolation (§P5): one FAILED item never aborts the batch or the
-- job, and a FAILED subset is selectively re-armed back to PENDING for a no-op-safe retry.
--
--   target_id          — the row's stable identity.
--   job_id             — the owning job (FK to bulk_operation_jobs). The frozen set this item
--                        belongs to.
--   instance_id        — the OPAQUE reference to the product instance (its stream id) the
--                        per-instance event is appended to. A structural id, never PII
--                        (ADR-PC-004 §P2) — the runner carries references, the engine resolves.
--   status             — the per-item lifecycle: PENDING -> APPLIED | SKIPPED | FAILED (§P5).
--                        APPLIED = the event was appended; SKIPPED = the precondition declined
--                        this instance; FAILED = an error appending (left for selective retry).
--                        A VARCHAR + CHECK status enum, the same shape as outbox.status (0001).
--   item_params        — OPTIONAL per-item params the adapter's event factory consumes (e.g.
--                        held_amount_cents for a FundsHeld operation). Opaque JSONB, family- and
--                        operation-agnostic: the spine never reads its shape, the adapter does.
--                        Set once at registration (frozen), never mutated.
--   precondition_input — OPTIONAL per-item input to the adapter's precondition verdict (e.g.
--                        from_version for a pack/schema migration). Opaque JSONB, set once at
--                        registration, never mutated. Null when the operation has no precondition.
--   attempts           — how many times the drainer has run the per-instance step for this row
--                        (incremented on each claim). Informational + a stuck-row signal.
--   failure_reason     — set on FAILED: a short operational-tier note for selective retry / audit
--                        (mirrors inbox.result_summary, 0012). MUST stay operational-tier — NEVER
--                        a NIF/IBAN/name/amount or any PII (ADR-PC-004 §P2). Null otherwise.
--   commit_sequence    — set on APPLIED: the per-stream head version the native append reached
--                        (the ADR-IC-005 §P3 read-your-writes token / the ENGINE_COMMAND_IDEMPOTENT
--                        receipt). Null until applied.
--   claimed_at         — when a drainer last claimed this row (DB clock). Observability + a
--                        stale-claim signal; never part of the claim decision (the partial index
--                        + SKIP LOCKED are). Null until first claimed.
--   processed_at       — when the terminal outcome (APPLIED/SKIPPED/FAILED) was recorded. Null
--                        until processed.
--   created_at         — when the row was frozen into the universe at registration (DB clock).
-- ---------------------------------------------------------------------------
CREATE TABLE bulk_operation_targets (
    target_id          UUID         NOT NULL,
    job_id             UUID         NOT NULL,
    instance_id        UUID         NOT NULL,
    status             VARCHAR      NOT NULL DEFAULT 'PENDING',
    item_params        JSONB,
    precondition_input JSONB,
    attempts           INTEGER      NOT NULL DEFAULT 0,
    failure_reason     VARCHAR,
    commit_sequence    BIGINT,
    claimed_at         TIMESTAMPTZ,
    processed_at       TIMESTAMPTZ,
    created_at         TIMESTAMPTZ  NOT NULL DEFAULT clock_timestamp(),

    CONSTRAINT bulk_operation_targets_pkey PRIMARY KEY (target_id),
    -- The per-item lifecycle is a closed set; a typo'd status fails LOUD at the boundary.
    CONSTRAINT bulk_operation_targets_status_chk
        CHECK (status IN ('PENDING', 'APPLIED', 'SKIPPED', 'FAILED')),
    -- The work-table belongs to one job (§P1). Targeting only an allowlisted engine table keeps
    -- the spine decoupled from any family's relational shape (EVENT_STORE_SCHEMA_FAMILY_AGNOSTIC).
    CONSTRAINT bulk_operation_targets_job_fk
        FOREIGN KEY (job_id) REFERENCES bulk_operation_jobs (job_id),
    -- One frozen universe has at most one row per instance — the audit set is decidable and a
    -- duplicate-instance registration fails LOUD rather than double-applying (§P7, §P3).
    CONSTRAINT bulk_operation_targets_job_instance_uq UNIQUE (job_id, instance_id)
);

-- §P2 — the SKIP-LOCKED claim index, the heart of the drainer. A partial index over PENDING rows
-- only (mirroring outbox_pending_idx, 0001) keeps the claim bounded to the unprocessed tail:
-- the drainer runs
--     SELECT ... FROM bulk_operation_targets
--      WHERE job_id = $1 AND status = 'PENDING'
--      ORDER BY created_at, target_id
--      LIMIT $batch
--      FOR UPDATE SKIP LOCKED;
-- and APPLIED/SKIPPED/FAILED rows leave the index, so a few drainers saturate the event store
-- (the real bottleneck, ADR-PC-035 Consequences) without re-scanning processed work. job_id leads
-- so one job's frozen set is claimed as a unit; (created_at, target_id) gives a stable FIFO order.
CREATE INDEX bulk_operation_targets_claim_idx
    ON bulk_operation_targets (job_id, created_at, target_id)
    WHERE status = 'PENDING';

-- §P5/§P6 — the progress + selective-retry index. Backs the {total, applied, skipped, failed,
-- pending} count breakdown (SELECT status, count(*) ... WHERE job_id = $1 GROUP BY status) and
-- the selective-retry scan (find this job's FAILED rows to re-arm to PENDING). Non-partial: it
-- must cover every status, not just the PENDING tail the claim index covers.
CREATE INDEX bulk_operation_targets_job_status_idx
    ON bulk_operation_targets (job_id, status);

-- ---------------------------------------------------------------------------
-- Least-privilege role grants for the engine's runtime role (provisioned in 0002), consistent
-- with the other engine migrations. The runner INSERTs the job header + its targets at
-- registration, SELECTs them on the claim/progress/retry paths, and UPDATEs the mutable lifecycle
-- columns as it drains — so both tables grant SELECT, INSERT, and a COLUMN-SCOPED UPDATE naming
-- ONLY the mutable columns (the same column-scoped discipline as outbox's
-- `GRANT UPDATE (status, published_at)`, 0001/0002).
--
-- The frozen, immutable inputs (job_id, operation_kind, matched_set, requested_batch_size,
-- total_count, actor, created_at on the job; target_id, job_id, instance_id, item_params,
-- precondition_input, created_at on the target) are written ONCE at registration and are
-- deliberately OUTSIDE the UPDATE grant — the database enforces "the frozen set is immutable"
-- (§P1/§P7), not merely code review.
--
-- NO DELETE, NO TRUNCATE on either table: the frozen target set + per-item outcome is an
-- IMMUTABLE audit record answered by query (§P6, the DORA/PSD2 audit-by-query story) — supersede
-- by status, never destroy, exactly like projections (0005/0010). The belt-and-braces REVOKE
-- keeps the intent explicit and survives a future GRANT mistake.
GRANT SELECT, INSERT ON bulk_operation_jobs TO babelstone_engine;
GRANT UPDATE (status, started_at, completed_at) ON bulk_operation_jobs TO babelstone_engine;
REVOKE DELETE, TRUNCATE ON bulk_operation_jobs FROM babelstone_engine;

GRANT SELECT, INSERT ON bulk_operation_targets TO babelstone_engine;
GRANT UPDATE (status, attempts, failure_reason, commit_sequence, claimed_at, processed_at)
    ON bulk_operation_targets TO babelstone_engine;
REVOKE DELETE, TRUNCATE ON bulk_operation_targets FROM babelstone_engine;
