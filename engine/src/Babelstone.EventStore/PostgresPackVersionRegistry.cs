using Babelstone.Packs;
using Npgsql;

namespace Babelstone.EventStore;

/// <summary>
/// The durable, PostgreSQL-backed <see cref="IPackVersionRegistry"/> (ADR-PC-007):
/// resolves a pinned pack version string (<c>pt.YYYY.N</c>) to its immutable OCI
/// coordinates — the reference plus the image and signature digests — out of the
/// <c>pack_versions</c> table (migration 0006). Hand-rolled against the table contract,
/// no ORM (ADR-PC-010), the same shape as <c>PostgresRateSheetStore</c>.
/// </summary>
/// <remarks>
/// This is the production replacement for the configuration-backed
/// <see cref="InMemoryPackVersionRegistry"/>: a live instance resolves its per-instance
/// pin (ADR-PC-009 — the <c>events.pack_version</c> on the event envelope) through this
/// store rather than a host config knob. A missing row resolves to <c>null</c>, which the
/// loader (<c>OciPackStore.GetAsync</c>) turns into a fail-loud
/// <see cref="PackLoadException"/> at startup (§P4); this store does not itself decide
/// fatality — it just reports presence.
/// <para>
/// Resolution is a pure SELECT on the load-time path; the runtime role holds SELECT only
/// (migration 0006). Curation (<see cref="RegisterAsync"/>) is the migration/deploy role's
/// job, so it is exercised by deploy tooling and tests under the owning role, not the
/// runtime connection.
/// </para>
/// </remarks>
public sealed class PostgresPackVersionRegistry(string connectionString) : IPackVersionRegistry
{
    public async Task<PackRef?> ResolveAsync(string packVersion, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(packVersion);

        // §P3: pack_version is the per-instance pin (unique across the table —
        // pack_versions_version_uq), so a lookup by it alone is unambiguous.
        const string sql = """
            SELECT oci_ref, image_digest, signature_digest
            FROM pack_versions
            WHERE pack_version = @pack_version;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("pack_version", packVersion);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null; // unknown/unpinned — the loader makes this fail-loud (§P4).
        }

        return new PackRef(reader.GetString(0), reader.GetString(1), reader.GetString(2));
    }

    // The UNIQUE (pack_version) constraint from migration 0006: a pack_version string is unique
    // across the whole table, independent of pack_id. The INSERT's ON CONFLICT clause targets only
    // the (pack_id, pack_version) PRIMARY KEY, so a collision on THIS constraint — a DIFFERENT
    // pack_id reusing an existing pack_version — is NOT absorbed and raises 23505 against it. We
    // match the constraint by NAME (not a blanket 23505) so an unrelated future unique constraint
    // does not get silently re-interpreted as a duplicate-pin conflict.
    private const string VersionUniqueConstraint = "pack_versions_version_uq";

    /// <summary>
    /// Pins a pack version to its OCI coordinates (ADR-PC-007) — the curation write the
    /// operator/deploy role performs, distinct from the runtime role's read-only resolve.
    /// Idempotent on re-pinning the SAME (ref, digests) triple; a conflicting re-pin of an
    /// existing <c>(pack_id, pack_version)</c> to DIFFERENT coordinates is rejected as a
    /// <see cref="DuplicatePackVersionException"/> rather than silently overwriting a pin a
    /// live instance may already be bound to. A DIFFERENT <c>pack_id</c> trying to claim an
    /// already-pinned <c>pack_version</c> string (the cross-<c>pack_id</c> collision against the
    /// <c>UNIQUE (pack_version)</c> constraint, migration 0006) is likewise a
    /// <see cref="DuplicatePackVersionException"/>: the per-instance pin keys on
    /// <c>pack_version</c> alone (ADR-PC-009), so two packs sharing a version string is the exact
    /// ambiguity the constraint — and this mapping — forbid.
    /// </summary>
    public async Task RegisterAsync(
        string packId, string packVersion, PackRef packRef, string registeredBy, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(packId);
        ArgumentException.ThrowIfNullOrEmpty(packVersion);
        ArgumentException.ThrowIfNullOrEmpty(registeredBy);

        // ON CONFLICT DO NOTHING makes re-registering the identical pin a no-op; a row that
        // exists with different coordinates is detected by the follow-up read and surfaced
        // as a conflict (never an overwrite — a pin is immutable once a live instance uses it).
        const string sql = """
            INSERT INTO pack_versions (
                pack_id, pack_version, oci_ref, image_digest, signature_digest, registered_by)
            VALUES (@pack_id, @pack_version, @oci_ref, @image_digest, @signature_digest, @registered_by)
            ON CONFLICT (pack_id, pack_version) DO NOTHING;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("pack_id", packId);
        command.Parameters.AddWithValue("pack_version", packVersion);
        command.Parameters.AddWithValue("oci_ref", packRef.OciRef);
        command.Parameters.AddWithValue("image_digest", packRef.Digest);
        command.Parameters.AddWithValue("signature_digest", packRef.SignatureDigest);
        command.Parameters.AddWithValue("registered_by", registeredBy);

        int inserted;
        try
        {
            inserted = await command.ExecuteNonQueryAsync(ct);
        }
        catch (PostgresException e)
            when (e.SqlState == PostgresErrorCodes.UniqueViolation
                  && string.Equals(e.ConstraintName, VersionUniqueConstraint, StringComparison.Ordinal))
        {
            // A different pack_id is trying to claim a pack_version string already pinned to another
            // pack_id. The ON CONFLICT clause (keyed on the PK) does not cover this constraint, so it
            // surfaces here — mapped to the same typed conflict, NEVER an overwrite or a raw 23505.
            throw new DuplicatePackVersionException(packId, packVersion, e);
        }

        if (inserted == 1)
        {
            return;
        }

        // The pin already existed: idempotent only if the existing row is byte-identical.
        var existing = await ResolveAsync(packVersion, ct);
        if (existing is null || existing != packRef)
        {
            throw new DuplicatePackVersionException(packId, packVersion);
        }
    }

    /// <summary>
    /// The distinct set of pack versions any live instance references — every value of
    /// <c>events.pack_version</c> (ADR-PC-007). This is the eager-load worklist: the host
    /// resolves + verifies + pulls + caches each of these at startup, fail-loud on any failure.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListLivePackVersionsAsync(CancellationToken ct = default)
    {
        // Every event carries pack_version in its envelope (ADR-PC-001); the DISTINCT set
        // is exactly the packs a live instance might resolve against on the hot path.
        const string sql = "SELECT DISTINCT pack_version FROM events ORDER BY pack_version;";

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);

        var versions = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            versions.Add(reader.GetString(0));
        }

        return versions;
    }
}

/// <summary>
/// Raised when a pack-version pin conflicts with an existing row (ADR-PC-007): either a re-pin
/// of an existing <c>(pack_id, pack_version)</c> to DIFFERENT coordinates, or a DIFFERENT
/// <c>pack_id</c> claiming a <c>pack_version</c> string already pinned to another pack (the
/// cross-<c>pack_id</c> collision against <c>UNIQUE (pack_version)</c>). A pin is immutable once a
/// live instance binds to it (ADR-PC-009): the registry refuses to silently rewrite a digest a
/// constituted instance may already resolve against, and refuses to let two packs share a version
/// string the per-instance pin keys on. Re-registering the SAME triple is idempotent and does not
/// throw.
/// </summary>
public sealed class DuplicatePackVersionException(string packId, string packVersion, Exception? inner = null)
    : Exception(
        $"Pack version '{packVersion}' (pack '{packId}') conflicts with an existing pin — either a re-pin to different OCI coordinates or another pack already owns this version string; a pin is immutable (ADR-PC-007 §P3, ADR-PC-009).",
        inner)
{
    public string PackId { get; } = packId;

    public string PackVersion { get; } = packVersion;
}
