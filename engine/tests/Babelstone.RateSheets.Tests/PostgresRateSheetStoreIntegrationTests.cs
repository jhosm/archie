using Babelstone.EventStore.Migrations;
using Babelstone.RateSheets;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Babelstone.RateSheets.Tests;

/// <summary>
/// The <see cref="PostgresRateSheetStore"/> against a real PostgreSQL (ADR-IC-009):
/// round-trip, the §P3 point-in-time resolve, the two uniqueness constraints, and the
/// §P3 append-only privilege envelope (migration 0004). Tagged Integration so the
/// default Docker-free CI job skips it.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresRateSheetStoreIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder()
        .WithImage("postgres:18-alpine")
        .Build();

    private string ConnectionString => _pg.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        await new MigrationRunner(ConnectionString).ApplyAsync();
    }

    public async Task DisposeAsync() => await _pg.DisposeAsync();

    [Fact]
    public async Task Insert_then_get_round_trips_the_body_and_stamps_published_at()
    {
        var store = new PostgresRateSheetStore(ConnectionString);
        var sheet = RateSheetTestData.ValidSheet();

        await store.InsertAsync(sheet);
        var stored = await store.TryGetAsync(sheet.RateSheetVersionId);

        Assert.NotNull(stored);
        Assert.Equal(sheet.ProductFamily, stored.ProductFamily);
        Assert.Equal(sheet.EffectiveFrom, stored.EffectiveFrom);
        Assert.Equal(
            RateSheetJson.Canonical(sheet.Body),
            RateSheetJson.Canonical(stored.Body));
        Assert.NotNull(stored.PublishedAt); // database default clock_timestamp()
    }

    [Fact]
    public async Task Resolve_returns_the_sheet_active_at_the_instant()
    {
        var store = new PostgresRateSheetStore(ConnectionString);
        var january = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var june = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        await store.InsertAsync(RateSheetTestData.ValidSheet(versionId: "v-jan", effectiveFrom: january));
        await store.InsertAsync(RateSheetTestData.ValidSheet(versionId: "v-jun", effectiveFrom: june));

        var inMarch = await store.ResolveAsync("term_deposit", new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        var inJuly = await store.ResolveAsync("term_deposit", new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
        var before = await store.ResolveAsync("term_deposit", new DateTimeOffset(2025, 12, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal("v-jan", inMarch?.RateSheetVersionId);
        Assert.Equal("v-jun", inJuly?.RateSheetVersionId);
        Assert.Null(before);
    }

    [Fact]
    public async Task Insert_rejects_a_duplicate_version_id()
    {
        var store = new PostgresRateSheetStore(ConnectionString);
        await store.InsertAsync(RateSheetTestData.ValidSheet(versionId: "dup"));

        await Assert.ThrowsAsync<DuplicateRateSheetVersionException>(() =>
            store.InsertAsync(RateSheetTestData.ValidSheet(
                versionId: "dup",
                effectiveFrom: new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero))));
    }

    [Fact]
    public async Task Insert_rejects_two_sheets_sharing_a_family_effective_from()
    {
        var store = new PostgresRateSheetStore(ConnectionString);
        var when = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
        await store.InsertAsync(RateSheetTestData.ValidSheet(versionId: "a", effectiveFrom: when));

        await Assert.ThrowsAsync<DuplicateRateSheetVersionException>(() =>
            store.InsertAsync(RateSheetTestData.ValidSheet(versionId: "b", effectiveFrom: when)));
    }

    [Fact]
    public async Task Runtime_role_can_append_but_cannot_update_or_delete_rate_sheets()
    {
        await new PostgresRateSheetStore(ConnectionString).InsertAsync(RateSheetTestData.ValidSheet());

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await new NpgsqlCommand("SET ROLE babelstone_engine;", connection).ExecuteNonQueryAsync();

        var update = await Assert.ThrowsAsync<PostgresException>(() =>
            new NpgsqlCommand("UPDATE rate_sheets SET approval_ref = 'tamper';", connection).ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, update.SqlState);

        var delete = await Assert.ThrowsAsync<PostgresException>(() =>
            new NpgsqlCommand("DELETE FROM rate_sheets;", connection).ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, delete.SqlState);
    }
}
