using Babelstone.EventStore.Migrations;
using Babelstone.Packs;
using Babelstone.TestFixtures;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Babelstone.EventStore.Tests;

/// <summary>
/// The durable, Postgres-backed <see cref="PostgresPackVersionRegistry"/> against a real
/// PostgreSQL (ADR-IC-009), exercising ADR-PC-007 §P3 (pin → ref + digests resolution) and the
/// §P4 host eager-load worklist + fail-loud discipline composed over <see cref="OciPackStore"/>.
/// Tagged Integration so the default Docker-free engine CI job skips it; the integration lane runs it.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresPackVersionRegistryIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:18-alpine").Build();

    private string ConnectionString => _pg.GetConnectionString();

    private static readonly PackRef Ref =
        new("registry.example/babelstone-packs/pt-deposit", "sha256:image2026", "sha256:signature2026");

    public async Task InitializeAsync()
    {
        await _pg.GatedStartAsync();
        await new MigrationRunner(ConnectionString).ApplyAsync();
    }

    public async Task DisposeAsync() => await _pg.DisposeAsync();

    // ── §P3: pin → (ref, image digest, signature digest) resolution ─────────────────────────────

    [Fact]
    public async Task Register_then_resolve_round_trips_the_ref_and_both_digests()
    {
        var registry = new PostgresPackVersionRegistry(ConnectionString);
        await registry.RegisterAsync("pt-deposit", "pt.2026.1", Ref, registeredBy: "ci");

        var resolved = await registry.ResolveAsync("pt.2026.1");

        Assert.NotNull(resolved);
        Assert.Equal(Ref.OciRef, resolved.OciRef);
        Assert.Equal(Ref.Digest, resolved.Digest);                   // the OCI image digest (§P3)
        Assert.Equal(Ref.SignatureDigest, resolved.SignatureDigest); // the cosign signature digest (§P3)
    }

    [Fact]
    public async Task Resolve_of_an_unpinned_version_is_null_so_the_loader_can_fail_loud()
    {
        var registry = new PostgresPackVersionRegistry(ConnectionString);
        Assert.Null(await registry.ResolveAsync("pt.9999.1"));
    }

    [Fact]
    public async Task Re_registering_the_same_pin_is_idempotent_but_a_conflicting_digest_is_rejected()
    {
        var registry = new PostgresPackVersionRegistry(ConnectionString);
        await registry.RegisterAsync("pt-deposit", "pt.2026.1", Ref, registeredBy: "ci");

        // Same coordinates again: a no-op, not an error.
        await registry.RegisterAsync("pt-deposit", "pt.2026.1", Ref, registeredBy: "ci-rerun");

        // A different digest under the same pin is forbidden — a pin is immutable (ADR-PC-009).
        var rePin = Ref with { Digest = "sha256:tampered" };
        await Assert.ThrowsAsync<DuplicatePackVersionException>(() =>
            registry.RegisterAsync("pt-deposit", "pt.2026.1", rePin, registeredBy: "attacker"));
    }

    [Fact]
    public async Task A_different_pack_id_claiming_an_existing_version_string_is_a_typed_conflict()
    {
        // bd babelstone-5grf: the UNIQUE (pack_version) constraint (migration 0006) is cross-pack_id.
        // A pack_version string is unique table-wide, so a DIFFERENT pack_id trying to reuse an
        // already-pinned version string collides against pack_versions_version_uq — NOT the
        // (pack_id, pack_version) PK the INSERT's ON CONFLICT clause covers. RegisterAsync must map
        // that constraint (by NAME, not blanket 23505) to DuplicatePackVersionException, never let a
        // raw PostgresException escape and never silently overwrite the existing pin.
        var registry = new PostgresPackVersionRegistry(ConnectionString);
        await registry.RegisterAsync("pt-deposit", "pt.2026.1", Ref, registeredBy: "ci");

        // A second pack family reusing the SAME version string, with its own coordinates.
        var otherRef = new PackRef(
            "registry.example/babelstone-packs/pt-credit", "sha256:imageOther", "sha256:signatureOther");
        var conflict = await Assert.ThrowsAsync<DuplicatePackVersionException>(() =>
            registry.RegisterAsync("pt-credit", "pt.2026.1", otherRef, registeredBy: "ci"));

        Assert.Equal("pt-credit", conflict.PackId);
        Assert.Equal("pt.2026.1", conflict.PackVersion);

        // The original pin is intact — the conflicting register was rejected, not applied.
        var resolved = await registry.ResolveAsync("pt.2026.1");
        Assert.Equal(Ref.Digest, resolved?.Digest);
    }

    // ── §P4: the eager-load worklist is events.pack_version ─────────────────────────────────────

    [Fact]
    public async Task ListLivePackVersions_returns_the_distinct_pack_versions_referenced_by_events()
    {
        await InsertEventAsync("pt.2026.1");
        await InsertEventAsync("pt.2026.1"); // duplicate collapses
        await InsertEventAsync("pt.2026.2");

        var live = await new PostgresPackVersionRegistry(ConnectionString).ListLivePackVersionsAsync();

        Assert.Equal(["pt.2026.1", "pt.2026.2"], live);
    }

    // ── §P3 privilege envelope: the runtime role reads, never writes ────────────────────────────

    [Fact]
    public async Task Runtime_role_can_resolve_but_cannot_curate_pack_versions()
    {
        await new PostgresPackVersionRegistry(ConnectionString)
            .RegisterAsync("pt-deposit", "pt.2026.1", Ref, registeredBy: "ci");

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await new NpgsqlCommand("SET ROLE babelstone_engine;", connection).ExecuteNonQueryAsync();

        // SELECT is granted: the runtime resolve works.
        var refCount = (long)(await new NpgsqlCommand(
            "SELECT count(*) FROM pack_versions WHERE pack_version = 'pt.2026.1';", connection).ExecuteScalarAsync())!;
        Assert.Equal(1L, refCount);

        // INSERT/UPDATE/DELETE are denied at the boundary (42501) — a pin is operator-curated.
        var insert = await Assert.ThrowsAsync<PostgresException>(() => new NpgsqlCommand(
            "INSERT INTO pack_versions (pack_id, pack_version, oci_ref, image_digest, signature_digest, registered_by) " +
            "VALUES ('x', 'pt.9.9', 'r', 'sha256:a', 'sha256:b', 'rogue');", connection).ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, insert.SqlState);

        var update = await Assert.ThrowsAsync<PostgresException>(() => new NpgsqlCommand(
            "UPDATE pack_versions SET image_digest = 'sha256:tamper';", connection).ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, update.SqlState);
    }

    // ── §P4: the durable registry composed under the Oci loader, fail-loud ──────────────────────

    [Fact]
    public async Task Loader_over_the_durable_registry_loads_a_pinned_pack()
    {
        var registry = new PostgresPackVersionRegistry(ConnectionString);
        await registry.RegisterAsync("pt-deposit", "pt.2026.1", Ref, registeredBy: "ci");

        var store = new OciPackStore(registry, new AcceptingVerifier(), new FixedSource(PackFixtures.LoadPt2026()));
        var pack = await store.GetAsync("pt.2026.1");

        Assert.Equal("pt.2026.1", pack.VersionKey);
    }

    [Fact]
    public async Task Loader_over_the_durable_registry_fails_loud_on_an_unpinned_pack()
    {
        // No row pinned: the registry resolves null, the loader turns that into a fatal PackLoadException
        // — the §P4 "pull/verify failure at startup is fatal" path the host lets escape Main.
        var store = new OciPackStore(
            new PostgresPackVersionRegistry(ConnectionString), new AcceptingVerifier(), new FixedSource(PackFixtures.LoadPt2026()));

        var ex = await Assert.ThrowsAsync<PackLoadException>(() => store.GetAsync("pt.2026.1"));
        Assert.Equal("pt.2026.1", ex.PackVersion);
    }

    [Fact]
    public async Task Loader_over_the_durable_registry_fails_loud_on_a_signature_digest_mismatch()
    {
        var registry = new PostgresPackVersionRegistry(ConnectionString);
        await registry.RegisterAsync("pt-deposit", "pt.2026.1", Ref, registeredBy: "ci");

        // A verifier that rejects the resolved digest stands in for a cosign-verify failure (a
        // tampered/re-signed digest that does not match a trusted signature) — load must abort.
        var rejectingVerifier = new RejectingDigestVerifier(rejectDigest: Ref.Digest);
        var source = new FixedSource(PackFixtures.LoadPt2026());
        var store = new OciPackStore(registry, rejectingVerifier, source);

        await Assert.ThrowsAsync<PackLoadException>(() => store.GetAsync("pt.2026.1"));
        Assert.Equal(0, source.PullCount); // a failed verify must never reach the pull (§P2 order)
    }

    private async Task InsertEventAsync(string packVersion)
    {
        const string sql = """
            INSERT INTO events (
                event_id, stream_id, sequence_number, event_type, event_schema_version,
                family, partition_key, pack_version, schema_version, valid_time,
                actor, payload, payload_schema_id)
            VALUES (
                @event_id, @stream_id, @sequence_number, 'term_deposit.DepositConstituted', 1,
                'term_deposit', @stream_id, @pack_version, 'term_deposit@2026.1', now(),
                'test', @payload, 42);
            """;
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var streamId = Guid.NewGuid();
        command.Parameters.AddWithValue("event_id", Guid.NewGuid());
        command.Parameters.AddWithValue("stream_id", streamId);
        command.Parameters.AddWithValue("sequence_number", 0L);
        command.Parameters.AddWithValue("pack_version", packVersion);
        command.Parameters.AddWithValue("payload", new byte[] { 0x01 });
        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>An <see cref="IPackVerifier"/> that accepts every signature (no real cosign).</summary>
internal sealed class AcceptingVerifier : IPackVerifier
{
    public Task VerifyAsync(string ociRef, string digest, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// An <see cref="IPackVerifier"/> that rejects one specific digest — a stand-in for a cosign-verify
/// failure on a digest that does not match a trusted signature (the §P4 fail-loud trigger).
/// </summary>
internal sealed class RejectingDigestVerifier(string rejectDigest) : IPackVerifier
{
    public Task VerifyAsync(string ociRef, string digest, CancellationToken ct = default)
        => string.Equals(digest, rejectDigest, StringComparison.Ordinal)
            ? throw new PackLoadException(null, digest, "cosign verify failed — digest does not match a trusted signature.")
            : Task.CompletedTask;
}

/// <summary>An <see cref="IPackSource"/> returning fixed files and counting pulls.</summary>
internal sealed class FixedSource(IReadOnlyDictionary<string, byte[]> files) : IPackSource
{
    public int PullCount { get; private set; }

    public Task<IReadOnlyDictionary<string, byte[]>> PullByDigestAsync(string ociRef, string digest, CancellationToken ct = default)
    {
        PullCount++;
        return Task.FromResult(files);
    }
}
