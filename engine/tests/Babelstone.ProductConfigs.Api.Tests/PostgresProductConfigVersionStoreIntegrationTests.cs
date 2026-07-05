using Babelstone.EventStore.Migrations;
using Babelstone.RateSheets;
using Babelstone.TestFixtures;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Babelstone.ProductConfigs.Api.Tests;

/// <summary>
/// The <see cref="PostgresProductConfigVersionStore"/> against a real PostgreSQL (ADR-IC-009):
/// round-trip, the point-in-time resolve, the two uniqueness constraints, and the append-only
/// privilege envelope (migration 0021). Mirrors <c>PostgresRateSheetStoreIntegrationTests</c>. Tagged
/// Integration so the default Docker-free CI job skips it.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresProductConfigVersionStoreIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:18-alpine")
        .Build();

    private string ConnectionString => _pg.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _pg.GatedStartAsync();
        await new MigrationRunner(ConnectionString).ApplyAsync();
    }

    public async Task DisposeAsync() => await _pg.DisposeAsync();

    [Fact]
    public async Task Insert_then_get_round_trips_the_body_and_stamps_published_at()
    {
        var store = new PostgresProductConfigVersionStore(ConnectionString);
        var version = ProductConfigTestData.ValidVersion();

        await store.InsertAsync(version);
        var stored = await store.TryGetAsync(version.ProductConfigVersionId);

        Assert.NotNull(stored);
        Assert.Equal(version.ProductId, stored.ProductId);
        Assert.Equal(version.PackVersion, stored.PackVersion);
        Assert.Equal(version.EffectiveFrom, stored.EffectiveFrom);
        Assert.Equal(version.ContentHash, stored.ContentHash);
        Assert.Equal(
            ProductConfigJson.Canonical(version.Body),
            ProductConfigJson.Canonical(stored.Body));
        Assert.NotNull(stored.PublishedAt); // database default clock_timestamp()
    }

    [Fact]
    public async Task Resolve_returns_the_version_active_at_the_instant()
    {
        var store = new PostgresProductConfigVersionStore(ConnectionString);
        var january = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var june = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        await store.InsertAsync(ProductConfigTestData.ValidVersion(versionId: "v-jan", effectiveFrom: january));
        await store.InsertAsync(ProductConfigTestData.ValidVersion(versionId: "v-jun", effectiveFrom: june));

        var inMarch = await store.ResolveAsync("dpz_pt_12m_juros_venc", new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        var inJuly = await store.ResolveAsync("dpz_pt_12m_juros_venc", new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
        var atExactlyJune = await store.ResolveAsync("dpz_pt_12m_juros_venc", june); // effective_from <= asOf is inclusive
        var before = await store.ResolveAsync("dpz_pt_12m_juros_venc", new DateTimeOffset(2025, 12, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal("v-jan", inMarch?.ProductConfigVersionId);
        Assert.Equal("v-jun", inJuly?.ProductConfigVersionId);
        Assert.Equal("v-jun", atExactlyJune?.ProductConfigVersionId); // the boundary resolves to that version, not the prior one
        Assert.Null(before);
    }

    [Fact]
    public async Task Insert_rejects_a_duplicate_version_id()
    {
        var store = new PostgresProductConfigVersionStore(ConnectionString);
        await store.InsertAsync(ProductConfigTestData.ValidVersion(versionId: "dup"));

        await Assert.ThrowsAsync<DuplicateProductConfigVersionException>(() =>
            store.InsertAsync(ProductConfigTestData.ValidVersion(
                versionId: "dup",
                effectiveFrom: new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero))));
    }

    [Fact]
    public async Task Insert_rejects_two_versions_sharing_a_product_effective_from()
    {
        var store = new PostgresProductConfigVersionStore(ConnectionString);
        var when = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
        await store.InsertAsync(ProductConfigTestData.ValidVersion(versionId: "a", effectiveFrom: when));

        await Assert.ThrowsAsync<DuplicateProductConfigVersionException>(() =>
            store.InsertAsync(ProductConfigTestData.ValidVersion(versionId: "b", effectiveFrom: when)));
    }

    [Fact]
    public async Task Runtime_role_can_append_but_cannot_update_or_delete_product_config_versions()
    {
        await new PostgresProductConfigVersionStore(ConnectionString).InsertAsync(ProductConfigTestData.ValidVersion());

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await new NpgsqlCommand("SET ROLE babelstone_engine;", connection).ExecuteNonQueryAsync();

        var update = await Assert.ThrowsAsync<PostgresException>(() =>
            new NpgsqlCommand("UPDATE product_config_versions SET approval_ref = 'tamper';", connection).ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, update.SqlState);

        var delete = await Assert.ThrowsAsync<PostgresException>(() =>
            new NpgsqlCommand("DELETE FROM product_config_versions;", connection).ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, delete.SqlState);
    }
}
