using Babelstone.Families.TermDeposit;
using Babelstone.Families.TermDeposit.Application;
using Npgsql;
using Xunit;

namespace Babelstone.Families.TermDeposit.Application.Tests;

/// <summary>
/// Integration tests for <see cref="PostgresDepositReadModelStore"/> against a real PostgreSQL
/// (Testcontainers). The store is FAMILY-OWNED (ADR-PC-021 §D2/§P2 — the deposit-shaped table +
/// maturity range scan name this family's domain shape, not the engine spine's), so its integration
/// test lives in the family's Application tests, alongside the decider it composes with. Exercises
/// the ADR-IC-005 CQRS read-model contract after the FAMILY-OWNED read-model migration
/// (Babelstone.Families.TermDeposit.Application.Migrations 0001_read_model.sql, relocated from the
/// engine's former 0013 per ADR-PC-021 family-owned ownership): the UPSERT-with-monotonicity-guard
/// write (§P2), the point lookup and maturity range scan, the freshness pair (§P3), and the
/// truncate-and-rebuild path (§P5). The ConstitutionFixture applies the engine then the family
/// migration set, engine-before-family.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresDepositReadModelStoreTests(ConstitutionFixture fixture)
    : IClassFixture<ConstitutionFixture>
{
    private readonly PostgresDepositReadModelStore _store = new(fixture.ConnectionString);

    private static DepositReadModelRow Sample(
        Guid streamId,
        long lastSequence,
        DateOnly maturityDate,
        string lifecycle = "Active",
        long totalPayoutCents = 1_000_000,
        DateTimeOffset? lastUpdated = null,
        string productCode = "dpz_pt_12m_juros_venc",
        string autoRenewalPolicy = "NONE",
        int paymentPeriodMonths = 0,
        long accruedGrossInterestCents = 0,
        long withholdingToDateCents = 0,
        long netInterestCents = 0,
        int couponsPaid = 0) =>
        new(
            StreamId: streamId,
            Sor: "engine",
            PrincipalCents: 1_000_000,
            TanBasisPoints: 300,
            RateSheetVersionId: "pt-deposits-2026.1",
            ProductCode: productCode,
            TermDays: 365,
            StartDate: new DateOnly(2026, 1, 15),
            MaturityDate: maturityDate,
            InterestVariant: "AT_MATURITY",
            AutoRenewalPolicy: autoRenewalPolicy,
            PaymentPeriodMonths: paymentPeriodMonths,
            Lifecycle: lifecycle,
            AccruedGrossInterestCents: accruedGrossInterestCents,
            WithholdingToDateCents: withholdingToDateCents,
            NetInterestCents: netInterestCents,
            TotalPayoutCents: totalPayoutCents,
            CouponsPaid: couponsPaid,
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
        // BOTH product keys round-trip under their honest names: rate_sheet_version_id (the
        // price/version key) AND the catalogue product_code (the structural "which product is this"
        // dimension, now carried end-to-end — bd babelstone-v794).
        Assert.Equal("pt-deposits-2026.1", row.RateSheetVersionId);
        Assert.Equal("dpz_pt_12m_juros_venc", row.ProductCode);
    }

    [Fact]
    public async Task The_full_financial_position_round_trips_through_postgres()
    {
        // D.4 single-resource enrichment: the read-model row carries the live financial facts + the
        // terms (the same fold the live path computes), so GET /v1/deposits/{id} serves the complete
        // position from the row without folding. Assert every enriched column round-trips.
        await ResetAsync();
        var streamId = Guid.NewGuid();

        await _store.UpsertAsync(Sample(
            streamId, lastSequence: 4, maturityDate: new DateOnly(2027, 1, 15), lifecycle: "Matured",
            totalPayoutCents: 1_021_900, autoRenewalPolicy: "SAME_TERM_SAME_RATE", paymentPeriodMonths: 3,
            accruedGrossInterestCents: 30_417, withholdingToDateCents: 8_517, netInterestCents: 21_900,
            couponsPaid: 2));

        var row = await _store.GetAsync(streamId);
        Assert.NotNull(row);
        Assert.Equal("SAME_TERM_SAME_RATE", row.AutoRenewalPolicy);
        Assert.Equal(3, row.PaymentPeriodMonths);
        Assert.Equal(30_417, row.AccruedGrossInterestCents);
        Assert.Equal(8_517, row.WithholdingToDateCents);
        Assert.Equal(21_900, row.NetInterestCents);
        Assert.Equal(1_021_900, row.TotalPayoutCents);
        Assert.Equal(2, row.CouponsPaid);
    }

    [Fact]
    public async Task Product_code_round_trips_and_defaults_empty_for_pre_v794_rows()
    {
        // bd babelstone-v794: the catalogue product_code denormalizes onto the read surface. A
        // deposit constituted before v794 carries no code — its event decodes the Avro "" default —
        // so the read-model row surfaces the empty code (PROSPECTIVE-only; the code is not
        // back-fillable from the log). A populated code round-trips verbatim.
        await ResetAsync();
        var legacy = Guid.NewGuid();
        var fresh = Guid.NewGuid();
        var maturity = new DateOnly(2027, 1, 15);

        await _store.UpsertAsync(Sample(legacy, lastSequence: 0, maturityDate: maturity, productCode: ""));
        await _store.UpsertAsync(Sample(fresh, lastSequence: 0, maturityDate: maturity, productCode: "dpz_pt_12m_juros_venc"));

        Assert.Equal("", (await _store.GetAsync(legacy))!.ProductCode);
        Assert.Equal("dpz_pt_12m_juros_venc", (await _store.GetAsync(fresh))!.ProductCode);
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
        // BOTH product keys are present under their honest names: rate_sheet_version_id (price/
        // version key) AND product_code (the catalogue structural code, now carried end-to-end —
        // bd babelstone-v794). The MISLABELLED product_id stays ABSENT — pin both so a column whose
        // name lies about its contents can never reappear.
        Assert.Contains("rate_sheet_version_id", columns);
        Assert.Contains("product_code", columns);
        Assert.DoesNotContain("product_id", columns);
    }
}
