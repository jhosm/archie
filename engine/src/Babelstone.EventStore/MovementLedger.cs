using Npgsql;

namespace Babelstone.EventStore;

/// <summary>
/// One recorded line of the <c>account_ref</c>-keyed movement ledger (ADR-PC-032 §A1 / §95 read side):
/// a single <c>Movement</c> the spine folded off a Movement-bearing event, flattened to family-agnostic
/// PRIMITIVES so this storage boundary names no engine domain type (the same split that keeps
/// <see cref="ProjectionRecord"/> / <see cref="IReadModelRow"/> generic — the typed
/// <c>Babelstone.Engine</c> projector maps the <c>Movement</c> atom onto these columns).
/// </summary>
/// <remarks>
/// <para>
/// The ledger is an APPEND-only, rebuildable derived cache: one row per applied movement, keyed for
/// idempotency by the producing event's identity — <see cref="StreamId"/> + <see cref="SequenceNumber"/>
/// + the movement's <see cref="MovementIndex"/> within that event (an event MAY bear several movements,
/// ADR-PC-032 §A3). An account's balance is the SUM of its lines, signed by <see cref="Direction"/>
/// (Credit adds, Debit subtracts) — order-insensitive, so out-of-order arrival within an account
/// self-heals on rebuild (ADR-PC-032 §A5). EUR-only integer cents (ADR-PC-010); no currency column.
/// </para>
/// <para>
/// No PII (ADR-PC-004 §P2): <see cref="AccountRef"/> is the opaque engine-resolved reference, never an
/// IBAN; <see cref="Direction"/> / <see cref="Operation"/> / <see cref="Origin"/> are closed-enum member
/// NAMES; the rest are structural identifiers, amounts, and dates.
/// </para>
/// </remarks>
/// <param name="AccountRef">The opaque account the value moved against — the ledger key, never PII.</param>
/// <param name="StreamId">The stream (aggregate) the producing event belongs to — part of the idempotency key.</param>
/// <param name="SequenceNumber">The producing event's per-stream sequence — part of the idempotency key.</param>
/// <param name="MovementIndex">The 0-based index of this movement within the event's carrier list — the
/// final part of the idempotency key, so a multi-movement event's legs are distinct ledger lines.</param>
/// <param name="Direction"><c>Debit</c> or <c>Credit</c> relative to <see cref="AccountRef"/> (the closed
/// <c>SettlementDirection</c> member name) — the sign the balance fold applies.</param>
/// <param name="AmountCents">The amount moved, integer cents (ADR-PC-010), as the event recorded it.</param>
/// <param name="ValueDate">The economic date the value moved (the movement's <c>value_date</c>).</param>
/// <param name="Operation">Which money move this records — the closed <c>MovementOperation</c> member name.</param>
/// <param name="Origin"><c>Originated</c> or <c>Observed</c> — the closed <c>MovementOrigin</c> member name.</param>
/// <param name="CommandId">The ADR-PC-029 append-idempotency command id the originating command carried.</param>
public sealed record MovementLedgerEntry(
    string AccountRef,
    Guid StreamId,
    long SequenceNumber,
    int MovementIndex,
    string Direction,
    long AmountCents,
    DateOnly ValueDate,
    string Operation,
    string Origin,
    Guid CommandId);

/// <summary>
/// One overdrawn account, as read by <see cref="IMovementLedgerStore.GetOverdrawnAccountsAsync"/>: the
/// opaque <see cref="AccountRef"/> and its strictly-negative accounting balance in integer cents. A
/// family-agnostic read shape over the balance fold — no family type, no PII (ADR-PC-004), just the
/// account key and the drawn balance the overdraft-accrual driver keys its per-account accrual on.
/// </summary>
/// <param name="AccountRef">The opaque account whose balance is below zero — never PII (ADR-PC-004).</param>
/// <param name="BalanceCents">The account's accounting balance in integer cents, always &lt; 0 here.</param>
public sealed record OverdrawnAccount(string AccountRef, long BalanceCents);

/// <summary>
/// The generic, family-agnostic storage boundary for the spine-owned <c>account_ref</c>-keyed movement
/// ledger (ADR-PC-032 §A1). The read side the ADR named but deferred: an account statement and its
/// balance fold, materialised off every Movement-bearing event the engine appends. Family-agnostic by
/// construction — it stores only the <see cref="MovementLedgerEntry"/> primitives, so adding a family is
/// zero diff here (ENGINE_FAMILY_AGNOSTIC, ADR-PC-021 §P2).
/// </summary>
/// <remarks>
/// <see cref="AppendAsync"/> is idempotent on the <c>(stream_id, sequence_number, movement_index)</c>
/// identity, so the at-least-once projection drainer is safe to replay (a re-delivered event re-applies
/// the same lines as a no-op). <see cref="TruncateAsync"/> is the rebuild path (truncate-then-refold);
/// the balance is order-insensitive, so a cold rebuild reproduces it exactly (ADR-PC-032 §A5).
/// </remarks>
public interface IMovementLedgerStore
{
    /// <summary>
    /// Append the given ledger lines, idempotently: a line whose
    /// <c>(stream_id, sequence_number, movement_index)</c> identity already exists is skipped, so a
    /// re-delivered event never double-counts. Safe to call with an empty list (a no-op).
    /// </summary>
    Task AppendAsync(IReadOnlyList<MovementLedgerEntry> entries, CancellationToken ct = default);

    /// <summary>
    /// The account's net balance in integer cents (ADR-PC-010): the SUM of its lines, Credit positive and
    /// Debit negative. Zero for an account with no recorded movements.
    /// </summary>
    Task<long> GetBalanceCentsAsync(string accountRef, CancellationToken ct = default);

    /// <summary>
    /// Every account whose accounting balance is strictly negative — the ledger-wide "who is overdrawn?"
    /// read (ADR-PC-032 read side / ADR-PC-037 §D5): one row per <c>account_ref</c> whose signed sum is
    /// below zero, with that (negative) balance in integer cents. Family-agnostic by construction — it
    /// keys only on the opaque <c>account_ref</c>, so it names no family (the overdraft-accrual driver
    /// applies the "which product" policy, ADR-PC-037). The set is a projection-derived read, never a
    /// clock read, so an accrual decided from it stays replay-deterministic. Empty when no account is
    /// overdrawn.
    /// </summary>
    Task<IReadOnlyList<OverdrawnAccount>> GetOverdrawnAccountsAsync(CancellationToken ct = default);

    /// <summary>
    /// The account's statement: every recorded line for <paramref name="accountRef"/> in stable
    /// (stream, sequence, index) order. Empty when the account has no movements.
    /// </summary>
    Task<IReadOnlyList<MovementLedgerEntry>> GetStatementAsync(string accountRef, CancellationToken ct = default);

    /// <summary>Truncate the whole ledger for a clean rebuild (truncate-then-refold, ADR-PC-032 §A5).</summary>
    Task TruncateAsync(CancellationToken ct = default);
}

/// <summary>
/// PostgreSQL-backed <see cref="IMovementLedgerStore"/>. Hand-rolled, Npgsql-only, all
/// <c>movement_ledger</c> SQL private to this type — the storage-boundary discipline of
/// <see cref="PostgresProjectionStore"/> applied to the account-keyed movement ledger (migration 0019).
/// The idempotent append is an <c>INSERT … ON CONFLICT DO NOTHING</c> on the
/// <c>(stream_id, sequence_number, movement_index)</c> unique key.
/// </summary>
public sealed class PostgresMovementLedgerStore(string connectionString) : IMovementLedgerStore
{
    public async Task AppendAsync(IReadOnlyList<MovementLedgerEntry> entries, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return;
        }

        // One row per movement line. ON CONFLICT DO NOTHING on the producing-event identity makes the
        // at-least-once drainer safe — a re-delivered event re-inserts the same lines as a no-op.
        const string sql = """
            INSERT INTO movement_ledger
                (account_ref, stream_id, sequence_number, movement_index, direction, amount_cents,
                 value_date, operation, origin, command_id)
            VALUES
                (@account_ref, @stream_id, @sequence_number, @movement_index, @direction, @amount_cents,
                 @value_date, @operation, @origin, @command_id)
            ON CONFLICT (stream_id, sequence_number, movement_index) DO NOTHING;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        foreach (var entry in entries)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("account_ref", entry.AccountRef);
            command.Parameters.AddWithValue("stream_id", entry.StreamId);
            command.Parameters.AddWithValue("sequence_number", entry.SequenceNumber);
            command.Parameters.AddWithValue("movement_index", entry.MovementIndex);
            command.Parameters.AddWithValue("direction", entry.Direction);
            command.Parameters.AddWithValue("amount_cents", entry.AmountCents);
            command.Parameters.AddWithValue("value_date", entry.ValueDate);
            command.Parameters.AddWithValue("operation", entry.Operation);
            command.Parameters.AddWithValue("origin", entry.Origin);
            command.Parameters.AddWithValue("command_id", entry.CommandId);
            await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    public async Task<long> GetBalanceCentsAsync(string accountRef, CancellationToken ct = default)
    {
        // The balance is the signed sum: Credit adds, Debit subtracts (direction is relative to the
        // account). COALESCE makes an account with no rows read as zero, not NULL. The ::bigint cast
        // is load-bearing: PostgreSQL's SUM(bigint) returns NUMERIC, which Npgsql surfaces as
        // decimal — without the cast the scalar read below would never see an Int64.
        const string sql = """
            SELECT COALESCE(SUM(CASE WHEN direction = 'Credit' THEN amount_cents ELSE -amount_cents END), 0)::bigint
            FROM movement_ledger
            WHERE account_ref = @account_ref;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("account_ref", accountRef);
        // Hard unbox: COALESCE guarantees a non-null value and ::bigint guarantees Int64, so any
        // other shape is schema/query drift — throw (InvalidCastException), never mask it as 0.
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    public async Task<IReadOnlyList<OverdrawnAccount>> GetOverdrawnAccountsAsync(CancellationToken ct = default)
    {
        // The ledger-wide overdraft read (ADR-PC-037 §D5): group the signed lines by account and keep only
        // the accounts whose net balance is below zero. Same signed-sum expression as GetBalanceCentsAsync
        // (Credit adds, Debit subtracts); the ::bigint cast keeps SUM(bigint)'s NUMERIC surfacing as Int64.
        // No per-account index yet — a full scan is the coarse-start (a (account_ref) grouped-balance index
        // is a pre-production perf follow-up under the ADR-PC-011 load proof).
        const string sql = """
            SELECT account_ref,
                   SUM(CASE WHEN direction = 'Credit' THEN amount_cents ELSE -amount_cents END)::bigint AS balance_cents
            FROM movement_ledger
            GROUP BY account_ref
            HAVING SUM(CASE WHEN direction = 'Credit' THEN amount_cents ELSE -amount_cents END) < 0
            ORDER BY account_ref;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);

        var overdrawn = new List<OverdrawnAccount>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            overdrawn.Add(new OverdrawnAccount(AccountRef: reader.GetString(0), BalanceCents: reader.GetInt64(1)));
        }

        return overdrawn;
    }

    public async Task<IReadOnlyList<MovementLedgerEntry>> GetStatementAsync(
        string accountRef, CancellationToken ct = default)
    {
        const string sql = """
            SELECT account_ref, stream_id, sequence_number, movement_index, direction, amount_cents,
                   value_date, operation, origin, command_id
            FROM movement_ledger
            WHERE account_ref = @account_ref
            ORDER BY stream_id, sequence_number, movement_index;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("account_ref", accountRef);

        var statement = new List<MovementLedgerEntry>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            statement.Add(new MovementLedgerEntry(
                AccountRef: reader.GetString(0),
                StreamId: reader.GetGuid(1),
                SequenceNumber: reader.GetInt64(2),
                MovementIndex: reader.GetInt32(3),
                Direction: reader.GetString(4),
                AmountCents: reader.GetInt64(5),
                ValueDate: reader.GetFieldValue<DateOnly>(6),
                Operation: reader.GetString(7),
                Origin: reader.GetString(8),
                CommandId: reader.GetGuid(9)));
        }

        return statement;
    }

    public async Task TruncateAsync(CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("TRUNCATE TABLE movement_ledger;", connection);
        await command.ExecuteNonQueryAsync(ct);
    }
}
