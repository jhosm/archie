using Babelstone.EventStore.Migrations;
using Babelstone.RateSheets;
using Babelstone.TestFixtures;
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
    public async Task Insert_then_get_round_trips_the_bands_through_the_read_only_init_setter()
    {
        // bd babelstone-z0as: pin the JSONB round-trip THROUGH the IReadOnlyList<RateBand> init
        // setter on RoleRates.Bands. The deserialize path on read-back populates Bands via the
        // { init; } setter (RoleRates has no other way in), so this proves the init-setter ⇄ JSONB
        // contract end-to-end: band COUNT, band ORDER (significant — the array order is preserved),
        // every From/To/TanBasisPoints, and the open-ended top band's null upper all survive.
        var store = new PostgresRateSheetStore(ConnectionString);
        var body = new RateSheetBody
        {
            Products = new()
            {
                ["dpz_pt_12m_juros_venc"] = new()
                {
                    ["standard"] = new RoleRates
                    {
                        Bands =
                        [
                            RateSheetTestData.Band(50_000, 5_000_000, 300),
                            RateSheetTestData.Band(5_000_000, 25_000_000, 325),
                            RateSheetTestData.Band(25_000_000, null, 350),
                        ],
                    },
                },
            },
        };

        await store.InsertAsync(RateSheetTestData.ValidSheet(versionId: "init-setter", body: body));
        var stored = await store.TryGetAsync("init-setter");

        Assert.NotNull(stored);
        var bands = stored.Body.Products["dpz_pt_12m_juros_venc"]["standard"].Bands;

        // Count + order: three bands, ascending by lower bound, exactly as written.
        Assert.Equal(3, bands.Count);
        Assert.Equal([50_000L, 5_000_000L, 25_000_000L], bands.Select(b => b.From));
        Assert.Equal([5_000_000L, 25_000_000L, (long?)null], bands.Select(b => b.To));
        Assert.Equal([300, 325, 350], bands.Select(b => b.TanBasisPoints));

        // The open-ended top band's null upper round-tripped as null, not 0 or a sentinel.
        Assert.Null(bands[^1].To);

        // And the canonical forms match — a defensive cross-check that nothing reordered or dropped.
        Assert.Equal(RateSheetJson.Canonical(body), RateSheetJson.Canonical(stored.Body));
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
        var atExactlyJune = await store.ResolveAsync("term_deposit", june); // effective_from <= asOf is inclusive
        var before = await store.ResolveAsync("term_deposit", new DateTimeOffset(2025, 12, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal("v-jan", inMarch?.RateSheetVersionId);
        Assert.Equal("v-jun", inJuly?.RateSheetVersionId);
        Assert.Equal("v-jun", atExactlyJune?.RateSheetVersionId); // the boundary resolves to that sheet, not the prior one
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
