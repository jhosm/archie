-- 0006_pack_versions.sql
--
-- The durable pack-version registry (ADR-PC-007 §P3): the table that resolves a
-- pinned pack version string to the immutable OCI coordinates a live instance loads
-- and verifies against. §P3 specifies the mapping
--   (pack_id, pack_version) -> (OCI image digest, signature digest);
-- this table also carries the OCI reference itself, because pulling/verifying by
-- digest needs the repository reference alongside the digest (the loader's
-- PackRef = (OciRef, Digest, SignatureDigest), Babelstone.Packs). The reference is
-- the repository coordinate; the digest is what makes the pin immutable (the engine
-- pulls by digest, never by tag — §P2).
--
-- This is durable reference data, not an append-only audit log: it is the operator-
-- curated map the deploy host eager-loads at startup (§P4). It is therefore NOT given
-- the strict append-only envelope of events/rate_sheets (0001/0004) — an operator
-- re-pinning a pack version to a re-signed digest is a legitimate UPDATE. The runtime
-- (engine) role only ever READS it: resolution is a pure lookup on the load-time path.
-- Curation (INSERT/UPDATE) is the migration/deploy role's job, the same split the
-- rate-sheet deploy endpoint uses — the runtime role is denied the mutating verbs so
-- a buggy engine PR cannot rewrite a pin at the database boundary.
--
-- ADR-PC-001 §P5 — migrations are forward-only; there is no down-migration.
-- ADR-PC-009 — per-instance pinning: this table is the (pack_id, pack_version) ->
--   digest resolution the per-instance pin (events.pack_version) is honoured through.

CREATE TABLE pack_versions (
    pack_id          VARCHAR     NOT NULL,   -- the pack family/repository, e.g. 'pt-deposit'
    pack_version     VARCHAR     NOT NULL,   -- the pinned version string, e.g. 'pt.2026.1'
    oci_ref          VARCHAR     NOT NULL,   -- the OCI repository reference the digest lives under
    image_digest     VARCHAR     NOT NULL,   -- sha256 image digest — the immutable pin (§P3, pull-by-digest §P2)
    signature_digest VARCHAR     NOT NULL,   -- sha256 cosign signature digest (§P3)
    registered_by    VARCHAR     NOT NULL,   -- operator/CI actor that pinned this version
    registered_at    TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp(),

    -- §P3 key: a pack version pin is unique within a pack family and resolves to
    -- exactly one (ref, digest, signature digest) triple. The loader's ResolveAsync
    -- keys on pack_version alone (the per-instance pin string on the event envelope),
    -- so pack_version is itself unique here — pinning the SAME version string to two
    -- different digests is the ambiguity this constraint forbids.
    CONSTRAINT pack_versions_pkey PRIMARY KEY (pack_id, pack_version),
    CONSTRAINT pack_versions_version_uq UNIQUE (pack_version)
);

-- The runtime role resolves pins (pure SELECT on the load-time path) — nothing else.
-- Curation (INSERT/UPDATE) belongs to the migration/deploy role that owns the table,
-- mirroring how the treasury-gated deploy endpoint owns rate_sheets writes (0004). The
-- mutating verbs are revoked explicitly so the read-only intent survives a future GRANT
-- mistake (the 0002 belt-and-braces pattern).
GRANT SELECT ON pack_versions TO babelstone_engine;
REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON pack_versions FROM babelstone_engine;
