using Babelstone.Families.PersonalLoan;
using Npgsql;

namespace Babelstone.Families.PersonalLoan.Application;

/// <summary>
/// PostgreSQL-backed <see cref="IInstallmentCalendarReadModelStore"/>: the personal_loan family's
/// denormalized CQRS installment-calendar read model (ADR-IC-005). Hand-rolled, Npgsql-only, all
/// <c>read_model.installment_calendar</c> SQL private to this type — the storage-boundary discipline of
/// <c>PostgresProjectionStore</c> applied to the read side. Lives in the FAMILY layer (the impure
/// <c>.Application</c> project that already reaches the DB seam), NOT the engine spine: the loan-shaped
/// table + the due-date range scan name one family's domain shape, so they are family-owned
/// (ADR-PC-021 §D2/§P2). The generic UPSERT/point-lookup/truncate primitives satisfy
/// <see cref="Babelstone.EventStore.IReadModelStore{TRow}"/>; <see cref="ListByDueDateAsync"/> is the
/// family-specific read. The read-side mirror of term-deposit's <c>PostgresDepositReadModelStore</c>.
/// </summary>
public sealed class PostgresInstallmentCalendarReadModelStore(string connectionString)
    : IInstallmentCalendarReadModelStore
{
    public async Task UpsertAsync(InstallmentCalendarReadModelRow row, CancellationToken ct = default)
    {
        // ADR-IC-005 §P2 — UPSERT with the monotonicity guard. The conditional UPDATE's WHERE only
        // overwrites when the incoming event is newer (last_sequence strictly greater), so a re-delivered
        // or out-of-order event from the at-least-once drainer is a no-op rather than a stale clobber. The
        // INSERT leg covers the first projection of a stream.
        const string sql = """
            INSERT INTO read_model.installment_calendar (
                stream_id, sor, first_installment_date, term_months, installment_amount_cents,
                installments_paid, next_installment_number, next_due_date, detail, last_sequence, last_updated)
            VALUES (
                @stream_id, @sor, @first_installment_date, @term_months, @installment_amount_cents,
                @installments_paid, @next_installment_number, @next_due_date, @detail, @last_sequence, @last_updated)
            ON CONFLICT (stream_id) DO UPDATE SET
                sor                      = EXCLUDED.sor,
                first_installment_date   = EXCLUDED.first_installment_date,
                term_months              = EXCLUDED.term_months,
                installment_amount_cents = EXCLUDED.installment_amount_cents,
                installments_paid        = EXCLUDED.installments_paid,
                next_installment_number  = EXCLUDED.next_installment_number,
                next_due_date            = EXCLUDED.next_due_date,
                detail                   = EXCLUDED.detail,
                last_sequence            = EXCLUDED.last_sequence,
                last_updated             = EXCLUDED.last_updated
            WHERE read_model.installment_calendar.last_sequence < EXCLUDED.last_sequence;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        BindRow(command, row);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<InstallmentCalendarReadModelRow?> GetAsync(Guid streamId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT stream_id, sor, first_installment_date, term_months, installment_amount_cents,
                   installments_paid, next_installment_number, next_due_date, detail, last_sequence, last_updated
            FROM read_model.installment_calendar
            WHERE stream_id = @stream_id;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("stream_id", streamId);

        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Map(reader) : null;
    }

    public async Task<IReadOnlyList<InstallmentCalendarReadModelRow>> ListByDueDateAsync(
        DateOnly fromInclusive, DateOnly toExclusive, CancellationToken ct = default)
    {
        // Range scan over the installment_calendar_next_due_date_idx B-tree (migration 0001): every loan
        // whose forward next-unpaid installment falls in [from, to). A loan with no next occurrence carries
        // a NULL next_due_date and is excluded by the half-open comparison (NULL is never >= @from), so a
        // terminal or fully-paid loan never appears. ORDER BY (next_due_date, stream_id) is a deterministic,
        // stable order — stream_id breaks ties so the page order never depends on physical row layout.
        const string sql = """
            SELECT stream_id, sor, first_installment_date, term_months, installment_amount_cents,
                   installments_paid, next_installment_number, next_due_date, detail, last_sequence, last_updated
            FROM read_model.installment_calendar
            WHERE next_due_date >= @from_inclusive AND next_due_date < @to_exclusive
            ORDER BY next_due_date ASC, stream_id ASC;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("from_inclusive", fromInclusive);
        command.Parameters.AddWithValue("to_exclusive", toExclusive);

        var rows = new List<InstallmentCalendarReadModelRow>();
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
        // read_model.installment_calendar (the personal_loan installment_calendar_read_model kind). The
        // drainer's per-runner RebuildAsync supersedes/resets only the runner's own kind, but this TRUNCATEs
        // the whole table — correct while one kind owns it. If a second read-model kind ever shares this
        // table, scope the truncate by a kind/family discriminator at that point. No present-day defect.
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("TRUNCATE read_model.installment_calendar;", connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static void BindRow(NpgsqlCommand command, InstallmentCalendarReadModelRow row)
    {
        command.Parameters.AddWithValue("stream_id", row.StreamId);
        command.Parameters.AddWithValue("sor", row.Sor);
        command.Parameters.AddWithValue("first_installment_date", row.FirstInstallmentDate);
        command.Parameters.AddWithValue("term_months", row.TermMonths);
        command.Parameters.AddWithValue("installment_amount_cents", row.InstallmentAmountCents);
        command.Parameters.AddWithValue("installments_paid", row.InstallmentsPaid);
        // The forward pointer is NULL for a loan with no next occurrence (terminal/fully-paid): bind DBNull
        // so the half-open range scan excludes it.
        command.Parameters.AddWithValue("next_installment_number", (object?)row.NextInstallmentNumber ?? DBNull.Value);
        command.Parameters.AddWithValue("next_due_date", (object?)row.NextDueDate ?? DBNull.Value);
        command.Parameters.AddWithValue("detail", row.Detail.ToArray());
        command.Parameters.AddWithValue("last_sequence", row.LastSequence);
        command.Parameters.AddWithValue("last_updated", row.LastUpdated);
    }

    private static InstallmentCalendarReadModelRow Map(NpgsqlDataReader reader) =>
        new(
            StreamId: reader.GetGuid(0),
            Sor: reader.GetString(1),
            FirstInstallmentDate: reader.GetFieldValue<DateOnly>(2),
            TermMonths: reader.GetInt32(3),
            InstallmentAmountCents: reader.GetInt64(4),
            InstallmentsPaid: reader.GetInt32(5),
            NextInstallmentNumber: reader.IsDBNull(6) ? null : reader.GetInt32(6),
            NextDueDate: reader.IsDBNull(7) ? null : reader.GetFieldValue<DateOnly>(7),
            Detail: reader.GetFieldValue<byte[]>(8),
            LastSequence: reader.GetInt64(9),
            LastUpdated: reader.GetFieldValue<DateTimeOffset>(10));
}
