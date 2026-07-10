using Npgsql;

namespace Babelstone.Families.TermDeposit.Application;

/// <summary>
/// PostgreSQL-backed <see cref="IDepositReadModelStore"/>: the term-deposit family's denormalized
/// CQRS read model (ADR-IC-005). Hand-rolled, Npgsql-only, all <c>read_model.deposits</c> SQL
/// private to this type — the storage-boundary discipline of <c>PostgresProjectionStore</c> applied
/// to the read side. Lives in the FAMILY layer (the impure <c>.Application</c> project that already
/// reaches the DB seam), NOT the engine spine: the deposit-shaped table + the maturity range scan
/// name one family's domain shape, so they are family-owned (ADR-PC-021 §D2/§P2). The generic
/// UPSERT/point-lookup/truncate primitives satisfy <see cref="Babelstone.EventStore.IReadModelStore{TRow}"/>;
/// <see cref="ListByMaturityAsync"/> is the family-specific read.
/// </summary>
public sealed class PostgresDepositReadModelStore(string connectionString) : IDepositReadModelStore
{
    public async Task UpsertAsync(DepositReadModelRow row, CancellationToken ct = default)
    {
        // ADR-IC-005 §P2 — UPSERT with the monotonicity guard. The conditional UPDATE's WHERE only
        // overwrites when the incoming event is newer (last_sequence strictly greater), so a
        // re-delivered or out-of-order event from the at-least-once drainer is a no-op rather than
        // a stale clobber. The INSERT leg covers the first projection of a stream.
        const string sql = """
            INSERT INTO read_model.deposits (
                stream_id, sor, principal_cents, tan_basis_points, rate_sheet_version_id, product_code,
                term_days, start_date, maturity_date, interest_variant, auto_renewal_policy,
                payment_period_months, lifecycle, accrued_gross_interest_cents, withholding_to_date_cents,
                net_interest_cents, total_payout_cents, coupons_paid, detail, last_sequence, last_updated,
                product_config_version)
            VALUES (
                @stream_id, @sor, @principal_cents, @tan_basis_points, @rate_sheet_version_id, @product_code,
                @term_days, @start_date, @maturity_date, @interest_variant, @auto_renewal_policy,
                @payment_period_months, @lifecycle, @accrued_gross_interest_cents, @withholding_to_date_cents,
                @net_interest_cents, @total_payout_cents, @coupons_paid, @detail, @last_sequence, @last_updated,
                @product_config_version)
            ON CONFLICT (stream_id) DO UPDATE SET
                sor                          = EXCLUDED.sor,
                principal_cents              = EXCLUDED.principal_cents,
                tan_basis_points             = EXCLUDED.tan_basis_points,
                rate_sheet_version_id        = EXCLUDED.rate_sheet_version_id,
                product_code                 = EXCLUDED.product_code,
                term_days                    = EXCLUDED.term_days,
                start_date                   = EXCLUDED.start_date,
                maturity_date                = EXCLUDED.maturity_date,
                interest_variant             = EXCLUDED.interest_variant,
                auto_renewal_policy          = EXCLUDED.auto_renewal_policy,
                payment_period_months        = EXCLUDED.payment_period_months,
                lifecycle                    = EXCLUDED.lifecycle,
                accrued_gross_interest_cents = EXCLUDED.accrued_gross_interest_cents,
                withholding_to_date_cents    = EXCLUDED.withholding_to_date_cents,
                net_interest_cents           = EXCLUDED.net_interest_cents,
                total_payout_cents           = EXCLUDED.total_payout_cents,
                coupons_paid                 = EXCLUDED.coupons_paid,
                detail                       = EXCLUDED.detail,
                last_sequence                = EXCLUDED.last_sequence,
                last_updated                 = EXCLUDED.last_updated,
                product_config_version       = EXCLUDED.product_config_version
            WHERE read_model.deposits.last_sequence < EXCLUDED.last_sequence;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        BindRow(command, row);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<DepositReadModelRow?> GetAsync(Guid streamId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT stream_id, sor, principal_cents, tan_basis_points, rate_sheet_version_id, product_code,
                   term_days, start_date, maturity_date, interest_variant, auto_renewal_policy,
                   payment_period_months, lifecycle, accrued_gross_interest_cents, withholding_to_date_cents,
                   net_interest_cents, total_payout_cents, coupons_paid, detail, last_sequence, last_updated,
                   product_config_version
            FROM read_model.deposits
            WHERE stream_id = @stream_id;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("stream_id", streamId);

        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Map(reader) : null;
    }

    public async Task<IReadOnlyList<DepositReadModelRow>> ListByMaturityAsync(
        DateOnly fromInclusive, DateOnly toExclusive, CancellationToken ct = default)
    {
        // Range scan over the deposits_maturity_date_idx B-tree (ADR-IC-005 upcoming_maturities).
        // ORDER BY (maturity_date, stream_id) is a deterministic, stable order — stream_id breaks
        // ties so the page order never depends on physical row layout.
        const string sql = """
            SELECT stream_id, sor, principal_cents, tan_basis_points, rate_sheet_version_id, product_code,
                   term_days, start_date, maturity_date, interest_variant, auto_renewal_policy,
                   payment_period_months, lifecycle, accrued_gross_interest_cents, withholding_to_date_cents,
                   net_interest_cents, total_payout_cents, coupons_paid, detail, last_sequence, last_updated,
                   product_config_version
            FROM read_model.deposits
            WHERE maturity_date >= @from_inclusive AND maturity_date < @to_exclusive
            ORDER BY maturity_date ASC, stream_id ASC;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("from_inclusive", fromInclusive);
        command.Parameters.AddWithValue("to_exclusive", toExclusive);

        var rows = new List<DepositReadModelRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(Map(reader));
        }

        return rows;
    }

    public async Task<IReadOnlyList<DepositReadModelRow>> ListWithWithholdingAsync(CancellationToken ct = default)
    {
        // The annual IRS-withholding statement population (bd babelstone-q15c): every deposit that has had
        // tax withheld, carrying the per-row accrual/withholding rollups (the read-model projection of
        // term_deposit.accrual_schedule + withholding_ledger) the statement interpolates. ORDER BY stream_id
        // is a deterministic, stable order (cf. ListActiveStreamIdsAsync) so the page order never depends on
        // physical row layout. Current belief, all lifecycles — a matured/renewed deposit still owes a
        // statement for the tax year it paid (and withheld) interest in; the downstream scheduler decides
        // the as-of statement date and the per-tax-year idempotency, never this read.
        const string sql = """
            SELECT stream_id, sor, principal_cents, tan_basis_points, rate_sheet_version_id, product_code,
                   term_days, start_date, maturity_date, interest_variant, auto_renewal_policy,
                   payment_period_months, lifecycle, accrued_gross_interest_cents, withholding_to_date_cents,
                   net_interest_cents, total_payout_cents, coupons_paid, detail, last_sequence, last_updated,
                   product_config_version
            FROM read_model.deposits
            WHERE withholding_to_date_cents > 0
            ORDER BY stream_id ASC;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);

        var rows = new List<DepositReadModelRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(Map(reader));
        }

        return rows;
    }

    public async Task<IReadOnlyList<Guid>> ListActiveStreamIdsAsync(CancellationToken ct = default)
    {
        // currently_active ⇔ the SINGLE live lifecycle (DepositLifecycle.Active); every other label is
        // terminal (Matured/Failed/Renewed/TerminatedEarly/TransferredToHeirs/Erased) or never persists a
        // row (Pending). nameof keeps the literal in lock-step with the enum — a rename breaks the build,
        // not the query silently. ORDER BY stream_id is a deterministic, stable order (cf.
        // ListByMaturityAsync); the deposits_lifecycle_idx B-tree (migration 0002) answers the predicate
        // with an index scan. Ids only — the migration write-path consumes a Guid list, not rows.
        const string sql = """
            SELECT stream_id
            FROM read_model.deposits
            WHERE lifecycle = @lifecycle
            ORDER BY stream_id ASC;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("lifecycle", nameof(DepositLifecycle.Active));

        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    public async Task<IReadOnlyList<DepositReadModelRow>> ListPayoutPendingAsync(CancellationToken ct = default)
    {
        // The undeliverable-payout hold population (ADR-PC-043 slot 5): every deposit
        // whose matured payout could not land, so it is held payout-pending at source. nameof keeps the
        // literal in lock-step with the enum — a rename breaks the build, not the query silently. ORDER BY
        // stream_id is a deterministic, stable order (cf. ListActiveStreamIdsAsync); the
        // deposits_lifecycle_idx B-tree (migration 0002) answers the predicate with an index scan. Full rows
        // — the retry rule needs the beneficiary/maturity facts, not ids only.
        const string sql = """
            SELECT stream_id, sor, principal_cents, tan_basis_points, rate_sheet_version_id, product_code,
                   term_days, start_date, maturity_date, interest_variant, auto_renewal_policy,
                   payment_period_months, lifecycle, accrued_gross_interest_cents, withholding_to_date_cents,
                   net_interest_cents, total_payout_cents, coupons_paid, detail, last_sequence, last_updated,
                   product_config_version
            FROM read_model.deposits
            WHERE lifecycle = @lifecycle
            ORDER BY stream_id ASC;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("lifecycle", nameof(DepositLifecycle.PayoutPending));

        var rows = new List<DepositReadModelRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(Map(reader));
        }

        return rows;
    }

    public async Task TruncateAsync(CancellationToken ct = default)
    {
        // ADR-IC-005 §P5 rebuild. ASSUMPTION: exactly one read-model runner owns
        // read_model.deposits (the term-deposit deposit_read_model kind). The drainer's per-runner
        // RebuildAsync supersedes/resets only the runner's own kind, but this TRUNCATEs the whole
        // table — correct while one kind owns it. If a second read-model kind ever shares this
        // table, a single-kind rebuild would wipe the other kind's rows; scope the truncate by a
        // kind/family discriminator at that point (cf. the bitemporal store, which supersedes only
        // its own (stream_id, projection_kind) rows). No present-day defect.
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("TRUNCATE read_model.deposits;", connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static void BindRow(NpgsqlCommand command, DepositReadModelRow row)
    {
        command.Parameters.AddWithValue("stream_id", row.StreamId);
        command.Parameters.AddWithValue("sor", row.Sor);
        command.Parameters.AddWithValue("principal_cents", row.PrincipalCents);
        command.Parameters.AddWithValue("tan_basis_points", row.TanBasisPoints);
        command.Parameters.AddWithValue("rate_sheet_version_id", row.RateSheetVersionId);
        command.Parameters.AddWithValue("product_code", row.ProductCode);
        command.Parameters.AddWithValue("term_days", row.TermDays);
        command.Parameters.AddWithValue("start_date", row.StartDate);
        command.Parameters.AddWithValue("maturity_date", row.MaturityDate);
        command.Parameters.AddWithValue("interest_variant", row.InterestVariant);
        command.Parameters.AddWithValue("auto_renewal_policy", row.AutoRenewalPolicy);
        command.Parameters.AddWithValue("payment_period_months", row.PaymentPeriodMonths);
        command.Parameters.AddWithValue("lifecycle", row.Lifecycle);
        command.Parameters.AddWithValue("accrued_gross_interest_cents", row.AccruedGrossInterestCents);
        command.Parameters.AddWithValue("withholding_to_date_cents", row.WithholdingToDateCents);
        command.Parameters.AddWithValue("net_interest_cents", row.NetInterestCents);
        command.Parameters.AddWithValue("total_payout_cents", row.TotalPayoutCents);
        command.Parameters.AddWithValue("coupons_paid", row.CouponsPaid);
        command.Parameters.AddWithValue("detail", row.Detail.ToArray());
        command.Parameters.AddWithValue("last_sequence", row.LastSequence);
        command.Parameters.AddWithValue("last_updated", row.LastUpdated);
        command.Parameters.AddWithValue("product_config_version", row.ProductConfigVersion);
    }

    private static DepositReadModelRow Map(NpgsqlDataReader reader) =>
        new(
            StreamId: reader.GetGuid(0),
            Sor: reader.GetString(1),
            PrincipalCents: reader.GetInt64(2),
            TanBasisPoints: reader.GetInt32(3),
            RateSheetVersionId: reader.GetString(4),
            ProductCode: reader.GetString(5),
            TermDays: reader.GetInt32(6),
            StartDate: reader.GetFieldValue<DateOnly>(7),
            MaturityDate: reader.GetFieldValue<DateOnly>(8),
            InterestVariant: reader.GetString(9),
            AutoRenewalPolicy: reader.GetString(10),
            PaymentPeriodMonths: reader.GetInt32(11),
            Lifecycle: reader.GetString(12),
            AccruedGrossInterestCents: reader.GetInt64(13),
            WithholdingToDateCents: reader.GetInt64(14),
            NetInterestCents: reader.GetInt64(15),
            TotalPayoutCents: reader.GetInt64(16),
            CouponsPaid: reader.GetInt32(17),
            Detail: reader.GetFieldValue<byte[]>(18),
            LastSequence: reader.GetInt64(19),
            LastUpdated: reader.GetFieldValue<DateTimeOffset>(20),
            ProductConfigVersion: reader.GetString(21));
}
