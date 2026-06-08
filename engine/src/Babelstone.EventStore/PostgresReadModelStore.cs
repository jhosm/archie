using Npgsql;

namespace Babelstone.EventStore;

/// <summary>
/// PostgreSQL-backed <see cref="IReadModelStore"/>. Hand-rolled, Npgsql-only, all
/// <c>read_model.deposits</c> SQL private to this type — the storage-boundary discipline of
/// <see cref="PostgresProjectionStore"/> applied to the denormalized CQRS read model (ADR-IC-005).
/// </summary>
public sealed class PostgresReadModelStore(string connectionString) : IReadModelStore
{
    public async Task UpsertAsync(ReadModelRow row, CancellationToken ct = default)
    {
        // ADR-IC-005 §P2 — UPSERT with the monotonicity guard. The conditional UPDATE's WHERE only
        // overwrites when the incoming event is newer (last_sequence strictly greater), so a
        // re-delivered or out-of-order event from the at-least-once drainer is a no-op rather than
        // a stale clobber. The INSERT leg covers the first projection of a stream.
        const string sql = """
            INSERT INTO read_model.deposits (
                stream_id, sor, product_id, principal_cents, tan_basis_points, rate_sheet_version_id,
                term_days, start_date, maturity_date, interest_variant, lifecycle, total_payout_cents,
                detail, last_sequence, last_updated)
            VALUES (
                @stream_id, @sor, @product_id, @principal_cents, @tan_basis_points, @rate_sheet_version_id,
                @term_days, @start_date, @maturity_date, @interest_variant, @lifecycle, @total_payout_cents,
                @detail, @last_sequence, @last_updated)
            ON CONFLICT (stream_id) DO UPDATE SET
                sor                   = EXCLUDED.sor,
                product_id            = EXCLUDED.product_id,
                principal_cents       = EXCLUDED.principal_cents,
                tan_basis_points      = EXCLUDED.tan_basis_points,
                rate_sheet_version_id = EXCLUDED.rate_sheet_version_id,
                term_days             = EXCLUDED.term_days,
                start_date            = EXCLUDED.start_date,
                maturity_date         = EXCLUDED.maturity_date,
                interest_variant      = EXCLUDED.interest_variant,
                lifecycle             = EXCLUDED.lifecycle,
                total_payout_cents    = EXCLUDED.total_payout_cents,
                detail                = EXCLUDED.detail,
                last_sequence         = EXCLUDED.last_sequence,
                last_updated          = EXCLUDED.last_updated
            WHERE read_model.deposits.last_sequence < EXCLUDED.last_sequence;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        BindRow(command, row);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<ReadModelRow?> GetAsync(Guid streamId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT stream_id, sor, product_id, principal_cents, tan_basis_points, rate_sheet_version_id,
                   term_days, start_date, maturity_date, interest_variant, lifecycle, total_payout_cents,
                   detail, last_sequence, last_updated
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

    public async Task<IReadOnlyList<ReadModelRow>> ListByMaturityAsync(
        DateOnly fromInclusive, DateOnly toExclusive, CancellationToken ct = default)
    {
        // Range scan over the deposits_maturity_date_idx B-tree (ADR-IC-005 upcoming_maturities).
        // ORDER BY (maturity_date, stream_id) is a deterministic, stable order — stream_id breaks
        // ties so the page order never depends on physical row layout.
        const string sql = """
            SELECT stream_id, sor, product_id, principal_cents, tan_basis_points, rate_sheet_version_id,
                   term_days, start_date, maturity_date, interest_variant, lifecycle, total_payout_cents,
                   detail, last_sequence, last_updated
            FROM read_model.deposits
            WHERE maturity_date >= @from_inclusive AND maturity_date < @to_exclusive
            ORDER BY maturity_date ASC, stream_id ASC;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("from_inclusive", fromInclusive);
        command.Parameters.AddWithValue("to_exclusive", toExclusive);

        var rows = new List<ReadModelRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(Map(reader));
        }

        return rows;
    }

    public async Task TruncateAsync(CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("TRUNCATE read_model.deposits;", connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static void BindRow(NpgsqlCommand command, ReadModelRow row)
    {
        command.Parameters.AddWithValue("stream_id", row.StreamId);
        command.Parameters.AddWithValue("sor", row.Sor);
        command.Parameters.AddWithValue("product_id", row.ProductId);
        command.Parameters.AddWithValue("principal_cents", row.PrincipalCents);
        command.Parameters.AddWithValue("tan_basis_points", row.TanBasisPoints);
        command.Parameters.AddWithValue("rate_sheet_version_id", row.RateSheetVersionId);
        command.Parameters.AddWithValue("term_days", row.TermDays);
        command.Parameters.AddWithValue("start_date", row.StartDate);
        command.Parameters.AddWithValue("maturity_date", row.MaturityDate);
        command.Parameters.AddWithValue("interest_variant", row.InterestVariant);
        command.Parameters.AddWithValue("lifecycle", row.Lifecycle);
        command.Parameters.AddWithValue("total_payout_cents", row.TotalPayoutCents);
        command.Parameters.AddWithValue("detail", row.Detail.ToArray());
        command.Parameters.AddWithValue("last_sequence", row.LastSequence);
        command.Parameters.AddWithValue("last_updated", row.LastUpdated);
    }

    private static ReadModelRow Map(NpgsqlDataReader reader) =>
        new(
            StreamId: reader.GetGuid(0),
            Sor: reader.GetString(1),
            ProductId: reader.GetString(2),
            PrincipalCents: reader.GetInt64(3),
            TanBasisPoints: reader.GetInt32(4),
            RateSheetVersionId: reader.GetString(5),
            TermDays: reader.GetInt32(6),
            StartDate: reader.GetFieldValue<DateOnly>(7),
            MaturityDate: reader.GetFieldValue<DateOnly>(8),
            InterestVariant: reader.GetString(9),
            Lifecycle: reader.GetString(10),
            TotalPayoutCents: reader.GetInt64(11),
            Detail: reader.GetFieldValue<byte[]>(12),
            LastSequence: reader.GetInt64(13),
            LastUpdated: reader.GetFieldValue<DateTimeOffset>(14));
}
