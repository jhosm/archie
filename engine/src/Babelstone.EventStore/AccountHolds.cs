using Npgsql;

namespace Babelstone.EventStore;

/// <summary>
/// One recorded hold in the <c>account_ref</c>-keyed active-hold read model (ADR-PC-033):
/// the fold of one hold's lifecycle (<c>HoldPlaced → HoldCaptured | HoldExpired</c>), flattened to
/// family-agnostic PRIMITIVES so this storage boundary names no engine domain type (the same split
/// that keeps <see cref="MovementLedgerEntry"/> generic — the typed <c>Babelstone.Engine</c> projector
/// maps the spine <c>Hold</c> shape onto these columns).
/// </summary>
/// <remarks>
/// <para>
/// The table is a REBUILDABLE derived cache keyed by <see cref="HoldId"/> — the ADR-PC-033
/// idempotency/correlation key every lifecycle event of one hold carries. While
/// <see cref="State"/> is <c>ACTIVE</c> the hold's <see cref="AmountCents"/> reduces the account's
/// available balance; a capture or expiry transitions the row out of the active set. The row is
/// state-transitioned, never deleted, so "what earmarks has this account carried" stays answerable
/// by query (migration 0020).
/// </para>
/// <para>
/// No PII (ADR-PC-004): <see cref="HoldId"/> / <see cref="AccountRef"/> are opaque structural
/// references; <see cref="State"/> is a closed-set member name; the rest are ids, amounts, dates.
/// </para>
/// </remarks>
/// <param name="HoldId">The hold's lifecycle idempotency/correlation key (ADR-PC-033).</param>
/// <param name="AccountRef">The opaque account the earmark applies to — the fold key, never PII.</param>
/// <param name="AmountCents">The earmarked amount, integer cents (ADR-PC-010).</param>
/// <param name="ValueDate">The economic date an AUTHORIZATION hold took effect — its expiry-horizon
/// axis (ADR-PC-023). Null for a LEGAL hold, which has no economic effective date on
/// <c>operations.FundsHeld</c> and uses <see cref="ExpiresAt"/> as its horizon instead (ADR-PC-041).</param>
/// <param name="State"><c>ACTIVE</c>, <c>CAPTURED</c>, <c>EXPIRED</c> (authorization lifecycle) or
/// <c>RELEASED</c> (legal-hold lift) — the closed lifecycle set (ADR-PC-041).</param>
/// <param name="PlacedStreamId">The stream that carried the placing event (<c>HoldPlaced</c> / <c>FundsHeld</c>).</param>
/// <param name="PlacedSequence">The placing event's per-stream sequence.</param>
/// <param name="CapturedAmountCents">Set on capture — MAY be less than <see cref="AmountCents"/>
/// (a partial capture releases the remainder, ADR-PC-033); null while active / on expiry / release.</param>
/// <param name="ReleasedStreamId">The stream that carried the releasing event; null while active.</param>
/// <param name="ReleasedSequence">The releasing event's per-stream sequence; null while active.</param>
/// <param name="Kind"><c>AUTHORIZATION</c> (an approved-but-unsettled earmark, ADR-PC-033) or
/// <c>LEGAL</c> (a court order / garnishment, ADR-PC-041). Both fold into <see cref="AmountCents"/> so
/// both lower available balance; the kind is what makes the "why" observable (HOLD_REASON_OBSERVABLE).</param>
/// <param name="LegalReference">The court/case reference a LEGAL hold names (ADR-PC-041 slot 1) — the
/// observable "why". Null for an authorization hold. STRUCTURAL, never PII (ADR-PC-004).</param>
/// <param name="ExpiresAt">A LEGAL hold's advisory expiry horizon (ADR-PC-041 slot 2 / ADR-PC-023);
/// null = open-ended, or an authorization hold (which uses <see cref="ValueDate"/>).</param>
public sealed record AccountHoldRow(
    string HoldId,
    string AccountRef,
    long AmountCents,
    DateOnly? ValueDate,
    string State,
    Guid PlacedStreamId,
    long PlacedSequence,
    long? CapturedAmountCents = null,
    Guid? ReleasedStreamId = null,
    long? ReleasedSequence = null,
    string Kind = "AUTHORIZATION",
    string? LegalReference = null,
    DateOnly? ExpiresAt = null);

/// <summary>
/// How a capture/expiry landed against the hold set — the three-way answer whose non-normal
/// members the projector must SURFACE, never silently absorb (ADR-PC-033).
/// </summary>
public enum HoldReleaseResult
{
    /// <summary>The hold was ACTIVE and is now released — the one normal outcome.</summary>
    Transitioned,

    /// <summary>The hold exists but had already left the active set — a duplicate/late release.
    /// Folds as a no-op (never a double-release); a reconciliation signal.</summary>
    AlreadyReleased,

    /// <summary>No hold with this id was ever placed — a fold-order error.</summary>
    NeverPlaced,
}

/// <summary>
/// The generic, family-agnostic storage boundary for the spine-owned active-hold read model
/// (ADR-PC-033, migration 0020). The available-balance fold subtracts
/// <see cref="GetActiveHoldCentsAsync"/> from the movement-ledger signed sum; the hold set itself is
/// a rebuildable fold of the three lifecycle events, never a stored source of truth. Family-agnostic
/// by construction — it stores only <see cref="AccountHoldRow"/> primitives, so adding a family is
/// zero diff here (ENGINE_FAMILY_AGNOSTIC, ADR-PC-021).
/// </summary>
/// <remarks>
/// Idempotency mirrors the lifecycle's own key (ADR-PC-033): <see cref="PlaceAsync"/> is a
/// no-op when the <c>hold_id</c> already exists, and <see cref="CaptureAsync"/> /
/// <see cref="ExpireAsync"/> transition ONLY an <c>ACTIVE</c> row — a re-delivered or duplicate
/// release folds at most once (the "no double-release" guarantee), reported as the
/// non-<see cref="HoldReleaseResult.Transitioned"/> result so the caller can surface it.
/// <see cref="TruncateAsync"/> is the rebuild path (truncate-then-refold, ACCOUNT_BALANCE_IS_A_FOLD).
/// </remarks>
public interface IAccountHoldStore
{
    /// <summary>
    /// Record a placed hold, idempotently: a row whose <c>hold_id</c> already exists is left
    /// untouched, so a re-delivered <c>HoldPlaced</c> never earmarks twice.
    /// </summary>
    Task PlaceAsync(AccountHoldRow hold, CancellationToken ct = default);

    /// <summary>
    /// Record a placed LEGAL hold (ADR-PC-041), idempotently on <c>hold_id</c>: a court order /
    /// garnishment (<c>operations.FundsHeld</c>) that sets funds aside as a second kind of active
    /// hold. Its <see cref="AccountHoldRow.Kind"/> is <c>LEGAL</c>, it carries a
    /// <see cref="AccountHoldRow.LegalReference"/> and an optional
    /// <see cref="AccountHoldRow.ExpiresAt"/> horizon, and it lowers available balance exactly as an
    /// authorization hold does (the Σ spans both kinds). A re-delivered <c>FundsHeld</c> never
    /// earmarks twice.
    /// </summary>
    Task PlaceLegalAsync(AccountHoldRow legalHold, CancellationToken ct = default);

    /// <summary>
    /// Transition an ACTIVE LEGAL hold to RELEASED (ADR-PC-041): the court order was discharged
    /// (<c>operations.FundsReleased</c>), restoring available balance with NO posting. A legal hold
    /// is never captured, so this is a distinct transition from
    /// <see cref="CaptureAsync"/>/<see cref="ExpireAsync"/>. A hold not currently ACTIVE (or not a
    /// legal hold) transitions nothing; the returned <see cref="HoldReleaseResult"/> says which no-op
    /// it was so the caller can surface it (a reconciliation signal, never a double-restore).
    /// </summary>
    Task<HoldReleaseResult> ReleaseLegalAsync(
        string holdId, Guid releasedStreamId, long releasedSequence, CancellationToken ct = default);

    /// <summary>
    /// Transition an ACTIVE hold to CAPTURED, recording the captured amount and the releasing
    /// event's identity. A hold not currently active transitions nothing; the returned
    /// <see cref="HoldReleaseResult"/> says which no-op it was so the caller can surface it.
    /// </summary>
    Task<HoldReleaseResult> CaptureAsync(
        string holdId, long capturedAmountCents, Guid releasedStreamId, long releasedSequence,
        CancellationToken ct = default);

    /// <summary>
    /// Transition an ACTIVE hold to EXPIRED, recording the releasing event's identity. A hold not
    /// currently active transitions nothing; the returned <see cref="HoldReleaseResult"/> says
    /// which no-op it was so the caller can surface it.
    /// </summary>
    Task<HoldReleaseResult> ExpireAsync(
        string holdId, Guid releasedStreamId, long releasedSequence, CancellationToken ct = default);

    /// <summary>
    /// Σ(active holds) for the account, integer cents — the subtrahend of the available-balance
    /// fold (ADR-PC-033). Zero for an account with no active holds (the uniform empty-set
    /// answer a non-transactional account reads).
    /// </summary>
    Task<long> GetActiveHoldCentsAsync(string accountRef, CancellationToken ct = default);

    /// <summary>The account's currently-active holds, in stable <c>hold_id</c> order.</summary>
    Task<IReadOnlyList<AccountHoldRow>> GetActiveHoldsAsync(string accountRef, CancellationToken ct = default);

    /// <summary>
    /// The cross-account expiry-horizon read (ADR-PC-023): every ACTIVE hold whose
    /// <c>value_date</c> is at or before <paramref name="valueDateHorizon"/>. The horizon is an
    /// INPUT — the operator/command shell that appends <c>HoldExpired</c> supplies it; this read
    /// never consults a clock, so the fold stays replay-deterministic.
    /// </summary>
    Task<IReadOnlyList<AccountHoldRow>> GetActiveHoldsWithValueDateAtOrBeforeAsync(
        DateOnly valueDateHorizon, CancellationToken ct = default);

    /// <summary>
    /// The cross-account LEGAL-hold expiry-horizon read (ADR-PC-041 slot 2 / ADR-PC-023): every
    /// ACTIVE legal hold whose <c>expires_at</c> is non-null and at or before
    /// <paramref name="expiryHorizon"/> — what the operator/command shell reads to decide which
    /// <c>FundsReleased</c> facts to append. Open-ended legal holds (null <c>expires_at</c>) are never
    /// candidates. The horizon is an INPUT, never a clock read, so the fold stays replay-deterministic.
    /// </summary>
    Task<IReadOnlyList<AccountHoldRow>> GetActiveLegalHoldsWithExpiryAtOrBeforeAsync(
        DateOnly expiryHorizon, CancellationToken ct = default);

    /// <summary>Truncate the whole hold set for a clean rebuild (truncate-then-refold, ADR-PC-033).</summary>
    Task TruncateAsync(CancellationToken ct = default);
}

/// <summary>
/// PostgreSQL-backed <see cref="IAccountHoldStore"/>. Hand-rolled, Npgsql-only, all
/// <c>account_holds</c> SQL private to this type — the storage-boundary discipline of
/// <see cref="PostgresMovementLedgerStore"/> applied to the active-hold read model (migration 0020).
/// The idempotent placement is <c>INSERT … ON CONFLICT DO NOTHING</c> on <c>hold_id</c>; the two
/// releases are single <c>UPDATE … WHERE state = 'ACTIVE'</c> transitions, so a duplicate release
/// affects zero rows.
/// </summary>
public sealed class PostgresAccountHoldStore(string connectionString) : IAccountHoldStore
{
    public async Task PlaceAsync(AccountHoldRow hold, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(hold);

        // ON CONFLICT DO NOTHING on hold_id: the lifecycle idempotency key (ADR-PC-033) —
        // a re-delivered HoldPlaced re-inserts the same row as a no-op, never a second earmark.
        // kind = 'AUTHORIZATION' explicitly (the 0021 default) — the legal-hold path is PlaceLegalAsync.
        const string sql = """
            INSERT INTO account_holds
                (hold_id, account_ref, amount_cents, value_date, state, placed_stream_id, placed_sequence, kind)
            VALUES
                (@hold_id, @account_ref, @amount_cents, @value_date, 'ACTIVE', @placed_stream_id, @placed_sequence, 'AUTHORIZATION')
            ON CONFLICT (hold_id) DO NOTHING;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("hold_id", hold.HoldId);
        command.Parameters.AddWithValue("account_ref", hold.AccountRef);
        command.Parameters.AddWithValue("amount_cents", hold.AmountCents);
        command.Parameters.AddWithValue("value_date", (object?)hold.ValueDate ?? DBNull.Value);
        command.Parameters.AddWithValue("placed_stream_id", hold.PlacedStreamId);
        command.Parameters.AddWithValue("placed_sequence", hold.PlacedSequence);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task PlaceLegalAsync(AccountHoldRow legalHold, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(legalHold);

        // The legal-hold placement (ADR-PC-041): kind = 'LEGAL', a legal_reference, an optional
        // expires_at horizon, and NO value_date (a legal hold has no economic effective date). Same
        // ON CONFLICT DO NOTHING idempotency on hold_id — a re-delivered FundsHeld never earmarks twice.
        const string sql = """
            INSERT INTO account_holds
                (hold_id, account_ref, amount_cents, value_date, state, placed_stream_id, placed_sequence,
                 kind, legal_reference, expires_at)
            VALUES
                (@hold_id, @account_ref, @amount_cents, NULL, 'ACTIVE', @placed_stream_id, @placed_sequence,
                 'LEGAL', @legal_reference, @expires_at)
            ON CONFLICT (hold_id) DO NOTHING;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("hold_id", legalHold.HoldId);
        command.Parameters.AddWithValue("account_ref", legalHold.AccountRef);
        command.Parameters.AddWithValue("amount_cents", legalHold.AmountCents);
        command.Parameters.AddWithValue("placed_stream_id", legalHold.PlacedStreamId);
        command.Parameters.AddWithValue("placed_sequence", legalHold.PlacedSequence);
        command.Parameters.AddWithValue("legal_reference", (object?)legalHold.LegalReference ?? DBNull.Value);
        command.Parameters.AddWithValue("expires_at", (object?)legalHold.ExpiresAt ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<HoldReleaseResult> ReleaseLegalAsync(
        string holdId, Guid releasedStreamId, long releasedSequence, CancellationToken ct = default)
    {
        // Transition ONLY an ACTIVE legal hold: a legal release settles nothing, so state -> RELEASED
        // (distinct from CAPTURED/EXPIRED). The kind = 'LEGAL' guard means an authorization hold id
        // never matches here — it would classify as a no-op and surface, not silently release.
        const string sql = """
            UPDATE account_holds
            SET state = 'RELEASED',
                released_stream_id = @released_stream_id,
                released_sequence = @released_sequence
            WHERE hold_id = @hold_id AND state = 'ACTIVE' AND kind = 'LEGAL';
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using (var command = new NpgsqlCommand(sql, connection))
        {
            command.Parameters.AddWithValue("hold_id", holdId);
            command.Parameters.AddWithValue("released_stream_id", releasedStreamId);
            command.Parameters.AddWithValue("released_sequence", releasedSequence);
            if (await command.ExecuteNonQueryAsync(ct) == 1)
            {
                return HoldReleaseResult.Transitioned;
            }
        }

        return await ClassifyNoOpReleaseAsync(connection, holdId, ct);
    }

    public async Task<HoldReleaseResult> CaptureAsync(
        string holdId, long capturedAmountCents, Guid releasedStreamId, long releasedSequence,
        CancellationToken ct = default)
    {
        // Transition ONLY an ACTIVE row: a hold already captured/expired (or never placed) is a
        // zero-row no-op ("a second HoldCaptured is a no-op, never a double-release", ADR-PC-033),
        // classified below so the projector can surface it.
        const string sql = """
            UPDATE account_holds
            SET state = 'CAPTURED',
                captured_amount_cents = @captured_amount_cents,
                released_stream_id = @released_stream_id,
                released_sequence = @released_sequence
            WHERE hold_id = @hold_id AND state = 'ACTIVE';
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using (var command = new NpgsqlCommand(sql, connection))
        {
            command.Parameters.AddWithValue("hold_id", holdId);
            command.Parameters.AddWithValue("captured_amount_cents", capturedAmountCents);
            command.Parameters.AddWithValue("released_stream_id", releasedStreamId);
            command.Parameters.AddWithValue("released_sequence", releasedSequence);
            if (await command.ExecuteNonQueryAsync(ct) == 1)
            {
                return HoldReleaseResult.Transitioned;
            }
        }

        return await ClassifyNoOpReleaseAsync(connection, holdId, ct);
    }

    public async Task<HoldReleaseResult> ExpireAsync(
        string holdId, Guid releasedStreamId, long releasedSequence, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE account_holds
            SET state = 'EXPIRED',
                released_stream_id = @released_stream_id,
                released_sequence = @released_sequence
            WHERE hold_id = @hold_id AND state = 'ACTIVE';
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using (var command = new NpgsqlCommand(sql, connection))
        {
            command.Parameters.AddWithValue("hold_id", holdId);
            command.Parameters.AddWithValue("released_stream_id", releasedStreamId);
            command.Parameters.AddWithValue("released_sequence", releasedSequence);
            if (await command.ExecuteNonQueryAsync(ct) == 1)
            {
                return HoldReleaseResult.Transitioned;
            }
        }

        return await ClassifyNoOpReleaseAsync(connection, holdId, ct);
    }

    // A zero-row release is one of two very different facts (ADR-PC-033): the hold exists but
    // already left the active set (a duplicate/late release), or it was NEVER placed (a fold-order
    // error). One existence probe on the already-open connection tells them apart.
    private static async Task<HoldReleaseResult> ClassifyNoOpReleaseAsync(
        NpgsqlConnection connection, string holdId, CancellationToken ct)
    {
        const string sql = "SELECT EXISTS (SELECT 1 FROM account_holds WHERE hold_id = @hold_id);";
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("hold_id", holdId);
        return (bool)(await command.ExecuteScalarAsync(ct))!
            ? HoldReleaseResult.AlreadyReleased
            : HoldReleaseResult.NeverPlaced;
    }

    public async Task<long> GetActiveHoldCentsAsync(string accountRef, CancellationToken ct = default)
    {
        // COALESCE makes an account with no active holds read as zero — the uniform empty-hold-set
        // answer that keeps the available-balance fold total over every account (ADR-PC-033).
        // ::bigint because SUM(bigint) returns NUMERIC (surfaced as decimal), never an Int64.
        const string sql = """
            SELECT COALESCE(SUM(amount_cents), 0)::bigint
            FROM account_holds
            WHERE account_ref = @account_ref AND state = 'ACTIVE';
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("account_ref", accountRef);
        // Hard unbox: COALESCE guarantees a non-null value and ::bigint guarantees Int64, so any
        // other shape is schema/query drift — throw (InvalidCastException), never mask it as 0.
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    public async Task<IReadOnlyList<AccountHoldRow>> GetActiveHoldsAsync(
        string accountRef, CancellationToken ct = default)
    {
        const string sql = """
            SELECT hold_id, account_ref, amount_cents, value_date, state, placed_stream_id,
                   placed_sequence, captured_amount_cents, released_stream_id, released_sequence,
                   kind, legal_reference, expires_at
            FROM account_holds
            WHERE account_ref = @account_ref AND state = 'ACTIVE'
            ORDER BY hold_id;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("account_ref", accountRef);
        return await ReadRowsAsync(command, ct);
    }

    public async Task<IReadOnlyList<AccountHoldRow>> GetActiveHoldsWithValueDateAtOrBeforeAsync(
        DateOnly valueDateHorizon, CancellationToken ct = default)
    {
        // AUTHORIZATION holds only: their expiry horizon is value_date (-> HoldExpired). Legal holds
        // have a separate horizon (expires_at -> FundsReleased) and their own read below, so the two
        // operator expiry lanes never cross (ADR-PC-041 slot 2).
        const string sql = """
            SELECT hold_id, account_ref, amount_cents, value_date, state, placed_stream_id,
                   placed_sequence, captured_amount_cents, released_stream_id, released_sequence,
                   kind, legal_reference, expires_at
            FROM account_holds
            WHERE state = 'ACTIVE' AND kind = 'AUTHORIZATION' AND value_date <= @value_date_horizon
            ORDER BY account_ref, hold_id;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("value_date_horizon", valueDateHorizon);
        return await ReadRowsAsync(command, ct);
    }

    public async Task<IReadOnlyList<AccountHoldRow>> GetActiveLegalHoldsWithExpiryAtOrBeforeAsync(
        DateOnly expiryHorizon, CancellationToken ct = default)
    {
        // LEGAL holds only, with a non-null horizon that has passed (ADR-PC-041 slot 2 / ADR-PC-023):
        // an open-ended legal hold (expires_at IS NULL) is never an expiry candidate.
        const string sql = """
            SELECT hold_id, account_ref, amount_cents, value_date, state, placed_stream_id,
                   placed_sequence, captured_amount_cents, released_stream_id, released_sequence,
                   kind, legal_reference, expires_at
            FROM account_holds
            WHERE state = 'ACTIVE' AND kind = 'LEGAL'
                  AND expires_at IS NOT NULL AND expires_at <= @expiry_horizon
            ORDER BY account_ref, hold_id;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("expiry_horizon", expiryHorizon);
        return await ReadRowsAsync(command, ct);
    }

    public async Task TruncateAsync(CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("TRUNCATE TABLE account_holds;", connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<IReadOnlyList<AccountHoldRow>> ReadRowsAsync(
        NpgsqlCommand command, CancellationToken ct)
    {
        var holds = new List<AccountHoldRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            holds.Add(new AccountHoldRow(
                HoldId: reader.GetString(0),
                AccountRef: reader.GetString(1),
                AmountCents: reader.GetInt64(2),
                ValueDate: reader.IsDBNull(3) ? null : reader.GetFieldValue<DateOnly>(3),
                State: reader.GetString(4),
                PlacedStreamId: reader.GetGuid(5),
                PlacedSequence: reader.GetInt64(6),
                CapturedAmountCents: reader.IsDBNull(7) ? null : reader.GetInt64(7),
                ReleasedStreamId: reader.IsDBNull(8) ? null : reader.GetGuid(8),
                ReleasedSequence: reader.IsDBNull(9) ? null : reader.GetInt64(9),
                Kind: reader.GetString(10),
                LegalReference: reader.IsDBNull(11) ? null : reader.GetString(11),
                ExpiresAt: reader.IsDBNull(12) ? null : reader.GetFieldValue<DateOnly>(12)));
        }

        return holds;
    }
}
