-- 0003_saga_outbox_traceparent.sql
--
-- H.5 (babelstone-xol8): couple the saga to a connected distributed trace. The saga-advance
-- handler opens an OpenTelemetry span (parented to the inbound event's W3C trace context) and
-- threads that span's context onto every command it emits, so the trace spans services as one
-- chain (ADR-IC-007 Layer 1: "its W3C Trace Context propagation (traceparent header) is the
-- mechanism by which the identity trio … becomes distributed tracing"; ADR-IC-003 §P3 requires
-- the orchestrator's spans to carry process_id + correlation_id). This migration adds the column
-- that persists the OUTBOUND traceparent on each saga_outbox row, so the drain (Epic E's relay)
-- re-emits it as the outbound Kafka header and the downstream consumer threads its spans under
-- this saga's trace.
--
-- Forward-only (ADR-PC-001 §P5, lifted): 0002 stays untouched; this is a higher-numbered
-- additive migration. The column is NULLABLE — a saga advanced with no tracer listening (the
-- common test/library path) writes NULL, and an inbound event that carried no trace context
-- roots a fresh trace downstream. No backfill: existing rows keep NULL.
--
-- NO PII (ADR-PC-004 §P2 / no-PII-on-the-durable-bus). A traceparent is the opaque W3C string
--   00-<trace-id>-<span-id>-<flags>
-- — structural identifiers only, never a NIF/IBAN/name/amount. It correlates to the saga's
-- already-PII-free correlation_id/causation_id (Document 06 — trace identifiers are pseudonymous),
-- so it rides the durable bus exactly as those references do.

ALTER TABLE saga_outbox
    ADD COLUMN traceparent VARCHAR;

COMMENT ON COLUMN saga_outbox.traceparent IS
    'Outbound W3C Trace Context (traceparent) header for the emitted command (H.5). Opaque '
    '00-<trace-id>-<span-id>-<flags>; operational, NOT PII. The drain re-emits it as the '
    'outbound Kafka header so the downstream consumer threads its spans under this saga''s trace '
    '(ADR-IC-007 Layer 1). NULL when no tracer was listening at advance time.';
