-- 0008_saga_outbox_sca_claims.sql
--
-- Step-up SCA through the saga money-mover path (bd babelstone-ls44; ADR-IC-010 §P8 A10,
-- ADR-IC-006 §P2 A2). The agent-DIRECT money-movers already enforce step-up SCA at the engine
-- (PR #274 / bd ziu3.5: the engine's ScaPrecondition returns 422 SCA_REQUIRED when the
-- gateway-attested acr/auth_time are absent or stale). ADR-IC-010 §P8 A10 deliberately scoped
-- that PR to the engine-direct path and named the saga-routed money-mover path as "its own lane"
-- — this is that lane. It threads the SAME gateway-attested SCA claims to the SAME engine gate
-- when a money-mover (maturity / interest) runs through the orchestrator saga: the dispatcher
-- re-emits these two columns as the X-SCA-Acr / X-SCA-Auth-Time request headers the engine's
-- route-group gate reads, exactly as it already re-emits the traceparent column (0003) as the
-- outbound traceparent header.
--
-- These mirror the traceparent column (0003): OUTBOUND, OPERATIONAL, gateway-attested values the
-- dispatcher forwards on the command's HTTP delivery. They are NOT the logical command body
-- (which stays byte-stable, ADR-PC-010 §P5) — they are the per-emission attestation columns, just
-- like message_id and traceparent.
--
-- Freshness is the ENGINE's authority, not this row's. The engine re-checks auth_time against its
-- own SCA_MAX_AGE window at DISPATCH time (ScaPrecondition.Check), so a claim that has gone stale
-- by the time the dispatcher delivers it — a delayed drain, a crash-recovery re-dispatch — is
-- 422'd at the engine and the dispatcher flips the row terminal FAILED for the saga's
-- compensation/escalation path. Persisting the attestation here therefore does NOT let a stale
-- proof settle a money-mover; it routes the same fail-closed verdict the engine-direct path gives.
--
-- Forward-only (ADR-PC-001 §P5, lifted): 0002/0003 stay untouched; this is a higher-numbered
-- additive migration. BOTH columns are NULLABLE — a command emitted with no SCA attestation (the
-- common case: every saga command except a money-mover, and any money-mover whose caller carried
-- no fresh SCA) writes NULL, and a NULL attestation is exactly the absent-proof case the engine
-- gate 422s on. No backfill: existing rows keep NULL.
--
-- NO PII (ADR-PC-004 §P2 / no-PII-on-the-durable-bus). sca_acr is the OIDC acr authentication-
-- context-class claim (an opaque URN such as urn:bank:sca:psd2); sca_auth_time is the OIDC
-- auth_time claim as seconds since the Unix epoch — both structural authentication metadata, never
-- a NIF/IBAN/name/amount. They ride the durable store exactly as the traceparent and the
-- correlation/causation references do.

ALTER TABLE saga_outbox
    ADD COLUMN sca_acr VARCHAR,
    ADD COLUMN sca_auth_time BIGINT;

COMMENT ON COLUMN saga_outbox.sca_acr IS
    'Gateway-attested OIDC acr (authentication-context-class) claim for the emitted money-mover '
    'command (bd babelstone-ls44; ADR-IC-010 §P8 A10). Opaque URN (e.g. urn:bank:sca:psd2); '
    'operational, NOT PII. The dispatcher re-emits it as the outbound X-SCA-Acr header the engine''s '
    'ScaPrecondition gate reads. NULL when no SCA was attested (then the engine gate 422s if the '
    'command is a money-mover).';

COMMENT ON COLUMN saga_outbox.sca_auth_time IS
    'Gateway-attested OIDC auth_time claim (seconds since the Unix epoch) for the emitted money-mover '
    'command (bd babelstone-ls44; ADR-IC-010 §P8 A10). When the customer last passed SCA; operational, '
    'NOT PII. The dispatcher re-emits it as the outbound X-SCA-Auth-Time header; the engine re-checks '
    'it against SCA_MAX_AGE at dispatch time, so a stale value is fail-closed 422''d there. NULL when '
    'no SCA was attested.';
