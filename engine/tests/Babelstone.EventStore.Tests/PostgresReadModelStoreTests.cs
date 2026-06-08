using Npgsql;
using Xunit;

namespace Babelstone.EventStore.Tests;

/// <summary>
/// Integration tests for <see cref="PostgresReadModelStore"/> against a real PostgreSQL
/// (Testcontainers). Exercises the ADR-IC-005 CQRS read-model contract after migration 0013: the
/// UPSERT-with-monotonicity-guard write (§P2), the point lookup and maturity range scan, the
/// freshness pair (§P3), and the truncate-and-rebuild path (§P5).
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresReadModelStoreTests(PostgresEventStoreFixture fixture)
    : IClassFixture<PostgresEventStoreFixture>
{
    private readonly PostgresReadModelStore _store = new(fixture.ConnectionString);

    private static ReadModelRow Sample(
        Guid streamId,
        long lastSequence,
        DateOnly maturityDate,
        string lifecycle = "Active",
        long totalPayoutCents = 1_000_000,
        DateTimeOffset? lastUpdated = null) =>
        new(
            StreamId: streamId,
            Sor: "engine",
            PrincipalCents: 1_000_000,
            TanBasisPoints: 300,
            RateSheetVersionId: "pt-deposits-2026.1",
            TermDays: 365,
            StartDate: new DateOnly(2026, 1, 15),
            MaturityDate: maturityDate,
            InterestVariant: "AT_MATURITY",
            Lifecycle: lifecycle,
            TotalPayoutCents: totalPayoutCents,
            Detail: new byte[] { 0x01, 0x02, 0x03 },
            LastSequence: lastSequence,
            LastUpdated: lastUpdated ?? new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero));

    private async Task ResetAsync() => await _store.TruncateAsync();

    [Fact]
    public async Task Upsert_then_get_returns_the_row()
    {
        await ResetAsync();
        var streamId = Guid.NewGuid();
        var maturity = new DateOnly(2027, 1, 15);

        await _store.UpsertAsync(Sample(streamId, lastSequence: 0, maturityDate: maturity));

        var row = await _store.GetAsync(streamId);
        Assert.NotNull(row);
        Assert.Equal("engine", row.Sor);
        Assert.Equal(maturity, row.MaturityDate);
        Assert.Equal(0, row.LastSequence);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, row.Detail.ToArray());
        // The product KEY round-trips as rate_sheet_version_id — the read surface carries the
        // rate-sheet version, NOT a catalogue product_id (the catalogue code does not survive onto
        // the event; bd babelstone-yfr2). Pin the meaning so a future product_id can't sneak back in
        // mislabelled.
        Assert.Equal("pt-deposits-2026.1", row.RateSheetVersionId);
    }

    [Fact]
    public async Task Get_is_null_when_absent()
    {
        await ResetAsync();
        Assert.Null(await _store.GetAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Upsert_advances_the_row_on_a_higher_sequence()
    {
        await ResetAsync();
        var streamId = Guid.NewGuid();
        var maturity = new DateOnly(2027, 1, 15);

        await _store.UpsertAsync(Sample(streamId, lastSequence: 0, maturityDate: maturity, lifecycle: "Active"));
        await _store.UpsertAsync(Sample(streamId, lastSequence: 3, maturityDate: maturity, lifecycle: "Matured", totalPayoutCents: 1_021_900));

        var row = await _store.GetAsync(streamId);
        Assert.Equal("Matured", row!.Lifecycle);
        Assert.Equal(3, row.LastSequence);
        Assert.Equal(1_021_900, row.TotalPayoutCents);
    }

    [Fact]
    public async Task Upsert_with_a_stale_sequence_is_a_no_op()
    {
        // ADR-IC-005 §P2: the monotonicity guard drops a re-delivered or out-of-order event whose
        // sequence is at or below the stored row's — the at-least-once drainer never clobbers fresher
        // state with a replay.
        await ResetAsync();
        var streamId = Guid.NewGuid();
        var maturity = new DateOnly(2027, 1, 15);

        await _store.UpsertAsync(Sample(streamId, lastSequence: 5, maturityDate: maturity, lifecycle: "Matured"));
        // A replay of an earlier event (sequence 2) must NOT overwrite the matured row.
        await _store.UpsertAsync(Sample(streamId, lastSequence: 2, maturityDate: maturity, lifecycle: "Active"));
        // An exactly-equal sequence (the canonical duplicate) is also a no-op.
        await _store.UpsertAsync(Sample(streamId, lastSequence: 5, maturityDate: maturity, lifecycle: "Active"));

        var row = await _store.GetAsync(streamId);
        Assert.Equal("Matured", row!.Lifecycle);
        Assert.Equal(5, row.LastSequence);
    }

    [Fact]
    public async Task ListByMaturity_returns_the_window_in_order()
    {
        await ResetAsync();
        var early = Guid.NewGuid();
        var mid = Guid.NewGuid();
        var late = Guid.NewGuid();
        var outside = Guid.NewGuid();

        await _store.UpsertAsync(Sample(mid, lastSequence: 0, maturityDate: new DateOnly(2027, 6, 1)));
        await _store.UpsertAsync(Sample(early, lastSequence: 0, maturityDate: new DateOnly(2027, 3, 1)));
        await _store.UpsertAsync(Sample(late, lastSequence: 0, maturityDate: new DateOnly(2027, 9, 1)));
        // Outside the window (on the exclusive upper bound) — must NOT be returned.
        await _store.UpsertAsync(Sample(outside, lastSequence: 0, maturityDate: new DateOnly(2028, 1, 1)));

        var rows = await _store.ListByMaturityAsync(new DateOnly(2027, 1, 1), new DateOnly(2028, 1, 1));

        Assert.Equal(3, rows.Count);
        // Ordered ascending by maturity_date.
        Assert.Equal([early, mid, late], rows.Select(r => r.StreamId).ToArray());
    }

    [Fact]
    public async Task Truncate_clears_the_read_model()
    {
        await ResetAsync();
        var streamId = Guid.NewGuid();
        await _store.UpsertAsync(Sample(streamId, lastSequence: 0, maturityDate: new DateOnly(2027, 1, 15)));

        await _store.TruncateAsync();

        Assert.Null(await _store.GetAsync(streamId));
    }

    [Fact]
    public async Task Read_model_holds_no_pii_columns()
    {
        // ADR-PC-004 §P2: the durable read surface carries structural deposit facts only — no holder
        // name, no NIF. Assert the column set has no obvious PII column (a schema-level guard).
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT column_name FROM information_schema.columns WHERE table_schema = 'read_model' AND table_name = 'deposits';",
            connection);
        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(0));
        }

        Assert.NotEmpty(columns);
        string[] forbidden = ["name", "holder", "nif", "tax_id", "email", "phone", "address"];
        Assert.All(columns, c => Assert.DoesNotContain(c, forbidden));
        // The ADR-PC-018 routing-truth column and the ADR-IC-005 §P3 freshness pair are present.
        Assert.Contains("sor", columns);
        Assert.Contains("last_sequence", columns);
        Assert.Contains("last_updated", columns);
        // The product key is rate_sheet_version_id; there is deliberately no catalogue product_id
        // column (bd babelstone-yfr2). Pin its absence so a mislabelled column can't reappear.
        Assert.Contains("rate_sheet_version_id", columns);
        Assert.DoesNotContain("product_id", columns);
    }
}
