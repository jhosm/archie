-- 0010_saga_outbox_settlement_promotion.sql
--
-- Forward-propagate the engine-CA settlement DESTINATION across the reserve→confirm hop (bd
-- babelstone-u79p.22; ADR-PC-043 §D5 amendment 2026-07-11, ADR-IC-018 §D5). In plain English: a
-- loan installment (or early-repayment) collects money FROM the customer's conta à ordem, and the
-- engine-CA leg has to land on the customer's REAL account for the RIGHT amount — not the
-- ACCT-{processId} placeholder. The CREDIT path already works because its single ConfirmCredit
-- fires on the START advance, where the Movement-bearing event's promoted account_ref / amount are
-- directly in scope (bd u79p.21). The DEBIT path cannot: its irreversible ConfirmDebit fires on a
-- LATER advance, off a dispatcher-SYNTHESIZED BalanceReserved result event that carries none of the
-- promoted values forward — so the confirm fell back to the placeholder and disagreed with the
-- reserve. These two columns close that gap the SAME way the SCA claims (0008) already cross the
-- same hop: the reserve leg's outbox row PERSISTS the promoted destination, and the dispatcher
-- re-emits them onto the synthesized result event's extension headers so the ConfirmDebit advance
-- re-threads the identical SettlementIntent (SETTLEMENT_LEG_ACCOUNT_REF_PROMOTED CA-17 for the
-- debit legs; SETTLEMENT_LEG_ACCOUNT_REF_STABLE CA-18).
--
-- The SCA columns (0008) are re-emitted as OUTBOUND HTTP request headers (X-SCA-Acr /
-- X-SCA-Auth-Time) on the command's delivery; these two are re-emitted onto the IN-PROCESS
-- synthesized result event's CloudEvents extension headers (movementaccountrefs / movementamounts)
-- that the next same-saga advance reads — the settlement-command-body destination the CA writer
-- lands on, NEVER a routing input (routing keys on ce_settlementtarget alone, ADR-IC-018 §D5). They
-- are NOT the logical command body (which stays byte-stable, ADR-PC-010 §P5) — they are the
-- per-emission promotion columns the fan-out already reduced to this leg's single entry, just like
-- message_id, traceparent, and the SCA claims.
--
-- Forward-only (ADR-PC-001 §P5, lifted): 0002–0009 stay untouched; this is a higher-numbered
-- additive migration. BOTH columns are NULLABLE — a command emitted with no promotion (every legacy
-- leg, every non-settlement saga, and the pre-fan-out default) writes NULL, and a NULL promotion is
-- exactly the placeholder-path case the substrate falls back to. No backfill: existing rows keep
-- NULL and keep the legacy ACCT-{processId} placeholder.
--
-- NO PII (ADR-PC-004 §P2 / no-PII-on-the-durable-bus). settlement_account_ref is the engine
-- current-account family's OPAQUE stream id (a GUID string, AccountRef == AccountId.ToString(),
-- ADR-PC-033) — never a NIF/IBAN/name; settlement_amount_cents is money as integer cents (a value
-- reference, never an amount-bearing identity). Both ALREADY ride this durable store inside the
-- byte-stable payload; the dedicated columns add no new exposure — they ride it exactly as the
-- traceparent, the SCA claims, and the correlation/causation references do.

ALTER TABLE saga_outbox
    ADD COLUMN settlement_account_ref VARCHAR,
    ADD COLUMN settlement_amount_cents BIGINT;

COMMENT ON COLUMN saga_outbox.settlement_account_ref IS
    'Engine-CA leg''s PROMOTED destination account_ref for the emitted settlement command (bd '
    'babelstone-u79p.22; ADR-PC-043 §D5). The engine current-account stream id (a GUID string), '
    'opaque; operational, NOT PII. The dispatcher re-emits it onto the SYNTHESIZED result event''s '
    'movementaccountrefs extension header so the next same-saga advance (ConfirmDebit off '
    'BalanceReserved) re-threads it into the CA-apply command body as the debit destination — never '
    'a routing input (routing keys on ce_settlementtarget alone). NULL on the legacy-DDA path and '
    'for every non-settlement saga (then the leg keeps the ACCT-{processId} placeholder).';

COMMENT ON COLUMN saga_outbox.settlement_amount_cents IS
    'Engine-CA leg''s PROMOTED amount in integer cents for the emitted settlement command (bd '
    'babelstone-u79p.22; ADR-PC-043 §D5) — exactly the source Movement.Amount, the in-band '
    'WRONG-AMOUNT guard. Money as integer cents, a value reference, NOT PII. The dispatcher '
    're-emits it onto the synthesized result event''s movementamounts extension header so the '
    'ConfirmDebit advance re-threads it, keeping the reserve and confirm legs in agreement on the '
    'amount. NULL on the legacy-DDA path and for every non-settlement saga.';
