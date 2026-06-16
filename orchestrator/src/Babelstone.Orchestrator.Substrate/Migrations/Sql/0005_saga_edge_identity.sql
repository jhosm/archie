-- 0005_saga_edge_identity.sql
--
-- The I.1 edge-over-saga front door (ADR-IC-006 §P4 / Document 05 §Step 0) needs two things
-- the saga aggregate did not carry before: a CLIENT-FACING process reference and the OWNING
-- client. This migration adds both as columns on saga_state. Forward-only (ADR-PC-001 §P5):
-- once applied this migration is never edited; shape changes land as higher-numbered migrations.
--
--   public_process_id — the PROC-… reference the EDGE returns to the client and the SSE
--                       stream_url is keyed on (Document 05 §Step 0: "process_id =
--                       PROC-2026-00098765"). The durable saga key stays the internal UUID
--                       process_id (saga_outbox / saga_transition reference it); this is the
--                       STABLE, opaque public handle the client sees. NOT a secret and NOT a
--                       capability token (Document 05 §Step 0 authorization note): the SSE
--                       endpoint independently enforces ownership (owning_client_id below). A
--                       structural reference, never PII (ADR-PC-004 §P2). UNIQUE so the SSE
--                       lookup resolves exactly one saga; nullable so the columns are additive
--                       over any saga_state rows that predate the edge (a consume-loop-started
--                       saga that the edge never minted a public id for).
--   owning_client_id  — the client that OWNS this process (the request's client_id). The SSE
--                       read enforces the requester's client_id matches this (ADR-IC-006 §P4 /
--                       Document 05 §Step 0): "the token's client_id matches the process's owning
--                       client … a client that guesses another's process_id must not receive
--                       their saga updates". A client_id is an OPAQUE business reference (e.g.
--                       CLI-2026-007842), NOT PII (no NIF/IBAN/name) — the same class of
--                       reference Document 05 carries on the deposits.process.events payload.
--                       Nullable for the same additive reason.
--
-- saga_state already carries the babelstone_orchestrator runtime grants (SELECT/INSERT/UPDATE,
-- 0001) — adding columns inherits them, so the edge can INSERT the start row with these set and
-- the SSE read can SELECT them. No new grant is required.

ALTER TABLE saga_state ADD COLUMN public_process_id VARCHAR;
ALTER TABLE saga_state ADD COLUMN owning_client_id   VARCHAR;

-- The SSE lookup is "resolve the saga for this PROC-… reference" — a unique index makes it an
-- index-only point lookup AND enforces that one public id maps to exactly one saga. Partial
-- (WHERE NOT NULL) so the additive nullable column does not constrain pre-edge rows.
CREATE UNIQUE INDEX saga_state_public_process_id_idx
    ON saga_state (public_process_id)
    WHERE public_process_id IS NOT NULL;
