-- 0002_append_only_role.sql
--
-- Append-only enforced by role privilege, not by trigger (ADR-PC-001 §P3).
-- This migration runs under the migration role (which owns the tables and holds
-- UPDATE/DELETE); it provisions the *runtime* role the engine connects as, and
-- that role is deliberately denied UPDATE/DELETE/TRUNCATE on the log. A buggy
-- engine PR that issues `UPDATE events` is rejected at the database boundary,
-- not merely by code review.
--
-- `babelstone_engine` is a NOLOGIN group role: deployments create a concrete
-- login user that is GRANTed membership (login provisioning is platform work,
-- ADR-PC-005), and tests `SET ROLE babelstone_engine` to assert the privilege
-- envelope. Idempotent so re-running the migration set is a no-op.

DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'babelstone_engine') THEN
        CREATE ROLE babelstone_engine NOLOGIN;
    END IF;
END
$$;

-- The runtime role appends and reads — nothing else.
GRANT SELECT, INSERT ON events TO babelstone_engine;
GRANT SELECT, INSERT ON outbox TO babelstone_engine;

-- The publisher marks rows PUBLISHED, so the runtime role needs UPDATE on outbox
-- only. events stays strictly append-only: no UPDATE, no DELETE, ever.
GRANT UPDATE (status, published_at) ON outbox TO babelstone_engine;

-- Belt-and-braces: revoke the mutating verbs the GRANTs above never gave, so the
-- intent is explicit in the schema and survives a future GRANT mistake on events.
REVOKE UPDATE, DELETE, TRUNCATE ON events FROM babelstone_engine;
REVOKE DELETE, TRUNCATE ON outbox FROM babelstone_engine;
