using Babelstone.Families.PersonalLoan;
using Babelstone.Families.PersonalLoan.Application;
using Npgsql;
using Xunit;

namespace Babelstone.Families.PersonalLoan.Application.Tests;

/// <summary>
/// Integration tests for <see cref="PostgresInstallmentCalendarReadModelStore"/> against a real PostgreSQL
/// (Testcontainers), bd babelstone-6cpq.12. The store is FAMILY-OWNED (ADR-PC-021 §D2/§P2 — the loan-shaped
/// table + due-date range scan name this family's domain shape, not the engine spine's), so its integration
/// test lives in the family's Application tests. Exercises the ADR-IC-005 CQRS read-model contract after the
/// FAMILY-OWNED read-model migrations (0001 creates the table, 0002 adds the <c>detail</c> body the producer
/// needs): the UPSERT-with-monotonicity-guard write (§P2), the point lookup and the "installments due in
/// [from, to)" range scan, the freshness pair (§P3), and the truncate-and-rebuild path (§P5). The shared
/// <see cref="PackMigrationIntegrationTests.LoanFixture"/> applies the engine then the family migration set,
/// engine-before-family.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresInstallmentCalendarReadModelStoreTests(PackMigrationIntegrationTests.LoanFixture fixture)
    : IClassFixture<PackMigrationIntegrationTests.LoanFixture>
{
    private readonly PostgresInstallmentCalendarReadModelStore _store = new(fixture.ConnectionString);

    private static InstallmentCalendarReadModelRow Sample(
        Guid streamId,
        long lastSequence,
        int? nextInstallmentNumber,
        DateOnly? nextDueDate,
        int installmentsPaid = 0,
        DateTimeOffset? lastUpdated = null) =>
        new(
            StreamId: streamId,
            Sor: "engine",
            FirstInstallmentDate: new DateOnly(2026, 2, 15),
            TermMonths: 12,
            InstallmentAmountCents: 88_849,
            InstallmentsPaid: installmentsPaid,
            NextInstallmentNumber: nextInstallmentNumber,
            NextDueDate: nextDueDate,
            Detail: new byte[] { 0x01, 0x02, 0x03 },
            LastSequence: lastSequence,
            LastUpdated: lastUpdated ?? new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero));

    private async Task ResetAsync() => await _store.TruncateAsync();

    [Fact]
    public async Task Upsert_then_get_returns_the_row()
    {
        await ResetAsync();
        var streamId = Guid.NewGuid();
        var due = new DateOnly(2026, 3, 15);

        await _store.UpsertAsync(Sample(streamId, lastSequence: 1, nextInstallmentNumber: 2, nextDueDate: due, installmentsPaid: 1));

        var row = await _store.GetAsync(streamId);
        Assert.NotNull(row);
        Assert.Equal("engine", row.Sor);
        Assert.Equal(new DateOnly(2026, 2, 15), row.FirstInstallmentDate);
        Assert.Equal(12, row.TermMonths);
        Assert.Equal(88_849, row.InstallmentAmountCents);
        Assert.Equal(1, row.InstallmentsPaid);
        Assert.Equal(2, row.NextInstallmentNumber);
        Assert.Equal(due, row.NextDueDate);
        Assert.Equal(1, row.LastSequence);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, row.Detail.ToArray());
    }

    [Fact]
    public async Task Get_is_null_when_absent()
    {
        await ResetAsync();
        Assert.Null(await _store.GetAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task A_null_next_occurrence_round_trips()
    {
        // A terminal or fully-paid loan carries a NULL forward pointer; both columns must round-trip as NULL.
        await ResetAsync();
        var streamId = Guid.NewGuid();

        await _store.UpsertAsync(Sample(streamId, lastSequence: 13, nextInstallmentNumber: null, nextDueDate: null, installmentsPaid: 12));

        var row = await _store.GetAsync(streamId);
        Assert.Null(row!.NextInstallmentNumber);
        Assert.Null(row.NextDueDate);
        Assert.Equal(12, row.InstallmentsPaid);
    }

    [Fact]
    public async Task Upsert_advances_the_row_on_a_higher_sequence()
    {
        await ResetAsync();
        var streamId = Guid.NewGuid();

        await _store.UpsertAsync(Sample(streamId, lastSequence: 0, nextInstallmentNumber: 1, nextDueDate: new DateOnly(2026, 2, 15)));
        await _store.UpsertAsync(Sample(streamId, lastSequence: 3, nextInstallmentNumber: 4, nextDueDate: new DateOnly(2026, 5, 15), installmentsPaid: 3));

        var row = await _store.GetAsync(streamId);
        Assert.Equal(3, row!.LastSequence);
        Assert.Equal(4, row.NextInstallmentNumber);
        Assert.Equal(new DateOnly(2026, 5, 15), row.NextDueDate);
    }

    [Fact]
    public async Task Upsert_with_a_stale_sequence_is_a_no_op()
    {
        // ADR-IC-005 §P2: the monotonicity guard drops a re-delivered or out-of-order event whose sequence
        // is at or below the stored row's — the at-least-once drainer never clobbers fresher state.
        await ResetAsync();
        var streamId = Guid.NewGuid();

        await _store.UpsertAsync(Sample(streamId, lastSequence: 5, nextInstallmentNumber: 6, nextDueDate: new DateOnly(2026, 7, 15), installmentsPaid: 5));
        // A replay of an earlier event (sequence 2) must NOT overwrite the fresher row.
        await _store.UpsertAsync(Sample(streamId, lastSequence: 2, nextInstallmentNumber: 3, nextDueDate: new DateOnly(2026, 4, 15)));
        // An exactly-equal sequence (the canonical duplicate) is also a no-op.
        await _store.UpsertAsync(Sample(streamId, lastSequence: 5, nextInstallmentNumber: 3, nextDueDate: new DateOnly(2026, 4, 15)));

        var row = await _store.GetAsync(streamId);
        Assert.Equal(5, row!.LastSequence);
        Assert.Equal(6, row.NextInstallmentNumber);
    }

    [Fact]
    public async Task ListByDueDate_returns_the_window_in_order_excluding_nulls_and_the_upper_bound()
    {
        await ResetAsync();
        var early = Guid.NewGuid();
        var mid = Guid.NewGuid();
        var late = Guid.NewGuid();
        var outside = Guid.NewGuid();
        var closed = Guid.NewGuid();

        await _store.UpsertAsync(Sample(mid, lastSequence: 0, nextInstallmentNumber: 1, nextDueDate: new DateOnly(2027, 6, 1)));
        await _store.UpsertAsync(Sample(early, lastSequence: 0, nextInstallmentNumber: 1, nextDueDate: new DateOnly(2027, 3, 1)));
        await _store.UpsertAsync(Sample(late, lastSequence: 0, nextInstallmentNumber: 1, nextDueDate: new DateOnly(2027, 9, 1)));
        // On the exclusive upper bound — must NOT be returned.
        await _store.UpsertAsync(Sample(outside, lastSequence: 0, nextInstallmentNumber: 1, nextDueDate: new DateOnly(2028, 1, 1)));
        // No forward occurrence (terminal/fully-paid) — NULL due date is excluded by the range scan.
        await _store.UpsertAsync(Sample(closed, lastSequence: 0, nextInstallmentNumber: null, nextDueDate: null));

        var rows = await _store.ListByDueDateAsync(new DateOnly(2027, 1, 1), new DateOnly(2028, 1, 1));

        Assert.Equal(3, rows.Count);
        // Ordered ascending by next_due_date.
        Assert.Equal([early, mid, late], rows.Select(r => r.StreamId).ToArray());
    }

    [Fact]
    public async Task Truncate_clears_the_read_model()
    {
        await ResetAsync();
        var streamId = Guid.NewGuid();
        await _store.UpsertAsync(Sample(streamId, lastSequence: 0, nextInstallmentNumber: 1, nextDueDate: new DateOnly(2026, 2, 15)));

        await _store.TruncateAsync();

        Assert.Null(await _store.GetAsync(streamId));
    }

    [Fact]
    public async Task Read_model_holds_no_pii_columns_and_carries_the_detail_body()
    {
        // ADR-PC-004 §P2: the durable read surface carries structural schedule facts only — no borrower
        // name, NIF, or IBAN. Assert the column set has no obvious PII column (a schema-level guard), and
        // that migration 0002's detail body + the ADR-IC-005 §P3 freshness pair + the routing-truth column
        // are present.
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT column_name FROM information_schema.columns WHERE table_schema = 'read_model' AND table_name = 'installment_calendar';",
            connection);
        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(0));
        }

        Assert.NotEmpty(columns);
        string[] forbidden = ["name", "holder", "nif", "tax_id", "email", "phone", "address", "iban"];
        Assert.All(columns, c => Assert.DoesNotContain(c, forbidden));
        Assert.Contains("sor", columns);
        Assert.Contains("detail", columns);            // migration 0002 — the producer's read body
        Assert.Contains("next_due_date", columns);     // the range-scan dimension
        Assert.Contains("last_sequence", columns);
        Assert.Contains("last_updated", columns);
    }
}
