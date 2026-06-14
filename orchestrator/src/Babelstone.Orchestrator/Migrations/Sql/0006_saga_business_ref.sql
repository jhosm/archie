-- 0006_saga_business_ref.sql
--
-- The per-saga BUSINESS REFERENCES the constitution saga needs to (a) decide the approval fork and
-- (b) build the FULL typed command payloads (bd babelstone-t7o3.1, the H.2 follow-ups). Until now the
-- saga carried only the structural saga_state columns (process_id, state, version, correlation_id,
-- the edge identity); the concrete facts a command body needs — the amount to reserve, the source
-- account to debit, the product/deposit references, the approval-threshold the fork compares against
-- — lived nowhere durable, so SagaCommandOutboxSink could only write the minimal seam envelope. This
-- migration adds a PII-FREE side table the edge populates at start, the fork reads at
-- VALIDATIONS_COMPLETE, and the sink reads to assemble ReserveAccountBalanceCommand /
-- ActivateDepositCommand / … with the real references.
--
-- Forward-only (ADR-PC-001 §P5, lifted): 0001–0005 stay untouched; this is a higher-numbered
-- additive migration. A SEPARATE table (not new columns on saga_state) keeps the saga aggregate row
-- — the thing the optimistic-concurrency advance UPDATEs every transition — lean: these references
-- are written ONCE at start and never mutated by an advance, so a side table avoids widening the
-- hot aggregate row and bumping its version on an unrelated read.
--
-- NO PII (ADR-PC-004 §P2 / no-PII-on-the-durable-bus). EVERY column below is a structural reference
-- or an integer-cents scalar:
--   * amount_minor_units is INTEGER CENTS (the engine's money discipline) — never a float, never a
--     formatted amount string.
--   * source_account_ref / interest_account_ref are the OPAQUE account TOKENS the engine's PII
--     boundary already issued (ADR-PC-004), NOT raw IBANs.
--   * product_ref / deposit_ref are catalogue/aggregate references.
--   * client_type is a CLOSED code ('EXISTING' / 'NEW'), the approval fork's only client input
--     (Document 05 step 3) — never a name/NIF.
--   * auto_approval_threshold_minor_units is the policy ceiling PINNED at the edge (Document 05 step
--     3 "€25,000"), integer cents, so the fork's comparison is exact and replay-stable (ADR-PC-010
--     §P5 — pinned at the edge, never re-dereferenced from live config at decision time).
-- A subject's NIF/IBAN/name NEVER lands here; the saga carries references and resolves PII internally
-- behind the engine's OpenBao boundary, exactly as the durable bus does.

-- ---------------------------------------------------------------------------
-- saga_business_ref — ONE row per saga instance, written once at start (the edge's local
-- transaction, Document 05 §Step 0) and read by the fork + the command-payload assembly. process_id
-- is the PK and an FK to saga_state, so a business-ref row cannot reference a phantom saga and a
-- duplicate start is idempotent on it.
-- ---------------------------------------------------------------------------
CREATE TABLE saga_business_ref (
    process_id                          UUID         NOT NULL,
    -- The product catalogue reference being constituted (e.g. TD-TRAD-12M). A catalogue code, not PII.
    product_ref                         VARCHAR      NOT NULL,
    -- The deposit principal in INTEGER CENTS (the engine money discipline). NEVER a float / a
    -- formatted amount string. The amount ReserveAccountBalance holds and the fork compares.
    amount_minor_units                  BIGINT       NOT NULL,
    -- The OPAQUE source-account token to reserve/debit against — NOT a raw IBAN (ADR-PC-004 §P2).
    source_account_ref                  VARCHAR      NOT NULL,
    -- The OPAQUE interest-account token — NOT a raw IBAN. Nullable: not every product pays interest
    -- to a distinct account.
    interest_account_ref                VARCHAR,
    -- The deposit aggregate reference (e.g. DEP-…) the activation/limits commands target. Derived at
    -- the edge from the process id, stable and PII-free.
    deposit_ref                         VARCHAR      NOT NULL,
    -- The client's standing as the approval fork reads it — a CLOSED code ('EXISTING' / 'NEW'),
    -- Document 05 step 3. Never a name/NIF. Resolved at the edge.
    client_type                         VARCHAR      NOT NULL,
    -- The auto-approval ceiling PINNED at the edge (integer cents) from the policy in force at
    -- admission. The fork reads it as a scalar — NEVER a live rate-sheet dereference at decision
    -- time (ADR-PC-010 §P5 replay determinism; Document 05 step 3 "€25,000").
    auto_approval_threshold_minor_units BIGINT       NOT NULL,
    created_at                          TIMESTAMPTZ  NOT NULL DEFAULT clock_timestamp(),

    CONSTRAINT saga_business_ref_pkey PRIMARY KEY (process_id),
    CONSTRAINT saga_business_ref_process_fk FOREIGN KEY (process_id)
        REFERENCES saga_state (process_id),
    -- A closed client-type vocabulary, enforced in the schema so a typo cannot poison the fork.
    CONSTRAINT saga_business_ref_client_type_chk CHECK (client_type IN ('EXISTING', 'NEW')),
    -- Money is non-negative integer cents; the threshold likewise. A negative is a structurally
    -- impossible policy (mirrors ApprovalForkHandler.Decide's guard).
    CONSTRAINT saga_business_ref_amount_chk CHECK (amount_minor_units >= 0),
    CONSTRAINT saga_business_ref_threshold_chk CHECK (auto_approval_threshold_minor_units >= 0)
);

-- ---------------------------------------------------------------------------
-- Privilege envelope (ADR-PC-001 §P3, lifted; extends 0001's babelstone_orchestrator role).
--   saga_business_ref — SELECT/INSERT only. The edge INSERTs the row once at start (in the saga tx);
--                       the fork and the command-payload assembly only SELECT it. It is NEVER mutated
--                       by an advance (the references are pinned at start), so no UPDATE; never
--                       deleted at runtime, so no DELETE. The belt-and-braces REVOKEs keep the intent
--                       explicit and survive a future GRANT mistake.
-- ---------------------------------------------------------------------------
GRANT SELECT, INSERT ON saga_business_ref TO babelstone_orchestrator;
REVOKE UPDATE, DELETE, TRUNCATE ON saga_business_ref FROM babelstone_orchestrator;
