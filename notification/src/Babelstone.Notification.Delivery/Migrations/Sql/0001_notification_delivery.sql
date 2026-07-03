-- 0001_notification_delivery.sql
--
-- In plain English: these two tables are the webhook transport's durable memory. One row per
-- delivery obligation (ADR-IC-011 — the notification service's own PostgreSQL delivery store), so a
-- crash between "a notification is owed" and "the receiver confirmed" forgets nothing; and one row
-- per retry exhaustion, written in the SAME transaction as the DEAD_LETTERED flip (ADR-IC-004
-- outbox discipline) and drained to the Redpanda backbone as the NotificationDeliveryExhausted
-- event, so giving up is always announced. Governing decisions: ADR-IC-011, ADR-IC-004, ADR-PC-025.
--
-- Row lifecycle of notification_delivery (the behavioural half lives in PostgresDeliveryOutbox):
--   • PENDING       — owed and unconfirmed; claimable once next_attempt_at arrives. Enqueue is
--                     idempotent on notification_id (ON CONFLICT DO NOTHING): a re-presented signal
--                     from either at-least-once upstream re-opens NOTHING, ever — terminal rows are
--                     retained precisely so late redelivery is absorbed (ADR-PC-025 slot 4).
--   • DELIVERED     — the receiver confirmed (2xx). Terminal.
--   • DEAD_LETTERED — retries exhausted (MaxAttempts transient failures, ADR-IC-011). Terminal; the
--                     SAME transaction inserts the notification_delivery_exhausted row below.
--   • ABANDONED     — a non-429 4xx: the endpoint is misconfigured, retrying cannot fix it
--                     (ADR-IC-011). Terminal immediately; human review required.
--
-- Forward-only (the ADR-PC-001 discipline, lifted): this is the notification estate's OWN series,
-- version 0001; it is never edited in place, only superseded by higher-numbered migrations.
--
-- NO PII (ADR-PC-004 / ADR-PC-025): every column is structural — ids, template refs, pack versions,
-- integer-cent amount STRINGS inside the data map, dates, transport-status text. Rendered content
-- and render-time-resolved PII NEVER land here: they materialise per attempt and are discarded with
-- the request. customer_ref is an opaque subject REFERENCE, never a name/NIF/contact.

-- ---------------------------------------------------------------------------
-- The runtime role (the ADR-PC-001 role envelope, lifted): the delivery worker
-- connects as babelstone_notification, which holds ONLY the enqueue/claim/flip
-- envelope below — no DDL, no DELETE/TRUNCATE (terminal rows are the dedupe
-- memory and the audit trail; deleting one would re-open an absorbed
-- redelivery). NOLOGIN group role; deployment GRANTs a concrete login user
-- membership.
-- ---------------------------------------------------------------------------
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'babelstone_notification') THEN
        CREATE ROLE babelstone_notification NOLOGIN;
    END IF;
END
$$;

-- ---------------------------------------------------------------------------
-- notification_delivery — one row per delivery obligation (ADR-IC-011).
--
--   notification_id       PRIMARY KEY: the stable composite idempotency key (ADR-PC-025 slot 4) —
--                         the enqueue-dedupe key here AND the idempotency_key every webhook attempt
--                         carries, so this store and the receiver agree on delivery identity.
--   instance_id           the instance (stream) the notification is about.
--   customer_ref          the opaque recipient REFERENCE, when the signal carried one (the
--                         EVENT_DRIVEN bus signal does; the v1 SCHEDULED leg's does not).
--   template_ref /
--   template_pack_version the instance-pinned pack template to render (ADR-PC-007/ADR-PC-009).
--   trigger_kind          EVENT_DRIVEN | SCHEDULED | PRE_CONTRACTUAL (ADR-PC-025) — stored as
--                         the governed SCREAMING_SNAKE_CASE contract symbol.
--   causation_id          the causing domain event for EVENT_DRIVEN; NULL for SCHEDULED.
--   data                  STRUCTURAL interpolation values only (jsonb string→string map): amounts
--                         as integer-cent strings, dates, rates — never PII (ADR-PC-025).
--   due_at                the date the notification is due (= valid_time, ADR-PC-025).
--   status / attempts /
--   next_attempt_at /
--   last_error            the retry ledger (ADR-IC-011): where the obligation stands, how many
--                         attempts failed, when the next is due, the last transport diagnostic.
--   enqueued_at           the enqueue instant the caller supplied (the envelope occurred_at).
-- ---------------------------------------------------------------------------
CREATE TABLE notification_delivery (
    notification_id       UUID        NOT NULL PRIMARY KEY,
    instance_id           UUID        NOT NULL,
    customer_ref          UUID,
    template_ref          TEXT        NOT NULL,
    template_pack_version TEXT        NOT NULL,
    trigger_kind          TEXT        NOT NULL
        CONSTRAINT notification_delivery_trigger_kind_chk
            CHECK (trigger_kind IN ('EVENT_DRIVEN', 'SCHEDULED', 'PRE_CONTRACTUAL')),
    causation_id          UUID,
    data                  JSONB       NOT NULL DEFAULT '{}'::jsonb,
    due_at                DATE        NOT NULL,
    status                TEXT        NOT NULL DEFAULT 'PENDING'
        CONSTRAINT notification_delivery_status_chk
            CHECK (status IN ('PENDING', 'DELIVERED', 'DEAD_LETTERED', 'ABANDONED')),
    attempts              INT         NOT NULL DEFAULT 0,
    next_attempt_at       TIMESTAMPTZ NOT NULL,
    last_error            TEXT,
    enqueued_at           TIMESTAMPTZ NOT NULL
);

COMMENT ON TABLE notification_delivery IS
    'The notification service''s durable webhook delivery store (ADR-IC-011 / ADR-IC-004): one row '
    'per delivery obligation, keyed on the composite notification_id (ADR-PC-025 slot 4). PENDING '
    'rows are claimed by the drain pass once next_attempt_at arrives; terminal rows (DELIVERED / '
    'DEAD_LETTERED / ABANDONED) are retained as the idempotent-enqueue dedupe memory. Structural '
    'signal only, NO PII (ADR-PC-025): rendered content and resolved PII materialise per attempt '
    'and are never persisted.';

-- The drain query: the due PENDING slice, soonest-due first. A partial index keeps the hot claim
-- cheap as terminal rows accumulate (they are retained forever as dedupe memory).
CREATE INDEX notification_delivery_due_idx
    ON notification_delivery (next_attempt_at, notification_id)
    WHERE status = 'PENDING';

-- ---------------------------------------------------------------------------
-- notification_delivery_exhausted — the exhaustion outbox (ADR-IC-011): one
-- row per dead-lettered delivery, inserted in the SAME transaction as the
-- DEAD_LETTERED flip, drained to the backbone as the
-- NotificationDeliveryExhausted event (contracts/avro/operations/). PENDING →
-- PUBLISHED, forward-only; a produce failure leaves the row PENDING (never
-- FAILED — the ADR-IC-004 backpressure posture).
-- ---------------------------------------------------------------------------
CREATE TABLE notification_delivery_exhausted (
    notification_id       UUID        NOT NULL PRIMARY KEY
        CONSTRAINT notification_delivery_exhausted_delivery_fk
            REFERENCES notification_delivery (notification_id),
    -- The row's own event identity: the CloudEvents ce_id on the published record. DB-generated once,
    -- so relay retries republish the SAME id (consumer-side dedupe can key on it OR notification_id).
    event_id              UUID        NOT NULL DEFAULT gen_random_uuid(),
    instance_id           UUID        NOT NULL,
    customer_ref          UUID,
    template_ref          TEXT        NOT NULL,
    template_pack_version TEXT        NOT NULL,
    trigger_kind          TEXT        NOT NULL
        CONSTRAINT notification_delivery_exhausted_trigger_kind_chk
            CHECK (trigger_kind IN ('EVENT_DRIVEN', 'SCHEDULED', 'PRE_CONTRACTUAL')),
    -- The causing domain event the undelivered signal traced to (NULL for SCHEDULED) — copied
    -- through so the published event lets an operator walk exhaustion → causing fact (ADR-PC-023).
    causation_id          UUID,
    attempts              INT         NOT NULL,
    last_error            TEXT,
    exhausted_at          TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp(),
    status                TEXT        NOT NULL DEFAULT 'PENDING'
        CONSTRAINT notification_delivery_exhausted_status_chk
            CHECK (status IN ('PENDING', 'PUBLISHED')),
    published_at          TIMESTAMPTZ,
    -- published_at and status travel together: a PUBLISHED row always carries its ack stamp, a
    -- PENDING row never does — the backbone announcement cannot silently go half-recorded.
    CONSTRAINT notification_delivery_exhausted_published_at_chk
        CHECK ((status = 'PUBLISHED') = (published_at IS NOT NULL))
);

COMMENT ON TABLE notification_delivery_exhausted IS
    'The exhaustion outbox (ADR-IC-011 / ADR-IC-004): one row per dead-lettered delivery, written '
    'atomically with the DEAD_LETTERED flip so a crash between "give up" and "announce it" never '
    'loses the announcement. The backbone relay drains PENDING rows to Redpanda as '
    'operations.NotificationDeliveryExhausted and flips them PUBLISHED. Structural references and '
    'transport-status text only, NO PII (ADR-PC-004).';

-- The relay drain (and the pending-lag gauge): the PENDING slice in exhaustion order.
CREATE INDEX notification_delivery_exhausted_pending_idx
    ON notification_delivery_exhausted (exhausted_at)
    WHERE status = 'PENDING';

-- Privilege envelope (the ADR-PC-001 role discipline, lifted): the runtime role enqueues
-- (INSERT ... ON CONFLICT DO NOTHING), claims (SELECT), and flips (UPDATE) — and nothing else.
-- Terminal rows are dedupe memory and audit trail: DELETE/TRUNCATE stay denied (belt-and-braces
-- REVOKE keeps the intent explicit).
GRANT SELECT, INSERT, UPDATE ON notification_delivery TO babelstone_notification;
REVOKE DELETE, TRUNCATE ON notification_delivery FROM babelstone_notification;
GRANT SELECT, INSERT, UPDATE ON notification_delivery_exhausted TO babelstone_notification;
REVOKE DELETE, TRUNCATE ON notification_delivery_exhausted FROM babelstone_notification;
